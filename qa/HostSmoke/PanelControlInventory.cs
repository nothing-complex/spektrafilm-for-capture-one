using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

public static partial class StartupHook
{
    private sealed record PanelControl(string Section, string Label, string Kind, string Capability, string? Option = null);

    private static void SavePanelControlInventory(object panel, object spatial)
    {
        var bindings = new Dictionary<object, (string Capability, string? Option)>(ReferenceEqualityComparer.Instance);
        var rows = new List<PanelControl>();
        using var worker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!, "renderer", "parameter-capabilities.json")));
        var workerKeys = worker.RootElement.GetProperty("parameters").EnumerateArray()
            .Select(option => option.GetProperty("key").GetString()!).ToHashSet(StringComparer.Ordinal);

        foreach (var name in new[] { "values", "choices", "toggles" })
        {
            foreach (DictionaryEntry entry in (IDictionary)Field(spatial, name)!)
            {
                var key = (string)entry.Key;
                Bind(entry.Value!, key.StartsWith("custom-", StringComparison.Ordinal) ? "optical-controls" : "vulkan", key);
            }
        }
        var development = ((IEnumerable)spatial.GetType().GetProperty("DevelopmentRendererParameters", Flags)!
            .GetValue(null)!).Cast<ITuple>().ToDictionary(item => (string)item[0]!, item => (string)item[1]!, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in (IDictionary)Field(panel, "development")!)
        {
            var key = (string)entry.Key;
            var control = ((ITuple)entry.Value!)[0]!;
            if (key == "print_exhaustion")
                Bind(control, "unavailable");
            else
                Bind(control, "vulkan", development[key]);
        }
        BindFields(panel, "vulkan", ("mode", "mode"), ("film", "stock"), ("paper", "paper"), ("scanInvert", "mode"),
            ("filmPushPullMode", "film-push-pull-mode"), ("spectralMethod", "spectral-method"),
            ("exposure", "film-exposure"), ("printExposure", "print-exposure"));
        BindFields(spatial, "vulkan", ("inputSpace", "input-color"), ("outputSpace", "output-color"));
        BindFields(panel, "look-state", ("enableSpektraFilm", null), ("recoverSavedLook", null));
        BindFields(panel, "color-delivery", ("strength", null));
        BindFields(panel, "native-grain", ("grainType", null), ("grainImpact", null), ("grainSize", null),
            ("applyGrain", null), ("restoreGrain", null));
        BindFields(spatial, "viewer-overlay", ("showSource", null), ("holdCompare", null), ("savePreview", null));
        BindFields(spatial, "viewer-quality", ("previewSize", null), ("dragPreviewSize", null), ("fastSynthesis", null));
        BindFields(spatial, "native-export", ("cancelExport", null));
        BindFields(panel, "lut-export", ("cancelLut", null));
        BindFields(Field(spatial, "histogram")!, "viewer-analysis", ("markHighlights", null));

        var contextual = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Defaults"] = "look-state",
            ["Clear unsupported developer exhaustion"] = "look-state",
            ["Save preset…"] = "presets", ["Load preset…"] = "presets", ["Recent presets"] = "presets",
            ["Film and print"] = "presets", ["Optics and colour spaces"] = "presets",
            ["Staged Capture One grain"] = "presets", ["Master switch"] = "presets",
            ["Copy selected settings"] = "presets", ["Apply copied settings"] = "presets", ["Load selected sections…"] = "presets",
            ["Export with Spektrafilm…"] = "native-export",
            ["Export 33-point LUT…"] = "lut-export", ["Export 65-point LUT…"] = "lut-export",
        };
        Visit((DependencyObject)panel, "Tool");
        rows.AddRange(new PanelControl[]
        {
            new("Tool menu", "Float Tool / Show Floating Tool / Dock Tool", "menu", "floating-tool"),
            new("Tool menu", "Retry look save", "menu", "look-state"),
            new("Tool menu", "Copy support details", "menu", "diagnostics"),
            new("Tool / Film stock menu", "Add to favourites / Remove from favourites", "menu", "stock-favourites"),
            new("Tool / Print stock menu", "Add to favourites / Remove from favourites", "menu", "stock-favourites"),
        });
        Check(bindings.Count == 0, "all registered capability controls are present in the native panel logical tree");
        Check(rows.All(row => row.Capability != "vulkan" || workerKeys.Contains(row.Option!)),
            "each native Vulkan control maps to one option in the compiled worker");
        Check(rows.Select(row => (row.Section, row.Label)).Distinct().Count() == rows.Count,
            "each native control has a unique section and label");
        var receipt = rows.OrderBy(row => row.Section, StringComparer.Ordinal).ThenBy(row => row.Label, StringComparer.Ordinal).ToArray();
        File.WriteAllText(Path.Combine(folder, "panel-control-inventory.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        var expectedPath = Environment.GetEnvironmentVariable("SPEKTRAFILM_EXPECTED_CONTROL_INVENTORY");
        if (File.Exists(expectedPath))
        {
            var expected = JsonSerializer.Deserialize<PanelControl[]>(File.ReadAllBytes(expectedPath!))!;
            Check(receipt.SequenceEqual(expected), "the native control inventory matches the source used for package documentation");
        }

        void Bind(object control, string capability, string? option = null) => bindings.Add(control, (capability, option));

        void BindFields(object owner, string capability, params (string Field, string? Option)[] fields)
        {
            foreach (var entry in fields) Bind(Field(owner, entry.Field)!, capability, entry.Option);
        }

        void Visit(DependencyObject node, string section)
        {
            if (node is Expander expander && expander.Header is string header)
            {
                rows.Add(new(section, header, "section", "workspace-layout"));
                section += " / " + header;
            }
            var isSlider = node.GetType().Name == "NativeSlider";
            var kind = node switch
            {
                _ when isSlider => "slider",
                ComboBox => "select",
                CheckBox => "toggle",
                Button => "button",
                _ => null,
            };
            if (kind is not null)
            {
                var label = isSlider ? AutomationProperties.GetName((DependencyObject)Field(node, "control")!) :
                    node is ContentControl { Content: string text } ? text : AutomationProperties.GetName(node);
                if (string.IsNullOrWhiteSpace(label))
                    throw new InvalidOperationException($"Unnamed native {kind} in {section}.");
                if (ReferenceEquals(node, Field(panel, "recoverSavedLook")))
                    label = "Recover saved look / Restore previous saved look";
                if (!bindings.Remove(node, out var bound))
                {
                    if (!contextual.TryGetValue(label, out var capability))
                        throw new InvalidOperationException($"Native {kind} has no declared capability: {section} / {label}.");
                    bound = (capability, null);
                }
                if (bound.Capability == "unavailable")
                    Check(node.GetValue(UIElement.IsEnabledProperty) is false && node is FrameworkElement { ToolTip: string },
                        "developer exhaustion is visibly disabled with a reason");
                rows.Add(new(section, label, kind, bound.Capability, bound.Option));
            }
            if (node is not FrameworkElement current || isSlider) return;
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                Visit(child, section);
        }
    }
}
