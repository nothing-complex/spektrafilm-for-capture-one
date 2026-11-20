using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static void MeasureWpfDisplayPatches(string profile, string report)
    {
        var patches = new byte[][]
        {
            [0, 0, 0], [4, 4, 4], [32, 32, 32], [128, 128, 128], [250, 250, 250], [255, 255, 255],
            [220, 50, 60], [28, 190, 40], [30, 60, 225], [220, 200, 30], [27, 209, 185], [196, 30, 212],
            [183, 142, 121], [240, 201, 154], [78, 128, 162], [102, 122, 64], [23, 60, 89], [246, 47, 122],
        };
        var width = patches.Length;
        var raw = patches.SelectMany(rgb => new byte[] { rgb[2], rgb[1], rgb[0], 255 }).ToArray();
        var bitmap = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, raw, width * 4);
        var rgba64 = new FormatConvertedBitmap(bitmap, PixelFormats.Rgba64, null, 0);
        var standard = new ColorContext(PixelFormats.Bgra32);
        var alternate = new ColorContext(new Uri(Path.GetFullPath(profile)));
        var tagged = BitmapFrame.Create(bitmap, null, null, new ReadOnlyCollection<ColorContext>(new[] { alternate }));
        var profileToSrgb = new ColorConvertedBitmap(rgba64, alternate, standard, PixelFormats.Rgba64);
        var explicitSrgb = new FormatConvertedBitmap(profileToSrgb, PixelFormats.Bgra32, null, 0);
        var untaggedDraw = Draw(bitmap);
        var taggedDraw = Draw(tagged);
        var convertedDraw = Draw(explicitSrgb);
        Check(untaggedDraw.SequenceEqual(raw), "opaque untagged sRGB patch codes survive an offscreen WPF draw");
        Check(!convertedDraw.SequenceEqual(raw), "the alternate profile has a measurable explicit effect on WPF patch codes");
        Check(tagged.ColorContexts is { Count: 1 }, "the tagged input carries the alternate ICC context");
        using var icc = tagged.ColorContexts![0].OpenProfileStream();
        var sha256 = Convert.ToHexString(SHA256.HashData(icc));
        Check(sha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profile))), "the fixture context contains the exact alternate ICC");
        File.WriteAllText(report, JsonSerializer.Serialize(new
        {
            AlternateIccSha256 = sha256,
            UntaggedMatchesInput = untaggedDraw.SequenceEqual(raw),
            TagOnlyMatchesInput = taggedDraw.SequenceEqual(raw),
            TagOnlyMatchesExplicit = taggedDraw.SequenceEqual(convertedDraw),
            Patches = patches.Select((input, index) => new
            {
                Input = input.Select(code => (int)code).ToArray(),
                UntaggedDraw = Rgb(untaggedDraw, index),
                TagOnlyDraw = Rgb(taggedDraw, index),
                ExplicitSrgbDraw = Rgb(convertedDraw, index),
            }),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {assertions} offscreen WPF patch checks.");

        byte[] Draw(BitmapSource image)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
                drawing.DrawImage(image, new Rect(0, 0, width, 1));
            var rendered = new RenderTargetBitmap(width, 1, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            var output = new byte[raw.Length];
            rendered.CopyPixels(output, width * 4, 0);
            return output;
        }

        static int[] Rgb(byte[] pixels, int index) => [pixels[index * 4 + 2], pixels[index * 4 + 1], pixels[index * 4]];
    }
}
