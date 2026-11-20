using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestNativeSourceMutations(Assembly assembly, object panel, object spatial, object window)
    {
        var selection = new FixtureSelection(panel);
        Check(selection.Variants.Length == 4, "native source mutation matrix uses the real four-format RAW catalog");
        var quality = (ComboBox)Field(spatial, "previewSize")!;
        quality.SelectedItem = quality.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "1200");
        var input = (ComboBox)Field(spatial, "inputSpace")!;
        input.SelectedItem = input.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "srgb");
        var enabled = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var viewer = Field(spatial, "viewer")!;
        var host = assembly.GetType("HostImageSource", true)!;
        var profile = assembly.GetType("HostProfile", true)!;
        var capture = host.GetMethod("Capture", Flags)!;
        var revision = host.GetMethod("GetRevision", Flags)!;
        var encoding = Prop(input.SelectedItem, "Space")!;
        var native = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Core")
            .GetType("P1.C1.Core.ICoreVariant", true)!;
        var results = new List<object>();
        var unexpected = new List<string>();

        object Key(object frame) => Prop(Prop(Prop(frame, "Description")!, "Source")!, "AdjustmentKey")!;
        async Task<object> WaitForSource(Guid id)
        {
            var start = Stopwatch.GetTimestamp();
            while (true)
            {
                if (Field(spatial, "frameCurrent") is true && Prop(viewer, "HasPainted") is true
                    && Field(spatial, "sourceSize") is "1200" && Field(spatial, "source") is { } frame
                    && Equals(Key(frame), revision.Invoke(null, new[] { window, (object)id })))
                    return frame;
                if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(20))
                    throw new TimeoutException("The live Viewer did not paint the native edit's current source.");
                await Task.Delay(30);
            }
        }

        foreach (var id in selection.Variants)
        {
            selection.Select(id, id);
            enabled.IsChecked = true;
            var format = selection.Format(id);
            var target = profile.GetMethod("CaptureTarget", Flags)!.Invoke(null, new[] { window })!;
            var selected = profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
            var nativeVariant = Prop(selected, "Variant")!;
            var original = await WaitForSource(id);

            async Task Measure(string label, Action apply, Action restore)
            {
                try
                {
                    apply();
                    var expected = revision.Invoke(null, new[] { window, (object)id });
                    Check(!Equals(Key(original), expected), $"{format} {label} changes Capture One's actual revision counters");
                    var after = await WaitForSource(id);
                    Check(!ReferenceEquals(after, original) && Equals(Key(after), expected),
                        $"{format} {label} replaces the old developed source before publishing a settled frame");
                    var snapshot = capture.Invoke(null, new[] { window, target, encoding, false })!;
                    var reference = (await Task.Run(() => Call(snapshot, "RenderScaledPreview", 1200, CancellationToken.None)))!;
                    var width = (int)Prop(after, "Width")!;
                    var height = (int)Prop(after, "Height")!;
                    Check(Equals(Prop(after, "Description"), Prop(reference, "Description")) && Prop(after, "BitDepth") is 16,
                        $"{format} {label} matches the fresh native development identity, dimensions, format and profile");
                    var repeated = (await Task.Run(() => Call(snapshot, "RenderScaledPreview", 1200, CancellationToken.None)))!;
                    var cachedError = CompareSamples(after, reference);
                    var nativeRepeat = CompareSamples(reference, repeated);
                    // Independent developments produce sparse outliers, especially with Capture One grain.
                    // Bound the tail and mean as well as the peak, and compare the pixel change with the edit signal.
                    if (cachedError.Percentile999 > Math.Max(16d / 65535, 2 * nativeRepeat.Percentile999)
                        || cachedError.Mean > Math.Max(1d / 65535, 2 * nativeRepeat.Mean)
                        || cachedError.Maximum > Math.Max(1d / 128, 2 * nativeRepeat.Maximum))
                        unexpected.Add($"{format} {label}: cached difference {cachedError}, native repeat {nativeRepeat}");
                    else
                        Check(true, $"{format} {label} cached source matches fresh native development within native repeatability");
                    var change = CompareSamples(original, after);
                    Check(label == "asymmetric crop"
                            ? change.Mean <= Math.Max(1d / 65535, 2 * nativeRepeat.Mean)
                            : change.Mean > 20 * Math.Max(cachedError.Mean, 1d / 65535),
                        $"{format} {label} has the expected signal in the full-canvas source");
                    results.Add(new { format, label, beforeRevision = RevisionRecord(Key(original)), afterRevision = RevisionRecord(Key(after)),
                        width, height, cachedError, nativeRepeat, change });
                }
                finally { restore(); }
                original = await WaitForSource(id);
            }

            async Task Set(string label, PropertyInfo property, object value) =>
                await ApplyChanges(label, (property, value));

            async Task ApplyChanges(string label, params (PropertyInfo Property, object Value)[] changes)
            {
                var originals = changes.Select(change => (change.Property, change.Property.GetValue(nativeVariant))).ToArray();
                await Measure(label, () =>
                {
                    foreach (var change in changes) change.Property.SetValue(nativeVariant, change.Value);
                }, () =>
                {
                    foreach (var previous in originals.Reverse()) previous.Property.SetValue(nativeVariant, previous.Item2);
                });
            }

            var exposure = native.GetProperty("Exposure")!;
            await Set("exposure", exposure, (float)exposure.GetValue(nativeVariant)! + .5f);
            var crop = native.GetProperty("CropRawUnconstrained")!;
            var oldCrop = crop.GetValue(nativeVariant)!;
            var size = (SizeF)Prop(oldCrop, "Size")!;
            var center = (PointF)Prop(oldCrop, "Center")!;
            if (size.Width <= 0 || size.Height <= 0)
            {
                var image = native.GetProperty("CoreImage")!.GetValue(nativeVariant)!;
                var dimensions = (Size)Prop(image, "ImageDimensions")!;
                size = dimensions;
                center = new PointF(size.Width / 2, size.Height / 2);
            }
            var cropValue = Activator.CreateInstance(crop.PropertyType,
                new object[] { new PointF(center.X + size.Width * .04f, center.Y), new SizeF(size.Width * .72f, size.Height * .71f) })!;
            await Set("asymmetric crop", crop, cropValue);
            var grain = native.GetProperty("FilmGrainAmount")!;
            var grainType = native.GetProperty("FilmGrainType")!;
            var amount = (float)grain.GetValue(nativeVariant)!;
            await ApplyChanges("native grain", (grainType, Enum.ToObject(grainType.PropertyType, 1)), (grain, amount > 50 ? 0f : 85f));
            var lens = native.GetProperty("LensGeometricAberrationAmount")!;
            if (native.GetProperty("IsLensCorrectionSupported")!.GetValue(nativeVariant) is true
                && native.GetProperty("LensGeometricAberrationValid")!.GetValue(nativeVariant) is true)
                await Set("lens distortion", lens, (float)lens.GetValue(nativeVariant)! > .01f ? 0f : 1f);
            else
                results.Add(new { format, label = "lens distortion", status = "no-active-native-lens-correction" });
        }
        File.WriteAllText(Path.Combine(folder, "native-source-mutations.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Check(unexpected.Count == 0, "native source mutation parity: " + string.Join("; ", unexpected));
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native source mutation assertions");
    }

    private static object RevisionRecord(object key) => new
    {
        high = Prop(key, "HighRes"), low = Prop(key, "LowRes"), geometry = Prop(key, "Geometry"),
    };

    private sealed record PixelDifference(double Maximum, double Mean, double Percentile999, int ChangedSamples);

    private static PixelDifference CompareSamples(object first, object second)
    {
        var width = (int)Prop(first, "Width")!;
        var height = (int)Prop(first, "Height")!;
        if (!Equals(width, Prop(second, "Width")) || !Equals(height, Prop(second, "Height"))) return new(1, 1, 1, -1);
        var a = (byte[])Prop(first, "FloatBytes")!;
        var b = (byte[])Prop(second, "FloatBytes")!;
        double sum = 0;
        var changed = 0;
        var samples = new List<double>();
        for (var y = 16; y < height - 16; y += 13)
        for (var x = 16; x < width - 16; x += 17)
        for (var channel = 0; channel < 4; channel++)
        {
            var offset = ((y * width + x) * 4 + channel) * sizeof(float);
            var error = Math.Abs(BitConverter.ToSingle(a, offset) - BitConverter.ToSingle(b, offset));
            samples.Add(error);
            sum += error;
            if (error > 1d / 65535) changed++;
        }
        samples.Sort();
        var tail = Math.Max(0, (int)Math.Ceiling(samples.Count * .999) - 1);
        return new(samples[^1], sum / samples.Count, samples[tail], changed);
    }
}
