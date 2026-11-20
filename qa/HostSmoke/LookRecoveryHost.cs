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
        var cases = new[]
        {
            ("future version", Encoding.UTF8.GetBytes(future.ToJsonString())),
            ("unavailable film stock", Encoding.UTF8.GetBytes(oldStock.ToJsonString())),
            ("truncated UTF-8", new byte[] { 0x7b, 0x22, 0xf0, 0x9f, 0x92 }),
            ("oversized JSON", Enumerable.Repeat((byte)' ', 80 * 1024).ToArray()),
        };

        void Reload()
        {
            field.SetValue(panel, null);
            Call(panel, "SyncSelection");
        }

        foreach (var (name, rejected) in cases)
        {
            File.WriteAllBytes(path, rejected);
            Reload();
            Check(Call(store, "IsBlocked", variant) is true && !toggle.IsEnabled
                && recovery.Visibility == Visibility.Visible && warning.Visibility == Visibility.Visible,
                $"licensed panel visibly protects {name} and offers explicit recovery");
            using (var report = JsonDocument.Parse((string)Call(panel, "BuildSupportDetails")!))
                Check(report.RootElement.GetProperty("LookStorage").GetString() == "RecoveryRequired",
                    $"support status identifies the protected {name}");
            value.SetValue(exposure, 2.3);
            Check(File.ReadAllBytes(path).SequenceEqual(rejected), $"an attempted edit leaves {name} byte for byte");
            Call(store, "ResetBlocked", variant, original);
            Reload();
            Check(toggle.IsEnabled && toggle.IsChecked == true && File.ReadAllBytes(path).SequenceEqual(before),
                $"explicit recovery restores the previous valid look after {name}");
            var archives = Directory.EnumerateFiles(Path.Combine(folder, "Look recovery"), "*.json", SearchOption.AllDirectories);
            Check(archives.Any(file => File.ReadAllBytes(file).SequenceEqual(rejected)),
                $"explicit recovery archives the original {name} bytes");
        }
    }
}
