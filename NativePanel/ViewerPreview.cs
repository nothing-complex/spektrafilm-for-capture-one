using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using P1.C1;
using P1.C1.Core;
using P1.C1.Core.Interfaces;
using P1.C1.Core.ModelEntities;
using P1.C1.Tools.Viewer;
using P1.C1.Tools.Viewer.WPFViewing;
using P1.C1.ViewModels.Viewer;
using PaintEventArgs = P1.C1.Tools.Viewer.WPFViewing.WPFControlWithGDIOnPaint.PaintEventArgsWithWPFContext;

internal sealed class ViewerPreview : IDisposable
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string InactiveDocument = "Activate the selected catalog to resume the Spektrafilm preview.";
    private const string WaitingForGeometry = "Waiting for a visible image in the main Viewer.";
    private const string RecipeProofing = "Turn off Capture One Recipe Proofing to show the Spektrafilm Viewer.";
    private readonly object window;
    private readonly byte[] sampleBytes = new byte[4];
    private Attachment? attachment;
    private (BitmapSource Bitmap, ViewerFrameTiming Timing)? firstPaint;
    private DispatcherOperation? statusNotification;
    private BitmapSource? clippingSource;
    private BitmapSource? clippingMask;
    private bool inspectionEnabled;
    private bool disposed;

    internal ViewerPreview(object window) => this.window = window;
    internal string? UnavailableReason { get; private set; }
    internal bool HasPainted => UnavailableReason is null && attachment is { HasDrawn: true } current
        && ReferenceEquals(current.PaintedBitmap, current.Bitmap) && Availability(current) is null;
    internal event Action? StatusChanged;
    internal event Action<ViewerPixel?>? PixelChanged;

    internal double? ProjectedLongEdge(Guid variant) => UnavailableReason is null && attachment is { HasDrawn: true, PaintUnavailable: null } current
        && current.VariantId == variant ? current.ProjectedLongEdge : null;

    internal double? MeasureBeforePaint(Guid variant)
    {
        if (disposed) return null;
        try
        {
            if (ApplicationSettings.Default.IsProofingRecipeSettings) return null;
            var manager = HostDocumentAccess.Current();
            if (manager?.ActiveDocument() is not { } document || !ApprovedCatalog.IsApproved(manager.ActivePath(), document.Path)
                || !IsSelected(document, variant) || ReadGrid() is not { IsVisible: true } grid
                || FindVisibleItems(grid, document, variant) is not [{ Item: var item, View: var view }]
                || item.BeforeAfterLabelsViewModel is not { HasBeforeView: false }
                || !view.IsEnabled || view.IsMovie || view.BlackWhiteMode
                || view.WpfViewer is not { InSlideShowMode: false }
                || view.ViewerModel is not { IsDrawable: true, ViewMode: ViewMode.Default } model)
                return null;

            // The physical projection depends on the canvas corners. A unit
            // bitmap measures it before a developed frame has been allocated.
            return ViewerGeometry.TryCreate(model.GetClipRectangle(), model.CanvasCoordinates, 1, 1, out var geometry)
                ? geometry.ProjectedLongEdge : null;
        }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm initial Viewer geometry: " + ErrorMessage(error));
            return null;
        }
    }

    private FrameworkElement? ReadGrid() =>
        Get(window, "Viewer") is { } view ? Get(view, "VariantGridView") as FrameworkElement : null;

    internal void TrackFirstPaint(BitmapSource bitmap, ViewerFrameTiming timing)
    {
        if (!ReferenceEquals(attachment?.PaintedBitmap, bitmap))
        {
            if (firstPaint is { } pending && ReferenceEquals(pending.Bitmap, bitmap))
                return;
            DiscardUnpaintedFrame();
            firstPaint = (bitmap, timing);
        }
    }

    internal bool Show(Guid variantId, BitmapSource bitmap)
    {
        if (disposed)
            return false;
        if (firstPaint is { } pending && !ReferenceEquals(pending.Bitmap, bitmap))
            DiscardUnpaintedFrame();

        try
        {
            if (ReadGrid() is not { IsVisible: true } grid)
                return Unavailable("Show the main Viewer to see the live preview.");

            if (attachment is { } current && current.Grid == grid && current.VariantId == variantId
                && current.View.Variant?.UUID == variantId && grid.IsAncestorOf(current.View))
            {
                var reason = Availability(current) ?? current.PaintUnavailable;
                var changed = UnavailableReason != reason;
                SetStatus(reason);
                if (changed || !ReferenceEquals(current.Bitmap, bitmap))
                {
                    current.Bitmap = bitmap;
                    Invalidate(current.View);
                }
                return UnavailableReason is null;
            }

            var manager = HostDocumentAccess.Current();
            if (manager?.ActiveDocument() is not { } document || !IsSelected(document, variantId))
                return Unavailable("The selected image changed.");

            if (Field(grid, "m_viewModel") is not { } gridModel)
                return Unavailable("Capture One did not expose its Viewer image list.");

            if (FindVisibleItems(grid, document, variantId) is not [{ Item: var item, View: var view }])
            {
                const string reason = "Waiting for the selected image to reach a unique view in the main Viewer.";
                if (UnavailableReason != reason)
                    StartupHook.Log($"Selected Viewer image control unavailable under {DescribeVisuals(grid)}");
                return Unavailable(reason);
            }
            var painter = view.WpfViewer;
            if (painter is null)
                return Unavailable("Capture One did not expose the selected image’s Viewer renderer.");

            Detach();
            var readGridDocument = BindGetter<ICaptureOneDocument?>(gridModel, "Document");
            var readViewedVariants = BindGetter<IReadOnlyList<GridViewItemData>>(gridModel, "ViewedVariants");
            var attached = new Attachment(this, variantId, document, grid, item, view, readGridDocument, readViewedVariants, painter)
            {
                Bitmap = bitmap,
                PaintUnavailable = WaitingForGeometry,
            };
            attachment = attached;
            painter.Paint += attached.OnPaint;
            painter.AddHandler(UIElement.MouseMoveEvent, new MouseEventHandler(OnPointerMoved), handledEventsToo: true);
            painter.MouseLeave += OnPointerLeft;
            view.Unloaded += OnViewUnloaded;
            var unavailable = Availability(attached) ?? attached.PaintUnavailable;
            SetStatus(unavailable);
            Invalidate(view);
            return unavailable is null;
        }
        catch (Exception error)
        {
            const string reason = "Capture One’s Viewer could not show the live preview.";
            if (UnavailableReason != reason)
                StartupHook.Log(ErrorMessage(error));
            return Unavailable(reason);
        }
    }

    internal void Suspend(string reason = InactiveDocument)
    {
        DiscardUnpaintedFrame();
        Detach();
        SetStatus(reason);
    }

    internal void Clear()
    {
        DiscardUnpaintedFrame();
        Detach();
        SetStatus(null);
    }

    internal void InspectPixels(bool enabled)
    {
        inspectionEnabled = enabled;
        if (!enabled) PixelChanged?.Invoke(null);
    }

    internal void ShowClipping(BitmapSource? source, BitmapSource? mask)
    {
        if (ReferenceEquals(clippingSource, source) && ReferenceEquals(clippingMask, mask))
            return;
        clippingSource = source;
        clippingMask = mask;
        if (attachment is { } current)
            Invalidate(current.View);
    }

    private void Detach()
    {
        var previous = attachment;
        attachment = null;
        if (previous is null)
            return;

        previous.View.Unloaded -= OnViewUnloaded;
        previous.Painter.Paint -= previous.OnPaint;
        previous.Painter.RemoveHandler(UIElement.MouseMoveEvent, new MouseEventHandler(OnPointerMoved));
        previous.Painter.MouseLeave -= OnPointerLeft;
        if (inspectionEnabled) PixelChanged?.Invoke(null);
        try
        {
            Invalidate(previous.View);
        }
        catch (Exception error)
        {
            StartupHook.Log(ErrorMessage(error));
        }
    }

    private void DiscardUnpaintedFrame()
    {
        if (firstPaint is not { } pending) return;
        firstPaint = null;
        var timing = pending.Timing;
        ViewerFrameTiming.LogUnpublished(timing.Stage, timing.SettingsGeneration, timing.Started, "Unpainted");
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(attachment?.View, sender))
            Suspend("Show the main Viewer to see the live preview.");
    }

    public void Dispose()
    {
        disposed = true;
        DiscardUnpaintedFrame();
        Detach();
        statusNotification?.Abort();
        statusNotification = null;
        StatusChanged = null;
        PixelChanged = null;
        clippingSource = clippingMask = null;
    }

    private void OnPointerMoved(object sender, MouseEventArgs args)
    {
        if (inspectionEnabled && attachment is { } current && ReferenceEquals(sender, current.Painter))
            PixelChanged?.Invoke(ReadPixel(args.GetPosition(current.Painter)));
    }

    private void OnPointerLeft(object sender, MouseEventArgs args)
    {
        if (inspectionEnabled && ReferenceEquals(sender, attachment?.Painter))
            PixelChanged?.Invoke(null);
    }

    internal ViewerPixel? ReadPixel(Point position)
    {
        if (UnavailableReason is not null || attachment is not { Geometry: { } geometry } current
            || !ReferenceEquals(current.PaintedBitmap, current.Bitmap)
            || current.Bitmap.Format != PixelFormats.Bgra32 || Availability(current) is not null)
            return null;
        var dpi = VisualTreeHelper.GetDpi(current.Painter);
        var physical = new Point(position.X * dpi.DpiScaleX, position.Y * dpi.DpiScaleY);
        var image = current.Bitmap;
        if (!geometry.TryMapPixel(physical, image.PixelWidth, image.PixelHeight, out var pixel))
            return null;
        image.CopyPixels(new Int32Rect(pixel.X, pixel.Y, 1, 1), sampleBytes, 4, 0);
        return sampleBytes[3] == 0 ? null : new ViewerPixel(image, pixel.X, pixel.Y, sampleBytes[2], sampleBytes[1], sampleBytes[0]);
    }

    private void Paint(Attachment current, PaintEventArgs args)
    {
        if (!ReferenceEquals(attachment, current))
            return;

        try
        {
            if (Availability(current) is { } reason)
            {
                SetStatus(reason);
                return;
            }

            var drawing = args.WPFDC;
            var model = current.View.ViewerModel;
            var clip = model.GetClipRectangle();
            var width = current.Bitmap.PixelWidth;
            var height = current.Bitmap.PixelHeight;
            // The native painter scales physical pixels into DIPs. Validate the
            // full-image corners TL, TR, BR, BL and use its pan/zoom/crop clip.
            if (drawing is null || !ViewerGeometry.TryCreate(clip, model.CanvasCoordinates, width, height, out var geometry))
            {
                SetStatus(current.PaintUnavailable = WaitingForGeometry);
                return;
            }

            if (current.Clip is not { } cachedClip || cachedClip.Rect != geometry.Clip)
            {
                cachedClip = new RectangleGeometry(geometry.Clip);
                cachedClip.Freeze();
                current.Clip = cachedClip;
            }
            if (current.Transform is not { } transform || transform.Matrix != geometry.Transform)
            {
                transform = new MatrixTransform(geometry.Transform);
                transform.Freeze();
                current.Transform = transform;
            }

            drawing.PushClip(cachedClip);
            try
            {
                drawing.PushTransform(transform);
                try
                {
                    var destination = new Rect(0, 0, width, height);
                    drawing.DrawImage(current.Bitmap, destination);
                    if (ReferenceEquals(current.Bitmap, clippingSource) && clippingMask is { } mask)
                        drawing.DrawImage(mask, destination);
                }
                finally
                {
                    drawing.Pop();
                }
            }
            finally
            {
                drawing.Pop();
            }

            if (!current.HasDrawn)
            {
                current.HasDrawn = true;
                StartupHook.Log($"Viewer preview painted on {current.Painter.GetType().FullName} at {width}x{height}.");
            }
            current.PaintUnavailable = null;
            current.ProjectedLongEdge = geometry.ProjectedLongEdge;
            current.Geometry = geometry;
            current.PaintedBitmap = current.Bitmap;
            if (firstPaint is { } pending && ReferenceEquals(pending.Bitmap, current.Bitmap))
            {
                firstPaint = null;
                pending.Timing.Log(width, height);
            }
            SetStatus(null);
        }
        catch (Exception error)
        {
            const string reason = "Capture One’s Viewer could not draw the live preview.";
            if (UnavailableReason != reason)
                StartupHook.Log(ErrorMessage(error));
            SetStatus(current.PaintUnavailable = reason);
        }
    }

    private static bool IsSelected(ICaptureOneDocument document, Guid variantId) =>
        document is ISelectedCollectionsProvider { PrimarySelectedCollection: ISimpleCollection selection }
        && selection.PrimaryVariant is ICoreVariant variant && variant.UUID == variantId;

    private static string? Availability(Attachment current)
    {
        var manager = HostDocumentAccess.Current();
        if (manager is null || !ReferenceEquals(current.Document, manager.ActiveDocument())
            || !ApprovedCatalog.IsApproved(manager.ActivePath(), current.Document.Path))
            return InactiveDocument;
        if (!IsSelected(current.Document, current.VariantId))
            return "The selected image changed.";
        if (ApplicationSettings.Default.IsProofingRecipeSettings)
            return RecipeProofing;
        var view = current.View;
        if (current.Item.BeforeAfterLabelsViewModel is not { HasBeforeView: false })
            return "Exit Before/After comparison to resume live preview.";
        if (!current.Grid.IsVisible || !view.IsVisible)
            return "Show the main Viewer to see the live preview.";
        if (!view.IsEnabled)
            return "The Viewer is temporarily unavailable.";
        if (view.IsMovie)
            return "Select a still image to use the live preview.";
        if (current.Painter.InSlideShowMode)
            return "Exit the slideshow to resume live preview.";
        if (view.BlackWhiteMode)
            return "Exit the Viewer’s black-and-white mode to resume live preview.";
        if (view.Variant?.UUID != current.VariantId || !ReferenceEquals(view.Document, current.Document)
            || !ReferenceEquals(current.ReadGridDocument(), current.Document))
            return "Waiting for the selected image to reach the Viewer.";
        var viewedVariants = current.ReadViewedVariants();
        var matchingViews = 0;
        for (var index = 0; index < viewedVariants.Count; index++)
        {
            var item = viewedVariants[index];
            if (item.Variant?.UUID == current.VariantId && ReferenceEquals(item.Document, current.Document))
                matchingViews++;
        }
        if (matchingViews != 1)
            return "Waiting for the selected image to reach a unique view in the main Viewer.";
        if (view.ViewerModel is not { IsDrawable: true } model)
            return "Waiting for the selected image to become drawable.";
        return model.ViewMode == ViewMode.Default ? null : "Exit the Viewer’s current viewing mode to resume live preview.";
    }

    private static List<(CompareCapableVariantGridItem Item, VariantViewWpf View)> FindVisibleItems(
        DependencyObject grid, ICaptureOneDocument document, Guid variant)
    {
        var matches = new List<(CompareCapableVariantGridItem, VariantViewWpf)>(1);
        Visit(grid);
        return matches;

        void Visit(DependencyObject element)
        {
            if (matches.Count > 1 || element is UIElement { IsVisible: false })
                return;
            if (element is CompareCapableVariantGridItem item)
            {
                if (Field(item, "Viewer") is VariantViewWpf { IsVisible: true } view
                    && view.Variant?.UUID == variant && ReferenceEquals(view.Document, document))
                    matches.Add((item, view));
                return;
            }
            var count = VisualTreeHelper.GetChildrenCount(element);
            for (var index = 0; index < count; index++)
                Visit(VisualTreeHelper.GetChild(element, index));
        }
    }

    private static string DescribeVisuals(DependencyObject root)
    {
        var names = new StringBuilder();
        var elements = new Queue<DependencyObject>();
        elements.Enqueue(root);
        while (elements.Count > 0 && names.Length < 300)
        {
            var element = elements.Dequeue();
            names.Append(element.GetType().Name).Append(' ');
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                elements.Enqueue(VisualTreeHelper.GetChild(element, i));
        }
        return names.ToString().TrimEnd();
    }

    private bool Unavailable(string reason)
    {
        Suspend(reason);
        return false;
    }

    private void SetStatus(string? reason)
    {
        if (UnavailableReason == reason || disposed)
            return;
        UnavailableReason = reason;
        if (reason is not null)
            StartupHook.Log($"Viewer preview unavailable: {reason}");
        if (StatusChanged is not null && statusNotification is null)
        {
            var dispatcher = attachment?.View.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
            if (dispatcher is null || dispatcher.HasShutdownStarted)
                return;
            statusNotification = dispatcher.InvokeAsync(() =>
            {
                statusNotification = null;
                StatusChanged?.Invoke();
            }, DispatcherPriority.Background);
        }
    }

    private static string ErrorMessage(Exception error) =>
        (error is TargetInvocationException { InnerException: { } inner } ? inner : error).Message;

    private static void Invalidate(FrameworkElement view) => Call(view, "InvalidateImage");

    private static object? Get(object instance, string name) =>
        Find(instance.GetType(), name, static (type, key) => type.GetProperty(key, Instance | BindingFlags.DeclaredOnly))
            .GetValue(instance);

    private static Func<T> BindGetter<T>(object instance, string name) =>
        Find(instance.GetType(), name, static (type, key) => type.GetProperty(key, Instance | BindingFlags.DeclaredOnly))
            .GetMethod!.CreateDelegate<Func<T>>(instance);

    private static object? Field(object instance, string name) =>
        Find(instance.GetType(), name, static (type, key) => type.GetField(key, Instance | BindingFlags.DeclaredOnly))
            .GetValue(instance);

    private static object? Call(object instance, string name) =>
        Find(instance.GetType(), name, static (type, key) => type.GetMethod(key, Instance | BindingFlags.DeclaredOnly,
            binder: null, Type.EmptyTypes, modifiers: null)).Invoke(instance, null);

    private static T Find<T>(Type type, string name, Func<Type, string, T?> find) where T : MemberInfo
    {
        for (Type? declaring = type; declaring is not null; declaring = declaring.BaseType)
        {
            var member = find(declaring, name);
            if (member is not null)
                return member;
        }
        throw new MissingMemberException(type.FullName, name);
    }

    private sealed record Attachment(
        ViewerPreview Owner, Guid VariantId, ICaptureOneDocument Document,
        FrameworkElement Grid, CompareCapableVariantGridItem Item, VariantViewWpf View,
        Func<ICaptureOneDocument?> ReadGridDocument, Func<IReadOnlyList<GridViewItemData>> ReadViewedVariants, WPFViewer Painter)
    {
        internal void OnPaint(PaintEventArgs args) => Owner.Paint(this, args);
        internal required BitmapSource Bitmap { get; set; }
        internal string? PaintUnavailable { get; set; }
        internal bool HasDrawn { get; set; }
        internal double ProjectedLongEdge { get; set; }
        internal RectangleGeometry? Clip { get; set; }
        internal MatrixTransform? Transform { get; set; }
        internal ViewerGeometry? Geometry { get; set; }
        internal BitmapSource? PaintedBitmap { get; set; }
    }
}

internal readonly record struct ViewerPixel(BitmapSource Frame, int X, int Y, byte Red, byte Green, byte Blue);

internal enum ViewerFrameStage { Settled, Initial, Drag }

internal readonly record struct ViewerFrameTiming(long Started, long Ready, int SettingsGeneration, string Quality, bool AutoQuality,
    int SourceBitDepth, string SourceFormat, string HostVersion, string HostAssemblySha256, string InputEncoding, string FilmEncoding,
    bool FilmApplied, double? NativeDevelopmentMs, double? FilmRenderAndTransportMs, double? CreateDisplayBitmapMs, OpticalRenderReceipt? Render,
    ViewerFrameStage Stage = ViewerFrameStage.Settled)
{
    internal static void LogUnpublished(ViewerFrameStage stage, int generation, long started, string outcome) =>
        StartupHook.Log("viewer.unpublished.v1", new
        {
            Stage = stage.ToString(), SettingsGeneration = generation, Outcome = outcome,
            DurationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        });

    internal void Log(int width, int height)
    {
        var painted = Stopwatch.GetTimestamp();
        StartupHook.Log("viewer.performance.v1", new
        {
            FrameId = Guid.NewGuid(), Stage = Stage.ToString(), SettingsGeneration, Quality, AutoQuality, SourceBitDepth, SourceFormat, HostVersion, HostAssemblySha256,
            InputEncoding, FilmEncoding, FilmApplied, Width = width, Height = height, NativeDevelopmentMs, FilmRenderAndTransportMs,
            CreateDisplayBitmapMs, ReadyToPaintMs = Stopwatch.GetElapsedTime(Ready, painted).TotalMilliseconds,
            RequestToPaintMs = Stopwatch.GetElapsedTime(Started, painted).TotalMilliseconds,
            Worker = Render is { } worker ? new { worker.ProcessId, worker.Request, worker.SourceUploaded,
                worker.PackedDisplay, worker.ParameterContract, worker.AssetContract } : null,
        });
    }
}
