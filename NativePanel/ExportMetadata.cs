using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

internal sealed record ExportMetadata(string? Copyright, string? Creator, string? Title, DateTime? CaptureDate,
    int? Rating, IReadOnlyList<string> Keywords)
{
    internal string CreateXmp(int width, int height)
    {
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XNamespace x = "adobe:ns:meta/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace xmp = "http://ns.adobe.com/xap/1.0/";
        XNamespace tiff = "http://ns.adobe.com/tiff/1.0/";
        XNamespace exif = "http://ns.adobe.com/exif/1.0/";
        var data = new XElement(rdf + "Description",
            new XAttribute(rdf + "about", ""),
            new XAttribute(XNamespace.Xmlns + "dc", dc),
            new XAttribute(XNamespace.Xmlns + "xmp", xmp),
            new XAttribute(XNamespace.Xmlns + "tiff", tiff),
            new XAttribute(XNamespace.Xmlns + "exif", exif),
            new XAttribute(tiff + "Orientation", 1),
            new XAttribute(tiff + "ImageWidth", width),
            new XAttribute(tiff + "ImageLength", height),
            new XAttribute(exif + "PixelXDimension", width),
            new XAttribute(exif + "PixelYDimension", height),
            Rating is >= 0 and <= 5 ? new XAttribute(xmp + "Rating", Rating.Value) : null,
            CaptureDate is { } date && date != DateTime.MinValue
                ? new XAttribute(exif + "DateTimeOriginal", date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))
                : null);
        AddText(dc + "rights", Copyright);
        AddText(dc + "title", Title);
        if (Clean(Creator) is { } creator)
            data.Add(new XElement(dc + "creator", new XElement(rdf + "Seq", new XElement(rdf + "li", creator))));
        var keywords = Keywords.Select(Clean).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (keywords.Length > 0)
            data.Add(new XElement(dc + "subject", new XElement(rdf + "Bag", keywords.Select(value => new XElement(rdf + "li", value)))));

        return new XDocument(
            new XProcessingInstruction("xpacket", "begin=\"\uFEFF\" id=\"W5M0MpCehiHzreSzNTczkc9d\""),
            new XElement(x + "xmpmeta", new XAttribute(XNamespace.Xmlns + "x", x), new XElement(rdf + "RDF", data)),
            new XProcessingInstruction("xpacket", "end=\"w\"")).ToString(SaveOptions.DisableFormatting);

        void AddText(XName field, string? value)
        {
            if (Clean(value) is { } text)
                data.Add(new XElement(field, new XElement(rdf + "Alt",
                    new XElement(rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), text))));
        }
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (!rune.IsBmp || XmlConvert.IsXmlChar((char)rune.Value))
                result.Append(rune);
        }
        return result.Length == 0 ? null : result.ToString();
    }
}

internal static class PngXmp
{
    private static ReadOnlySpan<byte> EndChunk => new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 };
    private static ReadOnlySpan<byte> TextPrefix => "iTXtXML:com.adobe.xmp\0\0\0\0\0"u8;

    // WIC accepts a TIFF XMP tag directly. PNG uses the standardized uncompressed iTXt
    // chunk. Insert it before IEND in the private temporary file without copying pixels.
    internal static void Insert(Stream encodedImage, ReadOnlySpan<byte> packet)
    {
        var end = encodedImage.Length - EndChunk.Length;
        if (end < 8)
            throw new InvalidDataException("The PNG encoder produced an incomplete image.");
        Span<byte> endChunk = stackalloc byte[12];
        encodedImage.Position = end;
        encodedImage.ReadExactly(endChunk);
        if (!endChunk.SequenceEqual(EndChunk))
            throw new InvalidDataException("The PNG encoder did not produce the expected IEND chunk.");

        Span<byte> word = stackalloc byte[4];
        encodedImage.Position = end;
        BinaryPrimitives.WriteInt32BigEndian(word, checked(TextPrefix.Length - 4 + packet.Length));
        encodedImage.Write(word);
        encodedImage.Write(TextPrefix);
        encodedImage.Write(packet);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~UpdateCrc(UpdateCrc(uint.MaxValue, TextPrefix), packet));
        encodedImage.Write(word);
        encodedImage.Write(endChunk);
    }

    internal static bool Contains(Stream image, ReadOnlySpan<byte> packet)
    {
        var start = image.Length - EndChunk.Length - 4 - packet.Length - TextPrefix.Length - 4;
        if (start < 8)
            return false;
        image.Position = start;
        Span<byte> word = stackalloc byte[4];
        image.ReadExactly(word);
        if (BinaryPrimitives.ReadUInt32BigEndian(word) != TextPrefix.Length - 4 + packet.Length
            || !EncodedXmp.Matches(image, TextPrefix) || !EncodedXmp.Matches(image, packet))
            return false;
        image.ReadExactly(word);
        return BinaryPrimitives.ReadUInt32BigEndian(word) == ~UpdateCrc(UpdateCrc(uint.MaxValue, TextPrefix), packet)
            && EncodedXmp.Matches(image, EndChunk);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
        }
        return crc;
    }
}

internal static class EncodedXmp
{
    internal static bool TiffContains(Stream image, ReadOnlySpan<byte> packet)
    {
        image.Position = 0;
        Span<byte> header = stackalloc byte[8];
        image.ReadExactly(header);
        var littleEndian = header[..2].SequenceEqual("II"u8);
        if ((!littleEndian && !header[..2].SequenceEqual("MM"u8)) || Read16(header[2..]) != 42)
            return false;
        var directory = Read32(header[4..]);
        if (directory < 8 || directory > image.Length - 2)
            return false;

        image.Position = directory;
        image.ReadExactly(header[..2]);
        var tags = Read16(header);
        if (image.Length - image.Position < tags * 12L)
            return false;
        Span<byte> tag = stackalloc byte[12];
        for (var index = 0; index < tags; index++)
        {
            image.ReadExactly(tag);
            if (Read16(tag) != 700)
                continue;
            if (Read16(tag[2..]) is not (1 or 7) || Read32(tag[4..]) != packet.Length)
                return false;
            var offset = Read32(tag[8..]);
            if (offset < 8 || offset > image.Length - packet.Length)
                return false;
            image.Position = offset;
            return Matches(image, packet);
        }
        return false;

        ushort Read16(ReadOnlySpan<byte> value) => littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(value)
            : BinaryPrimitives.ReadUInt16BigEndian(value);
        uint Read32(ReadOnlySpan<byte> value) => littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(value)
            : BinaryPrimitives.ReadUInt32BigEndian(value);
    }

    internal static bool Matches(Stream stream, ReadOnlySpan<byte> expected)
    {
        Span<byte> block = stackalloc byte[1024];
        while (!expected.IsEmpty)
        {
            var part = block[..Math.Min(block.Length, expected.Length)];
            stream.ReadExactly(part);
            if (!part.SequenceEqual(expected[..part.Length]))
                return false;
            expected = expected[part.Length..];
        }
        return true;
    }
}
