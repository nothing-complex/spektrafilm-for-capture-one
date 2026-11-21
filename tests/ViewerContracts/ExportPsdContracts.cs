using System;
using System.Buffers.Binary;
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
        ExportPublication? publication = null;
        SpektraExport.SaveApproved(OutputPathGuard.Approve(path, source), image, CancellationToken.None,
            metadata, profile, verified: receipt => publication = receipt);

        var publishedBytes = File.ReadAllBytes(path);
        using (var file = File.OpenRead(path))
        {
            var decoded = PsdFixture.Read(file);
            Check(decoded.Width == width && decoded.Height == height && decoded.Samples.AsSpan().SequenceEqual(samples),
                "PSD reopens with the exact RGB16 sample in every channel, across multiple row blocks");
            Check(decoded.DpiX == 299.5 && decoded.DpiY == 304.25, "PSD stores both original resolution axes");
            Check(Convert.ToHexString(SHA256.HashData(decoded.Resources[1039])) == profile.Sha256
                && decoded.Resources[1060].AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(metadata.CreateXmp(width, height))),
                "PSD preserves the exact selected ICC and UTF-8 XMP resource");
        }
        Check(publication is { } receipt && receipt.Length == publishedBytes.Length
            && receipt.Sha256 == Convert.ToHexString(SHA256.HashData(publishedBytes)),
            "PSD supports the same byte-exact publication receipt as other still formats");

        using var cancellation = new CancellationTokenSource();
        try
        {
            var writes = 0;
            SpektraExport.SaveApproved(OutputPathGuard.Approve(path, source), image, cancellation.Token, metadata, profile,
                reportWrite: _ => { if (++writes == 2) cancellation.Cancel(); });
            throw new InvalidOperationException("The canceled PSD writer published an incomplete image.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check(File.ReadAllBytes(path).SequenceEqual(publishedBytes) && File.ReadAllText(source) == "original test image"
            && !Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(),
            "canceling a partial PSD write keeps both the verified destination and the original");

        if (writeReference)
        {
            var reference = new byte[samples.Length * 2];
            for (var index = 0; index < samples.Length; index++)
                BinaryPrimitives.WriteUInt16BigEndian(reference.AsSpan(index * 2), samples[index]);
            File.WriteAllBytes(Path.Combine(folder, "expected-rgb16-be.bin"), reference);
        }
    }
}
