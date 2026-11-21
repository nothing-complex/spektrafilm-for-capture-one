using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

internal static partial class Program
{
    private static void TestExportWatermark()
    {
        var srgb = ExportColorProfile.Srgb;
        var profile = new ExportColorProfile.Profile("Same sRGB", new Uri("file:///watermark-test.icc"), srgb.Sha256);
        var watermark = new ExportWatermark(9, 20, 4, 1, .5f,
            new byte[] { 20, 40, 240, 255, 255, 128, 16, 255, 255, 255, 255, 0, 10, 20, 100, 128 }, "test", profile);
        var crop = new Rectangle(10, 20, 5, 1);
        var overlay = watermark.Prepare(srgb, default);
        Check(overlay.AsSpan(4, 8).SequenceEqual(new ushort[] { 4112, 32896, 65535, 65535, 65535, 65535, 65535, 0 }),
            "The native BGRA watermark separates straight alpha and expands each foreground code exactly");
        var rgb = new ushort[] { 101, 103, 107, 201, 203, 207, 301, 303, 307, 401, 403, 407, 501, 503, 507 };
        watermark.Composite(rgb, 3, crop, overlay, default);
        Check(rgb.AsSpan().SequenceEqual(new ushort[] { 2106, 16500, 32821, 201, 203, 207, 6676, 1517, 875, 401, 403, 407, 501, 503, 507 }),
            "A cropped RGB16 export blends the correct native pixels and preserves every untouched 16-bit code");
        var rgba = new ushort[] { 1, 2, 3, 0 };
        watermark.Composite(rgba, 4, new Rectangle(10, 20, 1, 1), overlay, default);
        Check(rgba.AsSpan().SequenceEqual(new ushort[] { 4112, 32896, 65535, 32768 }),
            "A semi-transparent watermark on a transparent RGBA64 source retains straight foreground RGB");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var canceled = false;
        try { watermark.Composite(rgb, 3, crop, overlay, stop.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "Canceling the native watermark stops before writing the next output row");
    }

    private static void TestParallelExportPixels()
    {
        const int sourceWidth = 2048, sourceHeight = 2032;
        var (source, film) = FillExportSamples(sourceWidth, sourceHeight);
        var crop = new Rectangle(31, 17, 2000, 2000);
        foreach (var space in Enum.GetValues<PreviewInputSpace>())
        foreach (var strength in new[] { 0.0, .5, 1 })
        foreach (var alpha in new[] { false, true })
        {
            var encoding = strength == .5 ? "linear_rec709" : "srgb";
            var transform = new SrgbDelivery(space, encoding, strength);
            var output = strength == 0 ? null : film;
            var expected = EncodeSequential(sourceWidth, crop, source, output, transform, alpha);
            var actual = ExportPixels.Encode(sourceWidth, crop, source, output, transform, alpha, default);
            Check(actual.AsSpan().SequenceEqual(expected),
                $"Parallel 16-bit export must match sequential rounding and crop for {space} at {strength} with alpha {alpha}");
        }
    }

    private static void BenchmarkExportPixels(string path)
    {
        const int width = 6000, height = 4000;
        var (source, film) = FillExportSamples(width, height);
        var crop = new Rectangle(0, 0, width, height);
        var transform = new SrgbDelivery(PreviewInputSpace.ProPhotoRgb, "linear_rec709", .5);
        var sequential = new List<double>();
        var parallel = new List<double>();
        var coldSequentialMs = 0d;
        var coldParallelMs = 0d;
        for (var index = 0; index < 6; index++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            var start = Stopwatch.GetTimestamp();
            var reference = EncodeSequential(width, crop, source, film, transform, hasAlpha: false);
            var sequentialMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp();
            var result = ExportPixels.Encode(width, crop, source, film, transform, false, default);
            var parallelMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Check(reference.AsSpan().SequenceEqual(result), "Full 24 MP RGB16 export conversion must match before and after");
            if (index == 0)
            {
                coldSequentialMs = sequentialMs;
                coldParallelMs = parallelMs;
            }
            else
            {
                sequential.Add(sequentialMs);
                parallel.Add(parallelMs);
            }
        }
        sequential.Sort();
        parallel.Sort();
        using var output = new FileStream(path, FileMode.CreateNew);
        JsonSerializer.Serialize(output, new
        {
            width, height, exactMatchingRgb16Codes = width * height * 3,
            coldSequentialMs, coldParallelMs,
            sequentialMedianMs = sequential[sequential.Count / 2],
            parallelMedianMs = parallel[parallel.Count / 2],
            sequential, parallel,
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static (byte[] Source, byte[] Film) FillExportSamples(int width, int height)
    {
        var source = new byte[checked(width * height * 4 * sizeof(float))];
        var film = new byte[source.Length];
        var native = MemoryMarshal.Cast<byte, float>(source.AsSpan());
        var rendered = MemoryMarshal.Cast<byte, float>(film.AsSpan());
        for (var i = 0; i < native.Length; i += 4)
        {
            native[i] = (i % 65536 - 32) / 65530f;
            native[i + 1] = (i % 12301) / 12299f;
            native[i + 2] = ((i % 65791) - 31) / 65700f;
            native[i + 3] = (i % 221) / 165f;
            rendered[i] = 1.1f - native[i];
            rendered[i + 1] = native[i + 2];
            rendered[i + 2] = native[i + 1];
            rendered[i + 3] = 1;
        }
        return (source, film);
    }

    private static ushort[] EncodeSequential(int width, Rectangle crop, byte[] sourceBytes, byte[]? filmBytes,
        SrgbDelivery transform, bool hasAlpha)
    {
        var source = MemoryMarshal.Cast<byte, float>(sourceBytes.AsSpan());
        var film = MemoryMarshal.Cast<byte, float>(filmBytes.AsSpan());
        var destination = new ushort[checked(crop.Width * crop.Height * (hasAlpha ? 4 : 3))];
        var output = 0;
        for (var y = crop.Top; y < crop.Bottom; y++)
        {
            var input = (y * width + crop.Left) * 4;
            for (var x = 0; x < crop.Width; x++, input += 4)
            {
                var rgb = transform.GetSrgb(source, film, input);
                destination[output++] = (ushort)Math.Round(rgb.Red * ushort.MaxValue);
                destination[output++] = (ushort)Math.Round(rgb.Green * ushort.MaxValue);
                destination[output++] = (ushort)Math.Round(rgb.Blue * ushort.MaxValue);
                if (hasAlpha)
                    destination[output++] = (ushort)Math.Round(Math.Clamp((double)source[input + 3], 0, 1) * ushort.MaxValue);
            }
        }
        return destination;
    }
}
