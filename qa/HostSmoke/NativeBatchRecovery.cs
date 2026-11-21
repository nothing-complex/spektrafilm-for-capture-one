using System;
using System.Collections;
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
    private sealed record InterruptedBatchFixture(Guid Id, Guid[] Variants, Guid[] Jobs, string[] Paths, string FirstSha256,
        string SkippedSha256, long Written, int Process);

    private static async Task TestNativeBatchRecovery(object bridge, object panel, object spatial, object window)
    {
        var recipe = ReadSelectedRecipeForTest(bridge, out var suppress);
        var type = recipe.GetType();
        var fields = new[] { "FileType", "TiffBits", "FileOverwriteAction", "DestinationFilename" }
            .Select(name => type.GetProperty(name, Flags)!).ToDictionary(p => p.Name, p => (Property: p, Value: p.GetValue(recipe)));
        void Set(string name, object value) => fields[name].Property.SetValue(recipe, value);
        void Choose(string name, string value) => Set(name, Enum.Parse(fields[name].Property.PropertyType, value));
        suppress.SetValue(recipe, true);
        try
        {
            Choose("FileType", "Tiff");
            Set("TiffBits", 16);
            Choose("FileOverwriteAction", "Skip");
            Set("DestinationFilename", NativeNameToken("OriginalName"));
            await VerifyNativeBatchRecovery(panel, spatial, window);
        }
        finally
        {
            try { foreach (var (member, value) in fields.Values) member.SetValue(recipe, value); }
            finally { suppress.SetValue(recipe, false); }
        }
    }

    private static async Task VerifyNativeBatchRecovery(object panel, object spatial, object window)
    {
        const double secondExposure = -.55;
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var evidence = Path.Combine(folder, "native-batch-interrupted.json");
        var selected = new FixtureSelection(panel);
        var journal = Field(spatial, "exportJobs")!;
        var exposureSlider = Field(panel, "exposure")!;
        var exposure = exposureSlider.GetType().GetProperty("Value", Flags)!;
        var enable = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var resultButton = (Button)Field(spatial, "resumeExportBatch")!;
        object? Plan() => Call(journal, "ReadPendingBatch");
        static object[] Entries(object plan) => ((IEnumerable)Prop(plan, "Entries")!).Cast<object>().ToArray();
        static string Digest(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        if (phase == "first")
        {
            Check(selected.Variants.Length == 4, "batch recovery uses four private native RAW variants");
            var candidates = selected.Variants;
            selected.Select(candidates[0], candidates);
            var hostProfile = panel.GetType().Assembly.GetType("HostProfile")!;
            var order = ((IEnumerable)hostProfile.GetMethod("CaptureSelectionSet", Flags)!
                .Invoke(null, new[] { window, Field(panel, "document") })!).Cast<object>()
                .Select(row => (Guid)Prop(Prop(row, "Variant")!, "UUID")!).ToArray();
            for (var i = 0; i < order.Length; i++)
            {
                selected.Select(order[i], order[i]);
                exposure.SetValue(exposureSlider, i == 1 ? secondExposure : .65);
                enable.IsChecked = i != 2;
            }
            var output = Directory.CreateDirectory(Path.Combine(folder, "batch-recovery")).FullName;
            selected.Select(order[3], order[3]);
            var host = panel.GetType().Assembly.GetType("HostImageSource", true)!;
            var captured = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
            Check(Prop(captured, "FormatId") is "Tiff" && Prop(captured, "TiffBits") is 16
                && Prop(captured, "SkipExistingOutput") is true && Prop(captured, "SuggestedFilename") is string,
                "the fourth variant has a named 16-bit TIFF recipe with native Skip");
            var protectedFile = Path.Combine(output, (string)Prop(captured, "SuggestedFilename")! + ".tif");
            File.WriteAllBytes(protectedFile, new byte[] { 31, 12, 8, 47 });
            var protectedSha = Digest(protectedFile);
            selected.Select(order[0], order);
            var running = (Task<int>)Call(panel, "ExportSelectedImagesAsync",
                (Func<OpenFolderDialog, Window?, string?>)((_, _) => output))!;
            var wait = Stopwatch.StartNew();
            while (!running.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(75))
            {
                if (Plan() is { } plan && Call(journal, "ReadLatest") is { } current
                    && Equals(Prop(current, "VariantId"), order[1]) && Prop(current, "Stage")?.ToString() == "Encoding"
                    && Field(spatial, "exportWork") is { } work && Prop(work, "EncoderBytesWritten") is long bytes && bytes > 1_048_576)
                {
                    var entries = Entries(plan);
                    Check(entries.Length == 4 && entries.Take(3).All(item => Prop(item, "Retry") is { } retry && Prop(retry, "Valid") is true)
                        && Prop(entries[3], "SkipExisting") is true && Prop(entries[3], "Retry") is null,
                        "the verified destinations and read-only Skip decision reached disk before rendering");
                    var paths = entries.Select(item => (string)Prop(item, "Destination")!).ToArray();
                    var jobs = entries.Select(item => (Guid)Prop(item, "JobId")!).ToArray();
                    Check(entries.Select(item => (Guid)Prop(item, "VariantId")!).SequenceEqual(order)
                        && Prop(Call(journal, "ReadBatchItem", plan, entries[0])!, "Stage")?.ToString() == "Completed"
                        && File.Exists(paths[0]) && !File.Exists(paths[1]) && !File.Exists(paths[2])
                        && paths[3] == protectedFile && Digest(protectedFile) == protectedSha,
                        "the first job completed while the second is writing and the approved Skip file remains intact");
                    using var process = Process.GetCurrentProcess();
                    var receipt = new InterruptedBatchFixture((Guid)Prop(plan, "Id")!, order, jobs, paths,
                        Digest(paths[0]), protectedSha, bytes, process.Id);
                    File.WriteAllText(evidence, JsonSerializer.Serialize(receipt));
                    File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} checks before interrupted native batch encoding");
                    process.Kill();
                    return;
                }
                await Task.Delay(10);
            }
            throw new TimeoutException("The second batch image did not reach active encoding. " + ((TextBlock)Field(spatial, "exportStatus")!).Text);
        }

        var interrupted = JsonSerializer.Deserialize<InterruptedBatchFixture>(File.ReadAllText(evidence))!;
        var savedPlan = Plan() ?? throw new InvalidOperationException("The pending batch was missing on restart.");
        var savedEntries = Entries(savedPlan);
        string? Stage(int i) => Call(journal, "ReadBatchItem", savedPlan, savedEntries[i]) is { } receipt
            ? Prop(receipt, "Stage")?.ToString() : null;
        Check(interrupted.Process != Environment.ProcessId && interrupted.Written > 1_048_576
            && Equals(Prop(savedPlan, "Id"), interrupted.Id)
            && savedEntries.Select(item => (Guid)Prop(item, "JobId")!).SequenceEqual(interrupted.Jobs),
            "restart recovers the same batch and all four accepted job identities");
        Check(resultButton.Visibility == Visibility.Visible && Stage(0) == "Completed" && Stage(1) == "Encoding"
            && Stage(2) is null && Stage(3) is null && Digest(interrupted.Paths[0]) == interrupted.FirstSha256
            && Prop(savedEntries[3], "SkipExisting") is true && Digest(interrupted.Paths[3]) == interrupted.SkippedSha256,
            "the restarted panel distinguishes the completed, interrupted and pending jobs");
        Task<int> Resume(bool approve) => (Task<int>)Call(spatial, "ResumeExportBatchAsync", (Func<object, bool>)(_ => approve))!;
        Check(await Resume(false) == 0 && Stage(1) == "Encoding" && !File.Exists(interrupted.Paths[2]),
            "declining recovery leaves the journal and destinations alone");

        var movedSkipFile = Path.Combine(folder, "moved-approved-skip.tif");
        File.Move(interrupted.Paths[3], movedSkipFile);
        var secondPublished = File.Exists(interrupted.Paths[1]) ? Digest(interrupted.Paths[1]) : null;
        if (secondPublished is null)
        {
            selected.Select(interrupted.Variants[1], interrupted.Variants[1]);
            exposure.SetValue(exposureSlider, secondExposure + .24);
        }
        selected.Select(interrupted.Variants[0], interrupted.Variants[0]);
        Check(await Resume(true).WaitAsync(TimeSpan.FromSeconds(80)) == 1 && Stage(2) == "Completed"
            && Stage(3) == "Skipped" && !File.Exists(interrupted.Paths[3]) && Digest(movedSkipFile) == interrupted.SkippedSha256
            && Equals(Field(panel, "displayedVariant"), interrupted.Variants[0]),
            "recovery exports the disabled variant and does not reuse the filename approved for Skip");
        VerifyFile(interrupted.Paths[2]);
        var thirdSha = Digest(interrupted.Paths[2]);
        if (secondPublished is null)
        {
            Check(Stage(1) == "Encoding" && !File.Exists(interrupted.Paths[1]), "the changed look stays pending");
            selected.Select(interrupted.Variants[1], interrupted.Variants[1]);
            exposure.SetValue(exposureSlider, secondExposure);
            selected.Select(interrupted.Variants[0], interrupted.Variants[0]);
            Check(await Resume(true).WaitAsync(TimeSpan.FromSeconds(80)) == 1 && Stage(1) == "Completed",
                "restoring the interrupted look completes its original job");
        }
        else
        {
            Check(Stage(1) == "Skipped" && Digest(interrupted.Paths[1]) == secondPublished,
                "recovery preserves output published immediately before process termination");
        }
        Check(Plan() is null && Digest(interrupted.Paths[0]) == interrupted.FirstSha256 && Digest(interrupted.Paths[2]) == thirdSha
            && !File.Exists(interrupted.Paths[3]) && Digest(movedSkipFile) == interrupted.SkippedSha256
            && resultButton.Visibility == Visibility.Collapsed,
            "the finished queue preserves both completed outputs and the earlier Skip decision");
        VerifyFile(interrupted.Paths[1]);
        File.WriteAllText(Path.Combine(folder, "native-batch-recovered.json"), JsonSerializer.Serialize(new
        {
            interrupted.Id, interrupted.Jobs, interrupted.Written, outcomes = Enumerable.Range(0, 4).Select(Stage),
            CompletedFile = interrupted.FirstSha256, DisabledFile = thirdSha, SecondFile = Digest(interrupted.Paths[1]),
            SecondFileExistedAtRestart = secondPublished is not null, SkippedFile = interrupted.SkippedSha256,
        }));
        File.WriteAllText(Path.Combine(folder, "restart.passed"), $"PASS {checks} interrupted native batch recovery checks");

        static void VerifyFile(string path)
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnDemand).Frames.Single();
            Check((frame.Format == PixelFormats.Rgb48 || frame.Format == PixelFormats.Rgba64) && frame.ColorContexts is { Count: 1 },
                "a recovered queued image opens at 16 bits per channel with an embedded ICC");
        }
    }
}
