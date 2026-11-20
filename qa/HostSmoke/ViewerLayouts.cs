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
        var document = Field(panel, "document")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.Core");
        var app = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "CaptureOne");
        var provider = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
        var collection = provider.GetProperty("PrimarySelectedCollection")!.GetValue(document)!;
        var selection = provider.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
        var target = core.GetType("P1.C1.Core.Selection.ISelection", true)!.GetProperty("SelectedVariants")!.GetValue(selection)!;
        var selectionType = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!;
        var selectOnly = selectionType.GetMethod("SelectOnly")!;
        var selectPrimary = selectionType.GetMethod("SelectAndSetAsPrimary")!;
        var variantType = core.GetType("P1.C1.Core.ICoreVariant", true)!;
        var uuid = variantType.GetProperty("UUID")!;
        Guid Identity(object item) => (Guid)uuid.GetValue(item)!;
        var selected = (Guid)Field(panel, "displayedVariant")!;
        var candidates = ((IEnumerable)core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!
            .GetMethod("GetAllFilteredAndVisibleVariants")!.Invoke(collection, null)!).Cast<object>()
            .OrderByDescending(item => Identity(item) == selected).Take(4).ToArray();
        Check(candidates.Length == 4 && Identity(candidates[0]) == selected, "disposable catalog provides four real still-image targets");

        void Select(int count, int primary)
        {
            var items = Array.CreateInstance(selectOnly.GetParameters()[0].ParameterType.GetElementType()!, count);
            for (var index = 0; index < count; index++) items.SetValue(candidates[index], index);
            selectOnly.Invoke(target, new object[] { items });
            Call(panel, "SyncSelection");
            if (!Equals(Field(panel, "displayedVariant"), Identity(candidates[primary])))
            {
                selectPrimary.Invoke(target, new[] { candidates[primary], (object)true });
                Call(panel, "SyncSelection");
            }
        }

        var settingsType = app.GetType("P1.C1.ApplicationSettings", true)!;
        var settings = settingsType.GetProperty("Default", Flags)!.GetValue(null)!;
        var onlyPrimary = settingsType.GetProperty("ViewerShowOnlyPrimary", Flags)!;
        var wasOnlyPrimary = (bool)onlyPrimary.GetValue(settings)!;
        var viewer = Field(spatial, "viewer")!;
        var allFilmFrames = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var originalToggle = toggle.IsChecked;

        async Task RenderLayout(int count, int primary)
        {
            Select(count, primary);
            var expected = Identity(candidates[primary]);
            var started = Stopwatch.GetTimestamp();
            var lastSeen = "initializing";
            do
            {
                var container = Prop(window, "Viewer")!;
                var grid = (FrameworkElement)Prop(container, "VariantGridView")!;
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
                    yield return (Identity(variant), painter);
                yield break;
            }
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                foreach (var view in ViewNodes(VisualTreeHelper.GetChild(node, index))) yield return view;
        }

        try
        {
            onlyPrimary.SetValue(settings, false);
            toggle.IsChecked = true;
            await RenderLayout(1, 0);
            await RenderLayout(2, 0);
            await RenderLayout(2, 1);
            await RenderLayout(4, 1);
            await RenderLayout(4, 3);
            await RenderLayout(1, 0);
        }
        finally
        {
            Select(1, 0);
            toggle.IsChecked = originalToggle;
            onlyPrimary.SetValue(settings, wasOnlyPrimary);
        }
        Check(Equals(onlyPrimary.GetValue(settings), wasOnlyPrimary), "disposable Viewer fixture restores the native primary-only preference");
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native Viewer layout assertions");
    }
}
