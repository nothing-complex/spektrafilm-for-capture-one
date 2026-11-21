using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal readonly record struct PsdCropPath(double Left, double Top, double Right, double Bottom)
{
    internal bool IsValid => Left >= 0 && Top >= 0 && Right <= 1 && Bottom <= 1 && Right > Left && Bottom > Top;

    internal static PsdCropPath? FromCanvas(RectangleF bounds, SizeF canvas)
    {
        if (!float.IsFinite(canvas.Width) || !float.IsFinite(canvas.Height) || canvas.Width < 1 || canvas.Height < 1)
            return null;
        // The native crop has already been constrained. Allow the float
        // rectangle's final addition to round slightly past the canvas edge.
        var right = (double)bounds.Left + bounds.Width;
        var bottom = (double)bounds.Top + bounds.Height;
        const double edgeTolerance = 0.002;
        if (right - canvas.Width > edgeTolerance || bottom - canvas.Height > edgeTolerance)
            return null;
        var normalized = new PsdCropPath(bounds.Left / (double)canvas.Width, bounds.Top / (double)canvas.Height,
            Math.Min(1, right / canvas.Width), Math.Min(1, bottom / canvas.Height));
        return normalized.IsValid ? normalized : null;
    }
}

internal readonly record struct PsdGuide(bool Vertical, double Fraction)
{
    internal const int MaximumCount = 128;

    internal static PsdGuide[] InOutput(IReadOnlyList<PsdGuide> source, int canvasWidth, int canvasHeight, Rectangle crop)
    {
        var result = new List<PsdGuide>(source.Count);
        foreach (var guide in source)
        {
            var position = guide.Vertical ? guide.Fraction * canvasWidth - crop.Left : guide.Fraction * canvasHeight - crop.Top;
            var extent = guide.Vertical ? crop.Width : crop.Height;
            if (position >= 0 && position <= extent)
                result.Add(guide with { Fraction = position / extent });
        }
        return result.ToArray();
    }
}

internal readonly record struct PsdLayer(string Name, int Left, int Top, BitmapSource Image);

internal static class PsdExport
{
    private const int ResolutionResource = 1005;
    private const int IccResource = 1039;
    private const int GuidesResource = 1032;
    private const int XmpResource = 1060;
    private const int SavedPathResource = 2000;

    internal static void WriteVerified(Stream stream, BitmapSource image, ExportColorProfile profile, byte[]? xmp,
        CancellationToken stop, PsdCropPath? cropPath = null, IReadOnlyList<PsdGuide>? guides = null,
        IReadOnlyList<PsdLayer>? layers = null, bool largeDocument = false)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var maxDimension = largeDocument ? 300_000 : 30_000;
        if (width < 1 || width > maxDimension || height < 1 || height > maxDimension)
            throw new InvalidDataException($"Photoshop document dimensions must be between 1 and {maxDimension:N0} pixels.");

        using var profileStream = profile.Context.OpenProfileStream();
        if (profileStream.Length is < 128 or > 8 * 1024 * 1024)
            throw new InvalidDataException("The selected PSD colour profile has an invalid size.");
        var icc = new byte[checked((int)profileStream.Length)];
        profileStream.ReadExactly(icc);
        if (Convert.ToHexString(SHA256.HashData(icc)) != profile.Sha256)
            throw new InvalidDataException("The PSD export colour profile changed before encoding.");

        var layerHeader = LayerHeader(layers, width, height, largeDocument, out var layerLength, out var layerPadding);
        var header = Header(image, icc, xmp, cropPath, guides, layerLength, largeDocument);
        var expectedLength = header.LongLength + layerLength + 2 + (long)width * height * 6;
        ValidateContainerSize(expectedLength, largeDocument);
        stream.Write(header);
        stream.Write(layerHeader);
        if (layers is not null)
            foreach (var layer in layers)
                WritePlanarPixels(stream, layer.Image, stop, layerChannels: true);
        if (layerPadding != 0) WriteUInt16(stream, 0);
        WriteUInt16(stream, 0); // Uncompressed merged image.
        WritePlanarPixels(stream, image, stop, layerChannels: false);
        stop.ThrowIfCancellationRequested();
        if (stream.Length != expectedLength)
            throw new InvalidDataException("The PSD encoder did not preserve the expected 16-bit pixel count.");
        stream.Position = 0;
        VerifyBytes(stream, header);
        VerifyBytes(stream, layerHeader);
        stream.Position = header.LongLength + layerLength;
        VerifyBytes(stream, [0, 0]);
    }

    private static void ValidateContainerSize(long length, bool largeDocument)
    {
        if (!largeDocument && length > int.MaxValue)
            throw new InvalidDataException("The 16-bit export would exceed the PSD 2 GB limit. Choose 16-bit PSB or a smaller export size.");
    }

    private static byte[] Header(BitmapSource image, byte[] icc, byte[]? xmp, PsdCropPath? cropPath,
        IReadOnlyList<PsdGuide>? guides, long layerLength, bool largeDocument)
    {
        using var resources = new MemoryStream();
        using var resolution = new MemoryStream();
        WriteResolution(resolution, image.DpiX);
        WriteResolution(resolution, image.DpiY);
        WriteResource(resources, ResolutionResource, resolution.ToArray());
        if (guides is { Count: > 0 }) WriteResource(resources, GuidesResource, EncodeGuides(guides, image.PixelWidth, image.PixelHeight));
        WriteResource(resources, IccResource, icc);
        if (xmp is not null) WriteResource(resources, XmpResource, xmp);
        if (cropPath is { } path) WriteResource(resources, SavedPathResource, EncodeCropPath(path), "Crop"u8);

        using var header = new MemoryStream();
        header.Write("8BPS"u8);
        WriteUInt16(header, largeDocument ? (ushort)2 : (ushort)1);
        WriteUInt32(header, 0);
        WriteUInt16(header, 0); // Reserved
        WriteUInt16(header, 3); // RGB channels
        WriteUInt32(header, (uint)image.PixelHeight);
        WriteUInt32(header, (uint)image.PixelWidth);
        WriteUInt16(header, 16);
        WriteUInt16(header, 3); // RGB color mode
        WriteUInt32(header, 0); // RGB does not have a color mode data block.
        WriteUInt32(header, (uint)resources.Length);
        resources.WriteTo(header);
        WriteLayerLength(header, layerLength, largeDocument);
        return header.ToArray();
    }

    private static byte[] LayerHeader(IReadOnlyList<PsdLayer>? layers, int width, int height, bool largeDocument,
        out long length, out int padding)
    {
        length = 0;
        padding = 0;
        if (layers is not { Count: > 0 }) return [];
        if (layers.Count > short.MaxValue)
            throw new InvalidDataException("Too many layers for the PSD export.");

        using var records = new MemoryStream();
        WriteUInt16(records, (ushort)layers.Count);
        long pixelsLength = 0;
        foreach (var layer in layers)
        {
            var image = layer.Image;
            var channels = ChannelCount(image);
            if (layer.Left < 0 || layer.Top < 0
                || image.PixelWidth > width - layer.Left || image.PixelHeight > height - layer.Top
                || layer.Name.Length is < 1 or > 255)
                throw new InvalidDataException("A PSD layer has an invalid canvas position or name.");

            WriteUInt32(records, (uint)layer.Top);
            WriteUInt32(records, (uint)layer.Left);
            WriteUInt32(records, (uint)(layer.Top + image.PixelHeight));
            WriteUInt32(records, (uint)(layer.Left + image.PixelWidth));
            WriteUInt16(records, (ushort)channels);
            var channelLength = 2 + (long)image.PixelWidth * image.PixelHeight * 2;
            for (var channel = 0; channel < channels; channel++)
            {
                WriteUInt16(records, channel == 3 ? ushort.MaxValue : (ushort)channel); // -1 is transparency.
                WriteLayerLength(records, channelLength, largeDocument);
                pixelsLength += channelLength;
            }
            records.Write("8BIMnorm"u8);
            records.Write([255, 0, 8, 0]); // Full opacity, normal non-clipping pixel layer.
            var name = Encoding.ASCII.GetBytes(layer.Name);
            var nameLength = (1 + name.Length + 3) & ~3;
            WriteUInt32(records, (uint)(8 + nameLength));
            WriteUInt32(records, 0); // Layer mask.
            WriteUInt32(records, 0); // Blending ranges.
            records.WriteByte((byte)name.Length);
            records.Write(name);
            for (var index = 1 + name.Length; index < nameLength; index++)
                records.WriteByte(0);
        }

        var dataLength = records.Length + pixelsLength;
        padding = (int)((4 - dataLength % 4) % 4);
        var taggedHeaderLength = largeDocument ? 28 : 20;
        length = dataLength + padding + taggedHeaderLength;
        ValidateContainerSize(length, largeDocument);
        using var header = new MemoryStream();
        WriteLayerLength(header, 0, largeDocument); // The default layer block is empty at 16 bits.
        WriteUInt32(header, 0); // No global layer mask.
        // Both Photoshop versions accept 8BIM. PSB widens the length of
        // this particular tag independently of that signature.
        header.Write("8BIMLr16"u8);
        WriteLayerLength(header, dataLength, largeDocument);
        records.WriteTo(header);
        return header.ToArray();
    }

    private static void VerifyBytes(Stream stream, ReadOnlySpan<byte> expected)
    {
        var actual = new byte[expected.Length];
        stream.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("The PSD header or metadata changed before publication.");
    }

    private static int ChannelCount(BitmapSource image) => image.Format == PixelFormats.Rgb48 ? 3
        : image.Format == PixelFormats.Rgba64 ? 4
        : throw new InvalidDataException("PSD layers require RGB16 or RGBA16 samples.");

    private static void WritePlanarPixels(Stream stream, BitmapSource image, CancellationToken stop, bool layerChannels)
    {
        var width = image.PixelWidth;
        var channels = layerChannels ? ChannelCount(image) : 3;
        var stride = checked(width * channels * 2);
        var rowsPerBlock = Math.Min(image.PixelHeight, Math.Max(1, 256 * 1024 / stride));
        var interleaved = new ushort[rowsPerBlock * width * channels];
        var plane = new byte[rowsPerBlock * width * 2];
        for (var channel = 0; channel < channels; channel++)
        {
            if (layerChannels) WriteUInt16(stream, 0);
            for (var row = 0; row < image.PixelHeight; row += rowsPerBlock)
            {
                stop.ThrowIfCancellationRequested();
                var rows = Math.Min(rowsPerBlock, image.PixelHeight - row);
                image.CopyPixels(new Int32Rect(0, row, width, rows), interleaved, stride, 0);
                var samples = rows * width;
                for (var index = 0; index < samples; index++)
                    BinaryPrimitives.WriteUInt16BigEndian(plane.AsSpan(index * 2, 2), interleaved[index * channels + channel]);
                stream.Write(plane.AsSpan(0, samples * 2));
            }
        }
    }

    private static byte[] EncodeCropPath(PsdCropPath path)
    {
        if (!path.IsValid)
            throw new InvalidDataException("The PSD crop path is outside the delivered image.");
        const int recordSize = 26;
        var records = new byte[6 * recordSize];
        BinaryPrimitives.WriteUInt16BigEndian(records, 6); // Fill rule
        BinaryPrimitives.WriteUInt16BigEndian(records.AsSpan(recordSize + 2), 4); // Four closed knots
        WriteKnot(2, path.Left, path.Top);
        WriteKnot(3, path.Right, path.Top);
        WriteKnot(4, path.Right, path.Bottom);
        WriteKnot(5, path.Left, path.Bottom);
        return records;

        void WriteKnot(int index, double x, double y)
        {
            var knot = records.AsSpan(index * recordSize, recordSize);
            BinaryPrimitives.WriteUInt16BigEndian(knot, 2); // Closed, unlinked knot
            const int unit = 1 << 24;
            var vertical = checked((int)Math.Round(y * unit));
            var horizontal = checked((int)Math.Round(x * unit));
            // A straight segment has its two control points on the anchor.
            for (var offset = 2; offset < recordSize; offset += 8)
            {
                BinaryPrimitives.WriteInt32BigEndian(knot[offset..], vertical);
                BinaryPrimitives.WriteInt32BigEndian(knot[(offset + 4)..], horizontal);
            }
        }
    }

    private static byte[] EncodeGuides(IReadOnlyList<PsdGuide> guides, int width, int height)
    {
        if (guides.Count > PsdGuide.MaximumCount)
            throw new InvalidDataException("Too many guides for the PSD export.");
        using var resource = new MemoryStream();
        WriteUInt32(resource, 1); // Grid and guides version
        WriteUInt32(resource, 576); // Default horizontal and vertical grid cycle
        WriteUInt32(resource, 576);
        WriteUInt32(resource, (uint)guides.Count);
        foreach (var guide in guides)
        {
            if (!double.IsFinite(guide.Fraction) || guide.Fraction is < 0 or > 1)
                throw new InvalidDataException("A PSD guide is outside the delivered image.");
            var axis = guide.Vertical ? width : height;
            WriteUInt32(resource, checked((uint)Math.Round(guide.Fraction * axis * 32)));
            resource.WriteByte(guide.Vertical ? (byte)0 : (byte)1);
        }
        return resource.ToArray();
    }

    private static void WriteResource(Stream stream, ushort id, byte[] data, ReadOnlySpan<byte> name = default)
    {
        stream.Write("8BIM"u8);
        WriteUInt16(stream, id);
        stream.WriteByte(checked((byte)name.Length));
        stream.Write(name);
        if ((name.Length & 1) == 0) stream.WriteByte(0);
        WriteUInt32(stream, (uint)data.Length);
        stream.Write(data);
        if ((data.Length & 1) != 0) stream.WriteByte(0);
    }

    private static void WriteResolution(Stream stream, double dotsPerInch)
    {
        if (!double.IsFinite(dotsPerInch) || dotsPerInch <= 0 || dotsPerInch > 65535)
            throw new InvalidDataException("The PSD export resolution is outside its supported range.");
        WriteUInt32(stream, checked((uint)Math.Round(dotsPerInch * 65536)));
        WriteUInt16(stream, 1); // Pixels per inch
        WriteUInt16(stream, 1); // Display dimension in inches
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteLayerLength(Stream stream, long value, bool largeDocument)
    {
        if (!largeDocument)
        {
            ValidateContainerSize(value, largeDocument);
            WriteUInt32(stream, checked((uint)value));
            return;
        }
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
