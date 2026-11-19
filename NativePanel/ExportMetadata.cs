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
    internal static void Insert(Stream encodedImage, string packet)
    {
        var end = encodedImage.Length - EndChunk.Length;
        if (end < 8)
            throw new InvalidDataException("The PNG encoder produced an incomplete image.");
        Span<byte> endChunk = stackalloc byte[12];
        encodedImage.Position = end;
        encodedImage.ReadExactly(endChunk);
        if (!endChunk.SequenceEqual(EndChunk))
            throw new InvalidDataException("The PNG encoder did not produce the expected IEND chunk.");

        var text = Encoding.UTF8.GetBytes(packet);
        Span<byte> word = stackalloc byte[4];
        encodedImage.Position = end;
        BinaryPrimitives.WriteInt32BigEndian(word, checked(TextPrefix.Length - 4 + text.Length));
        encodedImage.Write(word);
        encodedImage.Write(TextPrefix);
        encodedImage.Write(text);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~UpdateCrc(UpdateCrc(uint.MaxValue, TextPrefix), text));
        encodedImage.Write(word);
        encodedImage.Write(endChunk);
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
