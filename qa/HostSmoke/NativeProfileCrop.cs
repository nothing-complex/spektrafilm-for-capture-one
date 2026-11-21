using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestNativeProfileCrop(Assembly assembly, object panel, object spatial, object window, Guid variantId)
    {
        var host = assembly.GetType("HostImageSource", true)!;
        var profile = assembly.GetType("HostProfile", true)!;
        var selection = profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
        var variant = Prop(selection, "Variant")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Core")
            .GetType("P1.C1.Core.ICoreVariant", true)!;
        var crop = core.GetProperty("CropRawUnconstrained")!;
        var originalCrop = crop.GetValue(variant);
        var image = core.GetProperty("CoreImage")!.GetValue(variant)!;
        var dimensions = (Size)Prop(image, "ImageDimensions")!;
        var cameraProfile = core.GetProperty("ColorProfile")!;
        var original = cameraProfile.GetValue(variant);
        var sourcePath = Directory.EnumerateFiles(Path.Combine(folder, "ICC fixture inputs")).Single();
        var baseline = File.ReadAllBytes(sourcePath);
        var alternate = File.ReadAllBytes(Path.Combine(folder, "Alternate camera profile.icm"));
        Check(!baseline.SequenceEqual(alternate), "the isolated fixture uses two distinct camera ICC profiles");

        var quality = (ComboBox)Field(spatial, "previewSize")!;
        quality.SelectedItem = quality.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "1200");
        var viewer = Field(spatial, "viewer")!;
        var readRevision = host.GetMethod("GetRevision", Flags)!;
        object Revision() => readRevision.Invoke(null, new[] { window, (object)variantId })!;
        object Key(object frame) => Prop(Prop(Prop(frame, "Description")!, "Source")!, "AdjustmentKey")!;
        async Task<object> Painted(object? previousRevision = null)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20))
            {
                if (Field(spatial, "frameCurrent") is true && Prop(viewer, "HasPainted") is true
                    && Field(spatial, "sourceSize") is "1200" && Field(spatial, "source") is { } frame
                    && !Equals(Key(frame), previousRevision) && Equals(Key(frame), Revision()))
                    return frame;
                await Task.Delay(30);
            }
            throw new TimeoutException("The selected native ICC revision did not paint in the Viewer.");
        }
        async Task<object> StablePainted()
        {
            var frame = await Painted();
            var start = Stopwatch.GetTimestamp();
            var stableSince = start;
            while (Stopwatch.GetElapsedTime(stableSince) < TimeSpan.FromMilliseconds(1250))
            {
                if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(15))
                    throw new TimeoutException("The new camera ICC did not settle before the crop measurement.");
                await Task.Delay(40);
                var next = await Painted();
                if (!ReferenceEquals(frame, next)) stableSince = Stopwatch.GetTimestamp();
                frame = next;
            }
            return frame;
        }
        object Rectangle(float offset) => Activator.CreateInstance(crop.PropertyType, new object[]
        {
            new PointF(dimensions.Width * offset, dimensions.Height * .46f),
            new SizeF(dimensions.Width * .66f, dimensions.Height * .64f),
        })!;
        async Task MeasureCropSeries(object originalBitmap)
        {
            var measurements = new List<object>();
            var cropIdentity = spatial.GetType().GetField("sourceCropIdentity", Flags)!;
            var expected = ReadCropPaint(viewer, spatial);
            foreach (var reuse in new[] { true, false })
            {
                var repaintTimes = new List<double>();
                var developments = 0;
                var filmRenders = 0;
                for (var edit = 0; edit < 6; edit++)
                {
                    var prior = Field(spatial, "source")!;
                    var worker = Field(spatial, "renderer")!;
                    var request = Prop(worker, "LastRender");
                    if (!reuse) cropIdentity.SetValue(spatial, null);
                    var start = Stopwatch.GetTimestamp();
                    crop.SetValue(variant, Rectangle(edit % 2 == 0 ? .50f : .47f));
                    var current = await Painted(Key(prior));
                    var snapshot = ReadCropPaint(viewer, spatial);
                    while (snapshot.Transform == expected.Transform && snapshot.Clip == expected.Clip
                        && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(5))
                    {
                        await Task.Delay(15);
                        snapshot = ReadCropPaint(viewer, spatial);
                    }
                    repaintTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    var workerAfter = Field(spatial, "renderer")!;
                    if (!ReferenceEquals(Prop(prior, "FloatBytes"), Prop(current, "FloatBytes"))) developments++;
                    if (!ReferenceEquals(worker, workerAfter) || !Equals(request, Prop(workerAfter, "LastRender"))) filmRenders++;
                    var diff = CompareCropBitmaps((System.Windows.Media.Imaging.BitmapSource)originalBitmap, snapshot.Bitmap);
                    Check((snapshot.Transform != expected.Transform || snapshot.Clip != expected.Clip) && snapshot.ClipMatches
                        && diff.Percentile999 <= 1 && diff.Mean < .1, "the paired crop edit repaints with the same full-canvas optical samples");
                    expected = snapshot;
                }
                measurements.Add(new { reuse, repaintTimes, developments, filmRenders });
                File.WriteAllText(Path.Combine(folder, "crop-work-measurements.json"), JsonSerializer.Serialize(measurements));
                Check(developments == (reuse ? 0 : 6) && filmRenders == (reuse ? 0 : 6),
                    $"camera-profile crop benchmark observed {developments} new source buffers and {filmRenders} new film receipts (reuse {reuse})");
            }
        }

        string? fixtureSidecar = null;
        object? fixtureId = null;
        try
        {
            cameraProfile.SetValue(variant, Activator.CreateInstance(cameraProfile.PropertyType, "Spektrafilm fixture ICC", baseline));
            var id = fixtureId = LocalIccIds(variant).Single();
            var file = (string)Prop(id, "FileName")!;
            var undo = Prop(id, "UndoID");
            fixtureSidecar = Path.Combine(folder, "Adjustments", "ICC", file);
            Directory.CreateDirectory(Path.GetDirectoryName(fixtureSidecar)!);
            if (!File.Exists(fixtureSidecar)) File.WriteAllBytes(fixtureSidecar, baseline);
            byte[] ReadIcc() => (byte[])Prop(Call(variant, "GetICCSideCarGivenID", id)!, "rawData")!;
            Check(ReadIcc().SequenceEqual(baseline), "the native variant resolves its isolated original camera ICC");
            ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
            var before = await StablePainted();
            var initialBitmap = Field(spatial, "viewerFrame");
            crop.SetValue(variant, Rectangle(.47f));
            var cropped = await Painted(Key(before));
            Check(!Equals(Key(before), Key(cropped)) && ReferenceEquals(Prop(before, "FloatBytes"), Prop(cropped, "FloatBytes"))
                && ReferenceEquals(initialBitmap, Field(spatial, "viewerFrame")),
                $"the Viewer reuses both buffers when cropping with the unchanged local ICC (before {Key(before)}, after {Key(cropped)})");
            await MeasureCropSeries(initialBitmap!);
            cropped = await Painted();
            var originalIdentity = Field(spatial, "sourceCropIdentity")!;
            var oldRevision = Key(cropped);
            var lastOptical = Field(spatial, "viewerFrame");

            File.WriteAllBytes(fixtureSidecar, alternate);
            Call(variant, "UnloadICCFromMemory", id);
            var liveId = LocalIccIds(variant).Single();
            Check(ReadIcc().SequenceEqual(alternate) && Equals(Prop(liveId, "FileName"), file) && Equals(Prop(liveId, "UndoID"), undo),
                "Capture One resolves alternate camera ICC bytes under the same local profile ID");
            var scheduler = (SemaphoreSlim)host.GetField("SchedulerSlot", Flags)!.GetValue(null)!;
            Check(await scheduler.WaitAsync(TimeSpan.FromSeconds(5)), "the fixture can hold native development after the new crop");
            object offered;
            try
            {
                crop.SetValue(variant, Rectangle(.53f));
                var afterEdit = Revision();
                var settings = Prop(variant, "Settings")!;
                var modelCounters = Prop(settings, "VariantProcessCounters")!;
                var geometryOnly = Activator.CreateInstance(modelCounters.GetType(), Prop(oldRevision, "HighRes")!,
                    Prop(oldRevision, "LowRes")!, Prop(afterEdit, "Geometry")!)!;
                Call(settings, "InitializeProcessCounters", geometryOnly);
                offered = Revision();
                Check(Equals(Prop(offered, "HighRes"), Prop(oldRevision, "HighRes"))
                    && Equals(Prop(offered, "LowRes"), Prop(oldRevision, "LowRes"))
                    && (long)Prop(offered, "Geometry")! > (long)Prop(oldRevision, "Geometry")!,
                    "the combined crop and ICC replacement presents only a geometry counter change");
                var start = Stopwatch.GetTimestamp();
                while (Field(spatial, "pending") is null && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(6))
                    await Task.Delay(25);
                Check(Field(spatial, "pending") is not null && Field(spatial, "frameCurrent") is false
                    && ReferenceEquals(lastOptical, Field(spatial, "viewerFrame"))
                    && ((Button)Field(spatial, "savePreview")!).IsEnabled is false,
                    "the previous bitmap remains visible and Save Preview stays disabled while native development is held");
            }
            finally { scheduler.Release(); }

            var replaced = await Painted(oldRevision);
            var newIdentity = Field(spatial, "sourceCropIdentity")!;
            Check(!ReferenceEquals(Prop(cropped, "FloatBytes"), Prop(replaced, "FloatBytes"))
                && !ReferenceEquals(lastOptical, Field(spatial, "viewerFrame"))
                && Equals(Prop(Prop(originalIdentity, "Model")!, "PixelSettingsSha256"),
                    Prop(Prop(newIdentity, "Model")!, "PixelSettingsSha256"))
                && !Equals(Prop(originalIdentity, "Profiles"), Prop(newIdentity, "Profiles")),
                "only the resolved ICC fingerprint changes the pixel identity and the Viewer develops fresh buffers");
            async Task<PixelDifference> CompareWithFreshDevelopment(object frame)
            {
                var target = profile.GetMethod("CaptureTarget", Flags)!.Invoke(null, new[] { window })!;
                var description = Prop(frame, "Description")!;
                var encoding = Prop(Prop(description, "Source")!, "InputEncoding")!;
                var independent = host.GetMethod("Capture", Flags)!.Invoke(null, new[] { window, target, encoding, false })!;
                var reference = (await Task.Run(() => Call(independent, "RenderScaledPreview", 1200, CancellationToken.None)))!;
                var difference = CompareSamples(frame, reference);
                Check(Prop(frame, "BitDepth") is 16 && Prop(reference, "BitDepth") is 16
                    && difference.Mean < 4d / 65535 && difference.Percentile999 < 16d / 65535,
                    $"the Viewer matches independent RGB16 development with the current ICC ({difference})");
                return difference;
            }
            var sameIdChange = CompareSamples(cropped, replaced);
            var repeat = await CompareWithFreshDevelopment(replaced);
            var paint = ReadCropPaint(viewer, spatial);
            Check(paint.ClipMatches && paint.CornerError <= .55, "the rebuilt film preview uses the exact native crop clip and geometry");

            File.WriteAllBytes(fixtureSidecar, baseline);
            Call(variant, "UnloadICCFromMemory", id);
            Check(ReadIcc().SequenceEqual(baseline), "the isolated camera ICC was restored byte for byte");
            cameraProfile.SetValue(variant, Activator.CreateInstance(cameraProfile.PropertyType, "Spektrafilm alternate ICC", alternate));
            var selectedProfile = await Painted(Key(replaced));
            var selectedProfileChange = CompareSamples(cropped, selectedProfile);
            Check(!ReferenceEquals(Prop(replaced, "FloatBytes"), Prop(selectedProfile, "FloatBytes")) && selectedProfileChange.Mean > .002,
                $"explicitly selecting the alternate native camera ICC changes the developed RGB16 pixels ({selectedProfileChange})");
            var selectedProfileRepeat = await CompareWithFreshDevelopment(selectedProfile);
            File.WriteAllText(Path.Combine(folder, "native-profile-crop.json"), JsonSerializer.Serialize(new
            {
                baselineSha256 = Convert.ToHexString(SHA256.HashData(baseline)),
                alternateSha256 = Convert.ToHexString(SHA256.HashData(alternate)),
                prior = RevisionRecord(oldRevision), offered = RevisionRecord(offered), settled = RevisionRecord(Key(replaced)),
                sameIdChange, repeat, selectedProfileChange, selectedProfileRepeat,
            }));
        }
        finally
        {
            if (fixtureSidecar is not null && File.Exists(fixtureSidecar) && !File.ReadAllBytes(fixtureSidecar).SequenceEqual(baseline))
            {
                File.WriteAllBytes(fixtureSidecar, baseline);
                Call(variant, "UnloadICCFromMemory", fixtureId);
            }
            crop.SetValue(variant, originalCrop);
            cameraProfile.SetValue(variant, original);
        }
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native Viewer crop profile assertions");
    }
}
