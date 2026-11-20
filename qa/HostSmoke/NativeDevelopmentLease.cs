using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
    private static async Task TestNativeDevelopmentLease(Assembly assembly, object spatial, object window)
    {
        var host = assembly.GetType("HostImageSource", true)!;
        var entered = (ManualResetEventSlim)host.GetField("DevelopmentLeaseEntered", Flags)!.GetValue(null)!;
        var release = (ManualResetEventSlim)host.GetField("DevelopmentLeaseContinue", Flags)!.GetValue(null)!;
        var armed = host.GetField("developmentLeaseArmed", Flags)!;
        var slot = (SemaphoreSlim)host.GetField("SchedulerSlot", Flags)!.GetValue(null)!;
        Call(spatial, "SetEnabled", false);
        using (var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await (Task)Call(spatial, "DrainPreviewWorkersAsync", drain.Token)!;
        await WaitFor(() => slot.CurrentCount == 1 && Field(spatial, "pending") is null && Field(spatial, "pendingDraft") is null,
            "A previous source still owns the native development slot.");
        var runtime = Path.GetFullPath(Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!);
        Check(Path.GetDirectoryName(runtime) == Path.GetFullPath(folder), "the lease test targets only a private working ICC copy");
        var profile = Path.Combine(runtime, "profiles", "preview", "ProPhoto RGB.icc");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profile)));
        bool CanOpenForWrite()
        {
            try
            {
                using var file = new FileStream(profile, FileMode.Open, FileAccess.Write, FileShare.Read);
                return file.CanWrite;
            }
            catch (IOException) { return false; }
        }
        Check(CanOpenForWrite(), "the fixture proves the private ICC has no earlier write lease");
        var encoding = Enum.Parse(assembly.GetType("PreviewInputSpace", true)!, "ProPhotoRgb");
        Task<object?> DevelopCurrent()
        {
            var target = assembly.GetType("HostProfile", true)!.GetMethod("CaptureTarget", Flags)!.Invoke(null, new[] { window })!;
            var snapshot = host.GetMethod("Capture", Flags)!.Invoke(null, new[] { window, target, encoding, false })!;
            return Task.Run(() => Call(snapshot, "RenderScaledPreview", 480, CancellationToken.None));
        }
        var clock = Stopwatch.GetTimestamp();
        double deadlineMs;
        bool heldSlot, heldProfile;
        try
        {
            armed.SetValue(null, 1);
            var work = DevelopCurrent();
            await WaitFor(() => entered.IsSet || work.IsCompleted, "The real native development did not reach the disposable release gate.");
            Check(entered.IsSet && slot.CurrentCount == 0 && !CanOpenForWrite(),
                "the completed native bitmap callback holds the slot and ICC before its caller times out");
            try
            {
                await work.WaitAsync(TimeSpan.FromSeconds(8));
                throw new InvalidOperationException("A callback paused beyond the deadline returned a frame.");
            }
            catch (TargetInvocationException error) when (error.GetBaseException().GetType().Name == "DevelopmentDeadlineException") { }
            deadlineMs = Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
            heldSlot = slot.CurrentCount == 0;
            heldProfile = !CanOpenForWrite();
        }
        finally { release.Set(); }
        await WaitFor(() => slot.CurrentCount == 1 && CanOpenForWrite(),
            "Finishing the late native callback did not release both resources.");
        var fresh = await DevelopCurrent();
        Check(fresh is not null && Prop(fresh, "BitDepth") is 16 && Prop(fresh, "Description") is { } description
            && Equals(Prop(Prop(description, "Source")!, "ProfileSha256"), hash),
            "the next native request still develops the captured high-depth ICC source");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profile))) == hash,
            "the private ICC kept all its bytes throughout the test");
        File.WriteAllText(Path.Combine(folder, "native-development-lease.json"), JsonSerializer.Serialize(new
        {
            deadlineMs, heldSlot, heldProfile, profileSha256 = hash,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(heldSlot && heldProfile, "a late native callback retains both the scheduler slot and input ICC after its requester times out");
        await TestLateViewerOwnership();
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native development lease assertions");

        async Task TestLateViewerOwnership()
        {
            var input = (ComboBox)Field(spatial, "inputSpace")!;
            var quality = (ComboBox)Field(spatial, "previewSize")!;
            var draft = (ComboBox)Field(spatial, "dragPreviewSize")!;
            var viewer = Field(spatial, "viewer")!;
            void Size(string id) => quality.SelectedItem = quality.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") as string == id);
            bool Painted(string id) => Field(spatial, "sourceSize") is string size && size == id
                && Field(spatial, "frameCurrent") is true && Prop(viewer, "HasPainted") is true && Field(spatial, "pending") is null;
            input.SelectedItem = input.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "prophoto");
            draft.SelectedItem = draft.ItemsSource.Cast<object>().Single(item => Prop(item, "LongEdge") is 0);
            Size("720");
            Call(spatial, "SetEnabled", true);
            await WaitFor(() => Painted("720"), "The licensed Viewer did not paint before the output-ownership test.");
            var oldFrame = Field(spatial, "viewerFrame");
            entered.Reset();
            release.Reset();
            var destination = Path.Combine(folder, "export-after-late-viewer.tif");
            Task<bool> job;
            try
            {
                armed.SetValue(null, 1);
                Size("900");
                await WaitFor(() => entered.IsSet, "The Viewer did not reach the delayed native bitmap boundary.");
                job = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, null,
                    (Func<SaveFileDialog, Window?, string?>)((_, _) => destination))!;
                await WaitFor(() => !job.IsCompleted && Field(spatial, "pending") is null && Field(spatial, "exportWork") is not null,
                    "The accepted export did not cancel the old request and reserve output rendering.");
                Check(slot.CurrentCount == 0 && !CanOpenForWrite() && !File.Exists(destination)
                    && Field(spatial, "frameCurrent") is false && ReferenceEquals(oldFrame, Field(spatial, "viewerFrame")),
                    "an accepted recipe export waits for the late native slot without publishing the canceled Viewer frame");
            }
            finally { release.Set(); }
            Check(await job.WaitAsync(TimeSpan.FromSeconds(35)), "the accepted export finished after the late native callback unlocked");
            using var image = File.OpenRead(destination);
            var frame = BitmapDecoder.Create(image, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Check(frame.Format == PixelFormats.Rgb48 && frame.ColorContexts is { Count: 1 },
                "the export waiting for the native slot reopens as an ICC-tagged 16-bit TIFF");
            await WaitFor(() => Painted("900") && !ReferenceEquals(oldFrame, Field(spatial, "viewerFrame")),
                "The current Viewer did not repaint after output ownership ended.");
            Check(CanOpenForWrite() && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profile))) == hash,
                "the Viewer and output release the private ICC without writing any profile bytes");

            entered.Reset();
            release.Reset();
            try
            {
                armed.SetValue(null, 1);
                Size("1200");
                await WaitFor(() => entered.IsSet, "The Viewer did not reach the native bitmap boundary before detach.");
                var hook = assembly.GetType("StartupHook", true)!;
                hook.GetMethod("Detach", Flags)!.Invoke(null, new object[] { false });
                await WaitFor(() => Field(spatial, "pending") is null, "Native detach left its Viewer request pending.");
                Check(Field(spatial, "disposed") is true && Field(spatial, "viewerFrame") is null
                    && Prop(viewer, "HasPainted") is false && slot.CurrentCount == 0 && !CanOpenForWrite(),
                    "detaching disposes the active Viewer while the canceled callback keeps its native resources");
            }
            finally { release.Set(); }
            await WaitFor(() => slot.CurrentCount == 1 && CanOpenForWrite(),
                "Detaching left the late callback's native slot or ICC locked.");
            Check(Field(spatial, "viewerFrame") is null && Field(spatial, "pending") is null && Field(spatial, "renderer") is null,
                "a native callback that finishes after detach cannot republish into the old panel");
        }

        static async Task WaitFor(Func<bool> condition, string error)
        {
            var started = Stopwatch.GetTimestamp();
            while (!condition())
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(8)) throw new TimeoutException(error);
                await Task.Delay(20);
            }
        }
    }
}
