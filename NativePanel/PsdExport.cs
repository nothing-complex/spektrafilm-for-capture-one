using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

internal static class PsdExport
{
    private const int MaximumDimension = 30_000;
    private const int ResolutionResource = 1005;
    private const int IccResource = 1039;
    private const int XmpResource = 1060;

    internal static void WriteVerified(Stream stream, BitmapSource image, ExportColorProfile profile, byte[]? xmp,
        CancellationToken stop)
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

        var header = Header(image, icc, xmp);
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

    private static byte[] Header(BitmapSource image, byte[] icc, byte[]? xmp)
    {
        using var resources = new MemoryStream();
        using var resolution = new MemoryStream();
        WriteResolution(resolution, image.DpiX);
        WriteResolution(resolution, image.DpiY);
        WriteResource(resources, ResolutionResource, resolution.ToArray());
        WriteResource(resources, IccResource, icc);
        if (xmp is not null) WriteResource(resources, XmpResource, xmp);

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

    private static void WriteResource(Stream stream, ushort id, byte[] data)
    {
        stream.Write("8BIM"u8);
        WriteUInt16(stream, id);
        WriteUInt16(stream, 0); // Empty Pascal name and its even-byte padding.
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
