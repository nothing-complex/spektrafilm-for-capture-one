using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

internal static class DurableEditProbe
{
    internal static int Run(string directory, bool journalCandidate = false)
    {
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new IOException("Choose a new measurement directory.");
        var root = Path.Combine(directory, "Variant looks");
        var catalog = Path.Combine(directory, "Measurement.cocatalogdb");
        var store = new VariantLookStore(root, catalog);
        var variants = Enumerable.Range(0, 64).Select(_ => Guid.NewGuid()).ToArray();
        var journal = journalCandidate ? new DurableLookJournalProbe(directory) : null;
        var expected = new VariantLook[variants.Length];
        const int warmup = 256;
        const int measured = 10_000;
        var reads = new double[measured];
        var comparisons = new double[measured];
        var prepare = new double[measured];
        var commit = new double[measured];
        var total = new double[measured];
        var allocations = 0L;
        var readAllocations = 0L;
        var comparisonAllocations = 0L;
        var patch = new Dictionary<string, double>(StringComparer.Ordinal);
        var optics = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var index = 0; index < 32; index++)
        {
            var suffix = index.ToString(CultureInfo.InvariantCulture);
            patch.Add("development-" + suffix, index / 32d);
            optics.Add("optics-" + suffix, index / 32d);
        }
        for (var index = 0; index < warmup + measured; index++)
        {
            var variant = index % variants.Length;
            var prior = expected[variant];
            var beforeReadBytes = GC.GetAllocatedBytesForCurrentThread();
            var beforeRead = Stopwatch.GetTimestamp();
            var stored = journal is null ? store.Read(variants[variant]) : journal.ReadMirror(variants[variant]);
            var afterRead = Stopwatch.GetTimestamp();
            var afterReadBytes = GC.GetAllocatedBytesForCurrentThread();
            var matches = prior is null ? stored is null : VariantLookStore.Equivalent(stored, prior);
            var afterComparison = Stopwatch.GetTimestamp();
            var afterComparisonBytes = GC.GetAllocatedBytesForCurrentThread();
            if (!matches)
                throw new InvalidDataException("The previous committed look did not reload exactly.");
            var look = new VariantLook(new("kodak_portra_400", "kodak_portra_endura", (index % 1141) / 100d - 5, .2, 1,
                "print", "hanatos2025", false, patch), new(optics, new() { ["input-color"] = "srgb" }, new()),
                Enabled: (index / variants.Length) % 2 == 0, Grain: new(2, 25, 50));
            expected[variant] = look;
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            var staged = journal?.Prepare(variants[variant], look);
            if (journal is null) store.Queue(variants[variant], look);
            var prepared = Stopwatch.GetTimestamp();
            if (journal is not null) journal.Commit(variants[variant], staged!);
            else store.Flush();
            var committed = Stopwatch.GetTimestamp();
            if (index < warmup) continue;
            var sample = index - warmup;
            reads[sample] = Stopwatch.GetElapsedTime(beforeRead, afterRead).TotalMilliseconds;
            comparisons[sample] = Stopwatch.GetElapsedTime(afterRead, afterComparison).TotalMilliseconds;
            prepare[sample] = Stopwatch.GetElapsedTime(start, prepared).TotalMilliseconds;
            commit[sample] = Stopwatch.GetElapsedTime(prepared, committed).TotalMilliseconds;
            total[sample] = Stopwatch.GetElapsedTime(start, committed).TotalMilliseconds;
            allocations += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            readAllocations += afterReadBytes - beforeReadBytes;
            comparisonAllocations += afterComparisonBytes - afterReadBytes;
        }
        var reopened = new VariantLookStore(root, catalog);
        for (var index = 0; index < variants.Length; index++)
            if (!VariantLookStore.Equivalent(journal is null ? reopened.Read(variants[index]) : journal.ReadVerified(variants[index]), expected[index]))
                throw new InvalidDataException("A saved variant did not round-trip.");
        if (journal is null && (store.HasPending || Directory.EnumerateFiles(root, ".spektra-*.tmp", SearchOption.AllDirectories).Any()))
            throw new InvalidDataException("A confirmed save left uncommitted state.");
        var report = JsonSerializer.Serialize(new
        {
            measured, warmup, variants = variants.Length,
            storage = journal is null ? "current-store" : "experimental-journal-and-buffered-mirrors",
            journalBytes = journal?.Bytes,
            readMs = Percentiles(reads), compareMs = Percentiles(comparisons),
            queueMs = Percentiles(prepare), flushMs = Percentiles(commit), queueAndFlushMs = Percentiles(total),
            managedBytesPerRead = readAllocations / (double)measured, managedBytesPerCompare = comparisonAllocations / (double)measured,
            managedBytesPerEdit = allocations / (double)measured,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "measurement.json"), report);
        Console.WriteLine(report);
        return 0;
    }

    private static object Percentiles(double[] values)
    {
        Array.Sort(values);
        return new { p50 = At(.50), p95 = At(.95), p99 = At(.99), max = values[^1] };

        double At(double fraction) => values[(int)Math.Ceiling(fraction * values.Length) - 1];
    }
}
