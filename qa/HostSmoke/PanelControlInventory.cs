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

    private static void VerifyNativeSliderTypes(object panel, object spatial, object store, Guid variant)
    {
        var sliders = (IDictionary)Field(spatial, "values")!;
        var cases = new[]
        {
            ("grain-seed", 17.5m, 18d), ("grain-sublayer-count", 3.5m, 4d),
            ("grain-synthesis-samples", 79.7m, 80d), ("grain-synthesis-cell-cap", 18.49m, 18d),
            ("camera-diffusion-strength", 1.375m, 1.375), ("camera-diffusion-warmth", -.375m, -.375),
        };
        var original = Call(spatial, "CaptureSettings")!;
        var cameraFilter = (ComboBox)((IDictionary)Field(spatial, "choices")!)["camera-diffusion-family"]!;
        cameraFilter.SelectedItem = OpticalOption(cameraFilter, "pro_mist");
        var legacy = Call(spatial, "CaptureSettings")!;
        var legacyNumbers = (IDictionary)Prop(legacy, "Numbers")!;
        foreach (var (key, entered, expected) in cases)
        {
            var slider = sliders[key]!;
            var control = Field(slider, "control")!;
            var value = control.GetType().GetProperty("Value", Flags)!;
            var notifications = new List<double>();
            var changed = slider.GetType().GetEvent("ValueChanged", Flags)!;
            EventHandler observe = (_, _) => notifications.Add((double)Prop(slider, "Value")!);
            changed.GetAddMethod(nonPublic: true)!.Invoke(slider, new object[] { observe });
            try
            {
                value.SetValue(control, entered);
            }
            finally
            {
                changed.GetRemoveMethod(nonPublic: true)!.Invoke(slider, new object[] { observe });
            }
            var saved = Call(store, "Read", variant)!;
            var numbers = (IDictionary)Prop(Prop(saved, "Optics")!, "Numbers")!;
            Check(notifications.SequenceEqual(new[] { expected }) && Equals(numbers[key], expected),
                $"{key} publishes and persists one valid setting from the native value change");
            legacyNumbers[key] = (double)entered;
            Call(slider, "ResetToDefault");
            Check(Equals(Prop(slider, "Value"), Prop(slider, "Initial")), $"{key} resets to its declared default");
        }
        Call(spatial, "ValidateSettings", legacy);
        Call(spatial, "LoadSettings", legacy);
        var controls = (IDictionary)Prop(Call(spatial, "CaptureSettings")!, "Numbers")!;
        var look = Call(panel, "CurrentSettings")!;
        var options = (string[])Call(spatial, "RendererOptions", look, "srgb", "srgb", false, false)!;
        var wire = Enumerable.Range(0, options.Length / 2)
            .ToDictionary(index => options[index * 2][2..], index => options[index * 2 + 1], StringComparer.Ordinal);
        foreach (var (key, entered, expected) in cases)
            Check(Equals(controls[key], expected) && Equals(legacyNumbers[key], (double)entered)
                && wire[key] == expected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{key} loads legacy numeric input and emits a supported renderer value");
        Call(spatial, "LoadSettings", original);
    }

    private static void VerifyOpticalModeDependencies(object panel, object spatial)
    {
        var mode = (ComboBox)Field(panel, "mode")!;
        var choices = (IDictionary)Field(spatial, "choices")!;
        var diffusion = (ComboBox)choices["print-diffusion-family"]!;
        var cameraFilter = (ComboBox)choices["camera-diffusion-family"]!;
        var scanner = (CheckBox)((IDictionary)Field(spatial, "toggles")!)["scanner-enabled"]!;
        var sliders = (IDictionary)Field(spatial, "values")!;
        var glare = sliders["scanner-glare"]!;
        var value = glare.GetType().GetProperty("Value", Flags)!;
        var enabled = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var previousMode = mode.SelectedItem;
        var previousDiffusion = diffusion.SelectedItem;
        var previousCameraFilter = cameraFilter.SelectedItem;
        var previousScanner = scanner.IsChecked;
        var previousGlare = value.GetValue(glare);
        var wasEnabled = enabled.IsChecked;
        var sections = ((IEnumerable)Field(spatial, "printOnlyControls")!).Cast<FrameworkElement>().ToArray();
        try
        {
            enabled.IsChecked = true;
            mode.SelectedItem = OpticalOption(mode, "print");
            diffusion.SelectedItem = OpticalOption(diffusion, "pro_mist");
            cameraFilter.SelectedItem = OpticalOption(cameraFilter, "pro_mist");
            scanner.IsChecked = true;
            value.SetValue(glare, .07d);
            var print = OpticalWire(panel, spatial);
            Check(sections.Length == 2 && sections.All(section => section.IsEnabled)
                && print["print-diffusion-family"] == "pro_mist" && print["scanner-glare"] == "0.07",
                "Print Simulation enables the two print-only groups and sends their staged values");

            mode.SelectedItem = OpticalOption(mode, "scan");
            var scan = OpticalWire(panel, spatial);
            var omitted = print.Keys.Except(scan.Keys).ToArray();
            Check(sections.All(section => !section.IsEnabled) && scan["mode"] == "scan" && omitted.Length == 13
                && omitted.All(key => key.StartsWith("print-diffusion-", StringComparison.Ordinal) || key.StartsWith("scanner-glare", StringComparison.Ordinal))
                && scan.ContainsKey("camera-diffusion-strength") && scan.ContainsKey("scanner-unsharp-amount"),
                "Scan Film disables and omits the print-only optics while retaining camera diffusion and scanner sharpening");
            var staged = Call(spatial, "CaptureSettings")!;
            Check(Equals(((IDictionary)Prop(staged, "Selections")!)["print-diffusion-family"], "pro_mist")
                && Equals(((IDictionary)Prop(staged, "Numbers")!)["scanner-glare"], .07d),
                "switching to Scan Film retains the dormant print optics in saved look state");

            mode.SelectedItem = OpticalOption(mode, "print");
            var returned = OpticalWire(panel, spatial);
            Check(sections.All(section => section.IsEnabled) && print.SequenceEqual(returned),
                "returning to Print Simulation restores the exact staged renderer request");
        }
        finally
        {
            mode.SelectedItem = previousMode;
            diffusion.SelectedItem = previousDiffusion;
            cameraFilter.SelectedItem = previousCameraFilter;
            scanner.IsChecked = previousScanner;
            value.SetValue(glare, previousGlare);
            enabled.IsChecked = wasEnabled;
        }
    }

    private static object OpticalOption(ComboBox chooser, string id) =>
        chooser.Items.Cast<object>().Single(item => Equals(Prop(item, "Id"), id));

    private static Dictionary<string, string> OpticalWire(object panel, object spatial, object? frozen = null)
    {
        var look = Call(panel, "CurrentSettings")!;
        var args = (string[])(frozen is null
            ? Call(spatial, "RendererOptions", look, "srgb", "srgb", false, false)!
            : Call(spatial, "BuildRendererOptions", look, "srgb", "srgb", true, false, frozen)!);
        return Enumerable.Range(0, args.Length / 2)
            .ToDictionary(i => args[i * 2][2..], i => args[i * 2 + 1], StringComparer.Ordinal);
    }

    private static void VerifyOpticalPrerequisites(object panel, object spatial)
    {
        var mode = (ComboBox)Field(panel, "mode")!;
        var enabled = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var numbers = (IDictionary)Field(spatial, "values")!;
        var choices = (IDictionary)Field(spatial, "choices")!;
        var toggles = (IDictionary)Field(spatial, "toggles")!;
        var previousMode = mode.SelectedItem;
        var wasEnabled = enabled.IsChecked;
        var previousSettings = Call(spatial, "CaptureSettings")!;
        void Toggle(string key, bool active) => ((CheckBox)toggles[key]!).IsChecked = active;
        void Select(string key, string option)
        {
            var chooser = (ComboBox)choices[key]!;
            chooser.SelectedItem = OpticalOption(chooser, option);
        }
        void Expect(string key, FrameworkElement control, string? value)
        {
            var wire = OpticalWire(panel, spatial);
            Check(control.IsEnabled == (value is not null)
                && wire.GetValueOrDefault(key) == value
                && (value is not null || control.ToolTip is string && ToolTipService.GetShowOnDisabled(control)),
                $"{key} has matching native availability, prerequisite hint and Viewer arguments");
        }
        try
        {
            enabled.IsChecked = true;
            mode.SelectedItem = OpticalOption(mode, "print");
            Toggle("auto-exposure", true);
            Select("auto-exposure-meter", "median");
            Expect("auto-exposure-meter", (ComboBox)choices["auto-exposure-meter"]!, "median");
            Toggle("auto-exposure", false);
            Expect("auto-exposure-meter", (ComboBox)choices["auto-exposure-meter"]!, null);

            Select("camera-diffusion-family", "glimmerglass");
            var filterStrength = (FrameworkElement)numbers["camera-diffusion-strength"]!;
            filterStrength.GetType().GetProperty("Value", Flags)!.SetValue(filterStrength, 1.375);
            Expect("camera-diffusion-strength", filterStrength, "1.375");
            Check(Equals(filterStrength.ToolTip, "Filter strength"), "an active optical slider retains its standard label tooltip");
            Select("camera-diffusion-family", "off");
            Expect("camera-diffusion-strength", filterStrength, null);

            Toggle("scanner-enabled", true);
            Toggle("scanner-white", true);
            Toggle("scanner-black", false);
            var white = (FrameworkElement)numbers["scanner-white-level"]!;
            white.GetType().GetProperty("Value", Flags)!.SetValue(white, .8);
            Expect("scanner-white-level", white, "0.8");
            Expect("scanner-black-level", (FrameworkElement)numbers["scanner-black-level"]!, null);
            Toggle("scanner-enabled", false);
            Expect("scanner-white", (CheckBox)toggles["scanner-white"]!, null);
            Expect("scanner-white-level", white, null);

            Toggle("color-adaptation", true);
            Toggle("color-input-compression", false);
            Expect("color-input-compression", (CheckBox)toggles["color-input-compression"]!, "false");
            Toggle("color-adaptation", false);
            Expect("color-input-compression", (CheckBox)toggles["color-input-compression"]!, null);
            var dormant = Call(spatial, "CaptureSettings")!;
            var dormantWire = OpticalWire(panel, spatial);

            Toggle("auto-exposure", true);
            Select("camera-diffusion-family", "glimmerglass");
            Toggle("scanner-enabled", true);
            Toggle("color-adaptation", true);
            Expect("auto-exposure-meter", (ComboBox)choices["auto-exposure-meter"]!, "median");
            Expect("camera-diffusion-strength", filterStrength, "1.375");
            Expect("scanner-white-level", white, "0.8");
            Expect("color-input-compression", (CheckBox)toggles["color-input-compression"]!, "false");
            Check(OpticalWire(panel, spatial, dormant).SequenceEqual(dormantWire),
                "frozen native batch export honors saved prerequisites after the live toggles change");
            Call(spatial, "LoadSettings", dormant);
            Expect("scanner-white-level", white, null);
            Check(Equals(((IDictionary)Prop(Call(spatial, "CaptureSettings")!, "Numbers")!)["scanner-white-level"], .8d),
                "reloading dormant optics restores availability and keeps the staged white point");
        }
        finally
        {
            mode.SelectedItem = previousMode;
            Call(spatial, "LoadSettings", previousSettings);
            enabled.IsChecked = wasEnabled;
        }
    }

    private static void SavePanelControlInventory(object panel, object spatial)
    {
        var bindings = new Dictionary<object, (string Capability, string? Option)>(ReferenceEqualityComparer.Instance);
        var rows = new List<PanelControl>();
        using var worker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!, "renderer", "parameter-capabilities.json")));
        var workerOptions = worker.RootElement.GetProperty("parameters").EnumerateArray()
            .ToDictionary(option => option.GetProperty("key").GetString()!, StringComparer.Ordinal);

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
        BindFields(spatial, "viewer-overlay", ("showSource", null), ("holdCompare", null), ("savePreview", null), ("retryViewer", null));
        BindFields(spatial, "viewer-quality", ("previewSize", null), ("dragPreviewSize", null), ("fastSynthesis", null));
        BindFields(spatial, "native-export", ("cancelExport", null));
        BindFields(panel, "lut-export", ("cancelLut", null));
        BindFields(panel, "presets", ("cancelBatch", null));
        BindFields(Field(spatial, "histogram")!, "viewer-analysis", ("markHighlights", null));

        var numericControls = bindings.Where(entry => entry.Value.Capability == "vulkan" && entry.Key.GetType().Name == "NativeSlider")
            .Select(entry =>
            {
                var option = entry.Value.Option!;
                var slider = entry.Key;
                var contract = workerOptions[option];
                return new
                {
                    Option = option,
                    Label = AutomationProperties.GetName((DependencyObject)Field(slider, "control")!),
                    Unit = Prop(Field(slider, "control")!, "Unit"),
                    Minimum = (double)Prop(slider, "Minimum")!,
                    Maximum = (double)Prop(slider, "Maximum")!,
                    Initial = (double)Prop(slider, "Initial")!,
                    Increment = (double)Prop(slider, "Increment")!,
                    WorkerKind = contract.GetProperty("kind").GetString()!,
                    WorkerMinimum = contract.GetProperty("minimum").GetDouble(),
                    WorkerMaximum = contract.GetProperty("maximum").GetDouble(),
                    WorkerDefault = contract.GetProperty("default").GetDouble(),
                };
            }).OrderBy(control => control.Option, StringComparer.Ordinal).ToArray();
        Check(numericControls.Length > 0 && numericControls.All(control =>
            (float)control.Minimum >= (float)control.WorkerMinimum && (float)control.Maximum <= (float)control.WorkerMaximum
            && control.Initial >= control.Minimum && control.Initial <= control.Maximum && control.Increment > 0
            && (control.WorkerKind != "integer" || control.Increment == Math.Truncate(control.Increment))),
            "every displayed worker slider's legal range and precision fit the compiled parameter type");
        File.WriteAllText(Path.Combine(folder, "panel-numeric-contract.json"),
            JsonSerializer.Serialize(numericControls, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        var contextual = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Defaults"] = "look-state",
            ["Clear unsupported developer exhaustion"] = "look-state",
            ["Save preset…"] = "presets", ["Load preset…"] = "presets", ["Recent presets"] = "presets",
            ["Film and print"] = "presets", ["Optics and colour spaces"] = "presets",
            ["Staged Capture One grain"] = "presets", ["Master switch"] = "presets",
            ["Copy selected settings"] = "presets", ["Apply copied settings"] = "presets", ["Load selected sections…"] = "presets",
            ["Apply to selected variants…"] = "presets",
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
        Check(rows.All(row => row.Capability != "vulkan" || workerOptions.ContainsKey(row.Option!)),
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
