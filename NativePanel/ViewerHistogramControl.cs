using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed class ViewerHistogramControl : IDisposable
{
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly HistogramGraph graph = new() { Height = 82, SnapsToDevicePixels = true };
    private BitmapSource? frame;
    private bool source;
    private CancellationTokenSource? cancellation;
    private Task scan = Task.CompletedTask;
    private bool disposed;

    internal ViewerHistogramControl()
    {
        var content = new StackPanel();
        AutomationProperties.SetName(graph, "sRGB Viewer red, green and blue histogram");
        content.Children.Add(graph);
        content.Children.Add(detail);
        Section = PanelPreferences.CreateSection("viewer-histogram", "Viewer histogram", content, 7);
        Section.Expanded += OnExpansion;
        Section.Collapsed += OnExpansion;
        Section.IsVisibleChanged += OnVisibility;
    }

    internal Expander Section { get; }
    internal ViewerHistogram? Histogram => graph.Bins;

    internal void SetFrame(BitmapSource? image, bool showSource = false)
    {
        if (disposed || ReferenceEquals(frame, image) && source == showSource)
            return;
        frame = image;
        source = showSource;
        Refresh();
    }

    private void OnExpansion(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(args.OriginalSource, Section)) Refresh();
    }

    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        cancellation?.Cancel();
        graph.Show(null);
        if (disposed || !Section.IsVisible || !Section.IsExpanded)
            return;
        if (frame is not { } image)
        {
            detail.Text = "Waiting for a settled Viewer frame.";
            return;
        }
        detail.Text = "Reading Viewer pixels…";
        var work = new CancellationTokenSource();
        cancellation = work;
        scan = ScanAsync(scan, image, source, work);
    }

    private async Task ScanAsync(Task previous, BitmapSource image, bool showSource, CancellationTokenSource work)
    {
        try
        {
            await previous;
            var bins = await Task.Run(() => ViewerHistogram.Read(image, work.Token), work.Token);
            work.Token.ThrowIfCancellationRequested();
            graph.Show(bins);
            var frameName = showSource ? "sRGB source" : "sRGB film preview";
            detail.Text = $"{frameName} · {bins.HighlightCount:N0} pixels with a channel at 255";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!work.IsCancellationRequested) detail.Text = "The current Viewer histogram is unavailable.";
            StartupHook.Log("Viewer histogram: " + error.GetBaseException().Message);
        }
        finally
        {
            if (ReferenceEquals(cancellation, work)) cancellation = null;
            work.Dispose();
        }
    }

    public void Dispose()
    {
        disposed = true;
        Section.Expanded -= OnExpansion;
        Section.Collapsed -= OnExpansion;
        Section.IsVisibleChanged -= OnVisibility;
        cancellation?.Cancel();
        frame = null;
        graph.Show(null);
    }

    private sealed class HistogramGraph : FrameworkElement
    {
        private static readonly Brush Background = Fill(255, 33, 33, 33);
        private static readonly Brush Red = Fill(132, 237, 91, 91);
        private static readonly Brush Green = Fill(132, 108, 202, 113);
        private static readonly Brush Blue = Fill(132, 99, 153, 251);
        internal ViewerHistogram? Bins { get; private set; }

        internal void Show(ViewerHistogram? value)
        {
            if (ReferenceEquals(Bins, value)) return;
            Bins = value;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawing)
        {
            var width = RenderSize.Width;
            var height = RenderSize.Height;
            drawing.DrawRectangle(Background, null, new Rect(RenderSize));
            if (Bins is not { PixelCount: > 0 } bins || width <= 0 || height <= 0)
                return;
            var maximum = Math.Max(bins.Red.Max(), Math.Max(bins.Green.Max(), bins.Blue.Max()));
            Draw(bins.Red, Red);
            Draw(bins.Green, Green);
            Draw(bins.Blue, Blue);

            void Draw(int[] counts, Brush brush)
            {
                var path = new StreamGeometry();
                using (var figure = path.Open())
                {
                    figure.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
                    for (var index = 0; index < counts.Length; index++)
                        figure.LineTo(new Point(width * index / (counts.Length - 1), height * (1 - counts[index] / (double)maximum)), true, false);
                    figure.LineTo(new Point(width, height), false, false);
                }
                path.Freeze();
                drawing.DrawGeometry(brush, null, path);
            }
        }

        private static Brush Fill(byte alpha, byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
            brush.Freeze();
            return brush;
        }
    }
}
