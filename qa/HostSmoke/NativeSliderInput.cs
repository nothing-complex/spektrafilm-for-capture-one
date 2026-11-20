using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
            var culture = input.GetType().GetProperty("CultureInfo", Flags)!;
            var previousCulture = culture.GetValue(input);
            try
            {
                foreach (var (language, number, expected) in new[] { ("da-DK", "1,4", 1.4), ("en-US", "-0.8", -0.8) })
                {
                    culture.SetValue(input, CultureInfo.GetCultureInfo(language));
                    editor.SetCurrentValue(TextBox.TextProperty, number);
                    foreach (var routedEvent in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent, Keyboard.KeyUpEvent })
                    {
                        editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Enter)
                        {
                            RoutedEvent = routedEvent,
                            Source = editor,
                        });
                    }
                    await editor.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Confirm(expected, $"native Enter commits a {language} decimal to the durable film look");
                }
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
}
