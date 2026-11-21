using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

// Holds the current and preceding generation in a bounded, catalog-scoped record.
// The writer owns publication for one variant until its compatibility files have been updated.
internal sealed class VariantLookGenerationJournal(string directory, string catalogIdentity)
{
    private const int DigestBytes = 32;
    private const int HeaderBytes = 64;
    private const int MaxRecordBytes = 2 * (HeaderBytes + PanelFiles.MaxPresetBytes + DigestBytes);
    private readonly byte[] catalog = ParseCatalog(catalogIdentity);

    internal sealed record Snapshot(long Generation, byte[] Current, byte[]? Previous, string Receipt);

    internal Snapshot? Read(Guid variant) => ReadFile(variant) is { } bytes ? Decode(variant, bytes) : null;

    internal byte[]? ReadPrevious(Guid variant)
    {
        var bytes = ReadFile(variant);
        return bytes is not null && bytes.AsSpan(0, 4).SequenceEqual("SLGP"u8)
            ? DecodeCapsule(variant, bytes, "SLGP"u8).Payload : null;
    }

    private byte[]? ReadFile(Guid variant)
    {
        try
        {
            using var file = File.OpenRead(PathFor(variant));
            if (file.Length is <= HeaderBytes + DigestBytes or > MaxRecordBytes)
                throw new InvalidDataException("The saved generation record has an invalid length.");
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private string? ReadReceipt(Guid variant)
    {
        try
        {
            using var file = File.OpenRead(PathFor(variant));
            if (file.Length is <= HeaderBytes + DigestBytes or > MaxRecordBytes)
                throw new InvalidDataException("The saved generation record has an invalid length.");
            return Convert.ToHexString(SHA256.HashData(file));
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    internal Writer OpenWriter(Guid variant)
    {
        if (variant == Guid.Empty) throw new ArgumentException("A saved generation needs a variant identity.", nameof(variant));
        Directory.CreateDirectory(directory);
        var path = PathFor(variant);
        // Leave the lock file in place so separate processes always open the
        // same lock. Process exit releases the exclusive handle.
        return new(this, variant, path, new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    internal string PathFor(Guid variant) => Path.Combine(directory, variant.ToString("N") + ".generations");

    private byte[] Encode(Guid variant, long generation, byte[] current, byte[]? previous)
    {
        var priorLength = previous?.Length ?? 0;
        if (current.Length is < 1 or > PanelFiles.MaxPresetBytes
            || previous is { Length: < 1 or > PanelFiles.MaxPresetBytes })
            throw new InvalidDataException("A look generation has an invalid payload size.");
        var currentOffset = previous is null ? 0 : HeaderBytes + priorLength + DigestBytes;
        var buffer = new byte[currentOffset + HeaderBytes + current.Length + DigestBytes];
        // Each generation is separately identifiable and checksummed. A damaged
        // or truncated current capsule must not hide a verified predecessor.
        if (previous is not null) EncodeCapsule(buffer.AsSpan(0, currentOffset), "SLGP"u8, variant, generation - 1, previous);
        EncodeCapsule(buffer.AsSpan(currentOffset), "SLGC"u8, variant, generation, current);
        return buffer;
    }

    private void EncodeCapsule(Span<byte> target, ReadOnlySpan<byte> version, Guid variant, long generation, byte[] payload)
    {
        version.CopyTo(target);
        catalog.CopyTo(target[4..36]);
        variant.TryWriteBytes(target[36..52]);
        BinaryPrimitives.WriteInt64LittleEndian(target[52..60], generation);
        BinaryPrimitives.WriteInt32LittleEndian(target[60..64], payload.Length);
        payload.CopyTo(target[HeaderBytes..]);
        SHA256.HashData(target[..^DigestBytes], target[^DigestBytes..]);
    }

    private Snapshot Decode(Guid variant, ReadOnlySpan<byte> bytes)
    {
        Capsule? previous = bytes[..4].SequenceEqual("SLGP"u8) ? DecodeCapsule(variant, bytes, "SLGP"u8) : null;
        var remainder = bytes[(previous?.TotalBytes ?? 0)..];
        var current = DecodeCapsule(variant, remainder, "SLGC"u8);
        if (current.TotalBytes != remainder.Length || current.Generation != (previous?.Generation ?? 0) + 1)
            throw new InvalidDataException("The saved generation pair is out of order or retains unexpected records.");
        return new(current.Generation, current.Payload, previous?.Payload, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private Capsule DecodeCapsule(Guid variant, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> version)
    {
        if (bytes.Length <= HeaderBytes + DigestBytes || !bytes[..4].SequenceEqual(version)
            || !bytes[4..36].SequenceEqual(catalog) || new Guid(bytes[36..52]) != variant)
            throw new InvalidDataException("The saved generation record has an unknown version or belongs to a different image or catalog.");
        var generation = BinaryPrimitives.ReadInt64LittleEndian(bytes[52..60]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..64]);
        if (generation < 0 || length is < 1 or > PanelFiles.MaxPresetBytes || bytes.Length < HeaderBytes + length + DigestBytes)
            throw new InvalidDataException("The saved generation record has invalid fields.");
        var end = HeaderBytes + length;
        Span<byte> digest = stackalloc byte[DigestBytes];
        SHA256.HashData(bytes[..end], digest);
        if (!digest.SequenceEqual(bytes.Slice(end, DigestBytes)))
            throw new InvalidDataException("The saved generation record failed its checksum.");
        return new(generation, bytes.Slice(HeaderBytes, length).ToArray(), end + DigestBytes);
    }

    private sealed record Capsule(long Generation, byte[] Payload, int TotalBytes);

    private static byte[] ParseCatalog(string identity)
    {
        if (identity.Length != 64) throw new ArgumentException("A saved generation needs a SHA-256 catalog identity.", nameof(identity));
        return Convert.FromHexString(identity);
    }

    internal sealed class Writer(VariantLookGenerationJournal journal, Guid variant, string path, FileStream ownership) : IDisposable
    {
        private readonly object sync = new();
        private bool disposed;
        private bool observed;
        private Snapshot? snapshot;

        internal Snapshot? Read()
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                // The winner holds the writer lease. Keep a private decoded copy
                // used only for this write, and verify the exact file at publish.
                snapshot = journal.Read(variant);
                observed = true;
                return snapshot is null ? null : Clone(snapshot);
            }
        }

        internal Snapshot Commit(string? expectedReceipt, byte[] current, byte[]? legacyPrevious = null, Action? verifySource = null)
        {
            lock (sync) return CommitLocked(expectedReceipt, current, legacyPrevious, verifySource);
        }

        private Snapshot CommitLocked(string? expectedReceipt, byte[] current, byte[]? legacyPrevious, Action? verifySource)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var prior = observed ? snapshot : journal.Read(variant);
            VerifyReceipt(prior?.Receipt);
            if (prior is not null && legacyPrevious is not null)
                throw new InvalidOperationException("The legacy look belongs only to the first generation.");
            var record = journal.Encode(variant, checked((prior?.Generation ?? 0) + 1), current, prior?.Current ?? legacyPrevious);
            var committed = journal.Decode(variant, record);
            PanelFiles.WriteAtomically(path, stream => stream.Write(record), () =>
            {
                verifySource?.Invoke();
                VerifyReceipt(journal.ReadReceipt(variant));
            }, overwrite: prior is not null, discardOnProcessExit: true);
            snapshot = committed;
            observed = true;
            return Clone(committed);

            void VerifyReceipt(string? value)
            {
                if (!StringComparer.Ordinal.Equals(value, expectedReceipt))
                    throw new InvalidDataException("The saved generation changed before publication.");
            }
        }

        private static Snapshot Clone(Snapshot value) => value with
        {
            Current = (byte[])value.Current.Clone(),
            Previous = value.Previous is { } previous ? (byte[])previous.Clone() : null,
        };

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                ownership.Dispose();
            }
        }
    }
}
