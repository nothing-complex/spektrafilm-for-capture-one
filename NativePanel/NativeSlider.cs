using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using P1.C1.Controls.Sliders;
using P1.C1.WPF.Styles;

internal sealed class NativeSlider : Grid
{
    private static readonly Uri StyleSource = new("/CaptureOne;component/WPF/Styles/DefaultStyle.xaml", UriKind.Relative);
    private readonly ToolSliderWPF control;
    private readonly decimal displayScale;
    private readonly double defaultValue;

    internal NativeSlider(string title, double minimum, double maximum, double initial, double step,
        string unit = "", double displayScale = 1, int? decimalPlaces = null)
    {
        this.displayScale = (decimal)displayScale;
        defaultValue = initial;
        Margin = new Thickness(0, 2, 0, 2);
        Resources.MergedDictionaries.Add(new SharedResourceDictionary { Source = StyleSource });

        var increment = (decimal)step * this.displayScale;
        var places = decimalPlaces ?? DecimalPlacesFor(increment);
        control = new ToolSliderWPF
        {
            LabelText = title,
            Minimum = ToDisplay(minimum),
            Maximum = ToDisplay(maximum),
            Increment = increment,
            DecimalPlaces = places,
            Unit = unit,
            Value = ToDisplay(initial),
        };
        // The standard box is 40 DIPs. Very small optical settings need more space.
        if (places > 4)
            control.UpDownWidth += (places - 4) * 8;
        AutomationProperties.SetName(control, title);
        var displayUnit = string.IsNullOrEmpty(unit) ? "" : " " + unit;
        AutomationProperties.SetHelpText(control,
            $"Default {ToDisplay(initial)}{displayUnit}. Double-click the label or slider to reset.");
        ToolTip = title;
        Children.Add(control);
        control.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ToolSliderWPF.Value))
                ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        control.SliderPointerDown += (_, _) => DragStarted?.Invoke(this, EventArgs.Empty);
        control.SliderPointerUp += (_, _) => DragCompleted?.Invoke(this, EventArgs.Empty);
        control.SliderMouseDoubleClick += (_, args) =>
        {
            ResetToDefault();
            args.Handled = true;
        };
        PreviewMouseLeftButtonDown += ResetOnDoubleClick;
    }

    internal double Value
    {
        get => (double)(control.Value / displayScale);
        set => control.Value = ToDisplay(Math.Clamp(value, Minimum, Maximum));
    }

    internal double Minimum => (double)(control.Minimum / displayScale);
    internal double Maximum => (double)(control.Maximum / displayScale);
    internal double Initial => defaultValue;
    internal double Increment => (double)(control.Increment / displayScale);

    internal event EventHandler? ValueChanged;
    internal event EventHandler? DragStarted;
    internal event EventHandler? DragCompleted;

    internal void ResetToDefault() => Value = defaultValue;

    private decimal ToDisplay(double value) => (decimal)value * displayScale;

    private static int DecimalPlacesFor(decimal increment)
    {
        var places = 0;
        while (decimal.Truncate(increment) != increment)
        {
            increment *= 10;
            places++;
        }
        return places;
    }

    private void ResetOnDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (args.ClickCount != 2 || args.OriginalSource is not Visual source || control.Template is not { } template)
            return;

        if (IsWithin(source, template.FindName("PART_Label", control))
            || IsWithin(source, template.FindName("PART_Slider", control)))
        {
            ResetToDefault();
            args.Handled = true;
        }
    }

    private static bool IsWithin(Visual source, object? part) =>
        part is Visual visual && (visual == source || visual.IsAncestorOf(source));
}
