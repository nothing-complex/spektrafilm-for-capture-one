using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

// Independent reader for the flat, uncompressed RGB16 files produced by fixtures.
internal sealed record PsdFixture(int Width, int Height, ushort[] Samples, Dictionary<ushort, byte[]> Resources,
    Dictionary<ushort, string> ResourceNames)
{
    internal double DpiX => BinaryPrimitives.ReadUInt32BigEndian(Resources[1005]) / 65536d;
    internal double DpiY => BinaryPrimitives.ReadUInt32BigEndian(Resources[1005].AsSpan(8)) / 65536d;

    internal (bool Vertical, double Pixels)[] ReadGuides()
    {
        var data = Resources[1032];
        if (data.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(data) != 1)
            throw new InvalidDataException("Invalid fixture PSD guide resource.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
        if (data.Length != 16 + count * 5L)
            throw new InvalidDataException("Invalid fixture PSD guide count.");
        var guides = new (bool Vertical, double Pixels)[count];
        for (var index = 0; index < guides.Length; index++)
        {
            var offset = 16 + index * 5;
            var location = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)) / 32d;
            var direction = data[offset + 4];
            if (direction > 1) throw new InvalidDataException("Invalid fixture PSD guide orientation.");
            guides[index] = (direction == 0, location);
        }
        return guides;
    }

    internal (double X, double Y)[] ReadCropPath()
    {
        if (ResourceNames.GetValueOrDefault((ushort)2000) != "Crop" || !Resources.TryGetValue(2000, out var data)
            || data.Length != 156 || BinaryPrimitives.ReadUInt16BigEndian(data) != 6
            || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(26)) != 0
            || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(28)) != 4)
            throw new InvalidDataException("The fixture PSD does not have a named, closed crop path.");
        var points = new (double X, double Y)[4];
        for (var index = 0; index < points.Length; index++)
        {
            var record = data.AsSpan((index + 2) * 26, 26);
            if (BinaryPrimitives.ReadUInt16BigEndian(record) != 2
                || !record[2..10].SequenceEqual(record[10..18]) || !record[18..26].SequenceEqual(record[10..18]))
                throw new InvalidDataException("The fixture PSD crop has a curved or invalid corner.");
            var y = BinaryPrimitives.ReadInt32BigEndian(record[10..]) / 16777216d;
            var x = BinaryPrimitives.ReadInt32BigEndian(record[14..]) / 16777216d;
            points[index] = (x, y);
        }
        return points;
    }

    internal static PsdFixture Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        static void Require(bool condition, string section)
        {
            if (!condition) throw new InvalidDataException($"Invalid fixture PSD {section}.");
        }
        ushort Word() => BinaryPrimitives.ReverseEndianness(reader.ReadUInt16());
        uint Long() => BinaryPrimitives.ReverseEndianness(reader.ReadUInt32());
        byte[] ReadBytes(int count)
        {
            var buffer = new byte[count];
            stream.ReadExactly(buffer);
            return buffer;
        }

        Require(ReadBytes(4).AsSpan().SequenceEqual("8BPS"u8) && Word() == 1, "version");
        Require(Long() == 0 && Word() == 0 && Word() == 3, "RGB channel count");
        var height = checked((int)Long());
        var width = checked((int)Long());
        Require(width > 0 && height > 0 && (long)width * height <= 100_000_000, "dimensions");
        Require(Word() == 16 && Word() == 3 && Long() == 0, "16-bit RGB mode");
        var resourceSize = Long();
        var end = stream.Position + resourceSize;
        Require(resourceSize <= 16 * 1024 * 1024 && end <= stream.Length, "resource size");
        var resources = new Dictionary<ushort, byte[]>();
        var names = new Dictionary<ushort, string>();
        while (stream.Position < end)
        {
            Require(ReadBytes(4).AsSpan().SequenceEqual("8BIM"u8), "resource signature");
            var id = Word();
            var nameSize = reader.ReadByte();
            names.Add(id, System.Text.Encoding.ASCII.GetString(ReadBytes(nameSize)));
            if ((nameSize & 1) == 0) stream.Position++;
            var size = checked((int)Long());
            Require(stream.Position + size <= end, "resource extent");
            Require(resources.TryAdd(id, ReadBytes(size)), "duplicate resource");
            stream.Position += size & 1;
        }
        Require(stream.Position == end && Long() == 0 && Word() == 0, "flattened raw composite");
        var pixels = checked(width * height);
        Require(stream.Length - stream.Position == (long)pixels * 6, "sample count");
        var samples = new ushort[pixels * 3];
        var row = new byte[width * 2];
        for (var channel = 0; channel < 3; channel++)
        {
            for (var y = 0; y < height; y++)
            {
                stream.ReadExactly(row);
                for (var x = 0; x < width; x++)
                    samples[(y * width + x) * 3 + channel] = BinaryPrimitives.ReadUInt16BigEndian(row.AsSpan(x * 2));
            }
        }
        return new(width, height, samples, resources, names);
    }
}
