using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static partial class StartupHook
{
    private static async Task TestViewerLayouts(object panel, object spatial, object window)
    {
        if (!File.Exists(Path.Combine(folder, "Capture One user.config.before-proofing")))
            throw new InvalidOperationException("A verified Capture One preferences backup is required for the Viewer layout fixture.");
        var app = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "CaptureOne");
        var selection = new FixtureSelection(panel);
        var candidates = selection.Variants;
        var selected = (Guid)Field(panel, "displayedVariant")!;
        Check(candidates.Length == 4 && candidates[0] == selected, "disposable catalog provides four real still-image targets");

        var settingsType = app.GetType("P1.C1.ApplicationSettings", true)!;
        var settings = settingsType.GetProperty("Default", Flags)!.GetValue(null)!;
        var onlyPrimary = settingsType.GetProperty("ViewerShowOnlyPrimary", Flags)!;
        var wasOnlyPrimary = (bool)onlyPrimary.GetValue(settings)!;
        var comparison = app.GetType("P1.C1.Program", true)!.GetProperty("VariantViewToggleManager", Flags)!.GetValue(null)!;
        var wasComparisonEnabled = (bool)Prop(comparison, "IsEnabled")!;
        var viewer = Field(spatial, "viewer")!;
        var allFilmFrames = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var originalToggle = toggle.IsChecked;
        FrameworkElement Grid() => (FrameworkElement)Prop(Prop(window, "Viewer")!, "VariantGridView")!;

        async Task RenderLayout(int count, int primary)
        {
            var expected = candidates[primary];
            selection.Select(expected, candidates[..count]);
            var started = Stopwatch.GetTimestamp();
            var lastSeen = "initializing";
            do
            {
                var grid = Grid();
                var model = Field(grid, "m_viewModel")!;
                var list = ((IEnumerable)Prop(model, "ViewedVariants")!).Cast<object>().ToArray();
                var inspected = ViewNodes(grid).ToArray();
                lastSeen = $"list {list.Length}, visible {inspected.Length}, requested {expected}, " +
                    $"selected {Field(panel, "displayedVariant")}, state {Prop(viewer, "UnavailableReason")}";
                if (list.Length == count && inspected.Length == count && Field(spatial, "frameCurrent") is true
                    && Field(viewer, "attachment") is { } attachment && Equals(Prop(attachment, "VariantId"), expected)
                    && Prop(viewer, "HasPainted") is true && Field(spatial, "viewerFrame") is BitmapSource bitmap)
                {
                    allFilmFrames.Add(bitmap);
                    var painter = (Visual)Prop(attachment, "Painter")!;
                    var expectedPaint = inspected.Single(view => view.Id == expected);
                    var layers = inspected.Select(view => (view.Id, Film: ImageLayers(view.Painter)
                        .OfType<BitmapSource>().Where(allFilmFrames.Contains).ToArray())).ToArray();
                    if (ReferenceEquals(expectedPaint.Painter, painter) && layers.Single(layer => layer.Id == expected)
                            .Film is [var film] && ReferenceEquals(film, bitmap) &&
                        layers.Where(layer => layer.Id != expected).All(layer => layer.Film.Length == 0))
                    {
                        Check(true, $"{count}-image native layout paints only selected variant {primary + 1}, with no stale film layers");
                        return;
                    }
                }
                await Task.Delay(100);
            } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(35));
            throw new InvalidOperationException($"Native Viewer did not settle for {count} images and primary slot {primary + 1}: {lastSeen}");
        }

        IEnumerable<(Guid Id, Visual Painter)> ViewNodes(DependencyObject node)
        {
            if (node is UIElement { IsVisible: false }) yield break;
            if (node.GetType().Name == "CompareCapableVariantGridItem")
            {
                var view = Field(node, "Viewer");
                if (view is UIElement { IsVisible: true } && Prop(view, "Variant") is { } variant &&
                    Prop(view, "WpfViewer") is Visual painter)
                    yield return (selection.Identity(variant), painter);
                yield break;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                foreach (var view in ViewNodes(VisualTreeHelper.GetChild(node, index))) yield return view;
        }

        async Task TestNativeComparison()
        {
            var previous = Field(spatial, "viewerFrame") as BitmapSource;
            var selectedAttachment = Field(viewer, "attachment")!;
            var view = Prop(selectedAttachment, "View")!;
            var viewType = app.GetType("P1.C1.Tools.Viewer.WPFViewing.VariantViewWpf", true)!;
            var model = viewType.GetProperty("ViewerModel", Flags | BindingFlags.DeclaredOnly)!.GetValue(view)!;
            var modelType = model.GetType();
            File.WriteAllLines(Path.Combine(folder, "viewer-model.txt"),
                new[] { modelType.FullName! }.Concat(modelType.GetMembers(Flags)
                    .Where(member => System.Text.RegularExpressions.Regex.IsMatch(member.Name, "scale|zoom|pan|translat", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    .Select(member => member.ToString()!)));
            Call(comparison, "ToggleOn");
            var started = Stopwatch.GetTimestamp();
            do
            {
                var current = Field(viewer, "attachment");
                if (current is not null && Prop(current, "Item") is { } item
                    && Prop(Prop(item, "BeforeAfterLabelsViewModel")!, "HasBeforeView") is true
                    && (Prop(viewer, "UnavailableReason") as string)?.Contains("Before/After", StringComparison.Ordinal) == true
                    && !ImageLayers(Grid()).OfType<BitmapSource>().Any(allFilmFrames.Contains))
                {
                    Check(previous is not null && Prop(comparison, "IsEnabled") is true,
                        $"native Before/After ({Prop(comparison, "SelectedMode")}) creates its before view and excludes Spektrafilm from the drawing");
                    Call(comparison, "ToggleOff");
                    await RenderLayout(1, 0);
                    Check(ReferenceEquals(Field(spatial, "viewerFrame"), previous),
                        "ending native Before/After returns to the same cached selected film bitmap");
                    return;
                }
                await Task.Delay(100);
            } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20));
            throw new InvalidOperationException($"Capture One did not suspend the film overlay in its native comparison mode. State: {Prop(viewer, "UnavailableReason")}");
        }

        try
        {
            onlyPrimary.SetValue(settings, false);
            if (wasComparisonEnabled) Call(comparison, "ToggleOff");
            toggle.IsChecked = true;
            await RenderLayout(1, 0);
            await RenderLayout(2, 0);
            await RenderLayout(2, 1);
            await RenderLayout(4, 1);
            await RenderLayout(4, 3);
            await RenderLayout(1, 0);
            await TestNativeComparison();
        }
        finally
        {
            if (Prop(comparison, "IsEnabled") is bool enabled && enabled != wasComparisonEnabled)
                Call(comparison, wasComparisonEnabled ? "ToggleOn" : "ToggleOff");
            selection.Select(candidates[0], candidates[0]);
            toggle.IsChecked = originalToggle;
            onlyPrimary.SetValue(settings, wasOnlyPrimary);
        }
        Check(Equals(Prop(comparison, "IsEnabled"), wasComparisonEnabled), "disposable layout fixture restored the native Before/After setting");
        Check(Equals(onlyPrimary.GetValue(settings), wasOnlyPrimary), "disposable Viewer fixture restores the native primary-only preference");
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native Viewer layout assertions");
    }
}
