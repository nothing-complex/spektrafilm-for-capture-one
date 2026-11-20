using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestInputProfileReplacement(Type host, object window, object target, object spatial)
    {
        var input = (ComboBox)Field(spatial, "inputSpace")!;
        var priorInput = input.SelectedItem;
        var quality = (ComboBox)Field(spatial, "previewSize")!;
        var priorQuality = quality.SelectedItem;
        var priorEnabled = Field(spatial, "enabled") is true;
        var selected = input.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "prophoto");
        var encoding = Prop(selected, "Space")!;
        var viewer = Field(spatial, "viewer")!;
        var status = (TextBlock)Field(spatial, "status")!;
        var runtime = Path.GetFullPath(Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!);
        Check(Path.GetDirectoryName(runtime) == Path.GetFullPath(folder), "input-profile fault only touches an isolated runtime copy");
        var path = Path.Combine(runtime, "profiles", "preview", "ProPhoto RGB.icc");
        var original = File.ReadAllBytes(path);
        var digest = Convert.ToHexString(SHA256.HashData(original));
        var backup = Path.Combine(folder, "profile-backup-" + File.ReadAllText(Path.Combine(folder, "phase.txt")) + ".icc");
        File.WriteAllBytes(backup, original);
        Check(File.ReadAllBytes(backup).SequenceEqual(original), "backed up the exact input ICC before injecting the failure");
        object? Source() => Field(spatial, "source");
        bool HasVerifiedImage(object? except = null) => Source() is { } frame && !ReferenceEquals(frame, except)
            && Prop(frame, "Description") is { } description && Equals(Prop(Prop(description, "Source")!, "ProfileSha256"), digest)
            && Prop(frame, "BitDepth") is 16 && Field(spatial, "sourceSize") is "2048"
            && Field(spatial, "frameCurrent") is true && Prop(viewer, "HasPainted") is true;
        async Task WaitFor(Func<bool> predicate, string detail)
        {
            var started = Stopwatch.GetTimestamp();
            while (!predicate())
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(18))
                    throw new TimeoutException(detail + " Last Viewer status was " + status.Text);
                await Task.Delay(60);
            }
        }
        try
        {
            quality.SelectedItem = quality.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "2048");
            input.SelectedItem = selected;
            Call(spatial, "SetEnabled", true);
            await WaitFor(() => HasVerifiedImage(), "A verified ProPhoto native Viewer frame did not paint before replacement.");
            await WaitFor(() => Field(spatial, "pending") is null && Field(spatial, "pendingDraft") is null,
                "The native source did not finish its outstanding work before replacement.");
            var first = Source()!;
            var bitmap = Field(spatial, "viewerFrame");
            var captured = host.GetMethod("Capture", Flags)!.Invoke(null, new[] { window, target, encoding, false })!;
            var corrupt = (byte[])original.Clone();
            corrupt[^1] ^= 1;
            File.WriteAllBytes(path, corrupt);
            var replaced = Stopwatch.GetTimestamp();
            await WaitFor(() => Source() is null && Field(spatial, "viewerFrame") is null && Prop(viewer, "HasPainted") is false
                && status.Text.Contains("invalid", StringComparison.OrdinalIgnoreCase), "The live Viewer did not discard its cached result after the input ICC changed.");
            var detection = Stopwatch.GetElapsedTime(replaced).TotalMilliseconds;
            Check(ReferenceEquals(input.SelectedItem, selected) && !ReferenceEquals(bitmap, Field(spatial, "viewerFrame")),
                "an externally changed input ICC suspends pixels while preserving the selected input encoding");
            try
            {
                await Task.Run(() => Call(captured, "RenderScaledPreview", 720, CancellationToken.None));
                throw new InvalidOperationException("A captured source used the replacement ICC with the old profile fingerprint.");
            }
            catch (TargetInvocationException error) when (error.GetBaseException() is IOException failure
                && failure.Message.Contains("profile changed", StringComparison.Ordinal))
            {
                Check(true, "an already captured render refuses to develop with a replacement profile");
            }
            File.WriteAllBytes(path, File.ReadAllBytes(backup));
            await WaitFor(() => HasVerifiedImage(first), "Restoring the original ICC did not automatically rebuild the preview.");
            Check(!ReferenceEquals(bitmap, Field(spatial, "viewerFrame")) && File.ReadAllBytes(path).SequenceEqual(original),
                "restoring the original profile automatically rebuilds a fresh matching 16-bit frame");
            var restored = Source()!;
            var originalDescription = Prop(restored, "Description")!;
            var resampler = host.Assembly.GetType("ViewerDraftSource", true)!.GetMethod("Downsample", Flags)!;
            var reduced = (await Task.Run(() => resampler.Invoke(null, new[] { restored, (object)720, CancellationToken.None, encoding })))!;
            var draft = Prop(reduced, "Description")!;
            Check(ReferenceEquals(Prop(draft, "Source"), Prop(originalDescription, "Source"))
                && Prop(draft, "Sampling")!.ToString() == "LinearLightArea" && Prop(draft, "Width") is 720
                && Call(draft, "Describes", 720, Prop(reduced, "Height"), 16, ((byte[])Prop(reduced, "FloatBytes")!).Length) is true,
                "the real RGB16 draft preserves exact profile and host identity and describes its reduced pixels");
            File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
                $"Input-profile replacement invalidation took {detection:F1} ms on the licensed Viewer.{Environment.NewLine}");
        }
        finally
        {
            if (!File.ReadAllBytes(path).SequenceEqual(original)) File.WriteAllBytes(path, File.ReadAllBytes(backup));
            input.SelectedItem = priorInput;
            quality.SelectedItem = priorQuality;
            Call(spatial, "SetEnabled", priorEnabled);
        }
    }
}
