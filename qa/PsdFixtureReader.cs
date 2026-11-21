using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

// Independent reader for the flat, uncompressed RGB16 files produced by fixtures.
internal sealed record PsdFixture(int Width, int Height, ushort[] Samples, Dictionary<ushort, byte[]> Resources)
{
    internal double DpiX => BinaryPrimitives.ReadUInt32BigEndian(Resources[1005]) / 65536d;
    internal double DpiY => BinaryPrimitives.ReadUInt32BigEndian(Resources[1005].AsSpan(8)) / 65536d;

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
        while (stream.Position < end)
        {
            Require(ReadBytes(4).AsSpan().SequenceEqual("8BIM"u8), "resource signature");
            var id = Word();
            var name = reader.ReadByte() + 1;
            stream.Position += ((name + 1) & ~1) - 1;
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
        return new(width, height, samples, resources);
    }
}
