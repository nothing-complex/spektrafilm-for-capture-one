using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed class ExportColorProfile
{
    private static readonly string StandardPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "spool", "drivers", "color", "sRGB Color Space Profile.icm");
    private readonly Profile source;
    private readonly Profile destination;

    private ExportColorProfile(string label, Profile source, Profile destination)
    {
        Label = label;
        this.source = source;
        this.destination = destination;
    }

    internal static ExportColorProfile Srgb => ForRecipe(null, null);
    internal string Label { get; }
    internal string Sha256 => destination.Sha256;
    internal string SrgbSha256 => source.Sha256;
    internal ColorContext Context => destination.Context;
    internal bool RequiresConversion => source.Sha256 != destination.Sha256;

    internal static ExportColorProfile ForRecipe(string? name, string? path)
    {
        var standard = Capture("sRGB", StandardPath);
        if (string.IsNullOrWhiteSpace(path))
            return new ExportColorProfile("sRGB", standard, standard);
        var label = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name;
        if (label.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) || label.EndsWith(".icm", StringComparison.OrdinalIgnoreCase))
            label = label[..^4];
        return new ExportColorProfile(label, standard, Capture(label, path));
    }

    internal void VerifyFiles()
    {
        source.VerifyFile();
        if (!ReferenceEquals(source, destination)) destination.VerifyFile();
    }

    internal void Prepare()
    {
        _ = source.Context;
        _ = destination.Context;
    }

    private static Profile Capture(string label, string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc)
            throw new InvalidDataException($"Choose a local RGB ICC export profile. The selected profile is {label}.");

        using var file = File.OpenRead(path);
        if (file.Length is < 128 or > 8 * 1024 * 1024)
            throw new InvalidDataException($"The selected ICC profile {label} has an invalid size.");
        Span<byte> header = stackalloc byte[128];
        file.ReadExactly(header);
        if (!header[16..20].SequenceEqual("RGB "u8) || !header[36..40].SequenceEqual("acsp"u8))
            throw new InvalidDataException($"Choose an RGB ICC export profile. The selected profile is {label}.");
        file.Position = 0;
        return new Profile(label, uri, Convert.ToHexString(SHA256.HashData(file)));
    }

    internal bool Matches(ColorContext context) => Matches(context, destination.Sha256);

    private static bool Matches(ColorContext context, string expected)
    {
        using var bytes = context.OpenProfileStream();
        return Convert.ToHexString(SHA256.HashData(bytes)) == expected;
    }

    internal BitmapSource ConvertSrgb(BitmapSource image, PixelFormat destination)
    {
        if (!RequiresConversion) return image;
        if (image.Format != PixelFormats.Rgba64 || (destination != PixelFormats.Rgb48 && destination != PixelFormats.Rgba64))
            throw new InvalidDataException("ICC conversion requires 16-bit RGB pixels.");
        // WIC implements this color transform on RGBA64. Strip alpha afterwards
        // for TIFF without converting any of the three 16-bit channel codes.
        var converted = new ColorConvertedBitmap(image, source.Context, Context, PixelFormats.Rgba64);
        BitmapSource output = destination == PixelFormats.Rgb48
            ? new FormatConvertedBitmap(converted, destination, null, 0)
            : converted;
        output.Freeze();
        return output;
    }

    private sealed class Profile(string label, Uri uri, string digest)
    {
        // Delay WIC until approval. It can hold the source file open as long as
        // the color context lives, including through the final encoder.
        private readonly Lazy<ColorContext> context = new(() =>
        {
            using var file = OpenVerified(label, uri, digest);
            var profile = new ColorContext(uri);
            if (!Matches(profile, digest)) throw Changed(label);
            return profile;
        });

        internal string Sha256 => digest;
        internal ColorContext Context => context.Value;

        internal void VerifyFile()
        {
            using var file = OpenVerified(label, uri, digest);
        }

        private static FileStream OpenVerified(string label, Uri uri, string digest)
        {
            FileStream file;
            try { file = File.OpenRead(uri.LocalPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw Changed(label, error); }
            try
            {
                if (file.Length is < 128 or > 8 * 1024 * 1024
                    || Convert.ToHexString(SHA256.HashData(file)) != digest)
                    throw Changed(label);
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private static InvalidDataException Changed(string label, Exception? error = null) =>
            new($"The export ICC profile {label} changed or became unavailable. Reopen Export to check the profile.", error);
    }
}
