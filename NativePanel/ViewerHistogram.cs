using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed class ViewerHistogram
{
    internal const int Levels = 256;
    internal int[] Red { get; } = new int[Levels];
    internal int[] Green { get; } = new int[Levels];
    internal int[] Blue { get; } = new int[Levels];
    internal int PixelCount { get; private set; }
    internal int HighlightCount { get; private set; }
    internal BitmapSource? HighlightMask { get; private set; }

    internal static ViewerHistogram Read(BitmapSource image, CancellationToken stop, bool markHighlights = false)
    {
        if (image.Format != PixelFormats.Bgra32)
            throw new ArgumentException("The Viewer histogram expects a BGRA display frame.", nameof(image));
        var stride = checked(image.PixelWidth * 4);
        var row = GC.AllocateUninitializedArray<byte>(stride);
        var maskStride = checked((image.PixelWidth + 7) / 8);
        var mask = markHighlights ? new byte[checked(maskStride * image.PixelHeight)] : null;
        var result = new ViewerHistogram();
        for (var y = 0; y < image.PixelHeight; y++)
        {
            stop.ThrowIfCancellationRequested();
            image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, stride, 0);
            for (var pixel = 0; pixel < stride; pixel += 4)
            {
                if (row[pixel + 3] == 0) continue;
                var blue = row[pixel];
                var green = row[pixel + 1];
                var red = row[pixel + 2];
                result.Blue[blue]++;
                result.Green[green]++;
                result.Red[red]++;
                result.PixelCount++;
                if (red == 255 || green == 255 || blue == 255)
                {
                    result.HighlightCount++;
                    if (mask is not null)
                    {
                        var x = pixel / 4;
                        mask[y * maskStride + x / 8] |= (byte)(128 >> (x % 8));
                    }
                }
            }
        }
        if (mask is not null)
        {
            stop.ThrowIfCancellationRequested();
            var palette = new BitmapPalette(new[] { Colors.Transparent, Color.FromArgb(148, 255, 38, 81) });
            var overlay = BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
                PixelFormats.Indexed1, palette, mask, maskStride);
            overlay.Freeze();
            result.HighlightMask = overlay;
        }
        return result;
    }
}
