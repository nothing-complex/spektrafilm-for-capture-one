using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static void TestPsdFromTiff(string path, string output)
    {
        using var input = File.OpenRead(path);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0];
        Check(frame.Format == PixelFormats.Rgb48 && frame.ColorContexts is { Count: 1 }, "the native film test TIFF is RGB16 with one ICC");
        using var color = frame.ColorContexts![0].OpenProfileStream();
        var profilePath = Path.ChangeExtension(output, ".icc");
        using (var local = new FileStream(profilePath, FileMode.CreateNew))
            color.CopyTo(local);
        var profile = ExportColorProfile.ForRecipe("Native test ICC", profilePath);
        SpektraExport.SaveApproved(OutputPathGuard.Approve(output, path), frame, CancellationToken.None, outputProfile: profile);
        using var exported = File.OpenRead(output);
        var decoded = PsdFixture.Read(exported);
        var expected = new ushort[frame.PixelWidth * frame.PixelHeight * 3];
        frame.CopyPixels(expected, frame.PixelWidth * 6, 0);
        Check(decoded.Width == frame.PixelWidth && decoded.Height == frame.PixelHeight
            && decoded.Samples.AsSpan().SequenceEqual(expected)
            && Convert.ToHexString(SHA256.HashData(decoded.Resources[1039])) == profile.Sha256,
            "all retained native film samples and the exact selected profile survive PSD delivery");
    }

    private static void TestPsdDelivery(string folder, bool writeReference = false)
    {
        var source = Path.Combine(folder, "psd-original.dng");
        var path = Path.Combine(folder, "rgb16-proof.psd");
        File.WriteAllBytes(source, "original test image"u8.ToArray());
        var profile = ExportColorProfile.ForRecipe("ProPhoto RGB",
            Path.Combine(AppContext.BaseDirectory, "Profiles", "ProPhoto RGB.icc"));
        var metadata = new ExportMetadata("Test copyright", "Test creator", "Proof for København", null, 5, ["film"]);

        // Cross a row-block boundary. Each channel includes adjacent low codes
        // and high codes that would identify an intermediate depth conversion.
        const int width = 257, height = 181;
        ushort[] probe = [0, 1, 2, 255, 256, 257, 32567, 32568, 65534, 65535];
        var samples = new ushort[width * height * 3];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = probe[(index * 7 + index / (width * 3)) % probe.Length];
        var image = BitmapSource.Create(width, height, 299.5, 304.25, PixelFormats.Rgb48, null, samples, width * 6);
        image.Freeze();
        var canvas = new SizeF(642.5f, 452.5f);
        var pathCrop = PsdCropPath.FromCanvas(RectangleF.FromLTRB(64.25f, 90.5f, 481.875f, 362f), canvas);
        Check(pathCrop is { IsValid: true }
            && PsdCropPath.FromCanvas(new RectangleF(-1, 4, 12, 12), canvas) is null
            && PsdCropPath.FromCanvas(new RectangleF(1, 4, 0, 12), canvas) is null
            && PsdCropPath.FromCanvas(new RectangleF(1, 4, 900, 12), canvas) is null,
            "the full-canvas crop uses normalized coordinates and refuses degenerate or outside geometry");
        PsdGuide[] canvasGuides = [new(true, .25), new(false, .5), new(true, .9), new(false, .1)];
        var guides = PsdGuide.InOutput(canvasGuides, 514, 362, new Rectangle(64, 60, width, height));
        ExportPublication? publication = null;
        SpektraExport.SaveApproved(OutputPathGuard.Approve(path, source), image, CancellationToken.None,
            metadata, profile, verified: receipt => publication = receipt, cropPath: pathCrop, guides: guides);

        var publishedBytes = File.ReadAllBytes(path);
        using (var file = File.OpenRead(path))
        {
            var decoded = PsdFixture.Read(file);
            Check(decoded.Version == 1 && decoded.Width == width && decoded.Height == height && decoded.Layers.Length == 0
                && decoded.Samples.AsSpan().SequenceEqual(samples),
                "PSD reopens with the exact RGB16 sample in every channel, across multiple row blocks");
            Check(decoded.DpiX == 299.5 && decoded.DpiY == 304.25, "PSD stores both original resolution axes");
            Check(Convert.ToHexString(SHA256.HashData(decoded.Resources[1039])) == profile.Sha256
                && decoded.Resources[1060].AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(metadata.CreateXmp(width, height))),
                "PSD preserves the exact selected ICC and UTF-8 XMP resource");
            (double X, double Y)[] expectedCorners = [(.10, .20), (.75, .20), (.75, .80), (.10, .80)];
            Check(!decoded.Resources.ContainsKey(2999)
                && decoded.ReadCropPath().Zip(expectedCorners).All(pair =>
                    Math.Abs(pair.First.X - pair.Second.X) * width < .001 && Math.Abs(pair.First.Y - pair.Second.Y) * height < .001),
                "the named edit path has all four expected canvas corners without forcing a clipping path");
            Check(decoded.ReadGuides().AsSpan().SequenceEqual(new (bool, double)[] { (true, 64.5), (false, 121) })
                && PsdGuide.InOutput(canvasGuides, 514, 362, new Rectangle(0, 0, 514, 362)).Length == canvasGuides.Length,
                "editable horizontal and vertical guides follow the actual crop and omit only off-canvas guides");
        }
        Check(publication is { } receipt && receipt.Length == publishedBytes.Length
            && receipt.Sha256 == Convert.ToHexString(SHA256.HashData(publishedBytes)),
            "PSD supports the same byte-exact publication receipt as other still formats");

        using var cancellation = new CancellationTokenSource();
        try
        {
            var writes = 0;
            SpektraExport.SaveApproved(OutputPathGuard.Approve(path, source), image, cancellation.Token, metadata, profile,
                reportWrite: _ => { if (++writes == 2) cancellation.Cancel(); }, cropPath: pathCrop, guides: guides);
            throw new InvalidOperationException("The canceled PSD writer published an incomplete image.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check(File.ReadAllBytes(path).SequenceEqual(publishedBytes) && File.ReadAllText(source) == "original test image"
            && !Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(),
            "canceling a partial PSD write keeps both the verified destination and the original");

        var largeFilm = new LayoutOnlyBitmap(10_000, PixelFormats.Rgb48);
        var largeLayer = new LayoutOnlyBitmap(10_000, PixelFormats.Rgba64);
        PsdLayer[] largeLayers = [new("Background", 0, 0, largeFilm), .. Enumerable.Range(1, 5)
            .Select(index => new PsdLayer($"Overlay {index}", 0, 0, largeLayer))];
        using var largeLayout = new MemoryStream();
        var canceled = new CancellationToken(canceled: true);
        try
        {
            PsdExport.WriteVerified(largeLayout, largeFilm, profile, null, canceled, layers: largeLayers);
            throw new InvalidOperationException("An oversized Photoshop document should require PSB.");
        }
        catch (InvalidDataException error)
        {
            Check(largeLayout.Length == 0 && error.Message.Contains("PSB", StringComparison.Ordinal),
                "oversized PSD is rejected before writing pixel data and offers PSB");
        }
        try
        {
            PsdExport.WriteVerified(largeLayout, largeFilm, profile, null, canceled, layers: largeLayers, largeDocument: true);
            throw new InvalidOperationException("The PSB probe should stop before copying virtual pixels.");
        }
        catch (OperationCanceledException) { }
        var prefix = largeLayout.ToArray();
        var layerOffset = 34 + checked((int)BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(30)));
        var layerLength = BinaryPrimitives.ReadInt64BigEndian(prefix.AsSpan(layerOffset));
        var taggedLength = BinaryPrimitives.ReadInt64BigEndian(prefix.AsSpan(layerOffset + 28));
        Check(prefix.Length < 10_000 && taggedLength > uint.MaxValue && layerLength == ((taggedLength + 3) & ~3L) + 28,
            "PSB writes layer and tag lengths above four GiB without allocating or storing the virtual pixels");
        var largePath = Path.ChangeExtension(path, ".psb");
        SpektraExport.SaveApproved(OutputPathGuard.Approve(largePath, source), image, default, metadata, profile);
        using (var flatPsb = File.OpenRead(largePath))
        {
            var decoded = PsdFixture.Read(flatPsb);
            Check(decoded.Version == 2 && decoded.Layers.Length == 0 && decoded.Samples.AsSpan().SequenceEqual(samples),
                "a flat RGB16 PSB stores its required eight-byte empty layer section and exact pixels");
        }

        TestPsdOverlays();
        if (writeReference)
            WriteReference("expected-rgb16-be.bin", samples);

        void TestPsdOverlays()
        {
            var output = Path.Combine(folder, "rgb16-layered.psd");
            var profilePath = Path.Combine(AppContext.BaseDirectory, "Profiles", "ProPhoto RGB.icc");
            var captureProfile = ExportColorProfile.Capture("Same ProPhoto RGB", profilePath);
            var watermark = new ExportOverlay(8, 24, 4, 2, .41314159f,
                [0, 0, 255, 255, 0, 0, 255, 255, 1, 2, 3, 255, 4, 5, 6, 0,
                 0, 0, 255, 255, 0, 0, 255, 255, 7, 8, 9, 128, 10, 11, 12, 13], "test", captureProfile);
            var crop = new Rectangle(10, 20, width, height);
            var asset = new byte[4 * 3 * 4];
            byte[] visible = [25, 10, 32, 131, 251, 190, 9, 193];
            visible.CopyTo(asset, 20);
            var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, asset, 16);
            var overlay = ExportOverlay.Capture(bitmap, 9, 24, .5f, "Test transparency overlay", profilePath)
                ?? throw new InvalidOperationException("The test transparency overlay should remain visible.");
            Check(overlay is { X: 10, Y: 25, Width: 2, Height: 1 } && overlay.Bgra.AsSpan().SequenceEqual(visible),
                "snapshot trimming preserves the exact visible overlay and its canvas origin");
            var annotation = new ExportOverlay(10, 25, 2, 1, 1, [31, 63, 29, 79, 141, 151, 214, 255],
                "annotation", captureProfile);
            List<PsdLayer> layers = [new("Background", 0, 0, image)];
            var composed = ExportOverlay.Composite(image, crop, [new("Watermark", watermark, watermark.Prepare(profile, default)),
                new("Overlay", overlay, overlay.Prepare(profile, default)),
                new("Annotations", annotation, annotation.Prepare(profile, default))], default, layers);
            SpektraExport.SaveApproved(OutputPathGuard.Approve(output, source), composed, CancellationToken.None,
                metadata, profile, cropPath: pathCrop, guides: guides, layers: layers);

            using var file = File.OpenRead(output);
            var decoded = PsdFixture.Read(file);
            Check(decoded.Layers is [var background, var mark, var transparency, var note] && background.Name == "Background"
                && background.Left == 0 && background.Top == 0 && background.Width == width && background.Height == height
                && background.Samples.AsSpan().SequenceEqual(samples)
                && mark.Name == "Watermark" && mark.Left == 0 && mark.Top == 4 && mark.Width == 2 && mark.Height == 2
                && transparency.Name == "Overlay" && transparency.Left == 0 && transparency.Top == 5
                && transparency.Width == 2 && transparency.Height == 1
                && note.Name == "Annotations" && note.Left == 0 && note.Top == 5 && note.Width == 2 && note.Height == 1,
                "the layer stack preserves the complete RGB16 film frame beneath the independent crop-relative overlays");
            ushort Alpha(int alpha) => (ushort)Math.Round(alpha * 257 * (double)watermark.Opacity);
            ushort[] expectedOverlay = [771, 514, 257, Alpha(255), 1542, 1285, 1028, 0,
                2313, 2056, 1799, Alpha(128), 3084, 2827, 2570, Alpha(13)];
            Check(decoded.Layers[1].Samples.AsSpan().SequenceEqual(expectedOverlay),
                "clipped RGBA16 layer stores straight RGB and the exact calculated alpha");
            ushort[] expectedTransparency = [8224, 2570, 6425, 16834, 2313, 48830, 64507, 24800];
            Check(decoded.Layers[2].Samples.AsSpan().SequenceEqual(expectedTransparency),
                "the transparency overlay keeps its own RGB16 samples and recipe-adjusted alpha");
            ushort[] expectedNote = [7453, 16191, 7967, 20303, 54998, 38807, 36237, 65535];
            Check(decoded.Layers[3].Samples.AsSpan().SequenceEqual(expectedNote),
                "the annotation layer preserves the exact RGBA16 samples");
            var expectedMerged = (ushort[])samples.Clone();
            BlendReference(expectedOverlay, 2, 4);
            BlendReference(expectedTransparency, 1, 5);
            BlendReference(expectedNote, 1, 5);
            Check(decoded.Samples.AsSpan().SequenceEqual(expectedMerged)
                && Convert.ToHexString(SHA256.HashData(decoded.Resources[1039])) == profile.Sha256,
                "the RGB16 composite matches the layer stack with the same embedded ICC");
            var psbPath = Path.ChangeExtension(output, ".psb");
            ExportPublication? psbReceipt = null;
            SpektraExport.SaveApproved(OutputPathGuard.Approve(psbPath, source), composed, default, metadata, profile,
                cropPath: pathCrop, guides: guides, layers: layers, verified: receipt => psbReceipt = receipt);
            using (var psbFile = File.OpenRead(psbPath))
            {
                var psb = PsdFixture.Read(psbFile);
                Check(psb.Version == 2 && psb.Samples.AsSpan().SequenceEqual(expectedMerged)
                    && psb.Layers.Length == decoded.Layers.Length
                    && psb.Layers.Zip(decoded.Layers).All(pair => pair.First with { Samples = pair.Second.Samples } == pair.Second
                        && pair.First.Samples.AsSpan().SequenceEqual(pair.Second.Samples)),
                    "the PSB layer and channel lengths are wide and every layer and merged sample matches");
                Check(psb.Resources.Count == decoded.Resources.Count && psb.Resources.All(pair => pair.Value.AsSpan().SequenceEqual(decoded.Resources[pair.Key]))
                    && psb.ReadCropPath().AsSpan().SequenceEqual(decoded.ReadCropPath())
                    && psb.ReadGuides().AsSpan().SequenceEqual(decoded.ReadGuides()),
                    "PSB keeps the same exact ICC, XMP, DPI, editable crop path and guide resources");
                psbFile.Position = 0;
                Check(psbReceipt is { } receipt && receipt.Length == psbFile.Length
                    && receipt.Sha256 == Convert.ToHexString(SHA256.HashData(psbFile)),
                    "the published PSB receipt verifies the complete large-document container");
            }
            foreach (var invisible in new[] { watermark with { X = 0 }, watermark with { Opacity = 0 },
                watermark with { Bgra = new byte[watermark.Bgra.Length] } })
            {
                var hidden = new List<PsdLayer>();
                Check(ReferenceEquals(ExportOverlay.Composite(image, crop, [new("Watermark", invisible,
                    invisible.Prepare(profile, default))], default, hidden), image) && hidden.Count == 0,
                    "an invisible watermark leaves the ordinary flat PSD path available");
            }
            if (!writeReference) return;
            WriteReference("expected-watermark-rgba16-be.bin", expectedOverlay);
            WriteReference("expected-overlay-rgba16-be.bin", expectedTransparency);
            WriteReference("expected-annotations-rgba16-be.bin", expectedNote);
            WriteReference("expected-merged-rgb16-be.bin", expectedMerged);

            void BlendReference(ushort[] rgba, int rows, int top)
            {
                for (var y = 0; y < rows; y++)
                for (var x = 0; x < 2; x++)
                {
                    var fg = (y * 2 + x) * 4;
                    var bg = ((y + top) * width + x) * 3;
                    var alpha = rgba[fg + 3] / 65535d;
                    for (var channel = 0; channel < 3; channel++)
                        expectedMerged[bg + channel] = (ushort)Math.Round(
                            (1 - alpha) * expectedMerged[bg + channel] + alpha * rgba[fg + channel]);
                }
            }
        }

        void WriteReference(string name, ushort[] pixels)
        {
            var reference = new byte[pixels.Length * 2];
            for (var index = 0; index < pixels.Length; index++)
                BinaryPrimitives.WriteUInt16BigEndian(reference.AsSpan(index * 2), pixels[index]);
            File.WriteAllBytes(Path.Combine(folder, name), reference);
        }
    }

    private sealed class LayoutOnlyBitmap(int edge, PixelFormat format) : BitmapSource
    {
        public override int PixelWidth => edge;
        public override int PixelHeight => edge;
        public override double DpiX => 300;
        public override double DpiY => 300;
        public override PixelFormat Format => format;
        protected override System.Windows.Freezable CreateInstanceCore() => new LayoutOnlyBitmap(edge, format);
    }
}
