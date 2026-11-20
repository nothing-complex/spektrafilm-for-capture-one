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

public static partial class StartupHook
{
    private sealed record ChangeReceipt(Guid Variant, bool Enabled, double[] Values, string FileSha256);
    private sealed record EventTimes(double P50, double P95, double P99, double Max);

    private static async Task TestDurableSliderEdits(object panel, object spatial, object store)
    {
        const int measuredEdits = 10_000;
        const int gestureLength = 100;
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var report = Path.Combine(folder, "last-acknowledged.json");
        var selection = new FixtureSelection(panel);
        Check(selection.Variants.Length == 4, "four independently saved real RAW variants available for slider durability");
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var saveError = (TextBlock)Field(panel, "saveError")!;
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
            return new ChangeReceipt(variant, toggle.IsChecked == true, committed,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
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
                    && restored.Values.SequenceEqual(receipt.Values) && restored.FileSha256 == receipt.FileSha256,
                    "forced restart restores the last acknowledged slider values, master switch and exact saved file");
            }
            File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native slider recovery assertions");
            return;
        }

        var eventTimes = new double[measuredEdits];
        var selectionTimes = new double[measuredEdits / gestureLength];
        var receipts = new Dictionary<Guid, ChangeReceipt>();
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        for (var gesture = 0; gesture < selectionTimes.Length; gesture++)
        {
            var variant = selection.Variants[gesture % selection.Variants.Length];
            var started = Stopwatch.GetTimestamp();
            selection.Select(variant, variant);
            selectionTimes[gesture] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            toggle.IsChecked = (gesture / selection.Variants.Length) % 2 == 0;
            // Isolate the edit acknowledgement from downstream Viewer work.
            // The actual native slider, change handler, look store and UI loop remain active.
            Call(spatial, "SetEnabled", false);
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
                }
            }
            finally { Call(spatial, "EndSliderDrag"); }
            receipts[variant] = CaptureReceipt(variant);
            await Task.Delay(1);
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
            SliderEventAndSaveMs = Percentiles(eventTimes),
            SelectionAndLoadMs = Percentiles(selectionTimes),
            Scope = "Programmatically invoke the real WPF value change and wait for its synchronous durable acknowledgement. Viewer rendering paused.",
        }, more));
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native slider durability assertions; {measuredEdits} individually checked writes");
    }

    private static EventTimes Percentiles(double[] samples)
    {
        Array.Sort(samples);
        double At(double percentile) => samples[(int)Math.Ceiling(percentile * samples.Length) - 1];
        return new EventTimes(At(.50), At(.95), At(.99), samples[^1]);
    }

    private sealed class FixtureSelection
    {
        private readonly object panel;
        private readonly object target;
        private readonly MethodInfo selectOnly;
        private readonly MethodInfo selectPrimary;
        private readonly PropertyInfo uuid;
        private readonly Dictionary<Guid, object> variants;
        internal Guid[] Variants { get; }

        internal FixtureSelection(object panel)
        {
            this.panel = panel;
            var document = Field(panel, "document")!;
            var core = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.Core");
            var provider = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
            var collection = provider.GetProperty("PrimarySelectedCollection")!.GetValue(document)!;
            var selection = provider.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
            target = core.GetType("P1.C1.Core.Selection.ISelection", true)!.GetProperty("SelectedVariants")!.GetValue(selection)!;
            var type = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!;
            selectOnly = type.GetMethod("SelectOnly")!;
            selectPrimary = type.GetMethod("SelectAndSetAsPrimary")!;
            uuid = core.GetType("P1.C1.Core.ICoreVariant", true)!.GetProperty("UUID")!;
            var available = (IEnumerable)core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!
                .GetMethod("GetAllFilteredAndVisibleVariants")!.Invoke(collection, null)!;
            var initial = (Guid)Field(panel, "displayedVariant")!;
            variants = available.Cast<object>().Select(item => (Id: Identity(item), Item: item))
                .OrderByDescending(item => item.Id == initial).Take(4).ToDictionary(item => item.Id, item => item.Item);
            Variants = variants.Keys.ToArray();
        }

        internal Guid Identity(object variant) => (Guid)uuid.GetValue(variant)!;

        internal void Select(Guid primary, params Guid[] members)
        {
            var items = Array.CreateInstance(selectOnly.GetParameters()[0].ParameterType.GetElementType()!, members.Length);
            for (var index = 0; index < members.Length; index++) items.SetValue(variants[members[index]], index);
            selectOnly.Invoke(target, new object[] { items });
            Call(panel, "SyncSelection");
            if (!Equals(Field(panel, "displayedVariant"), primary))
            {
                selectPrimary.Invoke(target, new[] { variants[primary], (object)true });
                Call(panel, "SyncSelection");
            }
            if (!Equals(Field(panel, "displayedVariant"), primary))
                throw new InvalidOperationException("Capture One did not select the expected fixture variant.");
        }
    }
}
