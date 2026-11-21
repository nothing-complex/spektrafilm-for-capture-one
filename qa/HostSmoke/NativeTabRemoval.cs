using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

public static partial class StartupHook
{
    private static async Task TestNativeTabRemoval(Type hook, object hosted, object panel, object spatial)
    {
        const double exposure = .73;
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var nativeTool = Prop(hosted, "Tool")!;
        var tabField = hook.GetField("tab", Flags)!;
        var hostedField = hook.GetField("hostedTool", Flags)!;
        var paletteField = hook.GetField("owner", Flags)!;
        var bindingField = hook.GetField("workspaceTab", Flags)!;
        var exportField = hook.GetField("exportHook", Flags)!;
        var tab = tabField.GetValue(null)!;
        var palette = paletteField.GetValue(null)!;
        var tabs = (IList)Prop(palette, "Tabs")!;
        var manager = Field(hosted, "workspaceManager")!;
        var contract = manager.GetType().GetInterface("P1.C1.ViewModels.Workspace.IWorkspaceManager")!;
        var active = contract.GetProperty("ActiveWorkspace")!;
        var apply = contract.GetMethod("ApplyWorkspace")!;
        var original = active.GetValue(manager)!;
        var key = (string)Field(hosted, "workspaceKey")!;
        var variant = (Guid)Field(panel, "displayedVariant")!;
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var slider = Field(panel, "exposure")!;
        var value = slider.GetType().GetProperty("Value", Flags)!;
        var receiptPath = Path.Combine(folder, "native-tab-removal.json");

        Check(File.Exists(Path.Combine(folder, "native-workspace-backup.json")), "native tab removal starts with a verified workspace backup");
        if (phase == "first")
        {
            toggle.IsChecked = true;
            value.SetValue(slider, exposure);
            var quality = (ComboBox)Field(spatial, "previewSize")!;
            quality.SelectedItem = quality.ItemsSource.Cast<object>().Single(item => Equals(Prop(item, "Id"), "1200"));
            await WaitFor(() => Prop(panel, "ViewerStatus")?.ToString() == "Visible", "the film Viewer before tab removal");
            var bridge = exportField.GetValue(null)!;
            var binding = bindingField.GetValue(null)!;

            AnswerNativeRemoval(approve: false);
            Check(tabs.Contains(tab) && Prop(binding, "IsHidden") is false,
                "canceling Capture One's removal prompt leaves the original visible tab");
            AnswerNativeRemoval(approve: true);
            await WaitFor(() => !tabs.Contains(tab) && Prop(binding, "IsHidden") is true, "the confirmed tab removal notification");
            hook.GetMethod("OnTick", Flags)!.Invoke(null, new object?[] { null, EventArgs.Empty });
            Check(ReferenceEquals(hostedField.GetValue(null), hosted) && ReferenceEquals(exportField.GetValue(null), bridge)
                && Prop(nativeTool, "IsDisposed") is true && !tabs.Contains(tab),
                "the watchdog respects confirmed removal while keeping the original running film session");
            Check(Prop(panel, "ViewerStatus")?.ToString() == "Visible" && Prop(hosted, "IsSpektraExportActive") is true
                && Equals(value.GetValue(slider), exposure), "removing the tab preserves the live Viewer, film export route and saved look");

            var output = Path.Combine(folder, "native-hidden-tab-export.png");
            Check(await ((Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, null,
                (Func<SaveFileDialog, Window?, string?>)((_, _) => output))!).WaitAsync(TimeSpan.FromSeconds(50)),
                "the production export path finishes with the native tab removed");
            using (var stream = File.OpenRead(output))
            {
                var image = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad).Frames.Single();
                Check(image.Format == PixelFormats.Rgba64 && image.ColorContexts is { Count: 1 },
                    "the hidden tool exports high-depth pixels and embeds the native recipe profile");
            }

            var other = new[] { "ApplicationPackageWorkspaces", "CustomWorkspaces" }
                .SelectMany(property => ((IEnumerable)contract.GetProperty(property)!.GetValue(manager)!).Cast<object>())
                .First(item => Selector(item) != Selector(original));
            var otherHost = await SwitchTo(other);
            Check(HasLiveTab() && !ReferenceEquals(otherHost, hosted), "another native workspace still opens its own Spektrafilm tab");
            var restored = await SwitchTo(original);
            Check(!HasLiveTab() && Prop(bindingField.GetValue(null)!, "IsHidden") is true
                && Prop(Field(restored, "panel")!, "ExportBridgeStatus")?.ToString() == "Connected",
                "returning to the original workspace keeps the tab hidden and reconnects native Export");
            File.WriteAllText(receiptPath, JsonSerializer.Serialize(new { key, variant, exposure }));
        }
        else
        {
            using var receipt = JsonDocument.Parse(File.ReadAllText(receiptPath));
            Check(receipt.RootElement.GetProperty("key").GetString() == key
                && receipt.RootElement.GetProperty("variant").GetGuid() == variant
                && Equals(value.GetValue(slider), exposure) && toggle.IsChecked == true,
                "restart opens the same workspace and image with its exact acknowledged film edit");
            await WaitFor(() => Prop(panel, "ViewerStatus")?.ToString() == "Visible", "the restored film Viewer after restart");
            Check(Prop(hosted, "IsSpektraExportActive") is true && Prop(panel, "ExportBridgeStatus")?.ToString() == "Connected",
                "the restarted Viewer and native Export remain active");
            if (phase == "restart")
                Check(!HasLiveTab() && Prop(bindingField.GetValue(null)!, "IsHidden") is true,
                    "a passive restart retains the deliberate removal for that workspace");
            else
                Check(HasLiveTab() && Prop(bindingField.GetValue(null)!, "IsHidden") is false
                    && ReferenceEquals(Prop(paletteField.GetValue(null)!, "Current"), tabField.GetValue(null)),
                    "an explicit Spektrafilm launch restores exactly one tab and selects it");
        }
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native tab removal and restore assertions");

        bool HasLiveTab()
        {
            var owner = paletteField.GetValue(null)!;
            var currentTab = tabField.GetValue(null)!;
            return ((IList)Prop(owner, "Tabs")!).Cast<object>().Count(item => item.GetType() == currentTab.GetType()) == 1;
        }

        static string Selector(object workspace) => Equals(Prop(workspace, "Name"), "CustomWorkspace")
            ? (string)Prop(workspace, "Text")! : (string)Prop(workspace, "Name")!;

        async Task<object> SwitchTo(object workspace)
        {
            var selector = Selector(workspace);
            apply.Invoke(manager, new object[] { selector });
            object? current = null;
            await WaitFor(() => Selector(active.GetValue(manager)!) == selector && (current = hostedField.GetValue(null)) is not null
                && Prop(Field(current, "panel")!, "StocksReady") is true
                && Field(current, "workspaceKey") is string currentKey && currentKey.EndsWith("|" + selector, StringComparison.Ordinal),
                "the requested native workspace");
            return current!;
        }

        void AnswerNativeRemoval(bool approve)
        {
            Check(ReferenceEquals(Prop(palette, "Current"), tab), "Capture One's context menu targets the Spektrafilm tab");
            var generate = palette.GetType().GetMethod("GenerateTabMenuItems", Flags)!;
            var area = Enum.Parse(generate.GetParameters()[0].ParameterType, "ScrollableArea");
            var menu = ((IEnumerable)generate.Invoke(palette, new[] { area })!).Cast<object>().Last();
            var command = (ICommand)Prop(menu, "Command")!;
            Check(((string)Prop(menu, "DisplayName")!).Contains((string)Prop(tab, "Text")!, StringComparison.Ordinal)
                && command.CanExecute(null), "the actual native tab menu offers its removal command");
            var localization = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Localization");
            var caption = (string)localization.GetType("P1.C1.Localization.I18N", true)!
                .GetProperty("TOOL_TAB_REMOVE_WARNING_CAPTION", Flags)!.GetValue(null)!;
            var modelName = "P1.C1.Components.MessageBoxes.MessageBoxGenericViewModel";
            var answered = false;
            Exception? error = null;
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) =>
            {
                var dialogs = PresentationSource.CurrentSources.OfType<HwndSource>()
                    .Where(source => source.Dispatcher.CheckAccess()).Select(source => source.RootVisual)
                    .OfType<DependencyObject>().Select(visual => visual as Window ?? Window.GetWindow(visual)).OfType<Window>();
                var dialog = dialogs.FirstOrDefault(item => item.IsVisible && item.DataContext?.GetType().FullName == modelName
                        && Equals(Prop(item.DataContext, "TextCaption"), caption));
                if (dialog is null) return;
                timer.Stop();
                try
                {
                    var model = dialog.DataContext;
                    var action = (ICommand)Prop(model, "ButtonPressedCommand")!;
                    var button = ((IEnumerable)Prop(model, "Buttons")!).Cast<object>()
                        .Single(item => Convert.ToInt32(Prop(item, "Result")) == (approve ? 1 : 2));
                    action.Execute(button);
                    answered = true;
                }
                catch (Exception failure) { error = failure; dialog.Close(); }
            };
            timer.Start();
            try { command.Execute(null); }
            finally { timer.Stop(); }
            if (error is not null) throw error;
            Check(answered, approve ? "confirmed the actual native removal prompt" : "canceled the actual native removal prompt");
        }

        static async Task WaitFor(Func<bool> predicate, string description)
        {
            var started = Stopwatch.GetTimestamp();
            while (!predicate())
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(22))
                    throw new TimeoutException("Did not observe " + description + ".");
                await Task.Delay(40);
            }
        }
    }
}
