using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private readonly object window;
    private Attachment? attachment;
    private DispatcherOperation? statusNotification;
    private bool disposed;

    internal ViewerPreview(object window) => this.window = window;
    internal string? UnavailableReason { get; private set; }
    internal bool HasPainted => UnavailableReason is null && attachment is { HasDrawn: true };
    internal event Action? StatusChanged;

    internal double? ProjectedLongEdge(Guid variant) => attachment is { HasDrawn: true, PaintUnavailable: null } current
        && current.VariantId == variant ? current.ProjectedLongEdge : null;

    internal bool Show(Guid variantId, BitmapSource bitmap)
    {
        if (disposed)
            return false;

        try
        {
            var viewer = Get(window, "Viewer");
            if (viewer is null || Get(viewer, "VariantGridView") is not FrameworkElement { IsVisible: true } grid)
                return Unavailable("Show the main Viewer to see the live preview.");

            if (attachment is { } current && current.Grid == grid && current.VariantId == variantId
                && grid.IsAncestorOf(current.View))
            {
                var reason = Availability(current) ?? current.PaintUnavailable;
                SetStatus(reason);
                if (!ReferenceEquals(current.Bitmap, bitmap))
                {
                    current.Bitmap = bitmap;
                    Invalidate(current.View);
                }
                return UnavailableReason is null;
            }

            if (HostProfile.SelectedVariant(window).Id != variantId)
                return Unavailable("The selected image changed.");

            var item = FindVisibleItem(grid);
            var view = item is null ? null : Field(item, "Viewer") as VariantViewWpf;
            if (item is null || view is null)
            {
                const string reason = "Show one image in the main Viewer.";
                if (UnavailableReason != reason)
                    StartupHook.Log($"Viewer image control unavailable under {DescribeVisuals(grid)}");
                return Unavailable(reason);
            }

            var gridModel = Field(grid, "m_viewModel");
            if (gridModel is null || Get(gridModel, "ViewedVariants") is not IReadOnlyList<GridViewItemData> viewedVariants)
                return Unavailable("Capture One did not expose its Viewer image list.");

            var painter = view.WpfViewer;
            if (painter is null)
                return Unavailable("Capture One did not expose the selected image’s Viewer renderer.");

            Detach();
            var activeDocument = BindActiveDocument();
            var document = activeDocument();
            if (document is null || !IsSelected(document, variantId))
                return Unavailable("The selected image changed.");
            var attached = new Attachment(variantId, document, activeDocument, grid, item, view, viewedVariants, painter)
            {
                Bitmap = bitmap,
                PaintUnavailable = WaitingForGeometry,
            };
            attachment = attached;
            painter.Paint += Paint;
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
        Detach();
        SetStatus(reason);
    }

    internal void Clear()
    {
        Detach();
        SetStatus(null);
    }

    private void Detach()
    {
        var previous = attachment;
        attachment = null;
        if (previous is null)
            return;

        previous.View.Unloaded -= OnViewUnloaded;
        previous.Painter.Paint -= Paint;
        try
        {
            Invalidate(previous.View);
        }
        catch (Exception error)
        {
            StartupHook.Log(ErrorMessage(error));
        }
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(attachment?.View, sender))
            Suspend("Show the main Viewer to see the live preview.");
    }

    public void Dispose()
    {
        disposed = true;
        Detach();
        statusNotification?.Abort();
        statusNotification = null;
        StatusChanged = null;
    }

    private void Paint(PaintEventArgs args)
    {
        var current = attachment;
        if (current is null)
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
                    drawing.DrawImage(current.Bitmap, new Rect(0, 0, width, height));
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
                StartupHook.Log($"Viewer preview painted on {current.Painter.GetType().FullName} for {current.VariantId:D} at {width}x{height}.");
            }
            current.PaintUnavailable = null;
            current.ProjectedLongEdge = geometry.ProjectedLongEdge;
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

    private static Func<ICaptureOneDocument?> BindActiveDocument()
    {
        var program = typeof(P1.C1.Components.InspectorTools.InspectorToolWpf).Assembly
            .GetType("P1.C1.Program", throwOnError: true)!;
        var manager = program.GetProperty("DocumentManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Capture One’s document manager is unavailable.");
        var getter = manager.GetType().GetProperty("ActiveDocument", Instance)?.GetMethod
            ?? throw new MissingMemberException(manager.GetType().FullName, "ActiveDocument");
        return getter.CreateDelegate<Func<ICaptureOneDocument?>>(manager);
    }

    private static bool IsSelected(ICaptureOneDocument document, Guid variantId) =>
        document is ISelectedCollectionsProvider { PrimarySelectedCollection: ISimpleCollection selection }
        && selection.PrimaryVariant is ICoreVariant variant && variant.UUID == variantId;

    private static string? Availability(Attachment current)
    {
        var documentPath = current.Document.Path;
        if (!ReferenceEquals(current.Document, current.ActiveDocument()) || !ApprovedCatalog.IsApproved(documentPath, documentPath))
            return InactiveDocument;
        if (!IsSelected(current.Document, current.VariantId))
            return "The selected image changed.";
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
        if (current.ViewedVariants.Count != 1)
            return "Show one image in the main Viewer.";
        if (view.Variant?.UUID != current.VariantId || current.ViewedVariants[0].Variant?.UUID != current.VariantId)
            return "Waiting for the selected image to reach the Viewer.";
        if (view.ViewerModel is not { IsDrawable: true } model)
            return "Waiting for the selected image to become drawable.";
        return model.ViewMode == ViewMode.Default ? null : "Exit the Viewer’s current viewing mode to resume live preview.";
    }

    private static CompareCapableVariantGridItem? FindVisibleItem(DependencyObject element)
    {
        if (element is UIElement { IsVisible: false })
            return null;
        if (element is CompareCapableVariantGridItem item)
            return item;

        var count = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < count; i++)
        {
            var view = FindVisibleItem(VisualTreeHelper.GetChild(element, i));
            if (view is not null)
                return view;
        }
        return null;
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
        Guid VariantId, ICaptureOneDocument Document, Func<ICaptureOneDocument?> ActiveDocument,
        FrameworkElement Grid, CompareCapableVariantGridItem Item, VariantViewWpf View,
        IReadOnlyList<GridViewItemData> ViewedVariants, WPFViewer Painter)
    {
        internal required BitmapSource Bitmap { get; set; }
        internal string? PaintUnavailable { get; set; }
        internal bool HasDrawn { get; set; }
        internal double ProjectedLongEdge { get; set; }
        internal RectangleGeometry? Clip { get; set; }
        internal MatrixTransform? Transform { get; set; }
    }
}
