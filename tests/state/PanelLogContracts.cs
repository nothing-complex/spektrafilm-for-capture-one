using System.Diagnostics;
using System.Text.Json;

internal static class PanelLogContracts
{
    internal static async Task<int> SoakAsync(string directory, int seconds)
    {
        if (seconds is < 1 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds));
        directory = Path.GetFullPath(directory);
        if (Directory.Exists(directory)) throw new IOException("Choose a fresh diagnostic measurement directory.");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "panel.log");
        var busy = Path.Combine(directory, "blocked.log");
        var live = new PanelLog(path);
        using var lockFile = new FileStream(busy, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var blocked = new PanelLog(busy);
        var message = new string('x', 512);
        var durations = new List<double>();
        var stopwatch = Stopwatch.StartNew();
        const int recordsPerBatch = 64;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            for (var index = 0; index < recordsPerBatch; index++)
            {
                var start = Stopwatch.GetTimestamp();
                live.Write(message);
                blocked.Write(message);
                durations.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            await Task.Delay(250);
        }
        live.Write("diagnostic soak complete");
        await Task.WhenAll(live.Complete(), blocked.Complete()).WaitAsync(TimeSpan.FromSeconds(15));
        var sizes = new[] { path, path + ".previous" }.Where(File.Exists).Select(file => new FileInfo(file).Length).ToArray();
        if (sizes.Length == 0 || sizes.Any(size => size > PanelLog.FileLimit) || blocked.Dropped != durations.Count
            || lockFile.Length != 0 || !File.ReadLines(path).Last().Contains("diagnostic soak complete", StringComparison.Ordinal))
            throw new InvalidDataException("Diagnostic soak exceeded a queue, file or accepted-output boundary.");
        durations.Sort();
        double At(double percentile) => durations[(int)Math.Ceiling(percentile * durations.Count) - 1];
        var report = JsonSerializer.Serialize(new
        {
            elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
            submittedPerLogger = durations.Count,
            normalDropped = live.Dropped,
            blockedDropped = blocked.Dropped,
            retainedFileSizes = sizes,
            pairOfWritesMs = new { p50 = At(.5), p95 = At(.95), p99 = At(.99), max = durations[^1] },
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "measurement.json"), report);
        Console.WriteLine(report);
        return 0;
    }

    internal static async Task RunAsync(string root, Action<string, bool> check)
    {
        var file = Path.Combine(root, "bounded-diagnostic.log");
        var content = new string('x', PanelLog.MessageLimit + 128);
        for (var pass = 0; pass < 3; pass++)
        {
            var log = new PanelLog(file);
            for (var index = 0; index < 200; index++)
                log.Write(content);
            log.Write("ending value\nwithout a second record");
            await log.Complete().WaitAsync(TimeSpan.FromSeconds(15));
            check("diagnostic writer drains each bounded batch", log.Dropped == 0);
        }
        var retained = new[] { file, file + ".previous" };
        check("diagnostic rotation retains two bounded generations",
            retained.All(path => File.Exists(path) && new FileInfo(path).Length <= PanelLog.FileLimit));
        var lines = retained.SelectMany(File.ReadLines).ToArray();
        check("diagnostic lines are structured and message length is capped", lines.All(text =>
        {
            using var value = JsonDocument.Parse(text);
            return value.RootElement.GetProperty("Utc").GetDateTime().Kind == DateTimeKind.Utc
                && value.RootElement.GetProperty("Message").GetString()!.Length <= PanelLog.MessageLimit + 1;
        }));
        using (var latest = JsonDocument.Parse(File.ReadLines(file).Last()))
            check("diagnostics escape embedded newlines and retain the latest complete entry",
                latest.RootElement.GetProperty("Message").GetString() == "ending value\nwithout a second record");

        var structured = Path.Combine(root, "structured-diagnostic.log");
        var events = new PanelLog(structured);
        events.Write("invalid sample", new { Milliseconds = double.NaN });
        events.Write("oversized sample", new { Block = new string('x', (int)PanelLog.FileLimit) });
        events.Write("export.performance.v1", new { Milliseconds = 12.5 });
        await events.Complete().WaitAsync(TimeSpan.FromSeconds(15));
        using (var sample = JsonDocument.Parse(File.ReadLines(structured).Single()))
            check("invalid and oversized structured details are dropped while later events retain typed data",
                events.Dropped == 2 && sample.RootElement.GetProperty("Details").GetProperty("Milliseconds").GetDouble() == 12.5);

        var busy = Path.Combine(root, "busy-diagnostic.log");
        using (var locked = new FileStream(busy, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var log = new PanelLog(busy);
            var stopwatch = Stopwatch.StartNew();
            Parallel.For(0, 10_000, _ => log.Write(content));
            check("diagnostic producers return while the writer's file is locked", stopwatch.Elapsed < TimeSpan.FromSeconds(5));
            await log.Complete().WaitAsync(TimeSpan.FromSeconds(15));
            check("diagnostic storage faults drop entries without altering the locked data",
                log.Dropped == 10_000 && locked.Length == 0);
        }
        var restored = new PanelLog(busy);
        restored.Write("storage restored");
        await restored.Complete().WaitAsync(TimeSpan.FromSeconds(15));
        check("diagnostic storage recovers after file access is restored",
            restored.Dropped == 0 && File.ReadAllText(busy).Contains("storage restored", StringComparison.Ordinal));
    }
}
