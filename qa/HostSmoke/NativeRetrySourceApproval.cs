using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

public static partial class StartupHook
{
    private static async Task TestNativeRetrySourceApproval(object panel, object spatial, object window)
    {
        var native = window.GetType().Assembly.GetType("P1.C1.Core.ICoreVariant")
            ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("P1.C1.Core.ICoreVariant")).First(t => t is not null)!;
        var select = new FixtureSelection(panel);
        var primary = select.Variants[0];
        select.Select(primary, primary);
        var model = panel.GetType().Assembly.GetType("HostProfile", true)!;
        var variant = Prop(model.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!, "Variant")!;
        var profileProperty = native.GetProperty("ColorProfile")!;
        var previousProfile = profileProperty.GetValue(variant);
        var layers = Prop(variant, "LocalAdjustmentsLayers")!;
        var input = Directory.EnumerateFiles(Path.Combine(folder, "ICC fixture inputs")).Single();
        var baseline = File.ReadAllBytes(input);
        var changed = WithDifferentCopyright(baseline);
        var protectedFile = Path.Combine(folder, "protected-retry.png");
        var marker = new byte[] { 7, 69, 25, 81 };
        File.WriteAllBytes(protectedFile, marker);
        var jobs = Field(spatial, "exportJobs")!;
        var status = (TextBlock)Field(spatial, "exportStatus")!;
        var probePath = Path.Combine(folder, "native-source-retry-probe.png");
        var approvedPath = Path.Combine(folder, "restored-native-source.png");
        var layer = AddFixtureAdjustmentLayer(native, variant, layers);
        try
        {
            var setValue = layer.GetType().GetMethods(Flags).Single(m =>
                m.Name == "SetValue" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
            setValue.MakeGenericMethod(typeof(float)).Invoke(layer, new object[] { "Exposure", .85f });
            Call(layer, "FillMaskNoUndo", variant);
            profileProperty.SetValue(variant, Activator.CreateInstance(profileProperty.PropertyType, "Spektrafilm retry fixture ICC", baseline));
            var id = LocalIccIds(variant).Single();
            var path = Path.Combine(folder, "Adjustments", "ICC", (string)Prop(id, "FileName")!);
            if (!File.Exists(path)) File.WriteAllBytes(path, baseline);
            ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
            Call(spatial, "SetEnabled", true);
            var interrupted = await SeedVerifiedNativeExportRetry(panel, spatial, primary, probePath);
            var pendingId = Prop(interrupted, "JobId");
            bool PendingUntouched() => Equals(Prop(Call(jobs, "ReadLatest")!, "JobId"), pendingId)
                && File.ReadAllBytes(protectedFile).SequenceEqual(marker);

            var offered = false;
            try
            {
                Check(!await Retry((_, _) =>
                {
                    offered = true;
                    ChangeProfile(changed);
                    return protectedFile;
                }) && offered && status.Text.Contains("camera profiles changed") && PendingUntouched(),
                    "switching the active camera ICC bytes during Save preserves the pending job and earlier file");
            }
            finally { ChangeProfile(baseline); }

            offered = false;
            try
            {
                Check(!await Retry((_, _) =>
                {
                    Call(layer, "InvertMask", variant);
                    offered = true;
                    return protectedFile;
                }) && offered && status.Text.Contains("source adjustments") && PendingUntouched(),
                    "editing the native mask during Save preserves the pending job and earlier file");
            }
            finally { if (offered) Call(layer, "InvertMask", variant); }

            var scheduler = (SemaphoreSlim)panel.GetType().Assembly.GetType("HostImageSource", true)!
                .GetField("SchedulerSlot", Flags)!.GetValue(null)!;
            var held = false;
            var inFlight = Retry((_, _) =>
            {
                held = scheduler.Wait(0);
                if (!held) throw new InvalidOperationException("The fixture could not hold native development.");
                return protectedFile;
            });
            try
            {
                var started = Stopwatch.GetTimestamp();
                while (!inFlight.IsCompleted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
                {
                    var active = Call(jobs, "ReadLatest")!;
                    if (Equals(Prop(active, "RetryOf"), pendingId) && Prop(active, "Stage")?.ToString() == "DevelopingSource")
                        break;
                    await Task.Delay(25);
                }
                Check(held && !inFlight.IsCompleted
                    && Prop(Call(jobs, "ReadLatest")!, "Stage")?.ToString() == "DevelopingSource",
                    "the retry has accepted its destination but has not started native development");
                ChangeProfile(changed);
            }
            finally { if (held) scheduler.Release(); }
            try
            {
                Check(!await inFlight && Prop(Call(jobs, "ReadLatest")!, "Stage")?.ToString() == "Failed"
                    && File.ReadAllBytes(protectedFile).SequenceEqual(marker),
                    "a changed camera ICC after acceptance fails the retry and preserves the earlier file");
            }
            finally { ChangeProfile(baseline); }

            var restored = await SeedVerifiedNativeExportRetry(panel, spatial, primary, probePath);
            Check(await Retry((_, _) => approvedPath) && Equals(Prop(Call(jobs, "ReadLatest")!, "RetryOf"), Prop(restored, "JobId")),
                "unchanged restored mask and camera ICC complete a verified retry");
            using var stream = File.OpenRead(approvedPath);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames.Single();
            Check(frame.Format == PixelFormats.Rgba64 && frame.ColorContexts is { Count: 1 }, "the restored retry produces a high-depth PNG and an ICC");
            File.WriteAllText(Path.Combine(folder, "native-retry-source-approval.json"), JsonSerializer.Serialize(new
            {
                cameraProfileSha256 = Convert.ToHexString(SHA256.HashData(baseline)),
                replacementSha256 = Convert.ToHexString(SHA256.HashData(changed)),
                frame.PixelWidth, frame.PixelHeight, protectedFilePreserved = File.ReadAllBytes(protectedFile).SequenceEqual(marker),
            }));

            void ChangeProfile(byte[] bytes)
            {
                File.WriteAllBytes(path, bytes);
                Call(variant, "UnloadICCFromMemory", id);
                var resolved = (byte[])Prop(Call(variant, "GetICCSideCarGivenID", id)!, "rawData")!;
                Check(resolved.SequenceEqual(bytes), "Capture One resolves the exact desired camera profile under the captured sidecar ID");
            }
        }
        finally
        {
            profileProperty.SetValue(variant, previousProfile);
            Call(layers, "RemoveNoUndo", layer);
        }
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native retry source approval assertions");

        Task<bool> Retry(Func<SaveFileDialog, Window?, string?> choose) =>
            ((Task<bool>)Call(spatial, "RetryInterruptedExportAsync", choose)!).WaitAsync(TimeSpan.FromSeconds(45));
    }
}
