using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

public static partial class StartupHook
{
    private sealed record InterruptedExport(Guid Job, Guid Variant, string Stage, string PreviousSha256,
        double Exposure, long StagedBytes, int InterruptedProcess);

    private static async Task TestNativeExportInterruption(object panel, object spatial, string stage)
    {
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var evidence = Path.Combine(folder, "export-interruption.json");
        var target = Path.Combine(folder, "interrupted-export.tif");
        var jobs = Field(spatial, "exportJobs")!;
        var exposure = Field(panel, "exposure")!;
        var exposureValue = exposure.GetType().GetProperty("Value", Flags)!;
        var strength = Field(panel, "strength")!;
        var strengthValue = strength.GetType().GetProperty("Value", Flags)!;
        object? Read() => Call(jobs, "ReadLatest");
        string Digest(string path)
        {
            using var bytes = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        Task<bool> Save(string path) => (Task<bool>)Call(spatial, "ExportWithDestinationAsync", false, null,
            (Func<SaveFileDialog, Window?, string?>)((_, _) => path))!;
        var variant = (Guid)Field(panel, "displayedVariant")!;
        if (phase == "first")
        {
            Check(!File.Exists(evidence) && !File.Exists(target), "the hard-interruption test is using a new private output");
            ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
            strengthValue.SetValue(strength, 0d);
            Check(await Save(target).WaitAsync(TimeSpan.FromSeconds(45)), "a full native high-depth reference is committed before interruption");
            var previous = Digest(target);
            var previousJob = Prop(Read()!, "JobId");
            strengthValue.SetValue(strength, 100d);
            exposureValue.SetValue(exposure, 1.37);
            var savedExposure = (double)exposureValue.GetValue(exposure)!;
            var writing = Save(target);
            var start = Stopwatch.GetTimestamp();
            while (!writing.IsCompleted && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(35))
            {
                if (Read() is { } current && Prop(current, "JobId") is Guid jobId && !Equals(jobId, previousJob)
                    && Equals(Prop(current, "Destination"), target) && Prop(current, "Stage")?.ToString() == stage)
                {
                    var stagedBytes = stage == "Encoding" ? SizeOfLargestStage() : 0;
                    if (stage != "Encoding" || stagedBytes >= 1024 * 1024)
                    {
                        using var process = Process.GetCurrentProcess();
                        File.WriteAllText(evidence, JsonSerializer.Serialize(new InterruptedExport(jobId, variant, stage, previous,
                            savedExposure, stagedBytes, process.Id)));
                        Check(!writing.IsCompleted, "the approved export is still in progress when the disposable process is stopped");
                        var marker = Path.Combine(folder, "validated-interruption");
                        File.WriteAllText(marker, $"PASS {checks} checks before abrupt stop during {stage}");
                        File.Move(marker, Path.Combine(folder, "first.passed"));
                        process.Kill();
                        return;
                    }
                }
                await Task.WhenAny(writing, Task.Delay(10));
            }
            throw new TimeoutException($"Did not observe an active export in {stage}.");
        }

        var interrupted = JsonSerializer.Deserialize<InterruptedExport>(File.ReadAllText(evidence))
            ?? throw new InvalidDataException("The interrupted export receipt was not recorded.");
        var last = Read()!;
        var status = (TextBlock)Field(spatial, "exportStatus")!;
        Check(interrupted.Stage == stage && interrupted.InterruptedProcess != Environment.ProcessId
            && interrupted.InterruptedProcess.ToString() == File.ReadAllText(Path.Combine(folder, "first.pid")),
            "the recovery check is running in a new process after its own fixture stopped");
        Check(Equals(Prop(last, "JobId"), interrupted.Job) && Equals(Prop(last, "VariantId"), variant)
            && variant == interrupted.Variant && Equals(Prop(last, "Destination"), target)
            && Prop(last, "Stage")?.ToString() == stage && Prop(last, "Finished") is false,
            "restart retains the exact approved destination and unfinished export stage");
        Check(status.Text.Contains("Unfinished Spektrafilm export", StringComparison.Ordinal)
            && Field(spatial, "exportWork") is null && Digest(target) == interrupted.PreviousSha256,
            "restart reports the interrupted job and leaves the previously committed file intact");
        Check(((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked == true
            && Equals(exposureValue.GetValue(exposure), interrupted.Exposure) && Equals(strengthValue.GetValue(strength), 100d),
            "abrupt export interruption preserves the acknowledged variant look and master switch");

        var orphanBytes = SizeOfLargestStage();
        var retry = Path.Combine(folder, "explicit-retry.tif");
        Check(await Save(retry).WaitAsync(TimeSpan.FromSeconds(45)), "a fresh explicit export succeeds after interruption");
        var completed = Read()!;
        Check(Prop(completed, "Stage")?.ToString() == "Completed" && !Equals(Prop(completed, "JobId"), interrupted.Job)
            && Equals(Prop(completed, "Destination"), retry) && Digest(target) == interrupted.PreviousSha256,
            "the retry accepted a distinct immutable destination and retained the previous output bytes");
        var retryDigest = Digest(retry);
        using var file = File.OpenRead(retry);
        var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnDemand).Frames[0];
        Check(frame.Format == PixelFormats.Rgb48 && frame.PixelWidth == 6000 && frame.PixelHeight == 4000
            && frame.ColorContexts is { Count: 1 } && retryDigest != interrupted.PreviousSha256,
            "the explicit retry reopens as a new 24 MP RGB48 film result with an embedded profile");
        File.WriteAllText(Path.Combine(folder, "export-interruption-restart.json"),
            JsonSerializer.Serialize(new { interrupted, orphanBytes, retryDigest }));
        File.WriteAllText(Path.Combine(folder, "restart.passed"), $"PASS {checks} hard interruption recovery checks for {stage}");

        long SizeOfLargestStage() => Directory.EnumerateFiles(folder, ".spektra-*.tmp")
            .Select(path =>
            {
                try { return new FileInfo(path).Length; }
                catch (IOException) { return 0; }
            }).DefaultIfEmpty().Max();
    }
}
