using System;
using System.Buffers.Binary;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
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

internal static class PsdExport
{
    private const int MaximumDimension = 30_000;
    private const int ResolutionResource = 1005;
    private const int IccResource = 1039;
    private const int XmpResource = 1060;
    private const int SavedPathResource = 2000;

    internal static void WriteVerified(Stream stream, BitmapSource image, ExportColorProfile profile, byte[]? xmp,
        CancellationToken stop, PsdCropPath? cropPath = null)
    {
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        if (width is < 1 or > MaximumDimension || height is < 1 or > MaximumDimension)
            throw new InvalidDataException("PSD dimensions must be between 1 and 30,000 pixels.");

        using var profileStream = profile.Context.OpenProfileStream();
        if (profileStream.Length is < 128 or > 8 * 1024 * 1024)
            throw new InvalidDataException("The selected PSD colour profile has an invalid size.");
        var icc = new byte[checked((int)profileStream.Length)];
        profileStream.ReadExactly(icc);
        if (Convert.ToHexString(SHA256.HashData(icc)) != profile.Sha256)
            throw new InvalidDataException("The PSD export colour profile changed before encoding.");

        var header = Header(image, icc, xmp, cropPath);
        stream.Write(header);
        WritePlanarPixels(stream, image, stop);
        stop.ThrowIfCancellationRequested();
        if (stream.Length != header.LongLength + (long)width * height * 6)
            throw new InvalidDataException("The PSD encoder did not preserve the expected 16-bit pixel count.");
        stream.Position = 0;
        var actual = new byte[header.Length];
        stream.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(header))
            throw new InvalidDataException("The PSD header or metadata changed before publication.");
    }

    private static byte[] Header(BitmapSource image, byte[] icc, byte[]? xmp, PsdCropPath? cropPath)
    {
        using var resources = new MemoryStream();
        using var resolution = new MemoryStream();
        WriteResolution(resolution, image.DpiX);
        WriteResolution(resolution, image.DpiY);
        WriteResource(resources, ResolutionResource, resolution.ToArray());
        WriteResource(resources, IccResource, icc);
        if (xmp is not null) WriteResource(resources, XmpResource, xmp);
        if (cropPath is { } path) WriteResource(resources, SavedPathResource, EncodeCropPath(path), "Crop"u8);

        using var header = new MemoryStream();
        header.Write("8BPS"u8);
        WriteUInt16(header, 1); // PSD version
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
        WriteUInt32(header, 0); // Flattened document, no layers or masks.
        WriteUInt16(header, 0); // Uncompressed planar samples.
        return header.ToArray();
    }

    private static void WritePlanarPixels(Stream stream, BitmapSource image, CancellationToken stop)
    {
        var width = image.PixelWidth;
        var stride = checked(width * 6);
        var rowsPerBlock = Math.Min(image.PixelHeight, Math.Max(1, 256 * 1024 / stride));
        var interleaved = new ushort[rowsPerBlock * width * 3];
        var plane = new byte[rowsPerBlock * width * 2];
        for (var channel = 0; channel < 3; channel++)
        {
            for (var row = 0; row < image.PixelHeight; row += rowsPerBlock)
            {
                stop.ThrowIfCancellationRequested();
                var rows = Math.Min(rowsPerBlock, image.PixelHeight - row);
                image.CopyPixels(new Int32Rect(0, row, width, rows), interleaved, stride, 0);
                var samples = rows * width;
                for (var index = 0; index < samples; index++)
                    BinaryPrimitives.WriteUInt16BigEndian(plane.AsSpan(index * 2, 2), interleaved[index * 3 + channel]);
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
}
