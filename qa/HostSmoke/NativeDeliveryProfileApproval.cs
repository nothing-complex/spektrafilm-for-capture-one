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
    private static async Task TestNativeDeliveryProfileApproval(object bridge, object panel, object spatial)
    {
        Check(File.Exists(Path.Combine(folder, "native-recipe-backup.json")), "the native recipe backup is verified");
        var destinations = ((Delegate)Field(bridge, "destinations")!).DynamicInvoke()!;
        var selected = Prop(destinations, "SelectedDestination")!;
        var path = selected.GetType().GetProperty("ColorProfilePath", Flags)!;
        var notifications = selected.GetType().GetField("m_suppressNotifications", Flags)!;
        var previousPath = path.GetValue(selected);
        Check(notifications.GetValue(selected) is false, "the native recipe notification guard is available");
        var profiles = Path.Combine(Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!, "profiles", "preview");
        var approved = File.ReadAllBytes(Path.Combine(profiles, "ProPhoto RGB.icc"));
        var replacement = File.ReadAllBytes(Path.Combine(profiles, "Linear Rec.2020.icc"));
        var approvedHash = Convert.ToHexString(SHA256.HashData(approved));
        var profile = Path.Combine(folder, "disposable-delivery.icc");
        var retryProfile = Path.Combine(folder, "disposable-retry-delivery.icc");
        var batchFolder = Directory.CreateDirectory(Path.Combine(folder, "profile-approval-batch")).FullName;
        var marker = Path.Combine(batchFolder, "keep.png");
        File.WriteAllText(marker, "existing output");
        var jobs = Field(spatial, "exportJobs")!;
        var status = (TextBlock)Field(spatial, "exportStatus")!;
        var select = new FixtureSelection(panel);
        var primary = select.Variants[0];
        notifications.SetValue(selected, true);
        try
        {
            File.WriteAllBytes(profile, approved);
            path.SetValue(selected, profile);
            select.Select(primary, select.Variants.Take(2).ToArray());
            var previousReceipt = Call(jobs, "ReadLatest");
            var folderOffered = false;
            Func<OpenFolderDialog, Window?, string?> chooseFolder = (_, _) =>
            {
                folderOffered = true;
                File.WriteAllBytes(profile, replacement);
                return batchFolder;
            };
            var batch = await ((Task<int>)Call(panel, "ExportSelectedImagesWithOwnerAsync", null, chooseFolder)!).WaitAsync(TimeSpan.FromSeconds(30));
            Check(folderOffered && batch == 0 && status.Text.Contains("2 failed") && status.Text.Contains("changed or became unavailable"),
                "batch folder approval rejects both images when the active recipe ICC changes");
            Check(Equals(previousReceipt, Call(jobs, "ReadLatest")) && File.ReadAllText(marker) == "existing output"
                && Directory.GetFiles(batchFolder).Length == 1, "the rejected batch leaves no accepted export or replacement");

            select.Select(primary, primary);
            ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
            Call(spatial, "SetEnabled", true);
            File.WriteAllBytes(profile, approved);
            var probePath = Path.Combine(folder, "profile-retry-probe.png");
            var interrupted = await SeedVerifiedNativeExportRetry(panel, spatial, primary, probePath);
            var interruptedId = Prop(interrupted, "JobId");
            var output = Path.Combine(folder, "profile-approved-retry.png");
            var saveOffered = false;
            path.SetValue(selected, retryProfile);
            File.WriteAllBytes(retryProfile, replacement);
            Check(!await Retry((_, _) => { saveOffered = true; return output; }) && !saveOffered
                && status.Text.Contains("Export Recipe changed"), "retry rejects a changed recipe ICC before offering Save");
            File.WriteAllBytes(retryProfile, approved);
            Check(!await Retry((_, _) => { saveOffered = true; File.WriteAllBytes(retryProfile, replacement); return output; })
                && saveOffered && status.Text.Contains("changed or became unavailable"),
                "retry detects a file change after matching the interrupted job and offering Save");
            Check(Equals(Prop(Call(jobs, "ReadLatest")!, "JobId"), interruptedId)
                && !File.Exists(output) && !File.Exists(probePath), "both rejected retries preserve the interrupted job and destination");

            File.WriteAllBytes(retryProfile, approved);
            Check(await Retry((_, _) => output) && Equals(Prop(Call(jobs, "ReadLatest")!, "RetryOf"), interruptedId),
                "retry accepts the restored profile and links to the interrupted receipt");
            using var stream = File.OpenRead(output);
            var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames.Single();
            Check(image.Format == PixelFormats.Rgba64 && image.ColorContexts is { Count: 1 },
                "native retry produces a 16-bit PNG with a single ICC");
            using var embedded = image.ColorContexts![0].OpenProfileStream();
            var embeddedHash = Convert.ToHexString(SHA256.HashData(embedded));
            Check(embeddedHash == approvedHash, "the native retry embeds exactly the approved recipe ICC");
            File.WriteAllText(Path.Combine(folder, "native-delivery-profile-approval.json"),
                JsonSerializer.Serialize(new { approvedHash, embeddedHash, image.PixelWidth, image.PixelHeight }));
        }
        finally
        {
            try { path.SetValue(selected, previousPath); }
            finally { notifications.SetValue(selected, false); }
        }
        Check(Equals(path.GetValue(selected), previousPath) && notifications.GetValue(selected) is false,
            "the selected native recipe is restored");
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native delivery profile approval assertions");

        Task<bool> Retry(Func<SaveFileDialog, Window?, string?> choose) =>
            ((Task<bool>)Call(spatial, "RetryInterruptedExportAsync", choose)!).WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static async Task<object> SeedVerifiedNativeExportRetry(object panel, object spatial, Guid primary, string probePath)
    {
        var jobs = Field(spatial, "exportJobs")!;
        var scheduler = (SemaphoreSlim)panel.GetType().Assembly.GetType("HostImageSource", true)!
            .GetField("SchedulerSlot", Flags)!.GetValue(null)!;
        var held = false;
        Func<SaveFileDialog, Window?, string?> chooseProbe = (_, _) =>
        {
            held = scheduler.Wait(0);
            if (!held) throw new InvalidOperationException("The fixture could not reserve native development.");
            return probePath;
        };
        var probe = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, null, chooseProbe)!;
        object? descriptor = null;
        try
        {
            var started = Stopwatch.GetTimestamp();
            while (!probe.IsCompleted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
            {
                if (Call(jobs, "ReadLatest") is { } saved && Prop(saved, "Stage")?.ToString() == "DevelopingSource")
                {
                    descriptor = Prop(saved, "Retry");
                    break;
                }
                await Task.Delay(25);
            }
            Check(held && descriptor is not null, "the native job durably captures its verification descriptor before developing");
            Call(Field(spatial, "exportWork")!, "Cancel");
        }
        finally { if (held) scheduler.Release(); }
        Check(!await probe.WaitAsync(TimeSpan.FromSeconds(30)) && !File.Exists(probePath),
            "the fixture captured a retry descriptor without writing an image");
        var interrupted = Call(jobs, "Accept", primary, probePath)!;
        interrupted = Call(jobs, "RecordRetry", interrupted, descriptor!)!;
        spatial.GetType().GetField("interruptedExport", Flags)!.SetValue(spatial, interrupted);
        return interrupted;
    }
}
