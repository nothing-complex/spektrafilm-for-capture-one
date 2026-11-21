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
        var unrelatedStage = Path.Combine(folder, ".spektra-unrelated.tmp");
        const string unrelatedText = "Unrelated disposable file";
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
        Task<bool> Retry(Func<SaveFileDialog, Window?, string?> choose) =>
            (Task<bool>)Call(spatial, "RetryInterruptedExportAsync", choose)!;
        void RecordModelFingerprint()
        {
            var host = panel.GetType().Assembly.GetType("HostProfile", true)!;
            var selection = host.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { Field(spatial, "window") })!;
            var settings = Prop(Prop(selection, "Variant")!, "Settings")!;
            var model = (string)Prop(settings, "ModelCoreRepresentation")!;
            // This fixture records identity without retaining the model values.
            var fields = model.Split('\n')
                .Select(line => (Line: line, Match: System.Text.RegularExpressions.Regex.Match(line, "^\\s*\"([A-Za-z0-9_]+)\"\\s*:")))
                .Where(field => field.Match.Success)
                .GroupBy(field => field.Match.Groups[1].Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(string.Join("\n", group.Select(field => field.Line))))));
            File.WriteAllText(Path.Combine(folder, phase + "-retry-model-fields.json"), JsonSerializer.Serialize(fields));
        }
        var variant = (Guid)Field(panel, "displayedVariant")!;
        if (phase == "first")
        {
            Check(!File.Exists(evidence) && !File.Exists(target), "the hard-interruption test is using a new private output");
            if (stage == "Encoding") File.WriteAllText(unrelatedStage, unrelatedText);
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
                    // While the encoder holds the staging handle exclusively,
                    // external file metadata can lag behind confirmed writes.
                    var active = Field(spatial, "exportWork");
                    var stagedBytes = stage == "Encoding" && active is not null
                        ? (long)Prop(active, "EncoderBytesWritten")! : 0;
                    if (stage != "Encoding" || stagedBytes >= 1024 * 1024)
                    {
                        using var process = Process.GetCurrentProcess();
                        File.WriteAllText(evidence, JsonSerializer.Serialize(new InterruptedExport(jobId, variant, stage, previous,
                            savedExposure, stagedBytes, process.Id)));
                        Check(!writing.IsCompleted, "the approved export is still in progress when the disposable process is stopped");
                        Check(Prop(current, "Retry") is { } descriptor && Prop(descriptor, "Valid") is true,
                            "the original, native model and delivery digests are durable before the process stops");
                        RecordModelFingerprint();
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

        var ownedStages = Directory.EnumerateFiles(folder, ".spektra-*.tmp")
            .Where(path => !string.Equals(path, unrelatedStage, StringComparison.OrdinalIgnoreCase)).ToArray();
        var orphanBytes = ownedStages.Select(path => new FileInfo(path).Length).DefaultIfEmpty().Max();
        if (stage == "Encoding")
            Check(interrupted.StagedBytes >= 1024 * 1024 && ownedStages.Length == 0
                && File.ReadAllText(unrelatedStage) == unrelatedText,
                "Windows released only the active Spektrafilm staging file after the process stopped mid-encoding");
        var retry = Path.Combine(folder, "explicit-retry.tif");
        var retryButton = (Button)Field(spatial, "retryExport")!;
        Check(retryButton.Visibility == Visibility.Visible && retryButton.IsEnabled,
            "the restarted tool offers a verified retry for the interrupted job");
        exposureValue.SetValue(exposure, interrupted.Exposure + .21);
        var offeredWhileChanged = false;
        Check(!await Retry((_, _) => { offeredWhileChanged = true; return retry; }).WaitAsync(TimeSpan.FromSeconds(45))
            && !offeredWhileChanged && !File.Exists(retry) && Equals(Prop(Read()!, "JobId"), interrupted.Job),
            "a changed look refuses the retry before opening Save or accepting another output");
        exposureValue.SetValue(exposure, interrupted.Exposure);
        RecordModelFingerprint();
        var offered = false;
        string SelectedDestination(SaveFileDialog picker)
        {
            Check(StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(picker.InitialDirectory), Path.GetDirectoryName(target))
                && picker.FileName == Path.GetFileName(target) && picker.DefaultExt.TrimStart('.') == "tif"
                && picker.FilterIndex == 1 && !picker.Filter.Contains("PNG", StringComparison.Ordinal) && picker.OverwritePrompt,
                "the retry suggests the approved path and keeps overwrite confirmation");
            offered = true;
            return retry;
        }
        Check(!await Retry((picker, _) => { SelectedDestination(picker); return null; }).WaitAsync(TimeSpan.FromSeconds(45))
            && offered && !File.Exists(retry) && Equals(Prop(Read()!, "JobId"), interrupted.Job),
            "canceling Save leaves the original interrupted receipt available");
        offered = false;
        Check(await Retry((picker, _) => SelectedDestination(picker)).WaitAsync(TimeSpan.FromSeconds(45)) && offered,
            "a verified explicit retry succeeds after the acknowledged look is restored");
        var completed = Read()!;
        Check(Prop(completed, "Stage")?.ToString() == "Completed" && !Equals(Prop(completed, "JobId"), interrupted.Job)
            && Equals(Prop(completed, "RetryOf"), interrupted.Job) && Equals(Prop(completed, "Destination"), retry)
            && Digest(target) == interrupted.PreviousSha256,
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
    }
}
