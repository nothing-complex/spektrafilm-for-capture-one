using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

    private static async Task TestNativeAutoQualityMotion(object spatial, object viewer, Guid variant)
    {
        var model = LiveViewerModel(Prop(Field(viewer, "attachment")!, "View")!);
        var fit = model.GetType().GetProperty("ZoomToFit", Flags)!;
        var zoom = model.GetType().GetProperty("DisplayZoom", Flags)!;
        var center = model.GetType().GetProperty("ViewCenter", Flags)!;
        var beforeFit = (bool)fit.GetValue(model)!;
        var beforeZoom = (float)zoom.GetValue(model)!;
        var beforeCenter = (PointF)center.GetValue(model)!;
        var nativeViewer = Prop(Field(spatial, "window")!, "Viewer")!;
        var grid = (FrameworkElement)Prop(nativeViewer, "VariantGridView")!;
        var previousMaxWidth = grid.MaxWidth;
        var previousMaxHeight = grid.MaxHeight;
        var start = Stopwatch.GetTimestamp();
        double Projection() => Call(viewer, "ProjectedLongEdge", variant) as double? ?? 0;
        double initialProjection = Projection();
        async Task<(object Frame, int Edge)> WaitForTier(Func<double, int, bool> accepts)
        {
            var wait = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(wait) < TimeSpan.FromSeconds(18))
            {
                if (Field(spatial, "frameCurrent") is true && Prop(viewer, "HasPainted") is true
                    && Field(spatial, "source") is { } frame && Field(spatial, "sourceSize") is string size
                    && Equals(Prop(spatial, "ViewerRenderSize"), size)
                    && int.TryParse(size, CultureInfo.InvariantCulture, out var edge) && accepts(Projection(), edge))
                    return (frame, edge);
                await Task.Delay(65);
            }
            throw new TimeoutException($"Auto did not settle to the new native Viewer tier. Native zoom {zoom.GetValue(model)}, " +
                $"projection {Projection():F1}, source {Field(spatial, "sourceSize")}.");
        }
        try
        {
            fit.SetValue(model, true);
            grid.SetCurrentValue(FrameworkElement.MaxWidthProperty, 640d);
            grid.SetCurrentValue(FrameworkElement.MaxHeightProperty, 460d);
            var small = await WaitForTier((projection, edge) => projection is > 0 and < 1300 && edge <= 1600);
            var smallProjection = Projection();
            grid.SetCurrentValue(FrameworkElement.MaxWidthProperty, previousMaxWidth);
            grid.SetCurrentValue(FrameworkElement.MaxHeightProperty, previousMaxHeight);
            Call(model, "ZoomToPointRelative", new PointF(.5f, .5f), 1.0f);
            var large = await WaitForTier((projection, edge) => projection > 4200 && edge == 4096);
            Check(small.Edge < large.Edge && !ReferenceEquals(small.Frame, large.Frame)
                && Prop(small.Frame, "BitDepth") is 16 && Prop(large.Frame, "BitDepth") is 16,
                "Auto uses a smaller RGB16 source in the resized native Viewer and develops a new RGB16 frame when zoom crosses the upper tier");
            var largeProjection = Projection();
            var bitmap = Field(spatial, "viewerFrame");
            var transform = (Matrix)Prop(Prop(Field(viewer, "attachment")!, "Geometry")!, "Transform")!;
            var viewCenter = (PointF)center.GetValue(model)!;
            center.SetValue(model, new PointF(viewCenter.X + 110, viewCenter.Y - 80));
            var panWait = Stopwatch.GetTimestamp();
            while (true)
            {
                if (Field(viewer, "attachment") is { } attachment && Prop(attachment, "Geometry") is { } geometry
                    && Prop(geometry, "Transform") is Matrix current
                    && Math.Abs(current.OffsetX - transform.OffsetX) + Math.Abs(current.OffsetY - transform.OffsetY) > 8)
                    break;
                if (Stopwatch.GetElapsedTime(panWait) > TimeSpan.FromSeconds(8))
                    throw new TimeoutException("The native Auto Viewer did not apply the pan.");
                await Task.Delay(50);
            }
            await Task.Delay(950);
            Check(Math.Abs(Projection() - largeProjection) < .05 && ReferenceEquals(bitmap, Field(spatial, "viewerFrame"))
                && ReferenceEquals(large.Frame, Field(spatial, "source")),
                "Auto pans the film overlay without redeveloping or reprocessing at the same projection");
            File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
                $"Auto Viewer native zoom {smallProjection:F0} to {largeProjection:F0} projected pixels, " +
                $"tiers {small.Edge} to {large.Edge}, including stable pan in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms." + Environment.NewLine);
        }
        finally
        {
            grid.SetCurrentValue(FrameworkElement.MaxWidthProperty, previousMaxWidth);
            grid.SetCurrentValue(FrameworkElement.MaxHeightProperty, previousMaxHeight);
            zoom.SetValue(model, beforeZoom);
            center.SetValue(model, beforeCenter);
            fit.SetValue(model, beforeFit);
        }
        await WaitForTier((projection, _) => Math.Abs(projection - initialProjection) < .05);
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
