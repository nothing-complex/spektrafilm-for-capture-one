using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class ViewerDisplayBitmap
{
    internal static unsafe BitmapSource Convert(HostImageSource.Frame original, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, CancellationToken token)
    {
        var stride = checked(original.Width * 4);
        var size = checked(stride * original.Height);
        token.ThrowIfCancellationRequested();
        var transform = new SrgbDelivery(space, outputEncoding, strength);
        // WIC copies the rows when creating the immutable bitmap. Retire this
        // temporary storage at that boundary, including after a canceled conversion.
        var buffer = (nint)NativeMemory.Alloc((nuint)size);
        try
        {
            PreviewRows.Process(original.Width, original.Height, token, (first, last) =>
            {
                var pixels = new Span<byte>((void*)buffer, size);
                var source = MemoryMarshal.Cast<byte, float>(original.FloatBytes.AsSpan());
                var floats = MemoryMarshal.Cast<byte, float>(rendered.AsSpan());
                for (var y = first; y < last; y++)
                {
                    token.ThrowIfCancellationRequested();
                    var start = y * stride;
                    for (var i = start; i < start + stride; i += 4)
                    {
                        var rgb = transform.GetSrgb(source, floats, i);
                        pixels[i] = (byte)Math.Round(rgb.Blue * 255);
                        pixels[i + 1] = (byte)Math.Round(rgb.Green * 255);
                        pixels[i + 2] = (byte)Math.Round(rgb.Red * 255);
                        pixels[i + 3] = (byte)MathF.Round(255 * Math.Clamp(source[i + 3], 0, 1));
                    }
                }
            });
            token.ThrowIfCancellationRequested();
            var image = BitmapSource.Create(original.Width, original.Height, 96, 96, PixelFormats.Bgra32, null, buffer, size, stride);
            image.Freeze();
            return image;
        }
        finally { NativeMemory.Free((void*)buffer); }
    }
}
