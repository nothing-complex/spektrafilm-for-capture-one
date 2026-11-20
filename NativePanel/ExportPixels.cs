using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

internal static class ExportPixels
{
    internal static ushort[] Encode(int sourceWidth, Rectangle crop, byte[] sourceBytes, byte[]? filmBytes,
        SrgbDelivery transform, bool hasAlpha, CancellationToken token)
    {
        var channels = hasAlpha ? 4 : 3;
        var pixels = GC.AllocateUninitializedArray<ushort>(checked(crop.Width * crop.Height * channels));
        PreviewRows.Process(crop.Width, crop.Height, token, (first, last) =>
        {
            var source = MemoryMarshal.Cast<byte, float>(sourceBytes.AsSpan());
            var film = MemoryMarshal.Cast<byte, float>(filmBytes.AsSpan());
            var destination = first * crop.Width * channels;
            for (var row = first; row < last; row++)
            {
                token.ThrowIfCancellationRequested();
                var i = ((crop.Top + row) * sourceWidth + crop.Left) * 4;
                for (var x = 0; x < crop.Width; x++, i += 4)
                {
                    var rgb = transform.GetSrgb(source, film, i);
                    pixels[destination++] = (ushort)Math.Round(rgb.Red * ushort.MaxValue);
                    pixels[destination++] = (ushort)Math.Round(rgb.Green * ushort.MaxValue);
                    pixels[destination++] = (ushort)Math.Round(rgb.Blue * ushort.MaxValue);
                    if (hasAlpha)
                        pixels[destination++] = (ushort)Math.Round(Math.Clamp((double)source[i + 3], 0, 1) * ushort.MaxValue);
                }
            }
        });
        return pixels;
    }
}
