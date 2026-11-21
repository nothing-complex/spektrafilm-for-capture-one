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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

public static partial class StartupHook
{
    private static async Task TestOriginalReplacement(object panel, object spatial, object window, string chart)
    {
        var originalHash = Digest(chart);
        var originalTimestamp = File.GetLastWriteTimeUtc(chart);
        var temp = chart + ".fixture-replacement";
        var backup = chart + ".fixture-backup";
        var output = Path.Combine(folder, "original-replacement-retry.png");
        var jobs = Field(spatial, "exportJobs")!;
        var previousReceipt = Call(jobs, "ReadLatest");
        var marker = new byte[] { 21, 67, 42, 193 };
        File.WriteAllBytes(output, marker);
        var owner = new Window
        {
            Title = "Disposable Spektrafilm original replacement check",
            Width = 360,
            Height = 120,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var hwnd = new WindowInteropHelper(owner) { Owner = ((Forms.Form)window).Handle };
        owner.Show();
        try
        {
            ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
            Call(spatial, "SetEnabled", true);
            var strength = Field(panel, "strength")!;
            strength.GetType().GetProperty("Value", Flags)!.SetValue(strength, 100d);

            File.Copy(chart, temp);
            using (var replacement = File.Open(temp, FileMode.Open, FileAccess.ReadWrite))
            {
                replacement.Position = replacement.Length - 1;
                var octet = replacement.ReadByte();
                replacement.Position--;
                replacement.WriteByte((byte)(octet ^ 1));
            }
            var replacementHash = Digest(temp);
            Check(replacementHash != originalHash && new FileInfo(temp).Length == new FileInfo(chart).Length,
                "the disposable replacement has changed bytes and the same length");
            var offered = false;
            var accepted = false;
            Exception? error = null;
            var frame = new DispatcherFrame();
            var warned = DismissOwnedMessageBox(hwnd.Handle, "Could not export with Spektrafilm", Forms.DialogResult.OK, () =>
            {
                _ = owner.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        accepted = await Save((_, _) =>
                        {
                            offered = true;
                            ReplaceOriginal();
                            return output;
                        });
                    }
                    catch (Exception failure) { error = failure; }
                    finally { frame.Continue = false; }
                });
                Dispatcher.PushFrame(frame);
            }, "The original image file changed. Reopen Export to check the new source.");
            if (error is not null) throw error;
            Check(offered && warned && !accepted && Equals(previousReceipt, Call(jobs, "ReadLatest")),
                "changing a RAW at Save shows a retry instruction before accepting a job");
            Check(File.ReadAllBytes(output).SequenceEqual(marker) && Digest(chart) == replacementHash
                && Digest(backup) == originalHash && File.GetLastWriteTimeUtc(chart) == originalTimestamp,
                "the previous destination, replacement RAW and verified original are preserved byte for byte");

            RestoreOriginal();
            Check(await Save((_, _) => output).WaitAsync(TimeSpan.FromSeconds(45)),
                "a new export completes after the original is restored");
            var completed = Call(jobs, "ReadLatest")!;
            Check(Prop(completed, "Stage")?.ToString() == "Completed", "the corrected export completes its journal entry");
            using var stream = File.OpenRead(output);
            var png = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames.Single();
            Check(png.Format == PixelFormats.Rgba64 && png.ColorContexts is { Count: 1 } && Digest(chart) == originalHash,
                "the new native export delivers a 16-bit PNG with an ICC and preserves the restored RAW");
            var finishedHash = Digest(output);

            var scheduler = (SemaphoreSlim)panel.GetType().Assembly.GetType("HostImageSource", true)!
                .GetField("SchedulerSlot", Flags)!.GetValue(null)!;
            var held = false;
            var probeOutput = Path.Combine(folder, "unfinished-probe.png");
            object? descriptor = null;
            var export = Save((_, _) =>
            {
                held = scheduler.Wait(0);
                if (!held) throw new InvalidOperationException("The fixture could not reserve the development scheduler.");
                return probeOutput;
            });
            try
            {
                var deadline = Stopwatch.GetTimestamp();
                while (!export.IsCompleted && Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(15))
                {
                    if (Call(jobs, "ReadLatest") is { } saved && Prop(saved, "Stage")?.ToString() == "DevelopingSource")
                    {
                        descriptor = Prop(saved, "Retry");
                        break;
                    }
                    await Task.Delay(25);
                }
                Check(held && descriptor is not null && Equals(Prop(descriptor, "Original"), originalHash),
                    "the new accepted job durably identifies the restored RAW before it develops");
                try
                {
                    using var writer = File.Open(chart, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    throw new InvalidOperationException("The original was writable during an accepted export.");
                }
                catch (IOException failure) when ((failure.HResult & 0xffff) == 32)
                {
                    Check(true, "Windows refuses another writer while the approved full-depth RAW is in use");
                }
                Call(Field(spatial, "exportWork")!, "Cancel");
            }
            finally { if (held) scheduler.Release(); }
            Check(!await export.WaitAsync(TimeSpan.FromSeconds(45)) && !File.Exists(probeOutput),
                "the descriptor fixture canceled before developing or publishing another image");
            var interrupted = Call(jobs, "Accept", (Guid)Prop(completed, "VariantId")!, output)!;
            interrupted = Call(jobs, "RecordRetry", interrupted, descriptor!)!;
            var retryField = spatial.GetType().GetField("interruptedExport", Flags)!;
            retryField.SetValue(spatial, interrupted);
            var resumed = Path.Combine(folder, "verified-raw-retry.png");
            var retryOffered = false;
            Func<SaveFileDialog, Window?, string?> pickRetry = (_, _) => { retryOffered = true; return resumed; };
            ReplaceOriginal();
            Check(!await Retry().WaitAsync(TimeSpan.FromSeconds(45)) && !retryOffered && !File.Exists(resumed)
                && Equals(Prop(Call(jobs, "ReadLatest")!, "JobId"), Prop(interrupted, "JobId"))
                && ((TextBlock)Field(spatial, "exportStatus")!).Text.Contains("original image contents changed", StringComparison.Ordinal),
                "verified retry refuses a replacement with the same size and write date before offering Save");
            RestoreOriginal();
            Check(await Retry().WaitAsync(TimeSpan.FromSeconds(45)) && retryOffered
                && Equals(Prop(Call(jobs, "ReadLatest")!, "RetryOf"), Prop(interrupted, "JobId"))
                && Digest(output) == finishedHash,
                "verified retry accepts the restored original and preserves the earlier 16-bit output");
            Check(!Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(), "the export checks leave no staged output behind");
            File.WriteAllText(Path.Combine(folder, "native-original-replacement.json"),
                JsonSerializer.Serialize(new { originalHash, replacementHash, outputHash = finishedHash, retryHash = Digest(resumed), png.PixelWidth, png.PixelHeight }));

            Task<bool> Retry() => (Task<bool>)Call(spatial, "RetryInterruptedExportAsync", pickRetry)!;
        }
        finally
        {
            owner.Close();
            RestoreOriginal();
        }
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native original replacement assertions");

        Task<bool> Save(Func<SaveFileDialog, Window?, string?> picker) =>
            (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, owner, picker)!;

        void ReplaceOriginal()
        {
            File.Move(chart, backup);
            File.Move(temp, chart);
            File.SetLastWriteTimeUtc(chart, originalTimestamp);
        }

        void RestoreOriginal()
        {
            if (!File.Exists(backup)) return;
            if (Digest(backup) != originalHash) throw new IOException("The disposable RAW backup did not verify.");
            if (File.Exists(chart)) File.Move(chart, temp);
            File.Move(backup, chart);
        }

        static string Digest(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
