using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

public static partial class StartupHook
{
    private static object LiveViewerModel(object view)
    {
        var type = view.GetType();
        while (type is not null && type.FullName != "P1.C1.Tools.Viewer.WPFViewing.VariantViewWpf")
            type = type.BaseType;
        return type!.GetProperty("ViewerModel", Flags | BindingFlags.DeclaredOnly)!.GetValue(view)!;
    }

    private static async Task TestNativePresentationMotion(object spatial, object viewer, Guid variant, object source)
    {
        var paintWait = Stopwatch.GetTimestamp();
        while (Prop(viewer, "HasPainted") is not true)
        {
            if (Stopwatch.GetElapsedTime(paintWait) >= TimeSpan.FromSeconds(12))
                throw new InvalidOperationException("The manual-resolution Viewer was not painted before the motion test.");
            await Task.Delay(75);
        }
        var attachment = Field(viewer, "attachment")!;
        var model = LiveViewerModel(Prop(attachment, "View")!);
        var fit = model.GetType().GetProperty("ZoomToFit", Flags)!;
        var zoom = model.GetType().GetProperty("DisplayZoom", Flags)!;
        var center = model.GetType().GetProperty("ViewCenter", Flags)!;
        var beforeFit = (bool)fit.GetValue(model)!;
        var beforeZoom = (float)zoom.GetValue(model)!;
        var beforeCenter = (PointF)center.GetValue(model)!;
        var worker = Field(spatial, "renderer")!;
        var bitmap = Field(spatial, "viewerFrame")!;
        Matrix Transform() => (Matrix)Prop(Prop(Field(viewer, "attachment")!, "Geometry")!, "Transform")!;
        double Projected() => (double)Call(viewer, "ProjectedLongEdge", variant)!;
        async Task<int> RenderSequence()
        {
            var diagnostic = await (Task<JsonElement?>)Call(worker, "ReadDiagnosticsAsync", CancellationToken.None)!;
            return diagnostic!.Value.GetProperty("renderSequence").GetInt32();
        }
        var before = Transform();
        var beforeProjected = Projected();
        var sequence = await RenderSequence();
        async Task WaitForPresentation(Func<Matrix, bool> predicate, string operation)
        {
            var started = Stopwatch.GetTimestamp();
            do
            {
                if (Field(viewer, "attachment") is { } live && Prop(live, "Geometry") is not null
                    && Prop(viewer, "HasPainted") is true && predicate(Transform())) return;
                await Task.Delay(75);
            } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(12));
            throw new InvalidOperationException($"Native {operation} did not update the film drawing geometry. Zoom {zoom.GetValue(model)}, centre {center.GetValue(model)}.");
        }
        async Task VerifySameRender(string operation)
        {
            await Task.Delay(550);
            var same = ReferenceEquals(Field(spatial, "source"), source)
                && ReferenceEquals(Field(spatial, "viewerFrame"), bitmap)
                && ReferenceEquals(Field(spatial, "renderer"), worker)
                && await RenderSequence() == sequence;
            Check(same, $"native {operation} reused the exact high-depth source, film bitmap and worker generation at manual resolution");
        }
        try
        {
            var maxZoom = (float)Prop(model, "ZoomMaximum")!;
            var nextZoom = Math.Min(maxZoom, Math.Max(1.0f, beforeZoom * 1.5f));
            Call(model, "ZoomToPointRelative", new PointF(.5f, .5f), nextZoom);
            await WaitForPresentation(matrix => matrix != before && Projected() > beforeProjected * 1.1, "zoom");
            await VerifySameRender("zoom");
            var zoomed = Transform();
            var zoomedProjected = Projected();
            var viewCenter = (PointF)center.GetValue(model)!;
            var canvas = Prop(model, "CanvasSize")!;
            var horizontal = Convert.ToSingle(Prop(canvas, "Width")) * .1f;
            var vertical = Convert.ToSingle(Prop(canvas, "Height")) * .07f;
            center.SetValue(model, new PointF(viewCenter.X + horizontal, viewCenter.Y - vertical));
            await WaitForPresentation(matrix => Math.Abs(matrix.OffsetX - zoomed.OffsetX) + Math.Abs(matrix.OffsetY - zoomed.OffsetY) > 8, "pan");
            Check(Math.Abs(Projected() - zoomedProjected) < 1e-5, "native pan changes the Viewer translation and retains its projected size");
            await VerifySameRender("pan");
            var panned = Transform();
            File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
                $"Native Viewer motion: projection {beforeProjected:F2} to {zoomedProjected:F2} px, " +
                $"pan offset {panned.OffsetX - zoomed.OffsetX:F2} by {panned.OffsetY - zoomed.OffsetY:F2} px, " +
                $"unchanged worker render sequence {sequence}." + Environment.NewLine);
        }
        finally
        {
            zoom.SetValue(model, beforeZoom);
            center.SetValue(model, beforeCenter);
            fit.SetValue(model, beforeFit);
        }
        await WaitForPresentation(_ => Math.Abs(Projected() - beforeProjected) < 0.05, "zoom restoration");
        Check(ReferenceEquals(Field(spatial, "viewerFrame"), bitmap), "restoring the native viewport preserves the same film bitmap");
    }
}
