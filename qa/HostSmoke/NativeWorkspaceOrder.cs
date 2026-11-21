using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public static partial class StartupHook
{
    private sealed record WorkspaceOrder(string Fixture, string Original, string Left, string Right,
        string[] NativeTabs, double Exposure);

    private static async Task TestNativeWorkspaceOrder(Type hook, object hosted, string fixture)
    {
        Check(File.Exists(Path.Combine(folder, "Capture One user.config.before-proofing"))
            && File.Exists(Path.Combine(folder, "native-workspace-backup.json")),
            "custom workspace verification starts from verified settings and workspace backups");
        var manager = Field(hosted, "workspaceManager")!;
        var contract = manager.GetType().GetInterface("P1.C1.ViewModels.Workspace.IWorkspaceManager")!;
        var active = contract.GetProperty("ActiveWorkspace")!;
        var custom = contract.GetProperty("CustomWorkspaces")!;
        var workspaceContract = active.PropertyType;
        var paletteSettingsProperty = workspaceContract.GetProperty("InspectorPaletteSettings")!;
        var save = contract.GetMethod("SaveActiveWorkspace")!;
        var apply = contract.GetMethod("ApplyWorkspace")!;
        var hostedField = hook.GetField("hostedTool", Flags)!;
        var paletteField = hook.GetField("owner", Flags)!;
        var tabField = hook.GetField("tab", Flags)!;
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var receipt = Path.Combine(folder, "native-workspace-order.json");
        var fixturePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaptureOne", "Workspaces130", fixture + ".xml");

        if (phase == "first")
        {
            var original = Selector(active.GetValue(manager)!);
            Check(!File.Exists(fixturePath) && !Custom().Any(item => Selector(item) == fixture),
                "the native workspace command will create a new private QA workspace");
            save.Invoke(manager, new object[] { fixture });
            VerifySavedFile();
            var (palette, tab, panel) = await SwitchTo(fixture);
            var existing = Tabs(palette).Cast<object>().Where(item => !ReferenceEquals(item, tab)).ToArray();
            Check(existing.Length >= 3, "the copied workspace exposes neighboring native tool tabs");
            var left = existing[0];
            var right = existing[1];
            Call(palette, "MoveTabBefore", right, tab);
            var names = existing.Select(Name).ToArray();
            Check(ReferenceEquals(Tabs(palette)[0], left) && ReferenceEquals(Tabs(palette)[1], tab)
                && ReferenceEquals(Tabs(palette)[2], right), "Capture One moves the same Spektrafilm tab between native neighbors");

            var exposure = Field(panel, "exposure")!;
            exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, .63d);
            var savedOrder = new WorkspaceOrder(fixture, original, Name(left), Name(right), names, .63d);
            var selected = existing[2];
            hook.GetMethod("SelectTab", Flags)!.Invoke(null, new[] { palette, selected });
            save.Invoke(manager, new object[] { fixture });
            VerifySavedFile();
            VerifyNativeLayout(savedOrder, Name(selected));
            File.Copy(fixturePath, Path.Combine(folder, "saved-native-workspace.xml"));
            File.WriteAllText(receipt, JsonSerializer.Serialize(savedOrder));

            _ = await SwitchTo(original);
            VerifyOrder(await SwitchTo(fixture), savedOrder);
        }
        else
        {
            var expected = JsonSerializer.Deserialize<WorkspaceOrder>(File.ReadAllText(receipt))!;
            Check(expected.Fixture == fixture, "restart uses the exact workspace and placement fixture from the first process");
            VerifySavedFile();
            VerifyNativeLayout(expected);
            VerifyOrder(await SwitchTo(fixture), expected);
        }

        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native workspace order assertions");

        object[] Custom() => ((IEnumerable)custom.GetValue(manager)!).Cast<object>().ToArray();
        string Selector(object workspace) => Equals(Prop(workspace, "Name"), "CustomWorkspace")
            ? (string)Prop(workspace, "Text")! : (string)Prop(workspace, "Name")!;
        static IList Tabs(object palette) => (IList)Prop(palette, "Tabs")!;
        static string Name(object tab) => (string)Prop(tab, "Name")!;

        void VerifySavedFile()
        {
            Check(File.Exists(fixturePath) && !File.GetAttributes(fixturePath).HasFlag(FileAttributes.ReparsePoint),
                "Capture One wrote the regular XML file for the uniquely named fixture workspace");
            var savedXml = File.ReadAllText(fixturePath);
            File.WriteAllText(Path.Combine(folder, $"{phase}-workspace-under-test.xml"), savedXml);
            Check(!savedXml.Contains("Spektrafilm for Capture One", StringComparison.Ordinal)
                && !savedXml.Contains("DummyInspectorTool", StringComparison.Ordinal),
                "the actual Capture One workspace file contains no transient Spektrafilm tab or injected tool");
        }

        void VerifyNativeLayout(WorkspaceOrder expected, string? selected = null)
        {
            var saved = Custom().Single(item => Selector(item) == fixture);
            var settings = paletteSettingsProperty.GetValue(saved)!;
            var serializedTabs = ((IEnumerable)Prop(settings, "PaletteTabs")!).Cast<object>().ToArray();
            Check(serializedTabs.Select(Name).SequenceEqual(expected.NativeTabs),
                "the saved native workspace retains the original order and identities of its other tabs");
            if (selected is null) return;
            var index = (int)Prop(settings, "SelectedTab")!;
            Check(index >= 0 && index < serializedTabs.Length && Name(serializedTabs[index]) == selected,
                "removing the transient tab from the native save retains the selected native tab");
        }

        void VerifyOrder((object Palette, object Tab, object Panel) live, WorkspaceOrder expected)
        {
            var tabs = Tabs(live.Palette);
            var index = tabs.IndexOf(live.Tab);
            Check(index > 0 && index + 1 < tabs.Count && Name(tabs[index - 1]!) == expected.Left
                && Name(tabs[index + 1]!) == expected.Right && tabs.Cast<object>().Count(item => item.GetType() == live.Tab.GetType()) == 1,
                "opening the native workspace restores one Spektrafilm tab between the saved native neighbors");
            Check(Equals(Prop(Field(live.Panel, "exposure")!, "Value"), expected.Exposure)
                && Prop(live.Panel, "ExportBridgeStatus")?.ToString() == "Connected",
                "the restored layout retains the exact saved image look and native Export binding");
        }

        async Task<(object Palette, object Tab, object Panel)> SwitchTo(string selector)
        {
            if (Selector(active.GetValue(manager)!) != selector) apply.Invoke(manager, new object[] { selector });
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
            {
                if (Selector(active.GetValue(manager)!) == selector && hostedField.GetValue(null) is { } current
                    && Field(current, "workspaceKey") is string key && (
                        Equals(Prop(active.GetValue(manager)!, "Name"), "CustomWorkspace")
                            ? key.EndsWith("|CustomWorkspace|" + selector, StringComparison.Ordinal)
                            : key.EndsWith("|" + selector, StringComparison.Ordinal))
                    && Field(current, "panel") is { } panel && Prop(panel, "StocksReady") is true
                    && paletteField.GetValue(null) is { } palette && tabField.GetValue(null) is { } tab && Tabs(palette).Contains(tab))
                    return (palette, tab, panel);
                await Task.Delay(75);
            }
            throw new TimeoutException("The native workspace did not settle on the selected preset.");
        }
    }
}
