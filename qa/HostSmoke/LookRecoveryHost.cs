using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static void TestBlockedLookInputs(object panel, object store, Guid variant, object exposure, CheckBox toggle)
    {
        var path = (string)Call(store, "GetPath", variant)!;
        var original = Call(store, "Read", variant)!;
        var before = File.ReadAllBytes(path);
        var backup = Path.Combine(folder, "format-recovery-" + File.ReadAllText(Path.Combine(folder, "phase.txt")) + ".json");
        File.WriteAllBytes(backup, before);
        Check(File.ReadAllBytes(backup).SequenceEqual(before), "verified format-recovery fixture backup before corrupting the disposable file");
        var field = panel.GetType().GetField("displayedVariant", Flags)!;
        var value = exposure.GetType().GetProperty("Value", Flags)!;
        var recovery = (Button)Field(panel, "recoverSavedLook")!;
        var warning = (TextBlock)Field(panel, "saveError")!;
        var future = JsonNode.Parse(before)!;
        future["Version"] = 9999;
        var oldStock = JsonNode.Parse(before)!;
        oldStock["Look"]!["Film"] = "fixture_removed_stock";
        var malformedDictionary = JsonNode.Parse(before)!;
        malformedDictionary["Look"]!["Development"] = new JsonArray(1, 2, 3);
        var cases = new[]
        {
            ("future version", "Unsupported", Encoding.UTF8.GetBytes(future.ToJsonString())),
            ("unavailable film stock", "Unsupported", Encoding.UTF8.GetBytes(oldStock.ToJsonString())),
            ("truncated UTF-8", "Recovered", new byte[] { 0x7b, 0x22, 0xf0, 0x9f, 0x92 }),
            ("malformed development dictionary", "Recovered", Encoding.UTF8.GetBytes(malformedDictionary.ToJsonString())),
            ("oversized JSON", "Recovered", Enumerable.Repeat((byte)' ', 80 * 1024).ToArray()),
        };

        void Reload()
        {
            field.SetValue(panel, null);
            Call(panel, "SyncSelection");
        }

        var originalExposure = (double)value.GetValue(exposure)!;
        var changedExposure = originalExposure < 1 ? originalExposure + 1.2 : originalExposure - 1.2;
        value.SetValue(exposure, changedExposure);
        var previousPath = Path.ChangeExtension(path, ".previous.look");
        Check(File.Exists(previousPath) && File.ReadAllBytes(previousPath).AsSpan(52).SequenceEqual(before)
            && !File.ReadAllBytes(path).SequenceEqual(before),
            "a genuine slider edit publishes a distinct look and keeps its previous generation");
        foreach (var (name, expectedState, rejected) in cases)
        {
            File.WriteAllBytes(path, rejected);
            Reload();
            Check(Call(store, "IsBlocked", variant) is true && !toggle.IsEnabled
                && recovery.Visibility == Visibility.Visible && warning.Visibility == Visibility.Visible,
                $"licensed panel visibly protects {name} and offers explicit recovery");
            var outcome = Call(store, "ReadOutcome", variant)!;
            Check(Prop(outcome, "State")?.ToString() == expectedState, $"the protected {name} has the intended typed outcome");
            var previous = Field(panel, "previousSavedLook");
            if (expectedState == "Recovered")
                Check(previous is not null && (double)value.GetValue(exposure)! == originalExposure
                    && (string)recovery.Content == "Restore previous saved look…",
                    $"{name} displays the product-managed previous values and offers explicit restore");
            else
                Check(previous is null && (string)recovery.Content == "Recover saved look…",
                    $"{name} remains unsupported even when an older look is available");
            using (var report = JsonDocument.Parse((string)Call(panel, "BuildSupportDetails")!))
                Check(report.RootElement.GetProperty("LookStorage").GetString() == "RecoveryRequired",
                    $"support status identifies the protected {name}");
            value.SetValue(exposure, 2.3);
            Check(File.ReadAllBytes(path).SequenceEqual(rejected), $"an attempted edit leaves {name} byte for byte");
            if (previous is not null)
                Call(panel, "RecoverSelectedLook", variant, previous);
            else
            {
                Call(store, "ResetBlocked", variant, original);
                Reload();
            }
            Check(toggle.IsEnabled && toggle.IsChecked == true && File.ReadAllBytes(path).SequenceEqual(before),
                $"explicit recovery restores the previous valid look after {name}");
            var archives = Directory.EnumerateFiles(Path.Combine(folder, "Look recovery"), "*.json", SearchOption.AllDirectories);
            Check(archives.Any(file => File.ReadAllBytes(file).SequenceEqual(rejected)),
                $"explicit recovery archives the original {name} bytes");
        }
    }
}
