using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum BatchResult { Interrupted, Completed, Stopped, Failed }

internal sealed record BatchOutcome(BatchResult Result, int Selected, Guid[] Applied, Guid? Uncertain);

internal sealed class VariantLookBatchJournal(string directory, Func<Guid, string?> savedHash)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaxJournalBytes = 512 * 1024;

    internal Writer Start(int selected)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(selected, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(selected, VariantLookBatch.MaxVariants);
        Directory.CreateDirectory(directory);
        var lease = AcquireLease();
        try
        {
            var id = Guid.NewGuid();
            var path = JournalPath(id);
            // Publish a complete header before the job may change any variant.
            PanelFiles.WriteAtomically(path, stream => WriteLine(stream, new(Kind.Header, Version: 1, Selected: selected)),
                overwrite: false);
            PanelFiles.WriteCompactPreset(LatestPath, id);
            return new Writer(lease, new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None));
        }
        catch { lease.Dispose(); throw; }
    }

    internal BatchOutcome? ReadLatest()
    {
        if (!Directory.Exists(directory))
            return null;
        using var lease = AcquireLease();
        Guid id;
        try { id = PanelFiles.ReadPreset<Guid>(LatestPath); }
        catch (FileNotFoundException) { return null; }
        if (id == Guid.Empty)
            throw InvalidJournal();
        using var stream = new FileStream(JournalPath(id), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaxJournalBytes)
            throw InvalidJournal();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var entries = bytes.AsSpan(0, bytes.AsSpan().LastIndexOf((byte)'\n') + 1);
        var lines = Utf8.GetString(entries).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            throw InvalidJournal();
        var header = Parse(lines[0]);
        if (header.Type != Kind.Header || header.Version != 1 || header.Selected is < 2 or > VariantLookBatch.MaxVariants)
            throw InvalidJournal();

        var applied = new List<Guid>();
        var seen = new HashSet<Guid>();
        var result = BatchResult.Interrupted;
        Entry? pending = null;
        foreach (var line in lines.Skip(1))
        {
            if (result != BatchResult.Interrupted)
                throw InvalidJournal();
            var record = Parse(line);
            switch (record.Type)
            {
                case Kind.Prepare when pending is null && record.Variant != Guid.Empty
                    && IsDigest(record.After) && (record.Before is null || IsDigest(record.Before))
                    && record.Before != record.After && seen.Add(record.Variant) && seen.Count <= header.Selected:
                    pending = record;
                    break;
                case Kind.Commit when pending is not null && record.Variant == pending.Variant:
                    applied.Add(record.Variant);
                    pending = null;
                    break;
                case Kind.Finish when record.Result is BatchResult.Stopped or BatchResult.Failed
                    || record.Result == BatchResult.Completed && pending is null:
                    result = record.Result;
                    break;
                default:
                    throw InvalidJournal();
            }
        }

        Guid? uncertain = null;
        if (pending is { } attempt)
        {
            try
            {
                var current = savedHash(attempt.Variant);
                if (current == attempt.After)
                    applied.Add(attempt.Variant);
                else if (current != attempt.Before)
                    uncertain = attempt.Variant;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                uncertain = attempt.Variant;
            }
        }
        return new(result, header.Selected, applied.ToArray(), uncertain);
    }

    private string LatestPath => Path.Combine(directory, "latest.json");
    private string JournalPath(Guid id) => Path.Combine(directory, id.ToString("N") + ".jsonl");

    private FileStream AcquireLease() => new(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate,
        FileAccess.ReadWrite, FileShare.None);

    private static InvalidDataException InvalidJournal() => new("The saved Spektrafilm batch history is invalid.");
    private static Entry Parse(string line) => JsonSerializer.Deserialize<Entry>(line) ?? throw InvalidJournal();

    private static bool IsDigest(string? hash) => hash is { Length: 64 }
        && hash.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static void WriteLine(Stream stream, Entry entry)
    {
        stream.Write(JsonSerializer.SerializeToUtf8Bytes(entry));
        stream.WriteByte((byte)'\n');
    }

    private enum Kind { Header, Prepare, Commit, Finish }

    private sealed record Entry([property: JsonRequired] Kind Type, int Version = 0, int Selected = 0, Guid Variant = default,
        string? Before = null, string? After = null, BatchResult Result = BatchResult.Interrupted);

    internal sealed class Writer(FileStream lease, FileStream stream) : IDisposable
    {
        private bool healthy = true;

        internal void Prepare(Guid variant, string? before, string after) => Append(new(Kind.Prepare, Variant: variant, Before: before, After: after));
        internal void Committed(Guid variant) => Append(new(Kind.Commit, Variant: variant));
        internal void Finish(BatchResult result)
        {
            if (healthy)
                Append(new(Kind.Finish, Result: result));
        }

        private void Append(Entry entry)
        {
            // A partial final line is recoverable. Never append after a failed
            // write, as that could move a torn record into the middle of the file.
            healthy = false;
            WriteLine(stream, entry);
            stream.Flush(flushToDisk: true);
            healthy = true;
        }

        public void Dispose()
        {
            try { stream.Dispose(); }
            finally { lease.Dispose(); }
        }
    }
}
