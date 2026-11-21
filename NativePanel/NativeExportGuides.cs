using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows;
using P1.C1;
using P1.C1.Components.Guides;
using P1.C1.Core;
using P1.C1.Core.DestinationProcessing;
using P1.C1.Core.Guides;
using P1.C1.Core.ModelEntities;
using P1.C1.Framework.Drawing;

internal static class NativeExportGuides
{
    private static readonly Lazy<MethodInfo?> ReadGuidesManager = new(() => typeof(GuidesManager).Assembly
        .GetType("P1.C1.Components.Document.Manager.DocumentManager")?.GetMethod("GetGuidesManager",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { typeof(ICaptureOneDocument) }, null));

    internal static PsdGuide[]? Capture(HostProfile.Selection selection, IDestinationProcessingSettings recipe,
        IVariantGeometryTransformer geometry)
    {
        try
        {
            if (!GlobalCapabilities.HasFeature(Feature.AdvancedGuides) || ReadGuidesManager.Value is not { } getGuides
                || getGuides.DeclaringType?.IsInstanceOfType(selection.Manager) != true
                || getGuides.Invoke(selection.Manager, new[] { selection.Document }) is not GuidesManager guides)
                return null;
            var selected = guides.Guides.Take(PsdGuide.MaximumCount + 1).ToArray();
            if (selected.Length > PsdGuide.MaximumCount)
                return null;
            if (selected.Length == 0)
                return [];

            var canvas = geometry.CanvasSize;
            var crop = geometry.CropCanvasConstrained;
            var relative = guides.RelativeToCrop;
            var area = relative ? new Rect(crop.Left, crop.Top, crop.Width, crop.Height)
                : new Rect(0, 0, (int)canvas.Width, (int)canvas.Height);
            return Project(selected, recipe, canvas, area, relative);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException or TargetInvocationException)
        {
            return null;
        }
    }

    internal static PsdGuide[]? Project(IReadOnlyList<GuideData> guides, IDestinationProcessingSettings recipe,
        SizeF canvas, Rect area, bool relativeToCrop)
    {
        if (!(canvas.Width >= 1 && canvas.Height >= 1 && float.IsFinite(canvas.Width) && float.IsFinite(canvas.Height))
            || area.IsEmpty || !double.IsFinite(area.Left) || !double.IsFinite(area.Top)
            || !(area.Width >= 1 && area.Width <= int.MaxValue && area.Height >= 1 && area.Height <= int.MaxValue)
            || guides.Count > PsdGuide.MaximumCount)
            return null;

        var scale = new ScaleCalc(new System.Drawing.Size((int)area.Width, (int)area.Height), recipe);
        var calculator = new GuideCalculator(scale, recipe, relativeToCrop);
        var target = new Rect(0, 0, area.Width, area.Height).ToRectangleInt();
        var result = new PsdGuide[guides.Count];
        for (var index = 0; index < guides.Count; index++)
        {
            var guide = guides[index];
            if (guide is null || !float.IsFinite(guide.Coordinate) || !Enum.IsDefined(guide.Unit) || !Enum.IsDefined(guide.RelativeTo))
                return null;
            var vertical = GuideCalculator.IsVertical(guide.RelativeTo);
            var onCanvas = calculator.GetTargetCoordinate(guide, target) + (vertical ? area.Left : area.Top);
            var fraction = onCanvas / (vertical ? canvas.Width : canvas.Height);
            if (!double.IsFinite(fraction))
                return null;
            result[index] = new PsdGuide(vertical, fraction);
        }
        return result;
    }
}
