using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestNativeLookBatch(object panel, object store)
    {
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var crash = Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_LOOK_BATCH") == "crash";
        var selector = new FixtureSelection(panel);
        Check(selector.Variants.Length == 4, "native batch uses the four-RAW disposable catalog");
        var source = selector.Variants[0];
        var first = selector.Variants[1];
        var second = selector.Variants[2];
        var other = selector.Variants[3];
        var progress = (TextBlock)Field(panel, "batchStatus")!;
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var exposure = Field(panel, "exposure")!;
        void SetExposure(double value) => exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, value);
        JsonNode? Saved(Guid variant) => Call(store, "Read", variant) is { } saved
            ? JsonSerializer.SerializeToNode(saved, saved.GetType()) : null;
        var expectedPath = Path.Combine(folder, "batch-expected.json");
        void SaveExpected() => File.WriteAllText(expectedPath, new JsonObject(selector.Variants.Select(id =>
            new System.Collections.Generic.KeyValuePair<string, JsonNode?>(id.ToString(), Saved(id)))).ToJsonString());
        if (phase == "restart")
        {
            var expected = JsonNode.Parse(File.ReadAllText(expectedPath))!.AsObject();
            foreach (var variant in selector.Variants)
                Check(JsonNode.DeepEquals(expected[variant.ToString()], Saved(variant)), "restart retains the exact saved batch outcome for " + selector.Format(variant));
            Check(progress.Visibility == Visibility.Visible
                && progress.Text.Contains(crash ? "was interrupted" : "batch stopped", StringComparison.Ordinal)
                && progress.Text.Contains("Saved 1 of 2 selected variants", StringComparison.Ordinal),
                "the restarted native panel displays the exact persisted partial batch result");
            File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native batch assertions");
            return;
        }

        selector.Select(source, source);
        SetExposure(1.37);
        var original = Saved(source);
        var assembly = panel.GetType().Assembly;
        var from = assembly.GetType("LookPreset", true)!.GetMethod("From", Flags)!;
        var recipe = from.Invoke(null, new[] { Call(panel, "CaptureLook") })!;
        var sections = Enum.Parse(assembly.GetType("LookSections", true)!, "Film");
        Task<int> Apply(Func<int, bool> approve) => (Task<int>)Call(panel, "ApplyCopiedToSelectionAsync", recipe, sections, approve)!;

        selector.Select(first, first);
        SetExposure(-.45);
        toggle.IsChecked = false;
        var firstBaseline = Saved(first)!;
        selector.Select(second, second);
        SetExposure(.33);
        var secondBaseline = Saved(second)!;
        var untouched = Saved(other);
        selector.Select(first, first, second);
        Check(await Apply(count => count == 0) == 0 && JsonNode.DeepEquals(firstBaseline, Saved(first))
            && JsonNode.DeepEquals(secondBaseline, Saved(second)), "declining confirmation keeps every selected look");
        var confirmedCount = 0;
        var applied = await Apply(count =>
        {
            confirmedCount = count;
            selector.Select(other, other);
            return true;
        });
        Check(confirmedCount == 2 && applied == 2 && Equals(Field(panel, "displayedVariant"), other),
            "selection churn after capture cannot add or retarget a batch variant");
        var firstApplied = Saved(first)!;
        var secondApplied = Saved(second)!;
        Check(JsonNode.DeepEquals(firstApplied["Look"], original!["Look"]) && JsonNode.DeepEquals(secondApplied["Look"], original["Look"])
            && JsonNode.DeepEquals(firstApplied["Optics"], firstBaseline["Optics"]) && JsonNode.DeepEquals(secondApplied["Optics"], secondBaseline["Optics"])
            && JsonNode.DeepEquals(firstApplied["Grain"], firstBaseline["Grain"]) && JsonNode.DeepEquals(secondApplied["Grain"], secondBaseline["Grain"])
            && firstApplied["Enabled"]!.GetValue<bool>() == false && secondApplied["Enabled"]!.GetValue<bool>(),
            "copy changes only the selected film section and preserves both variants’ master switches, optics and grain");
        Check(JsonNode.DeepEquals(original, Saved(source)) && JsonNode.DeepEquals(untouched, Saved(other)), "batch leaves the source and unselected RAW unchanged");

        var undo = Field(panel, "nativeUndo")!;
        var history = undo.GetType().GetInterfaces().Single(type => type.FullName == "P1.C1.ModelEntities.UndoInterfaces.IUndoManager");
        Check(history.GetProperty("TryGetUndoActionName")!.GetValue(undo) is "Apply Spektrafilm settings to selected variants",
            "the whole batch creates one named Capture One history action");
        history.GetMethod("Undo", new[] { typeof(bool) })!.Invoke(undo, new object[] { true });
        Check(JsonNode.DeepEquals(firstBaseline, Saved(first)) && JsonNode.DeepEquals(secondBaseline, Saved(second)),
            "one Capture One Undo restores both baseline looks");
        history.GetMethod("Redo")!.Invoke(undo, null);
        Check(JsonNode.DeepEquals(firstApplied, Saved(first)) && JsonNode.DeepEquals(secondApplied, Saved(second)),
            "one Capture One Redo restores both applied looks");

        selector.Select(second, second);
        SetExposure(.33);
        selector.Select(first, first);
        SetExposure(-.45);
        selector.Select(first, first, second);
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))!;
        var cancel = (Button)Field(panel, "cancelBatch")!;
        void CancelAfterFirst(object? sender, EventArgs args)
        {
            if (progress.Text.StartsWith("Applied copied settings to 1 of 2 changed", StringComparison.Ordinal))
            {
                if (crash)
                {
                    Check(new[] { first, second }.Count(id => JsonNode.DeepEquals(Saved(id)!["Look"], original["Look"])) == 1,
                        "hard-stop fixture reached the boundary after exactly one real look write");
                    SaveExpected();
                    File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native batch assertions before the hard stop");
                    // The disposable host runner terminates this process while
                    // the job is still active and before its next look write.
                    Thread.Sleep(Timeout.Infinite);
                }
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancel));
            }
        }
        descriptor.AddValueChanged(progress, CancelAfterFirst);
        try { Check(await Apply(count => count == 2) == 1, "the real panel Stop action leaves exactly one committed look"); }
        finally { descriptor.RemoveValueChanged(progress, CancelAfterFirst); }
        Check(progress.Text.Contains("Stopped", StringComparison.Ordinal) && cancel.Visibility == Visibility.Collapsed,
            "the panel reports the stopped batch and clears the progress control");
        var exactlyOne = new[] { first, second }.Count(id => JsonNode.DeepEquals(Saved(id)!["Look"], original["Look"]));
        Check(exactlyOne == 1, "stopping a batch leaves later destinations untouched");
        SaveExpected();
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native batch assertions");
    }
}
