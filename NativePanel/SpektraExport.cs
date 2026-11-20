using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class SpektraExport
{
    internal const string FileFilter = "16-bit TIFF image|*.tif|16-bit PNG image|*.png";

    internal static string ExtensionFor(string? format) => format?.ToUpperInvariant() switch
    {
        "PNG" => ".png",
        _ => ".tif",
    };

    internal static PixelFormat PixelFormatFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".tif" or ".tiff" => PixelFormats.Rgb48,
        ".png" => PixelFormats.Rgba64,
        _ => throw new InvalidOperationException("Choose 16-bit TIFF or PNG for the Spektrafilm file."),
    };

    internal static void Save(string path, BitmapSource image, CancellationToken stop, string? originalPath = null)
    {
        stop.ThrowIfCancellationRequested();
        SaveImage(path, image, stop, originalPath is null ? null : OutputPathGuard.Approve(path, originalPath));
    }

    internal static void SaveApproved(OutputPathGuard.ApprovedDestination destination, BitmapSource image, CancellationToken stop,
        ExportMetadata? metadata = null, ExportColorProfile? outputProfile = null) =>
        SaveImage(destination.Path, image, stop, destination, metadata, outputProfile);

    private static void SaveImage(string path, BitmapSource image, CancellationToken stop, OutputPathGuard.ApprovedDestination? destination,
        ExportMetadata? metadata = null, ExportColorProfile? outputProfile = null)
    {
        stop.ThrowIfCancellationRequested();
        var format = PixelFormatFor(path);
        if (image.Format != format)
            throw new InvalidDataException($"Spektrafilm export requires unpremultiplied {format} pixels.");

        var png = format == PixelFormats.Rgba64;
        BitmapEncoder encoder = png
            ? new PngBitmapEncoder()
            : new TiffBitmapEncoder { Compression = TiffCompressOption.Zip };
        outputProfile ??= ExportColorProfile.Srgb;
        var profile = new ReadOnlyCollection<ColorContext>(new[] { outputProfile.Context });
        var xmp = metadata is null ? null : Encoding.UTF8.GetBytes(metadata.CreateXmp(image.PixelWidth, image.PixelHeight));
        BitmapMetadata? tiff = null;
        if (!png)
        {
            tiff = new BitmapMetadata("tiff");
            tiff.SetQuery("/ifd/{ushort=274}", (ushort)1);
            if (xmp is not null)
                tiff.SetQuery("/ifd/{ushort=700}", xmp);
        }
        encoder.Frames.Add(BitmapFrame.Create(image, null, tiff, profile));
        void Encode(Stream stream)
        {
            stop.ThrowIfCancellationRequested();
            encoder.Save(stream);
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
}
