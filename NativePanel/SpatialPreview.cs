using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

internal sealed class SpatialPreview : IDisposable
{
    private const int InitialPreviewLongEdge = 1200;
    private const int DragSettleDelay = 220;
    private static readonly InputChoice[] InputSpaces =
    {
        new(PreviewInputSpace.Srgb, "srgb", "sRGB"),
        new(PreviewInputSpace.AdobeRgb, "adobe_rgb", "Adobe RGB (1998)"),
        new(PreviewInputSpace.ProPhotoRgb, "prophoto", "ProPhoto RGB"),
        new(PreviewInputSpace.LinearRec2020, "linear_rec2020", "Linear Rec.2020"),
    };
    private static readonly Choice[] OutputSpaces =
    {
        new("srgb", "sRGB"), new("rec709_gamma24", "Rec.709 gamma 2.4"), new("linear_rec709", "Linear Rec.709"),
    };
    private static readonly Choice[] PreviewSizes =
    {
        new("native", "Native resolution"), new("auto", "Auto (up to 4096 px)"), new("4096", "4096 px"),
        new("3072", "3072 px"), new("2560", "2560 px"),
        new("2048", "2048 px"), new("1600", "1600 px"), new("1200", "1200 px"), new("900", "900 px"), new("720", "720 px"),
    };
    private static readonly DragSize[] DragSizes =
    {
        new(0, "Off"), new(720, "720 px"), new(1200, "1200 px"), new(1600, "1600 px"),
        new(2048, "2048 px"), new(2560, "2560 px"),
    };
    private static readonly DragSize DefaultDragSize = DragSizes[2];
    private static readonly Choice[] DiffusionFamilies =
    {
        new("off", "Off"), new("black_pro_mist", "Black Pro-Mist"), new("glimmerglass", "Glimmerglass"),
        new("pro_mist", "Pro-Mist"), new("cinebloom", "CineBloom"),
    };
    private readonly object window;
    private readonly Func<LookSettings?> getSettings;
    private readonly Func<Guid, bool> useSelectedLook;
    private readonly Dictionary<string, NativeSlider> values = new();
    private readonly Dictionary<string, ComboBox> choices = new();
    private readonly Dictionary<string, CheckBox> toggles = new();
    private readonly Settings defaults;
    private readonly TextBlock status = new() { Margin = new Thickness(0, 5, 0, 3), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly ViewerPreview viewer;
    private readonly ExportJobJournal exportJobs;
    private readonly ViewportQuality viewportQuality = new();
    private readonly ViewerHistogramControl histogram = new();
    private OpticalRenderer? renderer;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? pendingDraft;
    private OpticalRenderer? draftRenderer;
    private HostImageSource.Frame? draftSource;
    private byte[]? draftPixels;
    private string[]? draftArguments;
    private bool draftWarmupAttempted;
    private int lastDraftRevision = -1;
    private bool dragging;
    private long lastDragChange;
    private HostImageSource.Frame? source;
    private NativeImageRevision? failedRevision;
    private readonly PreviewRendererRetry<(Guid Variant, int Generation, NativeImageRevision Source)> rendererRetry = new();
    private byte[]? renderedPixels;
    private string[]? renderedArguments;
    private BitmapSource? sourceDisplay;
    private BitmapSource? renderedDisplay;
    private double renderedStrength;
    private PreviewInputSpace? failedSpace;
    private readonly ComboBox inputSpace = new()
    {
        DisplayMemberPath = nameof(InputChoice.Label),
        ItemsSource = InputSpaces,
        SelectedIndex = 0,
    };
    private readonly ComboBox outputSpace = new() { DisplayMemberPath = nameof(Choice.Label), ItemsSource = OutputSpaces, SelectedIndex = 0 };
    private readonly ComboBox previewSize = new() { DisplayMemberPath = nameof(Choice.Label), ItemsSource = PreviewSizes, SelectedIndex = 0 };
    private readonly ComboBox dragPreviewSize = new()
    {
        DisplayMemberPath = nameof(DragSize.Label),
        ItemsSource = DragSizes,
        SelectedItem = DefaultDragSize,
        ToolTip = "Long edge of the temporary frame while dragging when the selected Viewer resolution is higher.",
    };
    private readonly CheckBox showSource = new() { Content = "Show source", Margin = new Thickness(0, 4, 0, 1), IsEnabled = false };
    private readonly Button holdCompare = new() { Content = "Hold to compare", Margin = new Thickness(8, 4, 0, 1) };
    private static readonly DependencyPropertyDescriptor ComparePress = DependencyPropertyDescriptor.FromProperty(Button.IsPressedProperty, typeof(Button));
    private readonly Button savePreview = new() { Content = "Save preview PNG…", Margin = new Thickness(0, 4, 0, 1), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
    private readonly CheckBox fastSynthesis = new()
    {
        Content = "Draft synthesis preview",
        ToolTip = "Uses up to two samples. Draft grain can look substantially noisier. Uncheck to see your selected sampling quality.",
        Margin = new Thickness(0, 4, 0, 1),
    };
    private bool queued;
    private bool loading;
    private bool enabled;
    private Guid? activeVariant;
    private BitmapSource? viewerFrame;
    private PreviewInputSpace? checkedProfileSpace;
    private string? checkedProfileHash;
    private long nextProfileCheck;

    private long nextViewerCheck;
    private int inputRevision;
    private string? sourceSize;
    private string? failedSize;
    private bool frameCurrent;
    private bool saveInProgress;
    private ExportOperation? exportWork;
    private bool lutRenderActive;
    private Task previewWorkersExited = Task.CompletedTask;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock exportStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button cancelExport = new() { Content = "Cancel export", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private bool disposed;
    private bool renderingFinal;
    private bool selectionUnavailable;
    private bool originalUnavailable;
    private string? previewDetail;

    private SpatialPreview(object window, Func<LookSettings?> getSettings, Func<Guid, bool> useSelectedLook, ExportJobJournal exportJobs)
    {
        this.window = window;
        this.getSettings = getSettings;
        this.useSelectedLook = useSelectedLook;
        this.exportJobs = exportJobs;
        viewer = new ViewerPreview(window);
        viewer.StatusChanged += UpdateViewerStatus;
        viewer.PixelChanged += histogram.SetSample;
        histogram.InspectionChanged += viewer.InspectPixels;
        histogram.ClippingChanged += viewer.ShowClipping;
        var liveControls = new StackPanel();
        liveControls.Children.Add(status);
        var actions = new WrapPanel();
        actions.Children.Add(showSource);
        holdCompare.SetBinding(UIElement.IsEnabledProperty, new Binding(nameof(UIElement.IsEnabled)) { Source = showSource });
        AutomationProperties.SetHelpText(holdCompare, "Hold the mouse button or Space to show the developed source.");
        actions.Children.Add(holdCompare);
        ComparePress.AddValueChanged(holdCompare, OnComparePressChanged);
        savePreview.Margin = new Thickness(8, 4, 0, 1);
        actions.Children.Add(savePreview);
        liveControls.Children.Add(actions);
        AddResolutionControls(liveControls);
        liveControls.Children.Add(histogram.Section);
        LiveControls = liveControls;
        Element = Build();
        var preferences = PanelPreferences.Current;
        loading = true;
        previewSize.SelectedItem = PreviewSizes.FirstOrDefault(x => x.Id == preferences.ViewerSize) ?? PreviewSizes[0];
        dragPreviewSize.SelectedItem = DragSizes.FirstOrDefault(x => x.LongEdge == preferences.DragSize) ?? DefaultDragSize;
        fastSynthesis.IsChecked = preferences.DraftSynthesis;
        loading = false;
        defaults = CaptureSettings();
        refresh.Tick += OnRefreshTick;
        showSource.Checked += (_, _) => RequestRefresh();
        showSource.Unchecked += (_, _) => RequestRefresh();
        savePreview.Click += async (_, _) => await SavePreviewAsync();
        var exportControls = new StackPanel();
        exportStatus.Text = DescribePreviousExport(exportJobs.ReadLatest());
        exportControls.Children.Add(exportStatus);
        exportControls.Children.Add(cancelExport);
        cancelExport.Click += (_, _) => exportWork?.Cancel();
        ExportControls = exportControls;
    }

    internal FrameworkElement Element { get; }
    internal FrameworkElement LiveControls { get; }
    internal FrameworkElement ExportControls { get; }
    internal string? VerifiedRendererAssets => renderer?.AssetContract ?? draftRenderer?.AssetContract;

    internal ViewerHealth Health => !enabled ? ViewerHealth.Disabled
        : failedRevision is not null ? ViewerHealth.ProcessingError
        : pending is not null || pendingDraft is not null ? ViewerHealth.Rendering
        : viewer.HasPainted ? ViewerHealth.Visible : ViewerHealth.WaitingForImage;

    internal event Action? SettingsChanged;
    internal event Action<bool>? SliderDragActivity;
    private async void OnRefreshTick(object? sender, EventArgs args)
    {
        try { await RefreshAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!disposed) ClearFrame(error); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        enabled = false;
        lifetime.Cancel();
        lifetime.Dispose();
        exportWork?.Cancel();
        ClearPreview();
        refresh.Tick -= OnRefreshTick;
        ComparePress.RemoveValueChanged(holdCompare, OnComparePressChanged);
        viewer.StatusChanged -= UpdateViewerStatus;
        viewer.Dispose();
        histogram.Dispose();
    }
    internal bool CompareSource
    {
        get => showSource.IsChecked == true || holdCompare.IsPressed && holdCompare.IsEnabled;
        set => showSource.IsChecked = value;
    }

    private void OnComparePressChanged(object? sender, EventArgs args)
    {
        if (showSource.IsChecked != true) RequestRefresh();
    }
    private int DragLongEdge => ((DragSize)dragPreviewSize.SelectedItem).LongEdge;

    internal static SpatialPreview BuildForPanel(object window, Func<LookSettings?> getSettings, Func<Guid, bool> useSelectedLook,
        ExportJobJournal exportJobs) => new(window, getSettings, useSelectedLook, exportJobs);

    internal void VerifyRendererOptions(RendererParameters renderer)
    {
        renderer.VerifyOptions("input-color", InputSpaces.Select(choice => choice.Id));
        renderer.VerifyOptions("output-color", OutputSpaces.Select(choice => choice.Id));
        foreach (var (key, selector) in choices)
            renderer.VerifyOptions(key, ((Choice[])selector.ItemsSource).Select(choice => choice.Id));
    }

    internal Settings CaptureSettings()
    {
        var numbers = new Dictionary<string, double>();
        var selections = new Dictionary<string, string>();
        var switches = new Dictionary<string, bool>();
        foreach (var (key, slider) in values)
            numbers.Add(key, slider.Value);
        foreach (var (key, chooser) in choices)
            selections.Add(key, ((Choice)chooser.SelectedItem).Id);
        selections.Add("input-color", ((InputChoice)inputSpace.SelectedItem).Id);
        selections.Add("output-color", ((Choice)outputSpace.SelectedItem).Id);
        foreach (var (key, item) in toggles)
            switches.Add(key, item.IsChecked == true);
        return new Settings(numbers, selections, switches);
    }

    internal void LoadSettings(Settings? settings)
    {
        loading = true;
        try
        {
            LoadControlValues(settings ?? defaults);
        }
        finally
        {
            loading = false;
        }
        RequestRefresh();
    }

    internal void ValidateSettings(Settings settings)
    {
        foreach (var (key, value) in settings.Numbers)
            if (!values.TryGetValue(key, out var slider) || !double.IsFinite(value)
                || value < slider.Minimum || value > slider.Maximum)
                throw new InvalidDataException($"Unsupported saved optical value: {key}.");
        foreach (var (key, value) in settings.Selections)
        {
            var valid = key switch
            {
                "input-color" => InputSpaces.Any(x => x.Id == value),
                "output-color" => OutputSpaces.Any(x => x.Id == value),
                // Accept older looks without making hardware preferences part of a look again.
                "preview-size" => PreviewSizes.Any(x => x.Id == value),
                "drag-preview-size" => DragSizes.Any(x => x.LongEdge.ToString(CultureInfo.InvariantCulture) == value),
                _ => choices.TryGetValue(key, out var chooser) && ((Choice[])chooser.ItemsSource).Any(x => x.Id == value),
            };
            if (!valid) throw new InvalidDataException($"Unsupported saved optical choice: {key}={value}.");
        }
        foreach (var key in settings.Switches.Keys)
            if (!toggles.ContainsKey(key) && key is not ("show-source" or "draft-synthesis" or "quick-drag-preview"))
                throw new InvalidDataException($"Unsupported saved optical switch: {key}.");
    }

    private void LoadControlValues(Settings settings)
    {
        foreach (var (key, slider) in values)
        {
            var value = settings.Numbers is { } numbers && numbers.TryGetValue(key, out var saved)
                ? saved : defaults.Numbers[key];
            slider.Value = double.IsFinite(value) && value >= slider.Minimum && value <= slider.Maximum
                ? value : defaults.Numbers[key];
        }
        foreach (var (key, chooser) in choices)
        {
            var baseline = defaults.Selections[key];
            var id = settings.Selections?.GetValueOrDefault(key) ?? baseline;
            var options = (Choice[])chooser.ItemsSource;
            chooser.SelectedItem = Array.Find(options, item => item.Id == id) ?? Array.Find(options, item => item.Id == baseline);
        }
        var colorId = settings.Selections?.GetValueOrDefault("input-color");
        inputSpace.SelectedItem = InputSpaces.FirstOrDefault(space => space.Id == colorId) ?? InputSpaces[0];
        outputSpace.SelectedItem = OutputSpaces.FirstOrDefault(item => item.Id == settings.Selections?.GetValueOrDefault("output-color")) ?? OutputSpaces[0];
        foreach (var (key, item) in toggles)
            item.IsChecked = settings.Switches is { } switches && switches.TryGetValue(key, out var saved) ? saved : defaults.Switches[key];
        // Compare and hardware quality settings are UI preferences, not part of a look.
        CompareSource = false;
    }

    private FrameworkElement Build()
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "Live optical Viewer uses Capture One’s development settings as input to Spektrafilm.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 1),
        });

        var color = new StackPanel();
        color.Children.Add(new TextBlock { Text = "Input color space", Margin = new Thickness(0, 4, 0, 1) });
        AutomationProperties.SetName(inputSpace, "Input color space");
        inputSpace.SelectionChanged += (_, _) => OnSettingsChanged();
        color.Children.Add(inputSpace);
        AddPreviewChoice(color, outputSpace, "Render output encoding");
        body.Children.Add(Section("optics-color-management", "Color management", color));

        var metering = new StackPanel();
        AddToggle(metering, "auto-exposure", "Automatic film exposure");
        AddChoice(metering, "auto-exposure-meter", "Metering", new[]
        {
            new Choice("center_weighted", "Center weighted"), new Choice("median", "Median"),
        });
        body.Children.Add(Section("optics-metering", "Exposure metering", metering));

        var halation = new StackPanel();
        AddSlider(halation, "halation-amount", "Halation amount", 0, 4, 0, 0.05);
        AddSlider(halation, "halation-scale", "Halation spread", 0, 4, 1, 0.05);
        AddSlider(halation, "scatter-amount", "Emulsion scatter", 0, 2, 0, 0.05);
        AddSlider(halation, "scatter-scale", "Emulsion scatter spread", 0, 4, 1, 0.05);
        AddSlider(halation, "halation-boost", "Highlight boost (EV)", 0, 20, 0, 0.1);
        AddSlider(halation, "halation-boost-range", "Highlight boost range", 0, 1, 0.3, 0.01);
        AddSlider(halation, "halation-protect", "Highlight protection (EV)", 0, 10, 4, 0.1);
        var halationChannels = new StackPanel();
        var customHalation = AddToggle(halation, "custom-halation-rgb", "Override stock halation channels");
        SetToggleEnabled(customHalation, halationChannels);
        AddSlider(halationChannels, "halation-red", "Red strength", 0, 1, 0.05, 0.005);
        AddSlider(halationChannels, "halation-green", "Green strength", 0, 1, 0.015, 0.005);
        AddSlider(halationChannels, "halation-blue", "Blue strength", 0, 1, 0, 0.005);
        halation.Children.Add(halationChannels);
        body.Children.Add(Section("optics-halation", "Halation and scatter", halation));
        body.Children.Add(MakeDiffusion("camera", "Camera diffusion"));
        body.Children.Add(MakeDiffusion("print", "Print diffusion"));

        var grain = new StackPanel();
        AddChoice(grain, "grain-model", "Grain model", new[]
        {
            new Choice("off", "Off"), new Choice("preview", "Preview"), new Choice("production", "Production"),
            new Choice("synthesis", "Grain synthesis"),
        });
        AddSlider(grain, "grain-amount", "Grain amount", 0, 2, 1, 0.05);
        AddSlider(grain, "grain-saturation", "Grain saturation", 0, 1, 1, 0.05);
        AddSlider(grain, "grain-final-blur", "Final blur (µm)", 0, 25, 7.17, 0.01);
        AddChoice(grain, "film-format", "Film format", new[]
        {
            new Choice("8", "8 mm"), new Choice("super8", "Super 8"), new Choice("16", "16 mm"),
            new Choice("super16", "Super 16"), new Choice("35", "35 mm"), new Choice("super35", "Super 35"),
            new Choice("65", "65 mm"), new Choice("70", "IMAX 70"),
        }, 4);
        AddSlider(grain, "grain-seed", "Seed", 0, 32767, 1, 1);
        var particle = new StackPanel();
        AddToggle(particle, "grain-sublayers", "Use emulsion sublayers", true);
        AddSlider(particle, "grain-sublayer-count", "Sublayers", 1, 8, 1, 1);
        AddSlider(particle, "grain-particle-area", "Particle area (µm²)", 0.01, 5, 0.1, 0.01);
        AddSlider(particle, "grain-dye-cloud-blur", "Dye cloud blur (µm)", 0, 10, 1, 0.1);
        AddSlider(particle, "grain-microstructure", "Microstructure scale", 0, 4, 0.2, 0.05);
        AddSlider(particle, "grain-microstructure-sigma", "Microstructure width (nm)", 0, 500, 30, 1);
        AddChannelSliders(particle, "grain-particle-scale", "Particle scale", 0.01, 20, (1.2, 1, 2.5), 0.01);
        AddChannelSliders(particle, "grain-density-min", "Minimum density", 0, 1, (0.04, 0.05, 0.06), 0.01);
        AddChannelSliders(particle, "grain-uniformity", "Uniformity", 0, 1, (0.99, 0.97, 0.98), 0.01);
        AddLayerSliders(particle, "grain-layer-scale", (6, 1, 0.4));
        grain.Children.Add(Section("optics-particle-grain", "Particle grain", particle));
        var synthesis = new StackPanel();
        fastSynthesis.Checked += (_, _) => OnPreferencesChanged();
        fastSynthesis.Unchecked += (_, _) => OnPreferencesChanged();
        synthesis.Children.Add(fastSynthesis);
        AddToggle(synthesis, "grain-synthesis-layered", "Layered grain", true);
        AddSlider(synthesis, "grain-synthesis-size", "Size", 0.25, 4, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-amount", "Amount", 0, 3, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-sharpness", "Sharpness", 0.25, 4, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-quality", "Quality", 0.25, 4, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-samples", "Samples", 1, 2048, 128, 1);
        AddSlider(synthesis, "grain-synthesis-radius", "Mean radius (µm)", 0.05, 10, 0.25, 0.01);
        AddSlider(synthesis, "grain-synthesis-radius-sd", "Radius variation", 0, 1, 0, 0.01);
        AddSlider(synthesis, "grain-synthesis-observation", "Observation width (µm)", 0, 20, 1, 0.1);
        AddSlider(synthesis, "grain-synthesis-cell", "Cell size ratio", 0.25, 2, 1, 0.05);
        AddSlider(synthesis, "grain-synthesis-radius-quantile", "Radius quantile", 0.95, 0.9999, 0.999, 0.0001);
        AddSlider(synthesis, "grain-synthesis-epsilon", "Coverage tolerance", 0.000001, 0.01, 0.0001, 0.000001);
        AddSlider(synthesis, "grain-synthesis-cell-cap", "Max grains per cell", 1, 128, 32, 1);
        AddChannelSliders(synthesis, "grain-synthesis-radius-scale", "Radius scale", 0.01, 20, (1.2, 1, 2.5), 0.01);
        AddLayerSliders(synthesis, "grain-synthesis-layer-scale", (6, 1, 0.4));
        grain.Children.Add(Section("optics-grain-synthesis", "Grain synthesis", synthesis));
        body.Children.Add(Section("optics-grain", "Spektrafilm grain", grain));

        var scanner = new StackPanel();
        AddToggle(scanner, "scanner-enabled", "Enable scanner controls");
        AddToggle(scanner, "scanner-white", "Correct white level");
        AddSlider(scanner, "scanner-white-level", "White point", 0, 1, 0.98, 0.01);
        AddToggle(scanner, "scanner-black", "Correct black level");
        AddSlider(scanner, "scanner-black-level", "Black point", 0, 1, 0.01, 0.01);
        AddSlider(scanner, "scanner-glare", "Scanner glare", 0, 0.2, 0.03, 0.001);
        AddSlider(scanner, "scanner-glare-roughness", "Glare roughness", 0, 4, 0.7, 0.05);
        AddSlider(scanner, "scanner-glare-blur", "Glare blur", 0, 32, 0.5, 0.1);
        AddSlider(scanner, "scanner-mtf", "Scanner MTF50 (lp/mm)", 0, 300, 60, 1);
        AddSlider(scanner, "scanner-unsharp-radius", "Unsharp radius (µm)", 0, 100, 5, 0.5);
        AddSlider(scanner, "scanner-unsharp-amount", "Unsharp amount", 0, 4, 0.7, 0.05);
        body.Children.Add(Section("optics-scanning", "Scanning", scanner));

        var geometry = new StackPanel();
        var scale = AddSlider(geometry, "enlarger-scale", "Enlarger scale", 1, 32, 1, 0.1);
        scale.ToolTip =
            "1.0 keeps the original framing. Higher values magnify the image inside the frame.";
        AddSlider(geometry, "enlarger-x", "Horizontal offset (%)", -100, 100, 0, 1);
        AddSlider(geometry, "enlarger-y", "Vertical offset (%)", -100, 100, 0, 1);
        geometry.PreviewMouseWheel += ScrollPastFramingControls;
        body.Children.Add(Section("optics-enlarger", "Enlarger positioning", geometry));

        var couplers = new StackPanel();
        AddSlider(couplers, "dir-diffusion", "Diffusion width (µm)", 0, 100, 20, 0.5);
        AddSlider(couplers, "dir-tail-size", "Tail width (µm)", 0, 1000, 200, 1);
        AddSlider(couplers, "dir-tail-weight", "Tail weight", 0, 1, 0.06, 0.01);
        var couplerChannels = new StackPanel();
        var customCouplers = AddToggle(couplers, "custom-dir-rgb", "Override stock coupler channels");
        SetToggleEnabled(customCouplers, couplerChannels);
        AddChannelSliders(couplerChannels, "dir-gamma", "Same-layer gamma", 0, 1, (0.341, 0.319, 0.273), 0.001);
        foreach (var (from, to, baseline) in new[]
        {
            ("r", "g", 0.353), ("r", "b", 0.302), ("g", "r", 0.154),
            ("g", "b", 0.353), ("b", "r", 0.168), ("b", "g", 0.226),
        })
            AddSlider(couplerChannels, $"dir-{from}-to-{to}", $"{from.ToUpperInvariant()} → {to.ToUpperInvariant()} coupling", 0, 1, baseline, 0.001);
        couplers.Children.Add(couplerChannels);
        body.Children.Add(Section("optics-couplers", "Coupler diffusion", couplers));

        var adaptation = new StackPanel();
        AddToggle(adaptation, "color-adaptation", "Color adaptation");
        AddToggle(adaptation, "color-input-compression", "Input spectral gamut compression", true);
        AddToggle(adaptation, "color-curve-smoothing", "Density curve smoothing", true);
        AddToggle(adaptation, "color-output-lightness-compression", "Output lightness compression", true);
        AddToggle(adaptation, "color-output-chroma-compression", "Output chroma compression", true);
        body.Children.Add(Section("optics-color-adaptation", "Color adaptation", adaptation));

        return Section("optics", "Spektrafilm optics", body);
    }

    private Expander MakeDiffusion(string stage, string name)
    {
        var section = new StackPanel();
        AddChoice(section, stage + "-diffusion-family", "Filter", DiffusionFamilies);
        AddSlider(section, stage + "-diffusion-strength", "Filter strength", 0, 2, 0.5, 0.125);
        AddSlider(section, stage + "-diffusion-scale", "Spatial scale", 0, 4, 1, 0.05);
        AddSlider(section, stage + "-diffusion-warmth", "Halo warmth", -1.5, 1.5, 0, 0.05);
        foreach (var (part, label) in new[] { ("core", "Core"), ("halo", "Halo"), ("bloom", "Bloom") })
        {
            AddSlider(section, $"{stage}-diffusion-{part}-intensity", label + " intensity", 0, 4, 1, 0.05);
            AddSlider(section, $"{stage}-diffusion-{part}-size", label + " size", 0.1, 4, 1, 0.05);
        }
        return Section("optics-" + stage + "-diffusion", name, section);
    }

    private static Expander Section(string key, string title, UIElement content) =>
        PanelPreferences.CreateSection(key, title, content, 5);

    private void AddChannelSliders(Panel panel, string key, string label, double min, double max,
        (double Red, double Green, double Blue) initial, double step)
    {
        AddSlider(panel, key + "-r", label + " · R", min, max, initial.Red, step);
        AddSlider(panel, key + "-g", label + " · G", min, max, initial.Green, step);
        AddSlider(panel, key + "-b", label + " · B", min, max, initial.Blue, step);
    }

    private void AddLayerSliders(Panel panel, string key, (double Layer0, double Layer1, double Layer2) initial)
    {
        AddSlider(panel, key + "-0", "Layer 1 scale", 0.01, 20, initial.Layer0, 0.01);
        AddSlider(panel, key + "-1", "Layer 2 scale", 0.01, 20, initial.Layer1, 0.01);
        AddSlider(panel, key + "-2", "Layer 3 scale", 0.01, 20, initial.Layer2, 0.01);
    }

    private CheckBox AddToggle(Panel panel, string key, string label, bool initial = false)
    {
        var item = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(0, 3, 0, 2) };
        item.Checked += (_, _) => OnSettingsChanged();
        item.Unchecked += (_, _) => OnSettingsChanged();
        toggles.Add(key, item);
        panel.Children.Add(item);
        return item;
    }

    private static void SetToggleEnabled(CheckBox toggle, FrameworkElement controls)
    {
        controls.IsEnabled = toggle.IsChecked == true;
        toggle.Checked += (_, _) => controls.IsEnabled = true;
        toggle.Unchecked += (_, _) => controls.IsEnabled = false;
    }

    private static void ScrollPastFramingControls(object sender, MouseWheelEventArgs args)
    {
        // Pass wheel input to the enclosing scroll area before it reaches a slider.
        args.Handled = true;
        ((UIElement)sender).RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
        });
    }

    private NativeSlider AddSlider(Panel panel, string key, string title, double lower, double upper, double value, double step)
    {
        var control = new NativeSlider(title, lower, upper, value, step);
        TrackSliderDrag(control);
        control.ValueChanged += (_, _) => OnSettingsChanged();
        values.Add(key, control);
        panel.Children.Add(control);
        return control;
    }

    private void AddChoice(Panel panel, string key, string title, Choice[] items, int selected = 0)
    {
        var chooser = new ComboBox { DisplayMemberPath = nameof(Choice.Label), ItemsSource = items, SelectedIndex = selected };
        AutomationProperties.SetName(chooser, title);
        chooser.SelectionChanged += (_, _) => OnSettingsChanged();
        choices.Add(key, chooser);
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 4, 0, 1) });
        panel.Children.Add(chooser);
    }

    private void AddPreviewChoice(Panel panel, ComboBox chooser, string title)
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 4, 0, 1) });
        AutomationProperties.SetName(chooser, title);
        chooser.SelectionChanged += (_, _) => OnSettingsChanged();
        panel.Children.Add(chooser);
    }

    private void AddResolutionControls(Panel panel)
    {
        var resolutions = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        resolutions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resolutions.ColumnDefinitions.Add(new ColumnDefinition());
        AddRow(0, "Viewer resolution", previewSize);
        AddRow(1, "Drag preview resolution", dragPreviewSize);
        previewSize.SelectionChanged += (_, _) => OnPreferencesChanged();
        dragPreviewSize.SelectionChanged += (_, _) =>
        {
            if (DragLongEdge == 0)
                EndSliderDrag();
            ResetDraft(keepRenderer: DragLongEdge > 0);
            OnPreferencesChanged();
        };
        panel.Children.Add(resolutions);

        void AddRow(int row, string title, ComboBox chooser)
        {
            resolutions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetLabeledBy(chooser, label);
            AutomationProperties.SetName(chooser, title);
            chooser.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(label, row);
            Grid.SetRow(chooser, row);
            Grid.SetColumn(chooser, 1);
            resolutions.Children.Add(label);
            resolutions.Children.Add(chooser);
        }
    }

    private void OnPreferencesChanged()
    {
        if (loading) return;
        PanelPreferences.Update(p => p with { ViewerSize = ((Choice)previewSize.SelectedItem).Id,
            DragSize = DragLongEdge, DraftSynthesis = fastSynthesis.IsChecked == true });
        RequestRefresh();
    }

    private void OnSettingsChanged()
    {
        if (loading)
            return;
        SettingsChanged?.Invoke();
        RequestRefresh();
    }

    internal void RequestRefresh()
    {
        if (loading || !enabled || disposed)
            return;
        if (dragging)
            lastDragChange = Environment.TickCount64;
        queued = true;
        frameCurrent = false;
        histogram.SetFrame(null);
        selectionUnavailable = false;
        savePreview.IsEnabled = false;
        inputRevision++;
        if (renderingFinal && !dragging)
            pending?.Cancel();
        refresh.Start();
    }

    internal void TrackSliderDrag(NativeSlider slider)
    {
        slider.DragStarted += (_, _) => BeginSliderDrag();
        slider.DragCompleted += (_, _) => EndSliderDrag();
        slider.LostMouseCapture += (_, _) => EndSliderDrag();
        slider.Unloaded += (_, _) => EndSliderDrag();
    }

    internal void BeginSliderDrag()
    {
        if (renderingFinal) pending?.Cancel();
        dragging = true;
        lastDragChange = 0;
        SliderDragActivity?.Invoke(true);
    }

    internal void EndSliderDrag()
    {
        if (!dragging)
            return;
        dragging = false;
        SliderDragActivity?.Invoke(false);
        if (lastDragChange != 0)
            RequestRefresh();
    }

    private bool WantsDragPreview => dragging && DragLongEdge > 0 && lastDragChange != 0
        && Environment.TickCount64 - lastDragChange < DragSettleDelay && !CompareSource;

    internal void SetEnabled(bool value)
    {
        if (disposed) return;
        enabled = value;
        if (enabled)
        {
            RequestRefresh();
        }
        else
        {
            ClearPreview();
        }
    }

    private bool OutputRenderActive => exportWork is not null || lutRenderActive;

    internal async Task<string> RenderLutAsync(NativeLutExporter.Plan plan, int resolution, double strength, CancellationToken stop)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (OutputRenderActive)
            throw new InvalidOperationException("Wait for the current Spektrafilm export to finish.");
        lutRenderActive = true;
        try
        {
            await DrainPreviewWorkersAsync(stop);
            return await NativeLutExporter.ExportAsync(plan, resolution, strength, stop);
        }
        finally
        {
            lutRenderActive = false;
            RequestRefresh();
        }
    }

    internal Task<bool> ExportAsync(bool useRecipeSize, Window? owner = null) =>
        ExportWithDestinationAsync(useRecipeSize, owner, ChooseExportDestination);

    private async Task<bool> ExportWithDestinationAsync(bool useRecipeSize, Window? owner,
        Func<SaveFileDialog, Window?, string?> chooseDestination)
    {
        if (disposed || !enabled || exportWork is not null)
            return false;
        if (lutRenderActive)
        {
            exportStatus.Text = "Wait for the Spektrafilm LUT export to finish.";
            return false;
        }
        ExportReceipt? receipt = null;
        try
        {
            var look = getSettings() ?? throw new InvalidOperationException("Wait for the Spektrafilm controls to finish loading.");
            var target = HostProfile.CaptureTarget(window);
            var input = (InputChoice)inputSpace.SelectedItem;
            var encoding = ((Choice)outputSpace.SelectedItem).Id;
            var args = look.Strength > 0 ? RendererOptions(look, input.Id, encoding, export: true) : Array.Empty<string>();
            var snapshot = HostImageSource.Capture(window, target,
                input.Space, captureRecipe: useRecipeSize);
            var recipe = snapshot.Recipe;
            var deliveryProfile = ExportColorProfile.ForRecipe(recipe?.ColorProfileName, recipe?.ColorProfilePath);
            var extension = SpektraExport.ExtensionFor(recipe?.FormatId);
            var dpi = recipe is null ? ExportResolution.DefaultDpi : ExportResolution.DotsPerInch(recipe.Resolution, recipe.ResolutionUnit);

            var picker = new SaveFileDialog
            {
                Title = recipe is null
                    ? "Save 16-bit Spektrafilm export (sRGB)"
                    : $"Save {recipe.Width} × {recipe.Height} 16-bit TIFF or PNG · {deliveryProfile.Label}",
                Filter = SpektraExport.FileFilter,
                FilterIndex = extension == ".png" ? 2 : 1,
                FileName = Path.GetFileNameWithoutExtension(target.SourcePath) + "-Spektrafilm",
                DefaultExt = extension,
                AddExtension = true,
                OverwritePrompt = true,
            };
            if (recipe is { RootFolderMode: "CustomPath" } && Directory.Exists(recipe.CustomRootFolder))
                picker.InitialDirectory = recipe.CustomRootFolder;
            if (recipe is { RootFolderMode: "ImageFolder" })
                picker.InitialDirectory = Path.GetDirectoryName(target.SourcePath)!;
            // The save dialog pumps UI events. Reserve the captured request before
            // opening it so a second export or a selection change cannot retarget it.
            using var work = new ExportOperation(target.VariantId);
            exportWork = work;
            try
            {
                var destination = chooseDestination(picker, owner);
                work.Token.ThrowIfCancellationRequested();
                if (destination is null)
                    return false;

                var approved = OutputPathGuard.Approve(destination, target.SourcePath);
                var outputFormat = SpektraExport.PixelFormatFor(approved.Path);
                receipt = exportJobs.Accept(work.VariantId, approved.Path);
                var jobId = receipt.JobId;
                var started = Stopwatch.GetTimestamp();
                cancelExport.Visibility = Visibility.Visible;
                Advance(ExportStage.WaitingForViewer, "Waiting for Viewer rendering to finish…");
                await DrainPreviewWorkersAsync(work.Token);
                var viewerDrainMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Advance(ExportStage.DevelopingSource, "Developing export pixels in Capture One…");
                var result = await Task.Run(async () =>
                {
                    var developStarted = Stopwatch.GetTimestamp();
                    var rendered = snapshot.RenderExport(null, work.Token, useRecipeSize);
                    var developMs = Stopwatch.GetElapsedTime(developStarted).TotalMilliseconds;
                    var source = rendered.Image;
                    if (source.BitDepth < 16 || source.Description is not { } description
                        || description.Source != snapshot.SourceIdentity
                        || description.Source.VariantId != work.VariantId || description.Source.InputEncoding != input.Space
                        || !description.Describes(source.Width, source.Height, source.BitDepth, source.FloatBytes.Length))
                        throw new InvalidDataException("Capture One did not produce the captured high-bit-depth input for this export.");
                    StartupHook.Log($"Capture One developed {target.SourceFormat} export input at {source.Width} × {source.Height}, {source.BitDepth}-bit {input.Space}.");
                    byte[]? pixels = null;
                    double? renderMs = null;
                    if (look.Strength > 0)
                    {
                        Advance(ExportStage.RenderingFilm, "Rendering the Spektrafilm look…");
                        await using var exporter = new OpticalRenderer(export: true)
                        {
                            ReportProgress = stage =>
                            {
                                var text = stage switch
                                {
                                    RendererStage.SourceAccepted => "Checking the source pixels…",
                                    RendererStage.Preparing => "Preparing the film render…",
                                    RendererStage.Vulkan => "Rendering the Spektrafilm look…",
                                    RendererStage.Verifying => "Verifying rendered pixels…",
                                    _ => "Reading the rendered pixels…",
                                };
                                if (!exportStatus.Dispatcher.HasShutdownStarted)
                                    _ = exportStatus.Dispatcher.InvokeAsync(() =>
                                    {
                                        if (!disposed && ReferenceEquals(exportWork, work) && !work.Token.IsCancellationRequested
                                            && receipt?.Stage == ExportStage.RenderingFilm)
                                            exportStatus.Text = text;
                                    });
                            },
                        };
                        var renderStarted = Stopwatch.GetTimestamp();
                        pixels = await exporter.RenderAsync(source.Width, source.Height, source.FloatBytes, args, work.Token).ConfigureAwait(false);
                        renderMs = Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
                    }
                    work.Token.ThrowIfCancellationRequested();
                    Advance(ExportStage.Encoding, "Writing the 16-bit export…", rendered.Crop.Width, rendered.Crop.Height);
                    using var memory = RenderMemoryBudget.SystemMemory.Reserve(deliveryProfile.RequiresConversion
                            ? RenderAllocation.ProfileImageEncoding : RenderAllocation.ImageEncoding,
                        (long)rendered.Crop.Width * rendered.Crop.Height, "Free memory or choose a smaller Export Recipe.");
                    var deliveryFormat = deliveryProfile.RequiresConversion ? PixelFormats.Rgba64 : outputFormat;
                    var encodingStarted = Stopwatch.GetTimestamp();
                    var image = DecodeExport(source, pixels, look.Strength, input.Space, encoding, deliveryFormat, rendered.Crop, work.Token, dpi);
                    var blendAndCropMs = Stopwatch.GetElapsedTime(encodingStarted).TotalMilliseconds;
                    var iccStarted = Stopwatch.GetTimestamp();
                    var output = deliveryProfile.ConvertSrgb(image, outputFormat);
                    var iccMs = Stopwatch.GetElapsedTime(iccStarted).TotalMilliseconds;
                    var writeStarted = Stopwatch.GetTimestamp();
                    SpektraExport.SaveApproved(approved, output, work.Token, recipe?.Metadata, deliveryProfile,
                        recipe?.TiffCompression ?? TiffCompressOption.Zip);
                    StartupHook.Log("export.performance.v1", new
                    {
                        JobId = jobId,
                        SourceFormat = target.SourceFormat,
                        SourceBitDepth = source.BitDepth,
                        InputEncoding = input.Id,
                        FilmEncoding = encoding,
                        OutputFormat = outputFormat == PixelFormats.Rgba64 ? "PNG" : "TIFF",
                        TiffCompression = outputFormat == PixelFormats.Rgb48 ? (recipe?.TiffCompression ?? TiffCompressOption.Zip).ToString() : null,
                        OutputWidth = output.PixelWidth,
                        OutputHeight = output.PixelHeight,
                        ViewerDrainMs = viewerDrainMs,
                        NativeDevelopmentMs = developMs,
                        FilmRenderAndTransportMs = renderMs,
                        BlendAndCropMs = blendAndCropMs,
                        IccConversionMs = iccMs,
                        EncodeVerifyAndCommitMs = Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds,
                        TotalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    });
                    return (output.PixelWidth, output.PixelHeight);
                }, work.Token);
                Advance(ExportStage.Completed, $"Saved 16-bit Spektrafilm export · {result.PixelWidth} × {result.PixelHeight}",
                    result.PixelWidth, result.PixelHeight);
                StartupHook.Log($"Spektrafilm 16-bit export saved at {result.PixelWidth}x{result.PixelHeight}: {approved.Path}");
                return true;
            }
            finally
            {
                exportWork = null;
                if (!disposed)
                {
                    cancelExport.Visibility = Visibility.Collapsed;
                    RequestRefresh();
                }
            }
        }
        catch (OperationCanceledException)
        {
            Advance(ExportStage.Canceled, "Spektrafilm export canceled.");
            return false;
        }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm export failed: " + error);
            var message = error.GetBaseException().Message;
            Advance(ExportStage.Failed, message, error: message);
            if (disposed) return false;
            if (owner is { IsVisible: true })
                MessageBox.Show(owner, message, "Could not export with Spektrafilm", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show(message, "Could not export with Spektrafilm", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        void Advance(ExportStage stage, string message, int width = 0, int height = 0, string? error = null)
        {
            if (receipt is not null)
                receipt = exportJobs.Report(receipt, stage, width, height, error);
            if (exportStatus.Dispatcher.CheckAccess())
            {
                if (!disposed) exportStatus.Text = message;
                return;
            }
            var current = exportWork;
            if (!exportStatus.Dispatcher.HasShutdownStarted)
                _ = exportStatus.Dispatcher.InvokeAsync(() =>
                {
                    if (!disposed && ReferenceEquals(exportWork, current)) exportStatus.Text = message;
                });
        }
    }

    private static string DescribePreviousExport(ExportReceipt? receipt) => receipt?.Stage switch
    {
        null => string.Empty,
        ExportStage.Completed => $"Last Spektrafilm export · {receipt.Width} × {receipt.Height} · {Path.GetFileName(receipt.Destination)}",
        ExportStage.Canceled => "Previous Spektrafilm export canceled.",
        ExportStage.Failed => "Previous Spektrafilm export failed. " + receipt.Error,
        _ => "Unfinished Spektrafilm export · " + Path.GetFileName(receipt.Destination),
    };

    private static string? ChooseExportDestination(SaveFileDialog picker, Window? owner) =>
        (owner is { IsVisible: true } ? picker.ShowDialog(owner) : picker.ShowDialog()) == true ? picker.FileName : null;

    private void ClearPreview()
    {
        refresh.Stop();
        queued = false;
        inputRevision++;
        pending?.Cancel();
        dragging = false;
        viewer.Clear();
        activeVariant = null;
        originalUnavailable = false;
        ResetSource();
        viewerFrame = null;
        previewDetail = null;
        frameCurrent = false;
        histogram.SetFrame(null);
        showSource.IsEnabled = false;
        savePreview.IsEnabled = false;
        if (pending is null)
            ReleaseRenderer();
        status.Text = string.Empty;
    }

    private void ResetSource(bool keepDraftRenderer = false)
    {
        rendererRetry.Reset();
        ResetDraft(keepRenderer: keepDraftRenderer);
        viewportQuality.Reset();
        source = null;
        sourceSize = null;
        failedRevision = null;
        failedSpace = null;
        failedSize = null;
        sourceDisplay = null;
        renderedPixels = null;
        renderedArguments = null;
        renderedDisplay = null;
    }

    private bool CheckSourceProfile(PreviewInputSpace space)
    {
        var now = Environment.TickCount64;
        if (checkedProfileSpace == space && now < nextProfileCheck)
            return checkedProfileHash is not null;
        checkedProfileSpace = space;
        nextProfileCheck = now + 1000;
        try
        {
            var (_, hash) = HostImageSource.ResolvePreviewProfile(space);
            if (string.Equals(checkedProfileHash, hash, StringComparison.Ordinal)) return true;
            if (checkedProfileHash is not null) InvalidateSourceProfile();
            else RequestRefresh();
            checkedProfileHash = hash;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            if (checkedProfileHash is not null) InvalidateSourceProfile();
            checkedProfileHash = null;
            ClearFrame(error);
            return false;
        }
    }

    private void InvalidateSourceProfile()
    {
        pending?.Cancel();
        ResetSource(keepDraftRenderer: true);
        viewer.Clear();
        viewerFrame = null;
        frameCurrent = false;
        showSource.IsEnabled = false;
        RequestRefresh();
    }

    private async Task RefreshAsync()
    {
        if (!enabled)
        {
            ClearPreview();
            return;
        }
        if (OutputRenderActive || !previewWorkersExited.IsCompletedSuccessfully)
            return;
        if ((!queued || selectionUnavailable || originalUnavailable || pending is not null && !WantsDragPreview)
            && Environment.TickCount64 < nextViewerCheck)
            return;
        nextViewerCheck = Environment.TickCount64 + 350;

        Guid selectedVariant;
        NativeImageRevision revision;
        bool originalAvailable;
        try
        {
            (selectedVariant, revision, originalAvailable) = HostImageSource.GetSelectedSourceState(window);
        }
        catch (InvalidOperationException error)
        {
            pendingDraft?.Cancel();
            selectionUnavailable = true;
            viewer.Suspend(error.Message);
            UpdateViewerStatus();
            return;
        }
        selectionUnavailable = false;
        if (!useSelectedLook(selectedVariant))
            return;
        var newSelection = activeVariant != selectedVariant;
        if (newSelection)
        {
            viewer.Clear();
            if (pending is not null)
            {
                pending.Cancel();
                pendingDraft?.Cancel();
                return;
            }
            activeVariant = selectedVariant;
            ResetSource(keepDraftRenderer: true);
            viewerFrame = null;
            frameCurrent = false;
            histogram.SetFrame(null);
            showSource.IsEnabled = false;
            queued = true;
        }
        if (!originalAvailable)
        {
            if (!originalUnavailable || newSelection)
            {
                originalUnavailable = true;
                inputRevision++;
                pending?.Cancel();
                ResetSource();
                ReleaseRenderer();
                viewerFrame = null;
                frameCurrent = false;
                showSource.IsEnabled = false;
                viewer.Suspend("Reconnect the original image to see Spektrafilm.");
                UpdateViewerStatus();
            }
            return;
        }
        if (originalUnavailable)
        {
            originalUnavailable = false;
            RequestRefresh();
        }
        var selectedSpace = (InputChoice)inputSpace.SelectedItem;
        var space = selectedSpace.Space;
        if (!CheckSourceProfile(space)) return;
        var encoding = (Choice)outputSpace.SelectedItem;
        var automatic = ((Choice)previewSize.SelectedItem).Id == "auto";
        if (automatic && pending is null && pendingDraft is null && !dragging)
        {
            var ratio = source is { } frame ? Math.Min(frame.Width, frame.Height) / (double)Math.Max(frame.Width, frame.Height) : 1;
            if (viewportQuality.Update(viewer.ProjectedLongEdge(selectedVariant), ratio,
                RenderMemoryBudget.SystemMemory.AvailableForNewAllocation, Environment.TickCount64))
                RequestRefresh();
        }
        var retryKey = (selectedVariant, inputRevision, revision);
        if (rendererRetry.IsWaiting(retryKey, Environment.TickCount64)) return;
        var size = ViewerRenderSize;
        var nativeSize = size == "native";
        var longEdge = nativeSize ? 0 : int.Parse(size, CultureInfo.InvariantCulture);
        var dragEdge = DragLongEdge;
        var cachedSource = source;
        var needsSource = cachedSource?.Description is not { } description || description.Source.VariantId != selectedVariant
            || description.Source.AdjustmentKey != revision || description.Source.InputEncoding != space || sourceSize != size
            || !string.Equals(description.Source.ProfileSha256, checkedProfileHash, StringComparison.Ordinal)
            || !description.Describes(cachedSource.Width, cachedSource.Height, cachedSource.BitDepth, cachedSource.FloatBytes.Length);
        if (!needsSource && WantsDragPreview
            && source is { } liveSource && Math.Max(liveSource.Width, liveSource.Height) > dragEdge)
        {
            if (pendingDraft is null && lastDraftRevision != inputRevision && getSettings() is { } draftLook)
                await RefreshDraftAsync(selectedVariant, revision, liveSource, selectedSpace, encoding, draftLook, size, dragEdge);
            return;
        }
        if (pending is not null)
            return;
        if (needsSource && (failedRevision != revision || failedSpace != space || failedSize != size))
            queued = true;

        if (!queued || getSettings() is not { } look)
        {
            if (viewerFrame is not null)
                ShowFrame(selectedVariant, viewerFrame);
            if (!needsSource && frameCurrent && !dragging && dragEdge > 0
                && !draftWarmupAttempted && pendingDraft is null
                && source is { } fullSource && Math.Max(fullSource.Width, fullSource.Height) > dragEdge
                && getSettings() is { } warmLook)
                await RefreshDraftAsync(selectedVariant, revision, fullSource, selectedSpace, encoding, warmLook, size, dragEdge, warmup: true);
            return;
        }
        queued = false;
        failedRevision = null;
        var settingsVersion = inputRevision;
        var started = Stopwatch.GetTimestamp();
        using var job = new CancellationTokenSource();
        pending = job;
        try
        {
            status.Text = nativeSize ? "Rendering at native resolution…" : "Rendering Viewer preview…";
            var comparing = CompareSource;
            var strength = comparing ? 0 : look.Strength;
            var args = strength > 0 ? RendererOptions(look, selectedSpace.Id, encoding.Id) : Array.Empty<string>();
            if (needsSource)
            {
                var target = HostProfile.CaptureTarget(window);
                var snapshot = HostImageSource.Capture(window, target, space);
                var initialStarted = Stopwatch.GetTimestamp();
                HostImageSource.Frame? initial = null;
                if ((nativeSize || longEdge > InitialPreviewLongEdge) && newSelection && dragEdge > 0 && pendingDraft is null && strength > 0)
                {
                    try
                    {
                        using var brief = CancellationTokenSource.CreateLinkedTokenSource(job.Token);
                        brief.CancelAfter(TimeSpan.FromSeconds(2));
                        initial = await Task.Run(() => snapshot.RenderDraft(Math.Min(dragEdge, InitialPreviewLongEdge), brief.Token), brief.Token);
                    }
                    catch (Exception error) when (!job.IsCancellationRequested)
                    {
                        StartupHook.Log("Spektrafilm initial source: " + error.GetBaseException().Message);
                    }
                }
                var development = Task.Run(() => nativeSize
                    ? snapshot.RenderNativePreview(job.Token)
                    : snapshot.RenderScaledPreview(longEdge, job.Token), job.Token);
                var developed = initial is not null
                    ? await RenderCoordination.PreferFinalAsync(development, token => TryShowInitialPreviewAsync(initial,
                        initialStarted, snapshot.AdjustmentKey, selectedVariant, selectedSpace, encoding, look, dragEdge, settingsVersion, token), job.Token)
                    : await development;
                job.Token.ThrowIfCancellationRequested();
                if (HostImageSource.GetRevision(window, selectedVariant) != snapshot.AdjustmentKey)
                {
                    queued = true;
                    return;
                }
                ResetDraft(keepRenderer: true);
                source = developed;
                sourceSize = size;
                sourceDisplay = null;
                renderedPixels = null;
                renderedArguments = null;
                renderedDisplay = null;
                StartupHook.Log($"Capture One developed {target.SourceFormat} input at {developed.Width} × {developed.Height}, {developed.BitDepth}-bit {space} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
            }
            if (settingsVersion != inputRevision)
                return;

            var input = source!;
            BitmapSource output;
            if (strength <= 0)
            {
                if (sourceDisplay is null)
                {
                    var display = await Task.Run(() => DecodeResult(input, null, 0, space, encoding.Id, job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    sourceDisplay = display;
                }
                output = sourceDisplay;
            }
            else
            {
                if (renderedArguments is null || !args.SequenceEqual(renderedArguments))
                {
                    renderedArguments = args;
                    renderedPixels = null;
                    renderedDisplay = null;
                }

                if (renderedDisplay is null || renderedStrength != strength)
                {
                    var displayPixels = strength == 1 && encoding.Id == "srgb" && renderedPixels is null;
                    byte[]? packed = null;
                    if (renderedPixels is null)
                    {
                        renderer ??= new OpticalRenderer(nativePreview: true);
                        renderingFinal = true;
                        byte[] reply;
                        try { reply = await renderer.RenderAsync(input.Width, input.Height, input.FloatBytes, args, job.Token, displayPixels); }
                        finally { renderingFinal = false; }
                        job.Token.ThrowIfCancellationRequested();
                        if (displayPixels)
                            packed = reply;
                        else
                            renderedPixels = reply;
                    }
                    if (settingsVersion != inputRevision)
                        return;

                    var completedPixels = renderedPixels;
                    var display = await Task.Run(() => packed is not null
                        ? CreateDisplayBitmap(input, packed)
                        : DecodeResult(input, completedPixels, strength, space, encoding.Id, job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    renderedDisplay = display;
                    renderedStrength = strength;
                }
                output = renderedDisplay;
            }
            job.Token.ThrowIfCancellationRequested();
            if (settingsVersion != inputRevision)
                return;
            if (HostImageSource.GetRevision(window, selectedVariant) != input.Description?.Source.AdjustmentKey)
            {
                queued = true;
                return;
            }
            viewerFrame = output;
            frameCurrent = true;
            showSource.IsEnabled = true;
            previewDetail = comparing ? "Source preview" : automatic ? "Auto Viewer" : "Live Viewer";
            ShowFrame(selectedVariant, output);
            StartupHook.Log($"Live Viewer frame {input.Width} × {input.Height} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms. {DescribeRender(args)}");
        }
        catch (OperationCanceledException)
        {
            if (!job.IsCancellationRequested)
                queued = true;
        }
        catch (Exception error) when (settingsVersion == inputRevision && !job.IsCancellationRequested
            && (error is RendererConnectionInterruptedException or RendererDeadlineException or RendererDeviceLostException
                or HostImageSource.DevelopmentDeadlineException)
            && rendererRetry.Schedule(retryKey, Environment.TickCount64))
        {
            ReleaseRenderer();
            ClearFrame(error);
            queued = true;
            status.Text = error is HostImageSource.DevelopmentDeadlineException
                ? "Retrying Capture One image development…"
                : "Restarting the Spektrafilm Viewer renderer…";
        }
        catch (Exception error) when (settingsVersion == inputRevision)
        {
            failedRevision = revision;
            failedSpace = space;
            failedSize = size;
            ClearFrame(error);
        }
        catch (Exception) when (settingsVersion != inputRevision) { }
        finally
        {
            pending = null;
            if (!enabled)
                ReleaseRenderer();
        }
    }

    private void ReleaseRenderer()
    {
        RetirePreviewWorker(renderer);
        renderer = null;
    }

    private void ResetDraft(bool keepRenderer = false)
    {
        pendingDraft?.Cancel();
        if (pendingDraft is null && !keepRenderer)
            ReleaseDraftRenderer();
        draftSource = null;
        draftPixels = null;
        draftArguments = null;
        draftWarmupAttempted = false;
        lastDraftRevision = -1;
    }

    private async Task TryShowInitialPreviewAsync(HostImageSource.Frame frame, long started,
        NativeImageRevision revision, Guid variant, InputChoice inputColor, Choice encoding, LookSettings look,
        int dragEdge, int version, CancellationToken token)
    {
        if (version != inputRevision || DragLongEdge != dragEdge)
            return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stop = work.Token;
        pendingDraft = work;
        try
        {
            var args = RendererOptions(look, inputColor.Id, encoding.Id, draft: true);
            draftRenderer ??= new OpticalRenderer(nativePreview: true);
            var packed = look.Strength == 1 && encoding.Id == "srgb";
            var pixels = await draftRenderer.RenderAsync(frame.Width, frame.Height, frame.FloatBytes, args, stop, packed);
            var display = await Task.Run(() => packed ? CreateDisplayBitmap(frame, pixels)
                : DecodeResult(frame, pixels, look.Strength, inputColor.Space, encoding.Id, stop), stop);
            stop.ThrowIfCancellationRequested();
            if (version != inputRevision || DragLongEdge != dragEdge || OutputRenderActive
                || HostImageSource.GetRevision(window, variant) != revision)
                return;
            viewerFrame = display;
            frameCurrent = false;
            previewDetail = "Loading Viewer preview";
            ShowFrame(variant, display);
            StartupHook.Log($"Initial Viewer preview {frame.Width} × {frame.Height} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && stop.IsCancellationRequested) { }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            StartupHook.Log("Spektrafilm initial preview: " + error.GetBaseException().Message);
        }
        finally
        {
            pendingDraft = null;
            if (stop.IsCancellationRequested)
                ReleaseDraftRenderer();
        }
    }

    private async Task RefreshDraftAsync(Guid selectedVariant, NativeImageRevision revision,
        HostImageSource.Frame input, InputChoice inputColor, Choice encoding, LookSettings look, string viewerSize, int dragEdge, bool warmup = false)
    {
        draftWarmupAttempted = true;
        if (!warmup)
            lastDraftRevision = inputRevision;
        var args = look.Strength > 0 ? RendererOptions(look, inputColor.Id, encoding.Id, draft: true) : Array.Empty<string>();
        var started = Stopwatch.GetTimestamp();
        using var job = new CancellationTokenSource();
        pendingDraft = job;
        try
        {
            if (draftSource is null)
            {
                var reduced = await Task.Run(() => ViewerDraftSource.Downsample(input, dragEdge, job.Token, inputColor.Space), job.Token);
                job.Token.ThrowIfCancellationRequested();
                draftSource = reduced;
            }
            var frame = draftSource;
            byte[]? packed = null;
            if (look.Strength > 0 && (draftPixels is null || !args.SequenceEqual(draftArguments!)))
            {
                draftRenderer ??= new OpticalRenderer(nativePreview: true);
                var pack = !warmup && look.Strength == 1 && encoding.Id == "srgb";
                var rendered = await draftRenderer.RenderAsync(frame.Width, frame.Height, frame.FloatBytes, args, job.Token, pack);
                job.Token.ThrowIfCancellationRequested();
                packed = pack ? rendered : null;
                draftPixels = pack ? null : rendered;
                draftArguments = args;
            }
            if (warmup)
                return;
            var pixels = draftPixels;
            var output = await Task.Run(() => packed is null
                ? DecodeResult(frame, pixels, look.Strength, inputColor.Space, encoding.Id, job.Token)
                : CreateDisplayBitmap(frame, packed), job.Token);
            job.Token.ThrowIfCancellationRequested();
            if (!WantsDragPreview || OutputRenderActive || activeVariant != selectedVariant || !ReferenceEquals(source, input)
                || !ReferenceEquals(inputSpace.SelectedItem, inputColor) || !ReferenceEquals(outputSpace.SelectedItem, encoding)
                || ViewerRenderSize != viewerSize || DragLongEdge != dragEdge
                || HostImageSource.GetRevision(window, selectedVariant) != revision)
                return;

            viewerFrame = output;
            frameCurrent = false;
            previewDetail = "Drag preview";
            ShowFrame(selectedVariant, output);
            StartupHook.Log($"Drag preview frame {frame.Width} × {frame.Height} in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (!WantsDragPreview || selectionUnavailable) { }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm drag preview: " + error.GetBaseException().Message);
        }
        finally
        {
            pendingDraft = null;
            if (job.IsCancellationRequested)
                ReleaseDraftRenderer();
        }
    }

    private void ReleaseDraftRenderer()
    {
        RetirePreviewWorker(draftRenderer);
        draftRenderer = null;
    }

    private string ViewerRenderSize => ((Choice)previewSize.SelectedItem).Id switch
    {
        "auto" => viewportQuality.SizeId,
        var manual => manual,
    };

    private void RetirePreviewWorker(OpticalRenderer? worker)
    {
        if (worker is null)
            return;
        var exit = worker.DisposeAsync().AsTask();
        previewWorkersExited = previewWorkersExited.IsCompletedSuccessfully
            ? exit
            : Task.WhenAll(previewWorkersExited, exit);
        // Log even when this viewer is disposed before the next output operation.
        _ = ObservePreviewWorkerExitAsync(exit);
    }

    private static async Task ObservePreviewWorkerExitAsync(Task exit)
    {
        try { await exit.ConfigureAwait(false); }
        catch (Exception error) { StartupHook.Log("Viewer renderer could not exit: " + error.GetBaseException().Message); }
    }

    private async Task DrainPreviewWorkersAsync(CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        var waiting = renderer is not null || draftRenderer is not null || !previewWorkersExited.IsCompletedSuccessfully;
        pending?.Cancel();
        pendingDraft?.Cancel();
        ReleaseRenderer();
        ReleaseDraftRenderer();
        await previewWorkersExited.WaitAsync(token);
        if (waiting)
            StartupHook.Log($"Spektrafilm Viewer renderer processes exited before export in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
    }

    private void ClearFrame(Exception error)
    {
        viewer.Clear();
        viewerFrame = null;
        frameCurrent = false;
        histogram.SetFrame(null);
        savePreview.IsEnabled = false;
        nextViewerCheck = Environment.TickCount64 + 350;
        var message = error.GetBaseException().Message;
        if (status.Text != message)
            StartupHook.Log(error.ToString());
        status.Text = message;
    }

    private void ShowFrame(Guid variant, BitmapSource frame)
    {
        nextViewerCheck = Environment.TickCount64 + 350;
        viewer.Show(variant, frame);
        UpdateViewerStatus();
    }

    private void UpdateViewerStatus()
    {
        if (!enabled || OutputRenderActive)
            return;
        var message = viewer.UnavailableReason;
        if (message is null && viewerFrame is { } frame)
            message = $"{previewDetail} · {frame.PixelWidth} × {frame.PixelHeight}";
        if (message is not null && status.Text != message)
            status.Text = message;
        histogram.SetFrame(viewer.UnavailableReason is null && frameCurrent ? viewerFrame : null, CompareSource);
        savePreview.IsEnabled = viewerFrame is not null && frameCurrent && !saveInProgress && viewer.UnavailableReason is null;
    }

    private Task SavePreviewAsync() => SavePreviewWithDestinationAsync(ChooseExportDestination);

    private async Task SavePreviewWithDestinationAsync(Func<SaveFileDialog, Window?, string?> chooseDestination)
    {
        if (disposed || !frameCurrent || viewerFrame is not { } frame || saveInProgress)
            return;
        var stop = lifetime.Token;
        try
        {
            var target = HostProfile.CaptureTarget(window);
            if (target.VariantId != activeVariant)
                return;
            var picker = new SaveFileDialog
            {
                Filter = "PNG image|*.png",
                FileName = "Spektrafilm preview.png",
                AddExtension = true,
                DefaultExt = ".png",
            };
            var destination = chooseDestination(picker, Window.GetWindow(savePreview));
            if (destination is null)
                return;

            stop.ThrowIfCancellationRequested();
            var approved = OutputPathGuard.Approve(destination, target.SourcePath);
            saveInProgress = true;
            savePreview.IsEnabled = false;
            await Task.Run(() => approved.Write(stream =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(frame, null, null,
                    new ReadOnlyCollection<ColorContext>(new[] { new ColorContext(PixelFormats.Bgra32) })));
                encoder.Save(stream);
            }, stop), stop);
            StartupHook.Log("Saved the sRGB Viewer preview as PNG.");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!disposed)
                MessageBox.Show(error.GetBaseException().Message, "Could not save Spektrafilm preview", MessageBoxButton.OK, MessageBoxImage.Error);
            StartupHook.Log(error.ToString());
        }
        finally
        {
            saveInProgress = false;
            if (!disposed) UpdateViewerStatus();
        }
    }

    private static string DescribeRender(string[] arguments)
    {
        var settings = new List<string>();
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var option = arguments[index];
            if (option is "--stock" or "--paper" or "--mode" or "--input-color" or "--output-color" or "--grain-model" or "--camera-diffusion-family"
                or "--print-diffusion-family" or "--halation-amount" or "--scatter-amount" or "--scanner-enabled"
                or "--enlarger-scale")
                settings.Add($"{option[2..]}={arguments[index + 1]}");
        }
        return string.Join(' ', settings);
    }

    internal static IReadOnlyList<(string StateKey, string WorkerKey)> DevelopmentRendererParameters { get; } = Array.AsReadOnly(new[]
    {
        ("film_gamma", "film-gamma"), ("film_push_pull", "film-push-pull"), ("print_gamma", "print-gamma"),
        ("print_push_pull", "print-push-pull"), ("negative_bleach_bypass", "negative-bleach-bypass"),
        ("print_bleach_bypass", "print-bleach-bypass"), ("negative_leuco_cyan", "negative-leuco-cyan"),
        ("print_shadows", "print-shadow-shape"), ("print_highlights", "print-highlight-shape"),
        ("cyan_filter", "cyan-filter"), ("magenta_filter", "magenta-filter"), ("yellow_filter", "yellow-filter"),
        ("preflash_exposure", "preflash"), ("preflash_magenta", "preflash-magenta"), ("preflash_yellow", "preflash-yellow"),
        ("uv_cutoff", "uv-cutoff"), ("ir_cutoff", "ir-cutoff"), ("dir_amount", "dir-amount"),
        ("dir_same_layer", "dir-same-layer"), ("dir_interlayer", "dir-interlayer"),
    });

    internal string[] RendererOptions(LookSettings look, string inputColor, string outputColor, bool export = false, bool draft = false)
    {
        if (look.Mode is "process_negative" or "process_negatives")
            throw new InvalidOperationException("Process Negative requires an encoded negative scan as input.");
        if (look.Development.GetValueOrDefault("print_exhaustion") != 0)
            throw new InvalidOperationException("Set Developer exhaustion to zero before rendering this optical preview.");

        var args = new List<string>();
        Choice Selected(string key) => (Choice)choices[key].SelectedItem;
        void Set(string key, string value) { args.Add("--" + key); args.Add(value); }
        void Numeric(string key, double value) => Set(key, value.ToString("0.######", CultureInfo.InvariantCulture));

        Set("stock", look.Film);
        Set("paper", look.Paper);
        Set("input-color", inputColor);
        Set("output-color", outputColor);
        Set("mode", look.Mode.Contains("scan", StringComparison.Ordinal) ? (look.ScanInvert ? "scan_inverted" : "scan") : "print");
        Set("spectral-method", look.SpectralMethod);
        Set("film-push-pull-mode", look.FilmPushPullMode);
        Numeric("film-exposure", look.Exposure);
        Numeric("print-exposure", look.PrintExposure);
        foreach (var (sourceKey, rendererKey) in DevelopmentRendererParameters)
        {
            if (look.Development.TryGetValue(sourceKey, out var value))
                Numeric(rendererKey, value);
        }
        Set("uv-enabled", look.Development.GetValueOrDefault("uv_cutoff", 380) > 380 ? "true" : "false");
        Set("ir-enabled", look.Development.GetValueOrDefault("ir_cutoff", 780) < 780 ? "true" : "false");

        foreach (var key in choices.Keys)
            Set(key, Selected(key).Id);
        var fastGrain = !export && (draft || fastSynthesis.IsChecked == true) && Selected("grain-model").Id == "synthesis";
        var grainQuality = Math.Min(values["grain-synthesis-quality"].Value, 2);
        foreach (var (key, slider) in values)
        {
            var halationChannel = key is "halation-red" or "halation-green" or "halation-blue";
            var couplerChannel = key.StartsWith("dir-gamma-", StringComparison.Ordinal) || key.Contains("-to-", StringComparison.Ordinal);
            if ((halationChannel && toggles["custom-halation-rgb"].IsChecked != true)
                || (couplerChannel && toggles["custom-dir-rgb"].IsChecked != true))
                continue;
            var value = slider.Value;
            if (fastGrain && key == "grain-synthesis-samples")
                value = Math.Min(value, Math.Floor(2 / grainQuality));
            if (fastGrain && key == "grain-synthesis-quality")
                value = grainQuality;
            Numeric(key, value);
        }
        foreach (var (key, toggle) in toggles)
            if (!key.StartsWith("custom-", StringComparison.Ordinal))
                Set(key, toggle.IsChecked == true ? "true" : "false");
        return args.ToArray();
    }

    private static BitmapSource DecodeResult(HostImageSource.Frame frame, byte[]? bytes, double strength,
        PreviewInputSpace space, string outputEncoding, CancellationToken token)
    {
        var pixels = ToDisplayPixels(frame, bytes, strength, space, outputEncoding, token);
        return CreateDisplayBitmap(frame, pixels);
    }

    private static BitmapSource CreateDisplayBitmap(HostImageSource.Frame frame, byte[] pixels)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, pixels, frame.Width * 4);
        image.Freeze();
        return image;
    }

    private static byte[] ToDisplayPixels(HostImageSource.Frame original, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, CancellationToken token)
    {
        var pixels = GC.AllocateUninitializedArray<byte>(original.FloatBytes.Length / sizeof(float));
        var transform = new SrgbDelivery(space, outputEncoding, strength);
        var rowWidth = original.Width * 4;
        PreviewRows.Process(original.Width, original.Height, token, (first, last) =>
        {
            var source = MemoryMarshal.Cast<byte, float>(original.FloatBytes.AsSpan());
            var floats = MemoryMarshal.Cast<byte, float>(rendered.AsSpan());
            for (var y = first; y < last; y++)
            {
                token.ThrowIfCancellationRequested();
                var start = y * rowWidth;
                for (var i = start; i < start + rowWidth; i += 4)
                {
                    var rgb = transform.GetSrgb(source, floats, i);
                    pixels[i] = (byte)Math.Round(rgb.Blue * 255);
                    pixels[i + 1] = (byte)Math.Round(rgb.Green * 255);
                    pixels[i + 2] = (byte)Math.Round(rgb.Red * 255);
                    pixels[i + 3] = (byte)MathF.Round(255 * Math.Clamp(source[i + 3], 0, 1));
                }
            }
        });
        return pixels;
    }

    private static BitmapSource DecodeExport(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, PixelFormat format, System.Drawing.Rectangle crop, CancellationToken token, double dpi)
    {
        var transform = new SrgbDelivery(space, outputEncoding, strength);
        var hasAlpha = format == PixelFormats.Rgba64;
        var channels = hasAlpha ? 4 : 3;
        var stride = checked(crop.Width * channels * sizeof(ushort));
        var pixels = ExportPixels.Encode(frame.Width, crop, frame.FloatBytes, rendered, transform, hasAlpha, token);
        var image = BitmapSource.Create(crop.Width, crop.Height, dpi, dpi, format, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private sealed record Choice(string Id, string Label);
    private sealed record DragSize(int LongEdge, string Label);
    private sealed record InputChoice(PreviewInputSpace Space, string Id, string Label);
    internal sealed record Settings(Dictionary<string, double> Numbers, Dictionary<string, string> Selections, Dictionary<string, bool> Switches);
}
