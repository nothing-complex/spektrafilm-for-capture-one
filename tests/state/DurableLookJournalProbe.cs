using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

// A storage experiment for the audit. The installed panel still uses VariantLookStore.
internal sealed class DurableLookJournalProbe
{
    private const int HeaderBytes = 56;
    private const int DigestBytes = 32;
    private readonly string root;
    private readonly Dictionary<Guid, byte[]> latest = new();

    internal DurableLookJournalProbe(string root) => this.root = Directory.CreateDirectory(root).FullName;
    internal long Bytes => Directory.EnumerateFiles(root, "*.journal").Sum(path => new FileInfo(path).Length);

    internal VariantLook? ReadMirror(Guid variant) => latest.ContainsKey(variant)
        ? PanelFiles.ReadPreset<VariantLook>(PathFor(variant, ".json")) : null;

    internal byte[] Prepare(Guid variant, VariantLook look)
    {
        LookStateValidation.Validate(look);
        return Encode(variant, latest.GetValueOrDefault(variant), PanelFiles.EncodeCompactPreset(look));
    }

    internal void Commit(Guid variant, byte[] record)
    {
        var previous = latest.GetValueOrDefault(variant);
        var primary = PathFor(variant, ".json");
        var payload = record.AsSpan(HeaderBytes, record.Length - HeaderBytes - DigestBytes).ToArray();
        VerifyPrimary();
        Append(PathFor(variant, ".journal"), record);
        VerifyPrimary();
        if (previous is not null)
        {
            var backup = new byte[52 + previous.Length];
            "SFL1"u8.CopyTo(backup);
            variant.TryWriteBytes(backup.AsSpan(4, 16));
            SHA256.HashData(previous, backup.AsSpan(20, DigestBytes));
            previous.CopyTo(backup, 52);
            PublishMirror(PathFor(variant, ".previous.look"), backup);
        }
        PublishMirror(primary, payload);
        latest[variant] = payload;

        void VerifyPrimary()
        {
            if (previous is null ? File.Exists(primary) : !File.ReadAllBytes(primary).AsSpan().SequenceEqual(previous))
                throw new IOException("The prototype's compatibility copy changed.");
        }
    }

    internal VariantLook ReadVerified(Guid variant)
    {
        var replay = Replay(PathFor(variant, ".journal"), variant);
        if (replay.Incomplete || replay.Latest is not { } current
            || !File.ReadAllBytes(PathFor(variant, ".json")).AsSpan().SequenceEqual(current)
            || replay.Previous is not { } previous
            || !File.ReadAllBytes(PathFor(variant, ".previous.look")).AsSpan(52).SequenceEqual(previous))
            throw new InvalidDataException("The journal and its final two compatibility generations diverged.");
        return PanelFiles.DecodePreset<VariantLook>(current);
    }

    private string PathFor(Guid variant, string suffix) => Path.Combine(root, variant.ToString("N") + suffix);

    private static byte[] Encode(Guid variant, byte[]? previous, byte[] payload)
    {
        var record = new byte[HeaderBytes + payload.Length + DigestBytes];
        "SLJ1"u8.CopyTo(record);
        variant.TryWriteBytes(record.AsSpan(4, 16));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20, 4), payload.Length);
        if (previous is not null) SHA256.HashData(previous, record.AsSpan(24, DigestBytes));
        payload.CopyTo(record, HeaderBytes);
        SHA256.HashData(record.AsSpan(0, HeaderBytes + payload.Length), record.AsSpan(HeaderBytes + payload.Length));
        return record;
    }

    private static void Append(string path, ReadOnlySpan<byte> record)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(record);
        stream.Flush(flushToDisk: true);
    }

    private static (byte[]? Latest, byte[]? Previous, bool Incomplete) Replay(string path, Guid variant)
    {
        using var stream = File.OpenRead(path);
        byte[]? latest = null, previous = null;
        Span<byte> header = stackalloc byte[HeaderBytes];
        Span<byte> expectedPrevious = stackalloc byte[DigestBytes];
        expectedPrevious.Clear();
        Span<byte> digest = stackalloc byte[DigestBytes];
        Span<byte> actual = stackalloc byte[DigestBytes];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < HeaderBytes) return (latest, previous, true);
            stream.ReadExactly(header);
            var size = BinaryPrimitives.ReadInt32LittleEndian(header[20..24]);
            if (!header[..4].SequenceEqual("SLJ1"u8) || new Guid(header[4..20]) != variant
                || size is < 1 or > PanelFiles.MaxPresetBytes || !header[24..].SequenceEqual(expectedPrevious))
                throw new InvalidDataException("The journal record is invalid or out of order.");
            if (stream.Length - stream.Position < size + DigestBytes) return (latest, previous, true);
            var payload = new byte[size];
            stream.ReadExactly(payload);
            stream.ReadExactly(digest);
            hash.AppendData(header);
            hash.AppendData(payload);
            hash.GetHashAndReset(actual);
            if (!actual.SequenceEqual(digest)) throw new InvalidDataException("The journal record checksum failed.");
            previous = latest;
            latest = payload;
            SHA256.HashData(payload, expectedPrevious);
        }
        return (latest, previous, false);
    }

    private static void PublishMirror(string path, byte[] bytes)
    {
        // The fixture keeps the flushed journal as the durable copy. These mirrors
        // estimate the additional time for the existing file readers to see a save.
        var staging = path + ".mirror-stage";
        using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(bytes);
        File.Move(staging, path, overwrite: true);
    }

    internal static int Hold(string path, Guid variant, string stage, string marker)
    {
        var initial = Replay(path, variant).Latest ?? throw new InvalidDataException("The fixture needs a prior record.");
        var next = Encode(variant, initial, "second acknowledged payload"u8.ToArray());
        Append(path, stage switch
        {
            "partial" => next.AsSpan(0, HeaderBytes + 5),
            "flushed" => next,
            _ => throw new ArgumentException("Choose a journal interruption boundary.", nameof(stage)),
        });
        File.WriteAllText(marker, stage);
        Thread.Sleep(Timeout.Infinite);
        return 1;
    }

    internal static void CheckRecovery(string directory, Action<string, bool> check, Func<string[], Process> startChild)
    {
        var variant = Guid.NewGuid();
        var first = "prior acknowledged payload"u8.ToArray();
        foreach (var stage in new[] { "partial", "flushed" })
        {
            var path = Path.Combine(directory, stage + ".journal");
            Append(path, Encode(variant, null, first));
            var marker = path + ".ready";
            using var child = startChild(["--hold-look-journal", path, variant.ToString("N"), stage, marker]);
            try
            {
                check("journal child reached " + stage, SpinWait.SpinUntil(() => child.HasExited || File.Exists(marker),
                    TimeSpan.FromSeconds(10)) && !child.HasExited && File.Exists(marker));
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                check("journal child stopped", child.WaitForExit(10_000));
            }
            var replay = Replay(path, variant);
            check("journal replays expected bytes after " + stage, stage == "partial"
                ? replay.Incomplete && replay.Latest!.SequenceEqual(first) && replay.Previous is null
                : !replay.Incomplete && replay.Latest!.AsSpan().SequenceEqual("second acknowledged payload"u8)
                    && replay.Previous!.SequenceEqual(first));
        }

        var complete = Path.Combine(directory, "flushed.journal");
        var damaged = File.ReadAllBytes(complete);
        damaged[^1] ^= 1;
        File.WriteAllBytes(complete, damaged);
        var rejected = false;
        try { _ = Replay(complete, variant); }
        catch (InvalidDataException) { rejected = true; }
        check("a complete record with a bad checksum is rejected", rejected);
    }
}
