using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

public static partial class StartupHook
{
    private static async Task TestNativeSliderInput(Type hook, object panel, object store, Guid variant, object exposure)
    {
        if (Field(exposure, "control") is not Control control) return;
        var palette = hook.GetField("owner", Flags)!.GetValue(null)!;
        var priorTab = Prop(palette, "Current");
        var ownTab = hook.GetField("tab", Flags)!.GetValue(null)!;
        var select = hook.GetMethod("SelectTab", Flags)!;
        var value = exposure.GetType().GetProperty("Value", Flags)!;
        var original = (double)value.GetValue(exposure)!;
        var saveError = (TextBlock)Field(panel, "saveError")!;

        void Confirm(double expected, string action)
        {
            var saved = Prop(Prop(Call(store, "Read", variant)!, "Look")!, "Exposure");
            Check(Equals(value.GetValue(exposure), expected) && Equals(saved, expected)
                && saveError.Visibility != Visibility.Visible, $"{action} (expected {expected}, slider {value.GetValue(exposure)}, saved {saved})");
        }

        try
        {
            select.Invoke(null, new[] { palette, ownTab });
            await control.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            control.ApplyTemplate();
            var label = (FrameworkElement)Prop(control, "Label")!;
            var slider = (FrameworkElement)Prop(control, "Slider")!;
            var input = (Control)Prop(control, "Input")!;
            Check(control.IsVisible && control.IsEnabled && label.IsVisible && slider.IsVisible,
                "Capture One created the visible native slider label, track and numeric editor");
            var clickCount = typeof(MouseButtonEventArgs).GetProperty("ClickCount", Flags)!.GetSetMethod(nonPublic: true)!;
            void DoubleClick(FrameworkElement target)
            {
                var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent,
                    Source = target,
                };
                clickCount.Invoke(click, new object[] { 2 });
                target.RaiseEvent(click);
            }

            value.SetValue(exposure, 1.4);
            DoubleClick(label);
            Confirm(0, "native label double click resets film exposure and durably saves its default");
            value.SetValue(exposure, -1.1);
            DoubleClick(slider);
            Confirm(0, "native slider track double click resets film exposure and durably saves its default");

            input.ApplyTemplate();
            var editor = FindEditor(input) ?? throw new InvalidOperationException("The live native slider has no numeric text box.");
            editor.Focus();
            var presentation = PresentationSource.FromVisual(editor)
                ?? throw new InvalidOperationException("The numeric editor is detached from the native presentation source.");
            Check(AutomationProperties.GetName(control) == "Film exposure" && AutomationProperties.GetHelpText(control).Contains("Double-click", StringComparison.Ordinal),
                "visible film slider provides its full accessible name and reset instructions");
            var peer = UIElementAutomationPeer.CreatePeerForElement(editor);
            Check(peer?.GetPattern(PatternInterface.Value) is System.Windows.Automation.Provider.IValueProvider provider && !provider.IsReadOnly,
                "the actual Capture One numeric editor exposes the editable UI Automation Value pattern");
            var culture = input.GetType().GetProperty("CultureInfo", Flags)!;
            var previousCulture = culture.GetValue(input);
            async Task RouteKey(string text, Key key)
            {
                editor.SetCurrentValue(TextBox.TextProperty, text);
                foreach (var routedEvent in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
                    editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, key)
                    {
                        RoutedEvent = routedEvent,
                        Source = editor,
                    });
                await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            try
            {
                foreach (var (language, number, expected) in new[] { ("da-DK", "1,4", 1.4), ("en-US", "-0.8", -0.8) })
                {
                    culture.SetValue(input, CultureInfo.GetCultureInfo(language));
                    await RouteKey(number, Key.Enter);
                    Confirm(expected, $"native Enter commits a {language} decimal to the durable film look");
                }
                await RouteKey("8", Key.Enter);
                Confirm(8, "native editor accepts the exact positive exposure bound");
                await RouteKey("-8", Key.Enter);
                Confirm(-8, "native editor accepts the exact negative exposure bound");
                var path = (string)Call(store, "GetPath", variant)!;
                var before = File.ReadAllBytes(path);
                await RouteKey("2.4", Key.Escape);
                Confirm(-8, "Escape discards uncommitted numeric text");
                Check(File.ReadAllBytes(path).SequenceEqual(before), "canceling a numeric change keeps the committed bytes");
                foreach (var invalid in new[] { "NaN", "Infinity", "exposure?" })
                {
                    await RouteKey(invalid, Key.Enter);
                    Confirm(-8, $"native editor rejects malformed value {invalid}");
                }
                Check(File.ReadAllBytes(path).SequenceEqual(before), "invalid numeric text never writes a saved look");

                await RouteKey("0", Key.Enter);
                await RouteKey("0", Key.Up);
                Confirm(.1, "the native numeric editor increments film exposure by one tick");
                await RouteKey("0.1", Key.Down);
                Confirm(0, "the native numeric editor decrements film exposure by one tick");

                var defaults = FindButton((DependencyObject)panel, "Defaults")
                    ?? throw new InvalidOperationException("The visible panel has no Defaults command.");
                Check(editor.Focus() && editor.IsKeyboardFocused, "the numeric editor receives keyboard focus before editing");
                editor.SetCurrentValue(TextBox.TextProperty, "0.6");
                Check(defaults.Focus(), "keyboard focus can move directly from the editor to the Defaults button");
                await defaults.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Confirm(.6, "leaving the numeric editor commits the pending film exposure");

                object Snapshot() => Call(panel, "CaptureLook")!;
                bool Same(object left, object right) => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(left, left.GetType()),
                    JsonSerializer.SerializeToNode(right, right.GetType()));
                var previous = Snapshot();
                defaults.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Confirm(0, "clicking Defaults resets the durable exposure through the real button");
                var reset = Snapshot();
                var undo = Field(panel, "nativeUndo")!;
                var model = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.ModelEntities");
                var contract = model.GetType("P1.C1.ModelEntities.UndoInterfaces.IUndoManager", true)!;
                var revert = contract.GetMethod("Undo", new[] { typeof(bool) })!;
                Check(contract.GetProperty("TryGetUndoActionName")!.GetValue(undo) is "Spektrafilm defaults",
                    "the Defaults button registered one named native undo action");
                revert.Invoke(undo, new object[] { true });
                Check(Same(Snapshot(), previous), "Undo restores the look captured before Defaults");
                contract.GetMethod("Redo")!.Invoke(undo, null);
                Check(Same(Snapshot(), reset), "Redo restores the complete native Defaults result");
                revert.Invoke(undo, new object[] { false });
            }
            finally { culture.SetValue(input, previousCulture); }
        }
        finally
        {
            value.SetValue(exposure, original);
            select.Invoke(null, new[] { palette, priorTab });
        }
        Confirm(original, "native input fixture restored the selected image's original film exposure");
    }

    private static TextBox? FindEditor(DependencyObject root)
    {
        if (root is TextBox editor) return editor;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindEditor(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        return null;
    }

    private static Button? FindButton(DependencyObject root, string content)
    {
        if (root is Button button && Equals(button.Content, content)) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindButton(VisualTreeHelper.GetChild(root, index), content) is { } child) return child;
        return null;
    }
}
