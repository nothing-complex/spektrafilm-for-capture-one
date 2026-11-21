using System;
using System.Collections;
using System.Collections.Generic;
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
using System.Windows.Media.Imaging;

public static partial class StartupHook
{
    private sealed record ChangeReceipt(Guid Variant, bool Enabled, double[] Values, string FileSha256, string? JournalSha256);
    private sealed record EventTimes(double P50, double P95, double P99, double Max);

    private static async Task TestDurableSliderEdits(object panel, object spatial, object store)
    {
        const int measuredEdits = 10_000;
        const int gestureLength = 100;
        const int activeEditCadenceMs = 20;
        var journalSaves = Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_JOURNAL_SAVES") == "1";
        var activeViewer = Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_DURABLE_VIEWER") == "1";
        if (journalSaves && Field(store, "useGenerationWriter") is not true)
            throw new InvalidOperationException("The native panel did not enable the journal fixture for its disposable catalog.");
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var report = Path.Combine(folder, "last-acknowledged.json");
        var selection = new FixtureSelection(panel);
        Check(selection.Variants.Length == 4, "four independently saved real RAW variants available for slider durability");
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var saveError = (TextBlock)Field(panel, "saveError")!;
        var viewer = Field(spatial, "viewer")!;
        var values = new[] { "exposure", "printExposure", "strength" }.Select(name => Field(panel, name)!).ToArray();
        var valueProperties = values.Select(slider => slider.GetType().GetProperty("Value", Flags)!).ToArray();
        var storedProperties = new[] { "Exposure", "PrintExposure", "Strength" };
        double ReadSlider(int slot) => (double)valueProperties[slot].GetValue(values[slot])!;
        double StoredValue(int slot) => slot == 2 ? ReadSlider(slot) / 100 : ReadSlider(slot);
        ChangeReceipt CaptureReceipt(Guid variant)
        {
            var saved = Call(store, "Read", variant)!;
            var look = Prop(saved, "Look")!;
            var committed = storedProperties.Select(property => (double)Prop(look, property)!).ToArray();
            if (!committed.SequenceEqual(Enumerable.Range(0, values.Length).Select(StoredValue))
                || !Equals(Prop(saved, "Enabled"), toggle.IsChecked) || Prop(store, "HasPending") is not false
                || saveError.Visibility == Visibility.Visible)
                throw new InvalidOperationException("A native acknowledgement did not match its durable slider or master state.");
            var path = (string)Call(store, "GetPath", variant)!;
            string? journalHash = null;
            if (journalSaves)
            {
                var journal = Field(store, "generationJournal")!;
                var journalPath = (string)Call(journal, "PathFor", variant)!;
                var acknowledged = Call(journal, "Read", variant)!;
                var snapshot = (byte[])Prop(acknowledged, "Current")!;
                Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(snapshot), "the actual WPF edit has a matching durable journal and look file");
                journalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(journalPath)));
                Check(journalHash == (string)Prop(acknowledged, "Receipt")!, "the saved journal receipt verifies its exact bytes");
            }
            return new ChangeReceipt(variant, toggle.IsChecked == true, committed,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), journalHash);
        }

        if (phase == "restart")
        {
            var expected = JsonSerializer.Deserialize<ChangeReceipt[]>(File.ReadAllText(report))!;
            Check(expected.Length == selection.Variants.Length, "pre-crash acknowledgement receipt covers all RAW variants");
            foreach (var receipt in expected)
            {
                selection.Select(receipt.Variant, receipt.Variant);
                var restored = CaptureReceipt(receipt.Variant);
                Check(restored.Variant == receipt.Variant && restored.Enabled == receipt.Enabled
                    && restored.Values.SequenceEqual(receipt.Values) && restored.FileSha256 == receipt.FileSha256
                    && restored.JournalSha256 == receipt.JournalSha256,
                    "forced restart restores the last acknowledged slider values, master switch and exact receipt bytes");
            }
            File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native slider recovery assertions");
            return;
        }

        var eventTimes = new double[measuredEdits];
        var selectionTimes = new double[measuredEdits / gestureLength];
        var receipts = new Dictionary<Guid, ChangeReceipt>();
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        if (activeViewer)
        {
            var resolution = (ComboBox)Field(spatial, "previewSize")!;
            var draft = (ComboBox)Field(spatial, "dragPreviewSize")!;
            resolution.SelectedItem = resolution.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "1200");
            draft.SelectedItem = draft.ItemsSource.Cast<object>().Single(item => Prop(item, "LongEdge") is 720);
        }
        var settledPaints = 0;
        var dragPaints = 0;
        for (var gesture = 0; gesture < selectionTimes.Length; gesture++)
        {
            var variant = selection.Variants[gesture % selection.Variants.Length];
            var started = Stopwatch.GetTimestamp();
            selection.Select(variant, variant);
            selectionTimes[gesture] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            toggle.IsChecked = activeViewer || (gesture / selection.Variants.Length) % 2 == 0;
            BitmapSource? startFrame = null;
            if (activeViewer)
                startFrame = await WaitForCurrentPaint(variant, previous: null);
            else
                Call(spatial, "SetEnabled", false);
            var dragFrame = startFrame;
            Call(spatial, "BeginSliderDrag");
            try
            {
                for (var offset = 0; offset < gestureLength; offset++)
                {
                    var index = gesture * gestureLength + offset;
                    var slot = index % values.Length;
                    var next = slot == 2 ? 1 + (index * 37 % 99) : (index * 37 % 97 - 48) / 10d;
                    if (next == ReadSlider(slot)) next += slot == 2 ? 1 : .1;
                    var tick = Stopwatch.GetTimestamp();
                    valueProperties[slot].SetValue(values[slot], next);
                    eventTimes[index] = Stopwatch.GetElapsedTime(tick).TotalMilliseconds;
                    var saved = Call(store, "Read", variant)!;
                    if (Math.Abs(ReadSlider(slot) - next) > 1e-9
                        || !Equals(Prop(Prop(saved, "Look")!, storedProperties[slot]), StoredValue(slot))
                        || Prop(store, "HasPending") is not false || saveError.Visibility == Visibility.Visible)
                        throw new InvalidOperationException($"Edit {index + 1} was not acknowledged with a matching committed value.");
                    if (activeViewer)
                    {
                        await Task.Delay(activeEditCadenceMs);
                        var painted = PaintedFrame(variant);
                        if (painted is not null && !ReferenceEquals(dragFrame, painted))
                        {
                            dragPaints++;
                            dragFrame = painted;
                        }
                    }
                }
            }
            finally { Call(spatial, "EndSliderDrag"); }
            if (activeViewer)
            {
                await WaitForCurrentPaint(variant, startFrame);
                settledPaints++;
            }
            receipts[variant] = CaptureReceipt(variant);
            await Task.Delay(1);
        }
        if (activeViewer)
        {
            Check(dragPaints > 0 && settledPaints == selectionTimes.Length, "the native Viewer draws during gestures and settles each edited variant");
            // Keep both master states in the restart receipt after the active run.
            for (var index = 0; index < selection.Variants.Length; index++)
            {
                var variant = selection.Variants[index];
                selection.Select(variant, variant);
                toggle.IsChecked = index % 2 == 0;
                receipts[variant] = CaptureReceipt(variant);
            }
        }
        Check(receipts.Count == 4 && !Directory.EnumerateFiles(Path.Combine(folder, "Variant looks"), ".spektra-*.tmp",
            SearchOption.AllDirectories).Any(), "all 10,000 native changes matched committed values without stranded staged files");
        var more = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(report, JsonSerializer.Serialize(receipts.Values, more));
        File.WriteAllText(Path.Combine(folder, "edit-latency.json"), JsonSerializer.Serialize(new
        {
            measuredEdits,
            gestureLength,
            imageVisits = selectionTimes.Length,
            variants = receipts.Count,
            journalSaves,
            activeViewer,
            editCadenceMs = activeViewer ? activeEditCadenceMs : 0,
            dragPaints,
            settledPaints,
            SliderEventAndSaveMs = Percentiles(eventTimes),
            SelectionAndLoadMs = Percentiles(selectionTimes),
            Scope = activeViewer ? "Real native WPF change and save while Viewer rendering is active. The 20 ms scheduling interval is outside the measurement."
                : "Real native WPF change and save with Viewer rendering paused.",
        }, more));
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native slider durability assertions; {measuredEdits} individually checked writes");

        BitmapSource? PaintedFrame(Guid variant) => Prop(viewer, "HasPainted") is true && Field(viewer, "attachment") is { } attachment
            && Equals(Prop(attachment, "VariantId"), variant) && Field(spatial, "viewerFrame") is BitmapSource bitmap
            && ReferenceEquals(Prop(attachment, "PaintedBitmap"), bitmap) ? bitmap : null;

        async Task<BitmapSource> WaitForCurrentPaint(Guid variant, BitmapSource? previous)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20))
            {
                if (Field(spatial, "frameCurrent") is true && Field(spatial, "pending") is null
                    && PaintedFrame(variant) is { } frame && !ReferenceEquals(previous, frame))
                    return frame;
                await Task.Delay(15);
            }
            throw new TimeoutException("The active durability fixture did not paint a settled Viewer frame for the selected variant.");
        }
    }

    private static EventTimes Percentiles(double[] samples)
    {
        Array.Sort(samples);
        double At(double percentile) => samples[(int)Math.Ceiling(percentile * samples.Length) - 1];
        return new EventTimes(At(.50), At(.95), At(.99), samples[^1]);
    }

    private sealed class FixtureSelection
    {
        private readonly object? panel;
        private readonly object selection;
        private readonly object target;
        private readonly MethodInfo selectOnly;
        private readonly MethodInfo selectPrimary;
        private readonly PropertyInfo primaryVariant;
        private readonly PropertyInfo uuid;
        private readonly PropertyInfo coreImage;
        private readonly PropertyInfo sourcePath;
        private readonly Dictionary<Guid, object> variants;
        internal Guid[] Variants { get; }

        internal FixtureSelection(object panel, int limit = 4) : this(Field(panel, "document")!, panel, limit) { }

        internal FixtureSelection(object document, object? panel, int limit = 4)
        {
            this.panel = panel;
            var core = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.Core");
            var provider = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
            var collection = provider.GetProperty("PrimarySelectedCollection")!.GetValue(document)!;
            selection = provider.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
            var selectionType = core.GetType("P1.C1.Core.Selection.ISelection", true)!;
            primaryVariant = selectionType.GetProperty("PrimaryVariant")!;
            target = selectionType.GetProperty("SelectedVariants")!.GetValue(selection)!;
            var type = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!;
            selectOnly = type.GetMethod("SelectOnly")!;
            selectPrimary = type.GetMethod("SelectAndSetAsPrimary")!;
            var variantType = core.GetType("P1.C1.Core.ICoreVariant", true)!;
            uuid = variantType.GetProperty("UUID")!;
            coreImage = variantType.GetProperty("CoreImage")!;
            sourcePath = coreImage.PropertyType.GetProperty("FullpathFilename") ??
                coreImage.PropertyType.GetInterfaces().Select(type => type.GetProperty("FullpathFilename")).First(info => info is not null)!;
            var available = (IEnumerable)core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!
                .GetMethod("GetAllFilteredAndVisibleVariants")!.Invoke(collection, null)!;
            var initial = Selected();
            variants = available.Cast<object>().Select(item => (Id: Identity(item), Item: item))
                .OrderByDescending(item => item.Id == initial).Take(limit).ToDictionary(item => item.Id, item => item.Item);
            Variants = variants.Keys.ToArray();
        }

        internal Guid Identity(object variant) => (Guid)uuid.GetValue(variant)!;
        internal string Format(Guid variant) =>
            Path.GetExtension((string)sourcePath.GetValue(coreImage.GetValue(variants[variant])!)!).TrimStart('.').ToUpperInvariant();

        private Guid? Selected() => panel is not null ? (Guid?)Field(panel, "displayedVariant")
            : primaryVariant.GetValue(selection) is { } current ? Identity(current) : null;

        internal void Select(Guid primary, params Guid[] members)
        {
            var items = Array.CreateInstance(selectOnly.GetParameters()[0].ParameterType.GetElementType()!, members.Length);
            for (var index = 0; index < members.Length; index++) items.SetValue(variants[members[index]], index);
            selectOnly.Invoke(target, new object[] { items });
            if (panel is not null) Call(panel, "SyncSelection");
            if (Selected() != primary)
            {
                selectPrimary.Invoke(target, new[] { variants[primary], (object)true });
                if (panel is not null) Call(panel, "SyncSelection");
            }
            if (Selected() != primary)
                throw new InvalidOperationException("Capture One did not select the expected fixture variant.");
        }
    }
}
