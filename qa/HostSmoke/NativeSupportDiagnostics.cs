using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

public static partial class StartupHook
{
    private static async Task TestNativeSupportDiagnostics(FieldInfo hookField, object exportHook, object hosted,
        object panel, object window, object spatial, object store, Guid variant, object exposure, CheckBox toggle)
    {
        var route = (TextBlock)Field(panel, "exportRouteStatus")!;
        var saveWarning = (TextBlock)Field(panel, "saveError")!;
        var bridgeWarning = (TextBlock)Field(panel, "exportBridgeStatus")!;
        var quality = (ComboBox)Field(spatial, "previewSize")!;
        quality.SelectedItem = quality.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "1200");
        var exposureValue = exposure.GetType().GetProperty("Value", Flags)!;
        var strength = Field(panel, "strength")!;
        var strengthValue = strength.GetType().GetProperty("Value", Flags)!;
        toggle.IsChecked = true;
        strengthValue.SetValue(strength, 100d);
        exposureValue.SetValue(exposure, 1.2d);
        Check(route.IsVisible && route.Text.Contains("includes Spektrafilm", StringComparison.Ordinal),
            "the opened tool visibly identifies native export with the film look");
        strengthValue.SetValue(strength, 0d);
        Check(route.IsVisible && route.Text.Contains("Look strength is 0%", StringComparison.Ordinal),
            "a zero-strength render keeps Spektrafilm output and visibly reports zero film strength");
        strengthValue.SetValue(strength, 100d);
        toggle.IsChecked = false;
        Check(route.IsVisible && route.Text.Contains("native adjustments", StringComparison.Ordinal),
            "the opened tool visibly identifies ordinary Capture One export when the master is off");
        toggle.IsChecked = true;
        TestExportBinding(hookField, exportHook, hosted, panel, window, exposure, toggle);
        Check(route.IsVisible && !bridgeWarning.IsVisible, "reattaching native Export restores the film export route");

        exposureValue.SetValue(exposure, 1.2d);
        await TestLockedLook(panel, store, variant, exposure, toggle);
        Check(route.IsVisible && route.Text.Contains("includes Spektrafilm", StringComparison.Ordinal),
            "the physical look-read failure recovers the visible native export route");

        var runtime = Path.GetFullPath(Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!);
        var worker = Path.Combine(runtime, "renderer", "SpektraStillRenderer.exe");
        var unavailable = worker + ".fixture-unavailable";
        var lookFile = (string)Call(store, "GetPath", variant)!;
        Check(Path.GetDirectoryName(runtime) == Path.GetFullPath(folder) && File.Exists(worker) && !File.Exists(unavailable),
            "the missing renderer fixture is confined to the disposable runtime");
        Call(spatial, "SetEnabled", false);
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        File.Move(worker, unavailable);
        try
        {
            using var locked = new FileStream(lookFile, FileMode.Open, FileAccess.Read, FileShare.None);
            exposureValue.SetValue(exposure, 1.43d);
            Check(saveWarning.IsVisible && saveWarning.Text.Contains("could not save", StringComparison.OrdinalIgnoreCase)
                && Prop(store, "HasPending") is true && Prop(store, "HasUnprotectedPending") is false,
                "a real native slider save failure remains visible and publishes its independent recovery checkpoint");
            Call(spatial, "SetEnabled", true);
            await WaitFor(() => Prop(spatial, "Health")?.ToString() == "ProcessingError"
                && ((Button)Field(spatial, "retryViewer")!).IsVisible, "missing Viewer renderer was not exposed with Retry");
            Check(saveWarning.IsVisible && route.IsVisible && route.Text.Contains("includes Spektrafilm", StringComparison.Ordinal)
                && Prop(panel, "ExportBridgeStatus")?.ToString() == "Connected",
                "Viewer failure and look protection remain visible separately from the connected export command");
            TestSupportClipboard(hosted, panel);
        }
        finally { File.Move(unavailable, worker); }

        Call(panel, "RetryPendingLookWrites");
        ((Button)Field(spatial, "retryViewer")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitFor(() => Prop(spatial, "Health")?.ToString() == "Visible" && !saveWarning.IsVisible,
            "both independent features did not recover after restoring the test runtime");
        Check(Math.Abs((double)Prop(Prop(Call(store, "Read", variant)!, "Look")!, "Exposure")! - 1.43) < 1e-9,
            "the recovered Viewer and save status retain the exact acknowledged edit");

        var approved = new Dictionary<string, (string Field, string? Value)>
        {
            ["native-export.bridge.v1"] = ("State", "Unavailable"),
            ["look.storage.v1"] = ("State", "RecoveryCheckpoint"),
            ["viewer.processing-failed.v1"] = ("ErrorKind", null),
        };
        var found = new HashSet<string>();
        await WaitFor(() =>
        {
            using var stream = new FileStream(Path.Combine(folder, "panel.log"), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var item = JsonDocument.Parse(line);
                    var json = item.RootElement;
                    if (json.GetProperty("Message").GetString() is not { } category || !approved.TryGetValue(category, out var expected)
                        || !json.TryGetProperty("Details", out var details) || !details.TryGetProperty(expected.Field, out var value))
                        continue;
                    var properties = details.EnumerateObject().Select(property => property.Name).ToArray();
                    if (expected.Value is null
                        ? properties.SequenceEqual(new[] { expected.Field }) && value.GetString() is { Length: > 0 }
                        : value.GetString() == expected.Value
                            && properties.All(name => name is "State" or "Warning"))
                        found.Add(category);
                }
                catch (JsonException) { /* The asynchronous writer may still be appending the last line. */ }
            }
            return found.Count == approved.Count;
        }, "the typed capability fault events were not flushed");
        Check(found.Count == 3, "export, Viewer, and look storage faults publish distinct structured events with allow-listed fields");
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native support diagnostics assertions");

        static async Task WaitFor(Func<bool> predicate, string failure)
        {
            var started = Stopwatch.GetTimestamp();
            while (!predicate())
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20)) throw new TimeoutException(failure);
                await Task.Delay(35);
            }
        }
    }

    private static void TestSupportClipboard(object hosted, object panel)
    {
        var sequence = GetClipboardSequenceNumber();
        var formats = Clipboard.GetDataObject()?.GetFormats(autoConvert: false) ?? [];
        if (formats.Any(format =>
                format != DataFormats.Text && format != DataFormats.UnicodeText && format != DataFormats.OemText
                && format != DataFormats.Locale)
            || formats.Length > 0 && !Clipboard.ContainsText())
        {
            File.AppendAllText(Path.Combine(folder, "host-smoke.log"), "SKIP Windows clipboard roundtrip preserves the existing rich data." + Environment.NewLine);
            return;
        }
        var originalText = formats.Length > 0 ? Clipboard.GetText() : null;
        var type = hosted.GetType().GetNestedType("PanelActionMenu", Flags)!;
        var menu = Activator.CreateInstance(type, Flags, null,
            new object[] { (Func<string>)(() => "Float Tool"), (Action)(() => { }), panel }, null)!;
        var command = (ICommand)Prop(((IEnumerable)Call(menu, "Generate")!).Cast<object>().Last(), "Command")!;
        var copied = 0u;
        var ownsCopy = false;
        try
        {
            Check(sequence == GetClipboardSequenceNumber(), "clipboard ownership remained stable before exercising the native menu command");
            command.Execute(null);
            copied = GetClipboardSequenceNumber();
            var text = Clipboard.GetText();
            ownsCopy = copied != sequence && GetClipboardSequenceNumber() == copied
                && text.Equals((string)Call(panel, "BuildSupportDetails")!, StringComparison.Ordinal);
            Check(ownsCopy,
                "the clipboard contains the exact current support report from the native menu");
            using var json = JsonDocument.Parse(text);
            Check(json.RootElement.GetProperty("Viewer").GetString() == "ProcessingError"
                && json.RootElement.GetProperty("LookStorage").GetString() == "RecoveryCheckpoint"
                && json.RootElement.GetProperty("NativeExportRoute").GetString() == "Spektrafilm"
                && !text.Contains(folder, StringComparison.OrdinalIgnoreCase) && !text.Contains("SpektraStillRenderer.exe", StringComparison.OrdinalIgnoreCase),
                "the real native menu action writes the actual fault states and no local paths to the Windows clipboard");
        }
        finally
        {
            // Leave a newer copy from another application alone.
            if (ownsCopy && GetClipboardSequenceNumber() == copied)
            {
                if (string.IsNullOrEmpty(originalText))
                    Clipboard.Clear();
                else
                    Clipboard.SetText(originalText);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
