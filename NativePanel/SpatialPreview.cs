using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
using P1.C1.Framework.Win10.ToastNotifications.Templates;

internal sealed class SpatialPreview : IDisposable
{
    private const int InitialPreviewLongEdge = 1200;
    private const int DragSettleDelay = 220;
    private const string ExportFailureTitleSuffix = " · Export failed";
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
    private readonly object window;
    private readonly Func<LookSettings?> getSettings;
    private readonly Func<Guid, bool> useSelectedLook;
    private readonly Dictionary<string, NativeSlider> values = new();
    private readonly Dictionary<string, ComboBox> choices = new();
    private readonly Dictionary<string, CheckBox> toggles = new();
    private readonly List<FrameworkElement> printOnlyControls = new();
    private readonly TextBlock status = new() { Margin = new Thickness(0, 5, 0, 3), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly ViewerPreview viewer;
    private readonly ExportJobJournal exportJobs;
    private Func<bool> showExportFailureNotice = ShowNativeExportFailureNotice;
    private readonly ViewportQuality viewportQuality = new();
    private readonly ViewerHistogramControl histogram = new();
    private OpticalRenderer? renderer;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? pendingDraft;
    private OpticalRenderer? draftRenderer;
    private readonly ViewerDraftPyramid draftPyramid = new();
    private (string[] Arguments, byte[] Pixels, bool Packed)? draftResult;
    private bool draftWarmupAttempted;
    private bool warmingDraft;
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
        DisplayMemberPath = nameof(OpticalInput.Label),
        ItemsSource = OpticalControls.Inputs,
        SelectedIndex = 0,
    };
    private readonly ComboBox outputSpace = new() { DisplayMemberPath = nameof(OpticalOption.Label), ItemsSource = OpticalControls.Outputs, SelectedIndex = 0 };
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
    private readonly Button retryViewer = new()
    {
        Content = "Retry Viewer",
        Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 4, 0, 1),
        ToolTip = "Try again with the current photo and saved film settings.",
    };
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
    private bool viewportSeedPending = true;
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
        liveControls.Children.Add(retryViewer);
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
        refresh.Tick += OnRefreshTick;
        showSource.Checked += (_, _) => RequestRefresh();
        showSource.Unchecked += (_, _) => RequestRefresh();
        savePreview.Click += async (_, _) => await SavePreviewAsync();
        retryViewer.Click += (_, _) =>
        {
            if (!enabled || disposed || failedRevision is null) return;
            rendererRetry.Reset();
            failedRevision = null;
            RequestRefresh();
        };
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

    internal void SetPrintMode(bool printing)
    {
        foreach (var control in printOnlyControls)
        {
            control.IsEnabled = printing;
            control.ToolTip = printing ? null : "Available in Print Simulation mode.";
        }
    }
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
        OpticalControls.VerifyRendererChoices(renderer);
        foreach (var (key, slider) in values)
            slider.BindRenderer(renderer, key);
        OpticalControls.VerifyRendererToggles(renderer);
    }

    internal Settings CaptureSettings()
    {
        var numbers = new Dictionary<string, double>();
        var selections = new Dictionary<string, string>();
        var switches = new Dictionary<string, bool>();
        foreach (var (key, slider) in values)
            numbers.Add(key, slider.Value);
        foreach (var (key, chooser) in choices)
            selections.Add(key, ((OpticalOption)chooser.SelectedItem).Id);
        selections.Add("input-color", ((OpticalInput)inputSpace.SelectedItem).Id);
        selections.Add("output-color", ((OpticalOption)outputSpace.SelectedItem).Id);
        foreach (var (key, item) in toggles)
            switches.Add(key, item.IsChecked == true);
        return new Settings(numbers, selections, switches);
    }

    internal void LoadSettings(Settings? settings)
    {
        loading = true;
        try
        {
            LoadControlValues(settings);
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
            OpticalControls.ValidateNumber(key, value);
        foreach (var (key, value) in settings.Selections)
        {
            var valid = key switch
            {
                // Accept older looks without making hardware preferences part of a look again.
                "preview-size" => PreviewSizes.Any(x => x.Id == value),
                "drag-preview-size" => DragSizes.Any(x => x.LongEdge.ToString(CultureInfo.InvariantCulture) == value),
                _ => OpticalControls.HasChoice(key, value),
            };
            if (!valid) throw new InvalidDataException($"Unsupported saved optical choice: {key}={value}.");
        }
        foreach (var key in settings.Switches.Keys)
            if ((!OpticalControls.Parameters.TryGetValue(key, out var binding) || binding.Parameter is not OpticalToggle)
                && key is not ("show-source" or "draft-synthesis" or "quick-drag-preview"))
                throw new InvalidDataException($"Unsupported saved optical switch: {key}.");
    }

    private void LoadControlValues(Settings? settings)
    {
        foreach (var binding in OpticalControls.Parameters.Values)
        {
            switch (binding.Parameter)
            {
                case OpticalSlider number:
                    var amount = settings?.Numbers?.GetValueOrDefault(number.Key, number.Initial) ?? number.Initial;
                    values[number.Key].Value = number.Contains(amount) ? amount : number.Initial;
                    break;
                case OpticalChoice choice:
                    var id = settings?.Selections?.GetValueOrDefault(choice.Key) ?? choice.Default;
                    choices[choice.Key].SelectedItem = choice.Options.FirstOrDefault(item => item.Id == id)
                        ?? choice.Options.Single(item => item.Id == choice.Default);
                    break;
                case OpticalToggle toggle:
                    toggles[toggle.Key].IsChecked = settings?.Switches?.GetValueOrDefault(toggle.Key, toggle.Initial) ?? toggle.Initial;
                    break;
            }
        }
        var colorId = settings?.Selections?.GetValueOrDefault("input-color");
        inputSpace.SelectedItem = OpticalControls.Inputs.FirstOrDefault(space => space.Id == colorId) ?? OpticalControls.Inputs[0];
        var encoding = settings?.Selections?.GetValueOrDefault("output-color");
        outputSpace.SelectedItem = OpticalControls.Outputs.FirstOrDefault(item => item.Id == encoding) ?? OpticalControls.Outputs[0];
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

        foreach (var group in OpticalControls.Sections)
            body.Children.Add(BuildSection(group));
        values["enlarger-scale"].ToolTip = "1.0 keeps the original framing. Higher values magnify the image inside the frame.";
        return Section("optics", "Spektrafilm optics", body);
    }

    private FrameworkElement BuildSection(OpticalSection group)
    {
        var panel = new StackPanel();
        if (group.Key == "optics-grain-synthesis")
        {
            fastSynthesis.Checked += (_, _) => OnPreferencesChanged();
            fastSynthesis.Unchecked += (_, _) => OnPreferencesChanged();
            panel.Children.Add(fastSynthesis);
        }
        if (group.Key == "optics-enlarger")
            panel.PreviewMouseWheel += ScrollPastFramingControls;
        foreach (var item in group.Items)
        {
            switch (item)
            {
                case OpticalSlider slider:
                    AddSlider(panel, slider);
                    break;
                case OpticalChoice choice:
                    AddChoice(panel, choice);
                    break;
                case OpticalToggle toggle:
                    AddToggle(panel, toggle);
                    break;
                case OpticalSection section:
                    panel.Children.Add(BuildSection(section));
                    break;
            }
        }
        FrameworkElement view = group.Key is { } key ? Section(key, group.Label, panel) : panel;
        if (group.PrintOnly)
        {
            ToolTipService.SetShowOnDisabled(view, true);
            printOnlyControls.Add(view);
        }
        if (group.RequiresToggle is { } required)
            SetToggleEnabled(toggles[required], view);
        return view;
    }

    private static Expander Section(string key, string title, UIElement content) =>
        PanelPreferences.CreateSection(key, title, content, 5);

    private void AddToggle(Panel panel, OpticalToggle definition)
    {
        var item = new CheckBox { Content = definition.Label, IsChecked = definition.Initial, Margin = new Thickness(0, 3, 0, 2) };
        item.Checked += (_, _) => OnSettingsChanged();
        item.Unchecked += (_, _) => OnSettingsChanged();
        toggles.Add(definition.Key, item);
        panel.Children.Add(item);
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

    private void AddSlider(Panel panel, OpticalSlider definition)
    {
        var control = new NativeSlider(definition.Label, definition.Minimum, definition.Maximum, definition.Initial, definition.Increment);
        TrackSliderDrag(control);
        control.ValueChanged += (_, _) => OnSettingsChanged();
        values.Add(definition.Key, control);
        panel.Children.Add(control);
    }

    private void AddChoice(Panel panel, OpticalChoice definition)
    {
        var chooser = new ComboBox
        {
            DisplayMemberPath = nameof(OpticalOption.Label),
            ItemsSource = definition.Options,
            SelectedItem = definition.Options.Single(option => option.Id == definition.Default),
        };
        AutomationProperties.SetName(chooser, definition.Label);
        chooser.SelectionChanged += (_, _) => OnSettingsChanged();
        choices.Add(definition.Key, chooser);
        panel.Children.Add(new TextBlock { Text = definition.Label, Margin = new Thickness(0, 4, 0, 1) });
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
            ResetDraft(keepRenderer: DragLongEdge > 0, keepSourceLevels: DragLongEdge > 0);
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
        retryViewer.Visibility = Visibility.Collapsed;
        inputRevision++;
        if (warmingDraft)
            pendingDraft?.Cancel();
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
        if (warmingDraft) pendingDraft?.Cancel();
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
        CancellationToken exportToken = default;
        try
        {
            var look = getSettings() ?? throw new InvalidOperationException("Wait for the Spektrafilm controls to finish loading.");
            var target = HostProfile.CaptureTarget(window);
            var input = (OpticalInput)inputSpace.SelectedItem;
            var encoding = ((OpticalOption)outputSpace.SelectedItem).Id;
            var args = look.Strength > 0 ? RendererOptions(look, input.Id, encoding, export: true) : Array.Empty<string>();
            // Reserve the export before yielding so the Viewer cannot start another
            // development while Capture One settles the source for the Save request.
            using var work = new ExportOperation(target.VariantId);
            exportToken = work.Token;
            exportWork = work;
            try
            {
                var drainStarted = Stopwatch.GetTimestamp();
                await DrainPreviewWorkersAsync(work.Token);
                await HostImageSource.WaitForNativeDevelopmentAsync(work.Token);
                work.Token.ThrowIfCancellationRequested();
                if (disposed || owner is { IsVisible: false })
                    return false;
                var viewerDrainMs = Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds;
                var snapshot = HostImageSource.CaptureForExport(window, target,
                    input.Space, captureRecipe: useRecipeSize);
                var recipe = snapshot.Recipe;
                var deliveryProfile = ExportColorProfile.ForRecipe(recipe?.ColorProfileName, recipe?.ColorProfilePath);
                var delivery = new SpektraExport.RecipeDelivery(recipe?.FormatId, recipe?.TiffBits ?? 0, recipe?.CropMode,
                    recipe?.Omissions ?? SpektraExport.RecipeOmission.None);
                var extension = delivery.Extension;
                var dpi = recipe is null ? ExportResolution.DefaultDpi : ExportResolution.DotsPerInch(recipe.Resolution, recipe.ResolutionUnit);
                var suggestedName = SpektraExport.SuggestedOutputName(recipe?.SuggestedFilename, target.SourcePath, extension,
                    recipe?.SuggestedDirectory, recipe?.AddSuffixToExistingFileName == true);
                var skipPath = delivery.CanSkipRecipeOutput && recipe is { SkipExistingOutput: true,
                    SuggestedFilename: { } stem, ExactDirectory: { } exactDirectory }
                    ? Path.Combine(exactDirectory, stem + extension) : null;

                var picker = new SaveFileDialog
                {
                    Title = delivery.SaveTitle(recipe?.Width ?? 0, recipe?.Height ?? 0, deliveryProfile.Label),
                    Filter = SpektraExport.FileFilter,
                    FilterIndex = extension == ".png" ? 2 : 1,
                    FileName = suggestedName,
                    DefaultExt = extension,
                    AddExtension = true,
                    OverwritePrompt = true,
                };
                if (recipe?.SuggestedDirectory is { } directory)
                    picker.InitialDirectory = directory;
                if (skipPath is not null && ExistingRecipeOutput(skipPath, target.SourcePath) is { } existing)
                    return Skip(existing);
                var destination = chooseDestination(picker, owner);
                work.Token.ThrowIfCancellationRequested();
                if (destination is null)
                    return false;

                var approved = OutputPathGuard.Approve(destination, target.SourcePath);
                if (approved.AlreadyExists && StringComparer.OrdinalIgnoreCase.Equals(skipPath, approved.Path))
                    return Skip(approved);
                var outputFormat = SpektraExport.PixelFormatFor(approved.Path);
                var deliveryNote = delivery.SavedNote(outputFormat);
                receipt = exportJobs.Accept(work.VariantId, approved.Path);
                if (owner is { IsVisible: true } && owner.Title.EndsWith(ExportFailureTitleSuffix, StringComparison.Ordinal))
                    owner.Title = owner.Title[..^ExportFailureTitleSuffix.Length];
                var jobId = receipt.JobId;
                var started = Stopwatch.GetTimestamp();
                cancelExport.Visibility = Visibility.Visible;
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
                    var profileSetupStarted = Stopwatch.GetTimestamp();
                    var output = deliveryProfile.ConvertSrgb(image, outputFormat);
                    var profileSetupMs = Stopwatch.GetElapsedTime(profileSetupStarted).TotalMilliseconds;
                    var writeStarted = Stopwatch.GetTimestamp();
                    SpektraExport.SaveApproved(approved, output, work.Token, recipe?.Metadata, deliveryProfile,
                        recipe?.TiffCompression ?? TiffCompressOption.Zip, work.ReportEncoderWrite);
                    StartupHook.Log("export.performance.v2", new
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
                        ViewerDrainBeforeApproval = true,
                        NativeDevelopmentMs = developMs,
                        FilmRenderAndTransportMs = renderMs,
                        BlendAndCropMs = blendAndCropMs,
                        IccProfileSetupMs = profileSetupMs,
                        TransformsDeliveryProfile = deliveryProfile.RequiresConversion,
                        EncoderBytesWritten = work.EncoderBytesWritten,
                        // WIC can evaluate the bitmap transform during encoding.
                        EncodeVerifyAndCommitMs = Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds,
                        // Count the automated preparation and export. Exclude time spent in Save.
                        TotalMs = viewerDrainMs + Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    });
                    return (output.PixelWidth, output.PixelHeight);
                }, work.Token);
                var counterStatus = !disposed && recipe?.Naming?.AdvanceCounter is { } advance
                    && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileNameWithoutExtension(suggestedName),
                        Path.GetFileNameWithoutExtension(approved.Path))
                    ? AdvanceNativeExportCounter(advance, jobId) : "";
                var noteText = deliveryNote is null ? "" : Environment.NewLine + deliveryNote;
                Advance(ExportStage.Completed, $"Saved 16-bit Spektrafilm export · {result.PixelWidth} × {result.PixelHeight}{counterStatus}{noteText}",
                    result.PixelWidth, result.PixelHeight, deliveryNote: deliveryNote);
                StartupHook.Log($"Spektrafilm 16-bit export saved at {result.PixelWidth}x{result.PixelHeight}: {approved.Path}");
                return true;

                bool Skip(OutputPathGuard.ApprovedDestination existingOutput)
                {
                    receipt = exportJobs.Accept(work.VariantId, existingOutput.Path);
                    var name = Path.GetFileName(existingOutput.Path);
                    Advance(ExportStage.Skipped, $"Skipped Spektrafilm export · {name} already exists.");
                    StartupHook.Log("export.recipe-skip.v1", new { JobId = receipt.JobId });
                    if (!exportStatus.IsVisible)
                    {
                        try { SubmitNativeExportNotice("Spektrafilm export skipped", "The recipe’s output file already exists."); }
                        catch (Exception error) { StartupHook.Log("Could not display the export skip notice: " + error.Message); }
                    }
                    return true;
                }
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
        catch (OperationCanceledException error) when (exportToken.IsCancellationRequested)
        {
            ReportStagingCleanup(error);
            Advance(ExportStage.Canceled, "Spektrafilm export canceled.");
            return false;
        }
        catch (Exception error)
        {
            ReportStagingCleanup(error);
            StartupHook.Log("Spektrafilm export failed: " + error);
            var message = error.GetBaseException().Message;
            Advance(ExportStage.Failed, $"Spektrafilm export failed. {message}", error: message);
            if (disposed) return false;
            if (receipt is not null)
            {
                if (owner is { IsVisible: true })
                    owner.Title += ExportFailureTitleSuffix;
                else if (!exportStatus.IsVisible)
                    ReportExportFailureNotice(receipt.JobId);
                return false;
            }
            if (owner is { IsVisible: true })
                MessageBox.Show(owner, message, "Could not export with Spektrafilm", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show(message, "Could not export with Spektrafilm", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        static void ReportStagingCleanup(Exception error)
        {
            if (error.Data["StagingCleanupError"] is string cleanupError)
                StartupHook.Log("Could not remove the temporary Spektrafilm export: " + cleanupError);
        }

        void Advance(ExportStage stage, string message, int width = 0, int height = 0, string? error = null, string? deliveryNote = null)
        {
            if (receipt is not null)
                receipt = exportJobs.Report(receipt, stage, width, height, error, deliveryNote);
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

    private static OutputPathGuard.ApprovedDestination? ExistingRecipeOutput(string path, string original)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var approved = OutputPathGuard.Approve(path, original);
            return approved.AlreadyExists ? approved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Keep the Save step if the recipe destination cannot be verified.
            return null;
        }
    }

    private static string AdvanceNativeExportCounter(Func<bool> advance, Guid jobId)
    {
        try
        {
            if (advance())
            {
                StartupHook.Log("export.naming-counter.v1", new { JobId = jobId, Outcome = "Advanced" });
                return "";
            }
            StartupHook.Log("export.naming-counter.v1", new { JobId = jobId, Outcome = "Changed" });
            return " · Capture One’s naming changed during export.";
        }
        catch (Exception error)
        {
            StartupHook.Log("export.naming-counter.v1", new { JobId = jobId, Outcome = "Unavailable", Error = error.GetBaseException().Message });
            return " · Capture One’s output counter could not be updated.";
        }
    }

    private void ReportExportFailureNotice(Guid jobId)
    {
        bool shown;
        try { shown = showExportFailureNotice(); }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm could not show the native export notification: " + error.GetType().Name);
            shown = false;
        }
        StartupHook.Log("export.failure-notice.v1", new { JobId = jobId, Shown = shown });
    }

    private static bool ShowNativeExportFailureNotice() =>
        SubmitNativeExportNotice("Spektrafilm export failed", "Open the Spektrafilm tool for details.");

    private static bool SubmitNativeExportNotice(string title, string message) =>
        P1.C1.ToastNotificationFactory.CreateAndShowToastNotification(
            new TextOnlyVisualTemplate(title, new[] { message }, P1.C1.CaptureOneAUMID.CaptureOneMainMenuShortcutAppID),
            new ActionTemplate());

    private static string DescribePreviousExport(ExportReceipt? receipt) => receipt?.Stage switch
    {
        null => string.Empty,
        ExportStage.Completed => $"Last Spektrafilm export · {receipt.Width} × {receipt.Height} · {Path.GetFileName(receipt.Destination)}"
            + (receipt.DeliveryNote is { } note ? Environment.NewLine + note : ""),
        ExportStage.Skipped => $"Previous Spektrafilm export skipped · {Path.GetFileName(receipt.Destination)} already exists.",
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
        viewportSeedPending = true;
        source = null;
        sourceSize = null;
        failedRevision = null;
        retryViewer.Visibility = Visibility.Collapsed;
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
        var selectedSpace = (OpticalInput)inputSpace.SelectedItem;
        var space = selectedSpace.Space;
        if (!CheckSourceProfile(space)) return;
        var encoding = (OpticalOption)outputSpace.SelectedItem;
        var automatic = ((Choice)previewSize.SelectedItem).Id == "auto";
        if (automatic && pending is null && pendingDraft is null && !dragging)
        {
            var projection = viewer.ProjectedLongEdge(selectedVariant);
            var budget = RenderMemoryBudget.SystemMemory.AvailableForNewAllocation;
            var now = Environment.TickCount64;
            if (source is null && viewportSeedPending)
            {
                viewportSeedPending = false;
                viewportQuality.Update(projection ?? viewer.MeasureBeforePaint(selectedVariant), 1, budget, now, firstFrame: true);
            }
            else
            {
                var ratio = source is { } frame ? Math.Min(frame.Width, frame.Height) / (double)Math.Max(frame.Width, frame.Height) : 1;
                if (viewportQuality.Update(projection, ratio, budget, now))
                    RequestRefresh();
            }
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
        retryViewer.Visibility = Visibility.Collapsed;
        var settingsVersion = inputRevision;
        var started = Stopwatch.GetTimestamp();
        double? nativeDevelopmentMs = null, filmRenderMs = null, bitmapMs = null;
        OpticalRenderReceipt? renderReceipt = null;
        var offered = false;
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
                var developStarted = Stopwatch.GetTimestamp();
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
                var initialSourceMs = initial is not null ? Stopwatch.GetElapsedTime(initialStarted).TotalMilliseconds : 0;
                var development = Task.Run(() => nativeSize
                    ? snapshot.RenderNativePreview(job.Token)
                    : snapshot.RenderScaledPreview(longEdge, job.Token), job.Token);
                var developed = initial is not null
                    ? await RenderCoordination.PreferFinalAsync(development, token => TryShowInitialPreviewAsync(initial,
                        started, initialSourceMs, snapshot.AdjustmentKey, selectedVariant, selectedSpace, encoding, look, dragEdge, settingsVersion, token), job.Token)
                    : await development;
                job.Token.ThrowIfCancellationRequested();
                if (!HostImageSource.IsCurrentAvailableSource(window, selectedVariant, snapshot.AdjustmentKey))
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
                nativeDevelopmentMs = Stopwatch.GetElapsedTime(developStarted).TotalMilliseconds;
                StartupHook.Log($"Capture One developed {target.SourceFormat} input at {developed.Width} × {developed.Height}, {developed.BitDepth}-bit {space} in {nativeDevelopmentMs:F0} ms.");
            }
            if (settingsVersion != inputRevision)
                return;

            var input = source!;
            BitmapSource output;
            if (strength <= 0)
            {
                if (sourceDisplay is null)
                {
                    var bitmapStarted = Stopwatch.GetTimestamp();
                    var display = await Task.Run(() => ViewerDisplayBitmap.Convert(input, null, 0, space, encoding.Id, job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    sourceDisplay = display;
                    bitmapMs = Stopwatch.GetElapsedTime(bitmapStarted).TotalMilliseconds;
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
                        var renderWorker = renderer ??= new OpticalRenderer(nativePreview: true);
                        renderingFinal = true;
                        byte[] reply;
                        var renderStarted = Stopwatch.GetTimestamp();
                        try
                        {
                            reply = await renderWorker.RenderAsync(input.Width, input.Height, input.FloatBytes, args, job.Token, displayPixels);
                            renderReceipt = renderWorker.LastRender;
                        }
                        finally { renderingFinal = false; }
                        filmRenderMs = Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
                        job.Token.ThrowIfCancellationRequested();
                        if (displayPixels)
                            packed = reply;
                        else
                            renderedPixels = reply;
                    }
                    if (settingsVersion != inputRevision)
                        return;

                    var completedPixels = renderedPixels;
                    var bitmapStarted = Stopwatch.GetTimestamp();
                    var display = await Task.Run(() => packed is not null
                        ? CreateDisplayBitmap(input, packed)
                        : ViewerDisplayBitmap.Convert(input, completedPixels, strength, space, encoding.Id, job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    renderedDisplay = display;
                    renderedStrength = strength;
                    bitmapMs = Stopwatch.GetElapsedTime(bitmapStarted).TotalMilliseconds;
                }
                output = renderedDisplay;
            }
            job.Token.ThrowIfCancellationRequested();
            if (settingsVersion != inputRevision)
                return;
            if (input.Description is not { } publishedSource
                || !HostImageSource.IsCurrentAvailableSource(window, selectedVariant, publishedSource.Source.AdjustmentKey))
            {
                queued = true;
                return;
            }
            viewerFrame = output;
            frameCurrent = true;
            showSource.IsEnabled = true;
            previewDetail = comparing ? "Source preview" : automatic ? "Auto Viewer" : "Live Viewer";
            viewer.TrackFirstPaint(output, new ViewerFrameTiming(started, Stopwatch.GetTimestamp(), settingsVersion, size, automatic,
                input.BitDepth, publishedSource.Source.SourceFormat, publishedSource.Source.HostVersion,
                publishedSource.Source.HostAssemblySha256, selectedSpace.Id, encoding.Id, strength > 0,
                nativeDevelopmentMs, filmRenderMs, bitmapMs, renderReceipt));
            ShowFrame(selectedVariant, output);
            offered = true;
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
            retryViewer.Visibility = Visibility.Visible;
            StartupHook.Log("viewer.processing-failed.v1", new { ErrorKind = error.GetBaseException().GetType().Name });
        }
        catch (Exception) when (settingsVersion != inputRevision) { }
        finally
        {
            if (!offered)
                TraceUnpublishedViewerWork(ViewerFrameStage.Settled, settingsVersion, started, job.IsCancellationRequested);
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

    private void ResetDraft(bool keepRenderer = false, bool keepSourceLevels = false)
    {
        pendingDraft?.Cancel();
        if (pendingDraft is null && !keepRenderer)
            ReleaseDraftRenderer();
        if (!keepSourceLevels)
            draftPyramid.Clear();
        draftResult = null;
        draftWarmupAttempted = false;
        lastDraftRevision = -1;
    }

    private async Task TryShowInitialPreviewAsync(HostImageSource.Frame frame, long started, double nativeDevelopmentMs,
        NativeImageRevision revision, Guid variant, OpticalInput inputColor, OpticalOption encoding, LookSettings look,
        int dragEdge, int version, CancellationToken token)
    {
        if (version != inputRevision || DragLongEdge != dragEdge)
            return;
        using var work = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stop = work.Token;
        var offered = false;
        pendingDraft = work;
        try
        {
            var args = RendererOptions(look, inputColor.Id, encoding.Id, draft: true);
            var worker = draftRenderer ??= new OpticalRenderer(nativePreview: true);
            var packed = look.Strength == 1 && encoding.Id == "srgb";
            var filmStarted = Stopwatch.GetTimestamp();
            var pixels = await worker.RenderAsync(frame.Width, frame.Height, frame.FloatBytes, args, stop, packed);
            var filmMs = Stopwatch.GetElapsedTime(filmStarted).TotalMilliseconds;
            var receipt = worker.LastRender;
            var bitmapStarted = Stopwatch.GetTimestamp();
            var display = await Task.Run(() => packed ? CreateDisplayBitmap(frame, pixels)
                : ViewerDisplayBitmap.Convert(frame, pixels, look.Strength, inputColor.Space, encoding.Id, stop), stop);
            var bitmapMs = Stopwatch.GetElapsedTime(bitmapStarted).TotalMilliseconds;
            stop.ThrowIfCancellationRequested();
            if (version != inputRevision || DragLongEdge != dragEdge || OutputRenderActive
                || !HostImageSource.IsCurrentAvailableSource(window, variant, revision))
                return;
            viewerFrame = display;
            frameCurrent = false;
            previewDetail = "Loading Viewer preview";
            TrackBriefPaint(display, frame, ViewerFrameStage.Initial, started, version, Math.Min(dragEdge, InitialPreviewLongEdge), inputColor, encoding,
                look.Strength, nativeDevelopmentMs, filmMs, bitmapMs, receipt);
            ShowFrame(variant, display);
            offered = true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && stop.IsCancellationRequested) { }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            StartupHook.Log("Spektrafilm initial preview: " + error.GetBaseException().Message);
        }
        finally
        {
            if (!offered) TraceUnpublishedViewerWork(ViewerFrameStage.Initial, version, started, stop.IsCancellationRequested);
            pendingDraft = null;
            if (stop.IsCancellationRequested)
                ReleaseDraftRenderer();
        }
    }

    private async Task RefreshDraftAsync(Guid selectedVariant, NativeImageRevision revision,
        HostImageSource.Frame input, OpticalInput inputColor, OpticalOption encoding, LookSettings look, string viewerSize, int dragEdge, bool warmup = false)
    {
        draftWarmupAttempted = true;
        if (!warmup)
            lastDraftRevision = inputRevision;
        var args = look.Strength > 0 ? RendererOptions(look, inputColor.Id, encoding.Id, draft: true) : Array.Empty<string>();
        var started = Stopwatch.GetTimestamp();
        var version = inputRevision;
        double? filmMs = null;
        OpticalRenderReceipt? receipt = null;
        var offered = false;
        using var job = new CancellationTokenSource();
        pendingDraft = job;
        warmingDraft = warmup;
        try
        {
            var frame = draftPyramid.Find(input, dragEdge, inputColor.Space);
            if (frame is null)
            {
                frame = await Task.Run(() => ViewerDraftSource.Downsample(input, dragEdge, job.Token, inputColor.Space), job.Token);
                job.Token.ThrowIfCancellationRequested();
                draftPyramid.Remember(input, dragEdge, inputColor.Space, frame);
            }
            var cached = look.Strength > 0 ? draftResult : null;
            var pack = look.Strength == 1 && encoding.Id == "srgb";
            if (look.Strength > 0 && (cached is not { } previous || !args.SequenceEqual(previous.Arguments)
                || previous.Packed && !pack))
            {
                var worker = draftRenderer ??= new OpticalRenderer(nativePreview: true);
                var filmStarted = Stopwatch.GetTimestamp();
                var rendered = await (warmup
                    ? worker.WarmPreviewAsync(frame.Width, frame.Height, frame.FloatBytes, args, job.Token, pack)
                    : worker.RenderAsync(frame.Width, frame.Height, frame.FloatBytes, args, job.Token, pack));
                filmMs = Stopwatch.GetElapsedTime(filmStarted).TotalMilliseconds;
                receipt = worker.LastRender;
                job.Token.ThrowIfCancellationRequested();
                cached = draftResult = (args, rendered, pack);
            }
            if (warmup)
                return;
            var bitmapStarted = Stopwatch.GetTimestamp();
            var output = await Task.Run(() => cached is { Packed: true } packed
                ? CreateDisplayBitmap(frame, packed.Pixels)
                : ViewerDisplayBitmap.Convert(frame, cached?.Pixels, look.Strength, inputColor.Space, encoding.Id, job.Token), job.Token);
            var bitmapMs = Stopwatch.GetElapsedTime(bitmapStarted).TotalMilliseconds;
            job.Token.ThrowIfCancellationRequested();
            if (!WantsDragPreview || OutputRenderActive || activeVariant != selectedVariant || !ReferenceEquals(source, input)
                || !ReferenceEquals(inputSpace.SelectedItem, inputColor) || !ReferenceEquals(outputSpace.SelectedItem, encoding)
                || ViewerRenderSize != viewerSize || DragLongEdge != dragEdge
                || !HostImageSource.IsCurrentAvailableSource(window, selectedVariant, revision))
                return;

            viewerFrame = output;
            frameCurrent = false;
            previewDetail = "Drag preview";
            TrackBriefPaint(output, frame, ViewerFrameStage.Drag, started, version, dragEdge, inputColor, encoding,
                look.Strength, null, filmMs, bitmapMs, receipt);
            ShowFrame(selectedVariant, output);
            offered = true;
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) when (!WantsDragPreview || selectionUnavailable) { }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm drag preview: " + error.GetBaseException().Message);
        }
        finally
        {
            if (!offered && !warmup) TraceUnpublishedViewerWork(ViewerFrameStage.Drag, version, started, job.IsCancellationRequested);
            pendingDraft = null;
            warmingDraft = false;
            if (job.IsCancellationRequested)
                ReleaseDraftRenderer();
        }
    }

    private void TrackBriefPaint(BitmapSource image, HostImageSource.Frame source, ViewerFrameStage stage, long started,
        int version, int requestedEdge, OpticalInput input, OpticalOption encoding, double strength,
        double? nativeMs, double? filmMs, double bitmapMs, OpticalRenderReceipt? receipt)
    {
        if (source.Description is not { } description) return;
        var identity = description.Source;
        viewer.TrackFirstPaint(image, new ViewerFrameTiming(started, Stopwatch.GetTimestamp(), version,
            requestedEdge.ToString(CultureInfo.InvariantCulture), false, source.BitDepth, identity.SourceFormat, identity.HostVersion,
            identity.HostAssemblySha256, input.Id, encoding.Id, strength > 0, nativeMs, filmMs, bitmapMs, receipt, stage));
    }

    private void TraceUnpublishedViewerWork(ViewerFrameStage stage, int version, long started, bool canceled) =>
        ViewerFrameTiming.LogUnpublished(stage, version, started,
            canceled ? "Canceled" : version != inputRevision ? "Superseded" : "Unavailable");

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

    internal static IReadOnlyList<(string StateKey, string WorkerKey)> DevelopmentRendererParameters { get; } =
        Array.AsReadOnly(FilmControls.Development.Values.Where(item => item.WorkerKey is not null)
            .Select(item => (item.StateKey, item.WorkerKey!)).ToArray());

    internal string[] RendererOptions(LookSettings look, string inputColor, string outputColor, bool export = false, bool draft = false)
    {
        if (look.Mode is "process_negative" or "process_negatives")
            throw new InvalidOperationException("Process Negative requires an encoded negative scan as input.");
        if (look.Development.GetValueOrDefault("print_exhaustion") != 0)
            throw new InvalidOperationException("Set Developer exhaustion to zero before rendering this optical preview.");

        var args = new List<string>();
        OpticalOption Selected(string key) => (OpticalOption)choices[key].SelectedItem;
        void Set(string key, string value) { args.Add("--" + key); args.Add(value); }
        void Numeric(string key, double value) => Set(key, RendererParameters.NumberToken(value));

        Set("stock", look.Film);
        Set("paper", look.Paper);
        Set("input-color", inputColor);
        Set("output-color", outputColor);
        var scanning = look.Mode.Contains("scan", StringComparison.Ordinal);
        Set("mode", scanning ? (look.ScanInvert ? "scan_inverted" : "scan") : "print");
        Set("spectral-method", look.SpectralMethod);
        Set("film-push-pull-mode", look.FilmPushPullMode);
        Numeric("film-exposure", look.Exposure);
        Numeric("print-exposure", look.PrintExposure);
        foreach (var (sourceKey, rendererKey) in DevelopmentRendererParameters)
        {
            if (look.Development.TryGetValue(sourceKey, out var value))
                Numeric(rendererKey, value);
        }
        SetFilter("uv_cutoff", "uv-enabled");
        SetFilter("ir_cutoff", "ir-enabled");
        void SetFilter(string key, string option)
        {
            var baseline = FilmControls.Development[key].Initial;
            Set(option, look.Development.GetValueOrDefault(key, baseline) != baseline ? "true" : "false");
        }

        Func<string, bool> isOn = key => toggles[key].IsChecked == true;
        bool Send(string key) => OpticalControls.Parameters[key].ShouldSend(!scanning, isOn);
        foreach (var key in choices.Keys)
            if (Send(key))
                Set(key, Selected(key).Id);
        var fastGrain = !export && (draft || fastSynthesis.IsChecked == true) && Selected("grain-model").Id == "synthesis";
        var grainQuality = Math.Min(values["grain-synthesis-quality"].Value, 2);
        foreach (var (key, slider) in values)
        {
            if (!Send(key))
                continue;
            var value = slider.Value;
            if (fastGrain && key == "grain-synthesis-samples")
                value = Math.Min(value, Math.Floor(2 / grainQuality));
            if (fastGrain && key == "grain-synthesis-quality")
                value = grainQuality;
            Numeric(key, value);
        }
        foreach (var (key, toggle) in toggles)
            if (Send(key))
                Set(key, toggle.IsChecked == true ? "true" : "false");
        return args.ToArray();
    }

    private static BitmapSource CreateDisplayBitmap(HostImageSource.Frame frame, byte[] pixels)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, pixels, frame.Width * 4);
        image.Freeze();
        return image;
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
    internal sealed record Settings(Dictionary<string, double> Numbers, Dictionary<string, string> Selections, Dictionary<string, bool> Switches);
}
