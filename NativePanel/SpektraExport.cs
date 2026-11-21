using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class SpektraExport
{
    private const string TiffFilter = "16-bit TIFF image|*.tif;*.tiff";
    private const string PngFilter = "16-bit PNG image|*.png";
    private const string PsdFilter = "16-bit Photoshop document|*.psd";
    internal const string FileFilter = TiffFilter + "|" + PngFilter + "|" + PsdFilter;

    internal enum Format { Tiff, Png, Psd }

    internal static string FilterFor(Format format) => format switch
    {
        Format.Png => PngFilter,
        Format.Psd => PsdFilter,
        _ => TiffFilter,
    };

    [Flags]
    internal enum RecipeOmission
    {
        None = 0,
        Watermark = 1,
        Sharpening = 2,
        TransparencyOverlay = 4,
        Guides = 8,
        Annotations = 16,
    }

    internal readonly record struct RecipeDelivery(string? Format, int TiffBits, string? CropMode,
        RecipeOmission Omissions = RecipeOmission.None, bool HasPsdCropPath = false, bool HasPsdGuides = false)
    {
        internal string Extension => Format switch
        {
            "Png" => ".png",
            "Psd" => ".psd",
            _ => ".tif",
        };
        internal bool CanSkipRecipeOutput => Format is "Png" || Format is ("Tiff" or "Psd") && TiffBits == 16;
        private bool CropPath => CropMode == "AsPath";
        private bool RequiresFormatChange => Format is not null && !CanSkipRecipeOutput;

        private string RequestedFormat => Format switch
        {
            "Tiff" when TiffBits is 8 or 16 => $"{TiffBits}-bit TIFF",
            "Tiff" => "TIFF with a different bit depth",
            "Png" => "PNG",
            "Jpeg" or "JpegQuickProof" => "JPEG",
            "Psd" when TiffBits is 8 or 16 => $"{TiffBits}-bit PSD",
            "Psd" => "PSD with a different bit depth",
            "Dng" => "DNG",
            _ => "a different file format",
        };

        internal string SaveTitle(int width, int height, string profile)
        {
            if (Format is null)
                return $"Save 16-bit Spektrafilm export ({profile})";
            var prefix = RequiresFormatChange ? $"Recipe requests {RequestedFormat} · " : "";
            var crop = CropPath ? HasPsdCropPath ? " · full image, editable crop path in PSD" : " · full image, crop path omitted" : "";
            var guides = HasPsdGuides ? " · editable guides in PSD" : "";
            var omissions = DescribeOmissions(Omissions);
            var effects = omissions is null ? "" : $" · omits {omissions}";
            return $"{prefix}Save 16-bit TIFF, PNG or PSD{crop}{guides}{effects} · {width} × {height} · {profile}";
        }

        internal string BatchTitle(int count, bool skipExisting)
        {
            var format = FormatFor(Extension).ToString().ToUpperInvariant();
            var title = $"Export {count} images as 16-bit {format}";
            if (RequiresFormatChange) title += $" · Recipe requests {RequestedFormat}";
            if (CropPath) title += HasPsdCropPath ? " · Full image with editable crop paths" : " · Full image, unavailable crop paths omitted";
            if (HasPsdGuides && Extension == ".psd") title += " · Editable guides";
            if (DescribeOmissions(OmissionsFor(FormatFor(Extension))) is { } omissions) title += $" · Omits {omissions}";
            if (skipExisting) title += " · Skip existing recipe files";
            return title;
        }

        internal string? SavedNote(SpektraExport.Format outputFormat, bool batch = false)
        {
            var changed = RequiresFormatChange || CanSkipRecipeOutput && Format != outputFormat.ToString();
            var notes = new List<string>(3);
            if (changed)
                notes.Add($"Recipe requested {RequestedFormat}. Saved 16-bit {outputFormat.ToString().ToUpperInvariant()}.");
            if (CropPath)
                notes.Add(CropPathNote(outputFormat, batch));
            if (DescribeOmissions(OmissionsFor(outputFormat)) is { } omissions)
                notes.Add($"The export omitted {omissions}.");
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }

        private string CropPathNote(SpektraExport.Format outputFormat, bool batch)
        {
            if (HasPsdCropPath && outputFormat == SpektraExport.Format.Psd)
                return batch ? "Saved the full images with their editable crop paths." : "Saved the full image with the editable crop path.";
            return batch ? "Saved the full images. One or more recipe crop paths were omitted."
                : "Saved the full image. The recipe’s crop path was omitted.";
        }

        private RecipeOmission OmissionsFor(SpektraExport.Format output) =>
            HasPsdGuides && output != SpektraExport.Format.Psd ? Omissions | RecipeOmission.Guides : Omissions;

        private static string? DescribeOmissions(RecipeOmission omissions)
        {
            if (omissions == RecipeOmission.None)
                return null;
            var names = new List<string>(4);
            if ((omissions & RecipeOmission.Watermark) != 0) names.Add("recipe watermark");
            if ((omissions & RecipeOmission.Sharpening) != 0) names.Add("recipe sharpening mode");
            if ((omissions & RecipeOmission.TransparencyOverlay) != 0) names.Add("recipe transparency overlay");
            if ((omissions & RecipeOmission.Guides) != 0) names.Add("recipe guides");
            if ((omissions & RecipeOmission.Annotations) != 0) names.Add("recipe annotations");
            return string.Join(", ", names);
        }
    }

    internal static string SuggestedOutputName(string? recipeName, string sourcePath, string extension,
        string? directory = null, bool addSuffix = false)
    {
        var name = ValidRecipeStem(recipeName) ?? Path.GetFileNameWithoutExtension(sourcePath) + "-Spektrafilm";
        var suggested = name + extension;
        if (!addSuffix || directory is null || !Path.IsPathFullyQualified(directory))
            return suggested;
        for (var suffix = 1; Occupied(Path.Combine(directory, suggested)); suffix++)
            suggested = name + " " + suffix.ToString(CultureInfo.InvariantCulture) + extension;
        return suggested;
    }

    internal static string? ValidRecipeStem(string? recipeName)
    {
        var name = recipeName?.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? null : name;
    }

    private static bool Occupied(string path) => File.Exists(path) || Directory.Exists(path);

    internal static (string? Suggested, string? Exact) ExistingRecipeLocation(string? root, string? subfolder, bool hasSubfolder)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) return default;
        try
        {
            var baseDirectory = Path.GetFullPath(root);
            if (!Directory.Exists(baseDirectory)) return default;
            if (!hasSubfolder) return (baseDirectory, baseDirectory);
            if (!string.IsNullOrWhiteSpace(subfolder) && !Path.IsPathRooted(subfolder))
            {
                var candidate = Path.GetFullPath(subfolder, baseDirectory);
                var relative = Path.GetRelativePath(baseDirectory, candidate);
                if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !Path.IsPathRooted(relative) && Directory.Exists(candidate))
                    return (candidate, candidate);
            }
            return (baseDirectory, null);
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        {
            return default;
        }
    }

    internal static Format FormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tif" or ".tiff" => Format.Tiff,
        ".png" => Format.Png,
        ".psd" => Format.Psd,
        _ => throw new InvalidOperationException("Choose 16-bit TIFF, PNG or PSD for the Spektrafilm file."),
    };

    internal static PixelFormat PixelFormatFor(Format format) => format == Format.Png ? PixelFormats.Rgba64 : PixelFormats.Rgb48;
    internal static PixelFormat PixelFormatFor(string path) => PixelFormatFor(FormatFor(path));

    internal static void Save(string path, BitmapSource image, CancellationToken stop, string? originalPath = null)
    {
        stop.ThrowIfCancellationRequested();
        SaveImage(path, image, stop, originalPath is null ? null : OutputPathGuard.Approve(path, originalPath));
    }

    internal static void SaveApproved(OutputPathGuard.ApprovedDestination destination, BitmapSource image, CancellationToken stop,
        ExportMetadata? metadata = null, ExportColorProfile? outputProfile = null, TiffCompressOption tiffCompression = TiffCompressOption.Zip,
        Action<int>? reportWrite = null, Action<ExportPublication>? verified = null, PsdCropPath? cropPath = null,
        IReadOnlyList<PsdGuide>? guides = null, IReadOnlyList<PsdLayer>? layers = null) =>
        SaveImage(destination.Path, image, stop, destination, metadata, outputProfile, tiffCompression, reportWrite, verified, cropPath, guides, layers);

    private static void SaveImage(string path, BitmapSource image, CancellationToken stop, OutputPathGuard.ApprovedDestination? destination,
        ExportMetadata? metadata = null, ExportColorProfile? outputProfile = null, TiffCompressOption tiffCompression = TiffCompressOption.Zip,
        Action<int>? reportWrite = null, Action<ExportPublication>? verified = null, PsdCropPath? cropPath = null,
        IReadOnlyList<PsdGuide>? guides = null, IReadOnlyList<PsdLayer>? layers = null)
    {
        stop.ThrowIfCancellationRequested();
        var output = FormatFor(path);
        var format = PixelFormatFor(output);
        if (image.Format != format)
            throw new InvalidDataException($"Spektrafilm export requires unpremultiplied {format} pixels.");

        outputProfile ??= ExportColorProfile.Srgb;
        var xmp = metadata is null ? null : Encoding.UTF8.GetBytes(metadata.CreateXmp(image.PixelWidth, image.PixelHeight));
        void Encode(Stream stream)
        {
            stop.ThrowIfCancellationRequested();
            var monitored = stop.CanBeCanceled || reportWrite is not null ? new EncoderStream(stream, stop, reportWrite) : stream;
            if (output == Format.Psd)
                PsdExport.WriteVerified(monitored, image, outputProfile, xmp, stop, cropPath, guides, layers);
            else
                EncodeWic(stream, monitored);
            if (verified is not null)
                verified(ExportPublication.Read(stream, stop));
        }

        void EncodeWic(Stream stream, Stream monitored)
        {
            var png = output == Format.Png;
            BitmapEncoder encoder = png
                ? new PngBitmapEncoder()
                : new TiffBitmapEncoder { Compression = tiffCompression };
            var profile = new ReadOnlyCollection<ColorContext>(new[] { outputProfile.Context });
            BitmapMetadata? tiff = null;
            if (!png)
            {
                tiff = new BitmapMetadata("tiff");
                tiff.SetQuery("/ifd/{ushort=274}", (ushort)1);
                if (xmp is not null)
                    tiff.SetQuery("/ifd/{ushort=700}", xmp);
            }
            encoder.Frames.Add(BitmapFrame.Create(image, null, tiff, profile));
            try
            {
                encoder.Save(monitored);
            }
            catch (Exception error) when (stop.IsCancellationRequested)
            {
                // WIC may translate the callback's cancellation into a codec error.
                throw new OperationCanceledException("Spektrafilm file encoding was canceled.", error, stop);
            }
            stop.ThrowIfCancellationRequested();
            if (png && xmp is not null)
                PngXmp.Insert(stream, xmp);
            stream.Position = 0;
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand).Frames[0];
            if (frame.Format != format || frame.PixelWidth != image.PixelWidth || frame.PixelHeight != image.PixelHeight)
                throw new InvalidDataException("The encoded image lost its pixel dimensions or 16-bit format.");
            if (frame.ColorContexts is not { Count: 1 } contexts || !outputProfile.Matches(contexts[0]))
                throw new InvalidDataException("The encoded image did not preserve the selected ICC profile.");
            var stored = frame.Metadata as BitmapMetadata;
            if (!png && stored?.GetQuery("/ifd/{ushort=274}") is not (ushort)1)
                throw new InvalidDataException("The TIFF encoder did not preserve the normalized orientation.");
            if (!png && !TiffCompressionMatches(stored?.GetQuery("/ifd/{ushort=259}"), tiffCompression))
                throw new InvalidDataException("The TIFF encoder did not preserve the selected compression.");
            // Windows codec builds expose the same XMP through different query paths.
            // Verify the stored UTF-8 packet and container encoding directly.
            if (xmp is not null && !(png ? PngXmp.Contains(stream, xmp) : EncodedXmp.TiffContains(stream, xmp)))
                throw new InvalidDataException("The encoded image did not preserve the export metadata.");
        }
        if (destination is null)
            PanelFiles.WriteAtomically(path, Encode, beforeCommit: stop.ThrowIfCancellationRequested);
        else
            destination.Write(Encode, stop);
    }

    private static bool TiffCompressionMatches(object? tag, TiffCompressOption expected) =>
        tag is ushort code && expected switch
        {
            TiffCompressOption.None => code == 1,
            TiffCompressOption.Lzw => code == 5,
            TiffCompressOption.Zip => code is 8 or 32946,
            _ => false,
        };

    private sealed class EncoderStream(Stream inner, CancellationToken stop, Action<int>? reportWrite) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
            stop.ThrowIfCancellationRequested();
            inner.Flush();
        }

        public override void SetLength(long value)
        {
            stop.ThrowIfCancellationRequested();
            inner.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            stop.ThrowIfCancellationRequested();
            inner.Write(buffer, offset, count);
            reportWrite?.Invoke(count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            stop.ThrowIfCancellationRequested();
            inner.Write(buffer);
            reportWrite?.Invoke(buffer.Length);
        }
    }
}
