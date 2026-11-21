using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using P1.C1.Core.ModelEntities;
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
    private readonly ICaptureOneDocument document;
    private readonly Func<Guid, VariantLook> readBatchLook;
    private readonly Func<LookSettings?> getSettings;
    private readonly Func<Guid, bool> useSelectedLook;
    private readonly Dictionary<string, NativeSlider> values = new();
    private readonly Dictionary<string, ComboBox> choices = new();
    private readonly Dictionary<string, CheckBox> toggles = new();
    private readonly List<FrameworkElement> printOnlyControls = new();
    private bool printing = true;
    private readonly TextBlock status = new() { Margin = new Thickness(0, 5, 0, 3), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly ViewerPreview viewer;
    private readonly ExportJobJournal exportJobs;
    private static readonly ConditionalWeakTable<ICaptureOneDocument, ExportActivity> ExportActivities = new();
    private readonly ExportActivity exportActivity;
    private Func<bool> showExportFailureNotice = ShowNativeExportFailureNotice;
    private readonly ViewportQuality viewportQuality = new();
    private readonly ViewerHistogramControl histogram = new();
    private OpticalRenderer? renderer;
    private CancellationTokenSource? pending;
    private CancellationTokenSource? pendingDraft;
    private OpticalRenderer? draftRenderer;
    private readonly ViewerDraftPyramid draftPyramid = new();
    private (string[] Arguments, byte[] Pixels, bool Packed)? draftResult;
    private IDisposable? draftResultLease;
    private bool draftWarmupAttempted;
    private bool warmingDraft;
    private int lastDraftRevision = -1;
    private bool dragging;
    private long lastDragChange;
    private HostImageSource.Frame? source;
    private HostImageSource.PreviewCropIdentity? sourceCropIdentity;
    private NativeImageRevision? failedRevision;
    private readonly PreviewRendererRetry<(Guid Variant, int Generation, NativeImageRevision Source)> rendererRetry = new();
    private byte[]? renderedPixels;
    private IDisposable? renderedPixelsLease;
    private string[]? renderedArguments;
    private BitmapSource? sourceDisplay;
    private IDisposable? sourceDisplayLease;
    private BitmapSource? renderedDisplay;
    private IDisposable? renderedDisplayLease;
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
    private bool exportWasActive;
    private bool lutRenderActive;
    private Task previewWorkersExited = Task.CompletedTask;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock exportStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button cancelExport = new() { Content = "Cancel export", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button stopExportBatch = new() { Content = "Stop batch", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button retryExport = new() { Content = "Retry interrupted export…", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button resumeExportBatch = new() { Content = "Resume interrupted batch…", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Button discardExportBatch = new() { Content = "Discard interrupted batch…", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private ExportReceipt? interruptedExport;
    private ExportBatchPlan? interruptedBatch;
    private bool disposed;
    private bool renderingFinal;
    private bool selectionUnavailable;
    private bool originalUnavailable;
    private string? previewDetail;

    private SpatialPreview(object window, ICaptureOneDocument document, Func<LookSettings?> getSettings, Func<Guid, bool> useSelectedLook,
        Func<Guid, VariantLook> readBatchLook,
        ExportJobJournal exportJobs)
    {
        this.window = window;
        this.document = document;
        this.readBatchLook = readBatchLook;
        this.getSettings = getSettings;
        this.useSelectedLook = useSelectedLook;
        this.exportJobs = exportJobs;
        exportActivity = ExportActivities.GetValue(document, static _ => new ExportActivity());
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
        RefreshOpticalAvailability();
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
        interruptedBatch = exportActivity.BatchActive ? null : exportJobs.ReadPendingBatch();
        var latest = exportActivity.Active is null ? exportJobs.ReadLatest() : null;
        interruptedExport = latest is { Finished: false, BatchId: null } && (latest.Retry is { Valid: true } || latest.Publication is not null)
            ? latest : null;
        exportStatus.Text = exportActivity.Message ?? (interruptedBatch is null ? DescribePreviousExport(latest)
            : "Interrupted batch · Review pending exports.");
        exportControls.Children.Add(exportStatus);
        exportControls.Children.Add(cancelExport);
        exportControls.Children.Add(stopExportBatch);
        exportControls.Children.Add(retryExport);
        exportControls.Children.Add(resumeExportBatch);
        exportControls.Children.Add(discardExportBatch);
        cancelExport.Click += (_, _) => exportActivity.Active?.Cancel();
        stopExportBatch.Click += (_, _) => exportActivity.StopBatch();
        retryExport.Click += async (_, _) => await RetryInterruptedExportAsync(ChooseExportDestination);
        resumeExportBatch.Click += async (_, _) => await ResumeExportBatchAsync(plan =>
            ConfirmBatch(resumeExportBatch, $"Resume pending exports in {plan.Folder}? Existing files are preserved."));
        discardExportBatch.Click += (_, _) =>
        {
            if (interruptedBatch is { } plan && ConfirmBatch(discardExportBatch, "Discard this batch’s pending jobs? Existing files are preserved.")
                && CloseBatchPlan(plan))
                exportActivity.Report(null, "Interrupted batch discarded.");
        };
        ExportControls = exportControls;
        exportActivity.Changed += UpdateExportControls;
        UpdateExportControls();
    }

    internal FrameworkElement Element { get; }
    internal FrameworkElement LiveControls { get; }
    internal FrameworkElement ExportControls { get; }

    internal void SetPrintMode(bool printing)
    {
        this.printing = printing;
        foreach (var control in printOnlyControls)
        {
            control.IsEnabled = printing;
            control.ToolTip = printing ? null : OpticalControls.Binding.PrintOnlyHint;
        }
        RefreshOpticalAvailability();
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

    public void Dispose() => Retire(keepAcceptedExport: false);

    internal void Retire(bool keepAcceptedExport)
    {
        if (disposed) return;
        disposed = true;
        enabled = false;
        lifetime.Cancel();
        lifetime.Dispose();
        exportActivity.Changed -= UpdateExportControls;
        if (!keepAcceptedExport && exportActivity.BatchActive)
            exportActivity.StopBatch(persist: false);
        if (!keepAcceptedExport || exportActivity.Active is { IsAccepted: false })
            exportActivity.Active?.Cancel();
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

    internal static SpatialPreview BuildForPanel(object window, ICaptureOneDocument document, Func<LookSettings?> getSettings,
        Func<Guid, bool> useSelectedLook, Func<Guid, VariantLook> readBatchLook, ExportJobJournal exportJobs) =>
        new(window, document, getSettings, useSelectedLook, readBatchLook, exportJobs);

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
        RefreshOpticalAvailability();
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
        choices["film-format"].ToolTip = "Film format sets the physical scale of grain, diffusion and other optical effects.";
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
        return view;
    }

    private static Expander Section(string key, string title, UIElement content) =>
        PanelPreferences.CreateSection(key, title, content, 5);

    private void AddToggle(Panel panel, OpticalToggle definition)
    {
        var item = new CheckBox { Content = definition.Label, IsChecked = definition.Initial, Margin = new Thickness(0, 3, 0, 2) };
        item.Checked += (_, _) => OnOpticalSelectorChanged();
        item.Unchecked += (_, _) => OnOpticalSelectorChanged();
        toggles.Add(definition.Key, item);
        panel.Children.Add(item);
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
        chooser.SelectionChanged += (_, _) => OnOpticalSelectorChanged();
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
        previewSize.SelectionChanged += (_, _) =>
        {
            if (previewSize.SelectedItem is Choice { Id: "auto" })
                ResetViewportQuality();
            OnPreferencesChanged();
        };
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

    private void OnOpticalSelectorChanged()
    {
        if (loading)
            return;
        RefreshOpticalAvailability();
        OnSettingsChanged();
    }

    private void RefreshOpticalAvailability()
    {
        foreach (var binding in OpticalControls.ConditionalParameters)
        {
            var key = binding.Parameter.Key;
            FrameworkElement control = binding.Parameter switch
            {
                OpticalSlider => values[key],
                OpticalChoice => choices[key],
                _ => toggles[key],
            };
            var hint = binding.UnavailableReason(printing, OpticalToggleEnabled, SelectedOpticalChoice);
            control.IsEnabled = hint is null;
            control.ToolTip = hint ?? (binding.Parameter is OpticalSlider ? binding.Parameter.Label : null);
            ToolTipService.SetShowOnDisabled(control, true);
        }
    }

    private string SelectedOpticalChoice(string key) => (choices[key].SelectedItem as OpticalOption)?.Id
        ?? ((OpticalChoice)OpticalControls.Parameters[key].Parameter).Default;

    private bool OpticalToggleEnabled(string key) => toggles[key].IsChecked == true;

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
        InvalidateSettledFrame();
        selectionUnavailable = false;
        retryViewer.Visibility = Visibility.Collapsed;
        inputRevision++;
        if (warmingDraft)
            pendingDraft?.Cancel();
        if (renderingFinal && !dragging)
            pending?.Cancel();
        refresh.Start();
    }

    private void InvalidateSettledFrame()
    {
        frameCurrent = false;
        histogram.SetFrame(null);
        savePreview.IsEnabled = false;
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
        retryExport.IsEnabled = value || interruptedExport?.Publication is not null;
        if (enabled)
        {
            RequestRefresh();
        }
        else
        {
            ClearPreview();
        }
    }

    private bool OutputRenderActive => exportActivity.Active is not null || exportActivity.BatchActive || lutRenderActive;

    private void UpdateExportControls()
    {
        var active = exportActivity.Active;
        if (exportActivity.Message is { } message)
            exportStatus.Text = message;
        cancelExport.Visibility = active is { IsAccepted: true } ? Visibility.Visible : Visibility.Collapsed;
        stopExportBatch.Visibility = exportActivity.BatchActive && !exportActivity.BatchStopped ? Visibility.Visible : Visibility.Collapsed;
        stopExportBatch.Content = active is { IsAccepted: true } ? "Stop after this image" : "Stop batch";
        var busy = active is not null || exportActivity.BatchActive;
        var resume = exportWasActive && !busy;
        exportWasActive = busy;
        if (resume)
        {
            interruptedBatch = exportJobs.ReadPendingBatch();
            RequestRefresh();
        }
        retryExport.Visibility = !busy && interruptedBatch is null && interruptedExport is not null ? Visibility.Visible : Visibility.Collapsed;
        retryExport.IsEnabled = enabled || interruptedExport?.Publication is not null;
        retryExport.Content = interruptedExport?.Publication is not null ? "Check interrupted export…" : "Retry interrupted export…";
        resumeExportBatch.Visibility = discardExportBatch.Visibility = !busy && interruptedBatch is not null
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record BatchExport(HostProfile.Target Target, HostImageSource.Snapshot Snapshot, OpticalInput Input,
        string Encoding, double Strength, string[] Arguments, OutputPathGuard.OriginalFile Original, string? RetryModel,
        ExportColorProfile DeliveryProfile,
        OutputPathGuard.ApprovedDestination? Destination = null,
        ExportBatchPlan? Plan = null, ExportBatchEntry? Entry = null);

    private async Task<BatchExport> CaptureBatchSourceAsync(HostProfile.Selection item, CancellationToken token)
    {
        var target = HostProfile.TargetFor(item.Variant);
        var saved = readBatchLook(target.VariantId);
        var strength = saved.Enabled ? saved.Look.Strength : 0;
        var input = OpticalControls.Inputs.Single(option => option.Id ==
            (strength == 0 ? "srgb" : saved.Optics.Selections.GetValueOrDefault("input-color", "srgb")));
        var encoding = strength == 0 ? "srgb" : saved.Optics.Selections.GetValueOrDefault("output-color", "srgb");
        ValidateSettings(saved.Optics);
        var arguments = strength == 0 ? Array.Empty<string>()
            : BuildRendererOptions(saved.Look, input.Id, encoding, export: true, draft: false, saved.Optics);
        var original = await Task.Run(() => new OutputPathGuard.OriginalFile(target.SourcePath), token).WaitAsync(token);
        token.ThrowIfCancellationRequested();
        var snapshot = HostImageSource.CaptureBatchExport(item, target, input.Space);
        if (snapshot.Recipe is not { } recipe)
            throw new InvalidOperationException("Select an Export Recipe before starting a batch.");
        return new(target, snapshot, input, encoding, strength, arguments, original, snapshot.ReadRetryModel(),
            ExportColorProfile.ForRecipe(recipe.ColorProfileName, recipe.ColorProfilePath));
    }

    private static bool ConfirmBatch(Button button, string text) =>
        (Window.GetWindow(button) is { IsVisible: true } owner
            ? MessageBox.Show(owner, text, StartupHook.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(text, StartupHook.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)) == MessageBoxResult.Yes;

    internal async Task<int> ExportBatchAsync(HostProfile.Selection[] selection, Window? owner,
        Func<OpenFolderDialog, Window?, string?> chooseFolder)
    {
        if (disposed || OutputRenderActive) return 0;
        if (interruptedBatch is not null)
        {
            exportActivity.Report(null, "Resume or discard the interrupted batch before starting another batch.");
            return 0;
        }
        if (selection.Length < 2)
        {
            exportActivity.Report(null, "Select at least two images for batch export.");
            return 0;
        }

        var saved = 0;
        var skipped = 0;
        var failed = 0;
        var canceled = 0;
        string? firstError = null;
        string? summary = null;
        SpektraExport.RecipeDelivery? completedDelivery = null;
        var token = exportActivity.BeginBatch(lifetime.Token);
        try
        {
            exportActivity.Report(null, "Preparing batch export…");
            await DrainPreviewWorkersAsync(token);
            await HostImageSource.WaitForNativeDevelopmentAsync(token);
            token.ThrowIfCancellationRequested();
            var sources = new List<BatchExport>();
            foreach (var item in selection)
            {
                try
                {
                    sources.Add(await CaptureBatchSourceAsync(item, token));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failed++;
                    firstError ??= error.GetBaseException().Message;
                    StartupHook.Log("Could not prepare a Spektrafilm batch image: " + error.GetBaseException().Message);
                }
            }
            if (sources.Count == 0)
                return saved;
            token.ThrowIfCancellationRequested();

            var batchRecipe = sources[0].Snapshot.Recipe!;
            var recipes = sources.Select(item => item.Snapshot.Recipe!).ToArray();
            if (recipes.Any(recipe => recipe.Name != batchRecipe.Name || recipe.FormatId != batchRecipe.FormatId
                || recipe.TiffBits != batchRecipe.TiffBits || recipe.CropMode != batchRecipe.CropMode))
                throw new InvalidOperationException("The selected Export Recipe changed during preparation. Start the batch again.");
            var omissions = recipes.Aggregate(SpektraExport.RecipeOmission.None, (flags, recipe) => flags | recipe.Omissions);
            var policy = new SpektraExport.RecipeDelivery(batchRecipe.FormatId, batchRecipe.TiffBits, batchRecipe.CropMode, omissions,
                HasPsdCropPath: recipes.All(recipe => recipe.CropPath is not null),
                HasPsdGuides: recipes.Any(recipe => recipe.Guides is { Length: > 0 }));
            var skipExisting = policy.CanSkipRecipeOutput && recipes.Any(recipe => recipe.SkipExistingOutput && recipe.SuggestedFilename is not null);
            var picker = new OpenFolderDialog
            {
                Title = policy.BatchTitle(sources.Count, skipExisting),
                Multiselect = false,
            };
            if (batchRecipe.SuggestedDirectory is { } suggested && sources.All(item =>
                StringComparer.OrdinalIgnoreCase.Equals(suggested, item.Snapshot.Recipe!.SuggestedDirectory)))
                picker.InitialDirectory = suggested;
            var destination = chooseFolder(picker, owner);
            if (destination is null || disposed || exportActivity.BatchStopped)
            {
                summary = "Batch export canceled.";
                return saved;
            }
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            var queue = new List<BatchExport>(sources.Count);
            var entries = new List<ExportBatchEntry>(sources.Count);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prepared = 0;
            foreach (var item in sources)
            {
                exportActivity.Report(null, $"Checking batch image {++prepared} of {sources.Count}…");
                token.ThrowIfCancellationRequested();
                try
                {
                    var recipe = item.Snapshot.Recipe!;
                    var name = SpektraExport.SuggestedOutputName(recipe.SuggestedFilename, item.Target.SourcePath, policy.Extension);
                    var requestedPath = Path.Combine(folder, name);
                    var allowSkip = policy.CanSkipRecipeOutput && recipe.SkipExistingOutput && recipe.SuggestedFilename is not null
                        && !reserved.Contains(requestedPath);
                    var approved = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (allowSkip && ExistingRecipeOutput(requestedPath, item.Original) is { } existing)
                            return existing;
                        item.DeliveryProfile.VerifyFiles();
                        return ApproveNewBatchDestination(folder, name, item.Original, reserved, token);
                    }, token).WaitAsync(token);
                    if (!approved.AlreadyExists) reserved.Add(approved.Path);
                    var descriptor = approved.AlreadyExists ? null : await CaptureBatchDescriptorAsync(item, token).WaitAsync(token);
                    queue.Add(item with { Destination = approved });
                    entries.Add(new(Guid.NewGuid(), item.Target.ImageId, item.Target.VariantId, item.Target.SourcePath,
                        approved.Path, descriptor, SkipExisting: approved.AlreadyExists));
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    failed++;
                    firstError ??= error.GetBaseException().Message;
                }
            }
            if (queue.Count == 0)
                return saved;
            token.ThrowIfCancellationRequested();
            var plan = ExportBatchPlan.Create(folder, entries.ToArray());
            exportJobs.SaveBatch(plan, token);
            interruptedBatch = plan;
            exportActivity.PersistBatchStop = () => CloseBatchPlan(plan);
            for (var index = 0; index < queue.Count; index++)
            {
                if (exportActivity.BatchStopped)
                {
                    canceled += queue.Count - index;
                    break;
                }
                var entry = plan.Entries[index];
                if (entry.SkipExisting)
                {
                    RecordBatchSkip(plan, entry);
                    skipped++;
                    continue;
                }
                var item = queue[index] with { Plan = plan, Entry = entry };
                exportActivity.Report(null, $"Exporting image {index + 1} of {queue.Count}…");
                var success = await RunExportWithDestinationAsync(true, null, ChooseExportDestination, null, item);
                if (success)
                {
                    saved++;
                    completedDelivery = policy with
                    {
                        Omissions = (completedDelivery?.Omissions ?? SpektraExport.RecipeOmission.None) | item.Snapshot.Recipe!.Omissions,
                        HasPsdCropPath = (completedDelivery?.HasPsdCropPath ?? true) && item.Snapshot.Recipe!.CropPath is not null,
                        HasPsdGuides = (completedDelivery?.HasPsdGuides ?? false) || item.Snapshot.Recipe!.Guides is { Length: > 0 },
                    };
                    continue;
                }
                var latest = exportJobs.ReadBatchItem(plan, entry);
                if (latest?.Stage == ExportStage.Canceled || latest is null && exportActivity.BatchStopped)
                {
                    canceled++;
                    continue;
                }
                failed++;
                firstError ??= exportActivity.Message;
            }
            if (plan.Entries.All(item => exportJobs.ReadBatchItem(plan, item) is { Finished: true }))
                CloseBatchPlan(plan);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            summary = "Batch export canceled.";
        }
        catch (Exception error)
        {
            summary = "Could not start the batch export. " + error.GetBaseException().Message;
            StartupHook.Log(error.ToString());
        }
        finally
        {
            var deliveryNote = completedDelivery is { } completed
                ? completed.SavedNote(SpektraExport.FormatFor(completed.Extension), batch: true) : null;
            var details = string.Join(Environment.NewLine, new[] { firstError, deliveryNote }.OfType<string>());
            var outcome = $"Saved {saved} of {selection.Length} images · {failed} failed · {canceled} canceled"
                + (skipped > 0 ? $" · {skipped} skipped" : "");
            exportActivity.EndBatch(summary ?? (details.Length == 0 ? outcome : outcome + Environment.NewLine + details));
        }
        return saved;
    }

    private void RecordBatchSkip(ExportBatchPlan plan, ExportBatchEntry entry)
    {
        var receipt = exportJobs.Report(exportJobs.BeginBatchItem(plan, entry), ExportStage.Skipped);
        StartupHook.Log("export.recipe-skip.v1", new { JobId = receipt.JobId });
    }

    private Task<ExportRetryDescriptor?> CaptureBatchDescriptorAsync(BatchExport item, CancellationToken token, FileStream? held = null)
    {
        return CaptureRetryDescriptorAsync(item.Snapshot, item.RetryModel, item.Original, item.Arguments, item.Strength, item.Encoding,
            item.DeliveryProfile, followRecipe: true, token, held);
    }

    private bool CloseBatchPlan(ExportBatchPlan plan)
    {
        try
        {
            exportJobs.SaveBatch(plan with { Closed = true });
            interruptedBatch = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StartupHook.Log("Could not close the saved export batch: " + error.GetBaseException().Message);
            if (!OutputRenderActive)
                exportActivity.Report(null, "Could not close the saved batch. Check access to the Spektrafilm data folder.");
            return false;
        }
    }

    private async Task<int> ResumeExportBatchAsync(Func<ExportBatchPlan, bool> confirm)
    {
        if (disposed || OutputRenderActive || interruptedBatch is not { } plan) return 0;
        var saved = 0;
        var occupied = 0;
        var alreadySaved = 0;
        var pending = plan.Entries.Length;
        var approvedRecovery = false;
        string? detail = null;
        var token = exportActivity.BeginBatch(lifetime.Token);
        try
        {
            if (!confirm(plan) || disposed || exportActivity.BatchStopped) return 0;
            approvedRecovery = true;
            exportActivity.PersistBatchStop = () => CloseBatchPlan(plan);
            await DrainPreviewWorkersAsync(token);
            await HostImageSource.WaitForNativeDevelopmentAsync(token);
            token.ThrowIfCancellationRequested();
            foreach (var entry in plan.Entries)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var previous = exportJobs.ReadBatchItem(plan, entry);
                    if (previous is { Finished: true }) continue;
                    if (entry.SkipExisting)
                    {
                        RecordBatchSkip(plan, entry);
                        occupied++;
                        continue;
                    }
                    if (previous is { Publication: not null } && await CompletePublishedAsync(previous, token) is not null)
                    {
                        alreadySaved++;
                        continue;
                    }
                    var source = await CaptureBatchSourceAsync(HostProfile.RecoverSelection(window, document, entry), token);
                    var approved = await Task.Run(() => source.Original.Approve(entry.Destination), token).WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    if (approved.AlreadyExists)
                    {
                        RecordBatchSkip(plan, entry);
                        occupied++;
                        continue;
                    }
                    if (entry.Retry is null)
                        throw new InvalidOperationException("A pending image could not be verified at approval.");
                    await using var held = await OpenBatchOriginalAsync(source.Original, token);
                    var descriptor = await CaptureBatchDescriptorAsync(source, token, held);
                    if (descriptor != entry.Retry)
                    {
                        StartupHook.Log("export.batch.retry-mismatch.v1", new
                        {
                            Verified = descriptor is not null,
                            SourceChanged = descriptor is not null && descriptor.Original != entry.Retry.Original,
                            NativeModelChanged = descriptor is not null && descriptor.NativeModel != entry.Retry.NativeModel,
                            DeliveryChanged = descriptor is not null && descriptor.Delivery != entry.Retry.Delivery,
                        });
                        throw new InvalidOperationException("A queued image, its saved look or the Export Recipe changed.");
                    }
                    if (exportActivity.BatchStopped) break;
                    token.ThrowIfCancellationRequested();
                    if (await RunExportWithDestinationAsync(true, null, ChooseExportDestination, null,
                        source with { Destination = approved, Plan = plan, Entry = entry }))
                        saved++;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    detail ??= error.GetBaseException().Message;
                    StartupHook.Log("A saved batch entry could not resume: " + error.GetBaseException().Message);
                }
            }
            pending = plan.Entries.Count(item => exportJobs.ReadBatchItem(plan, item) is not { Finished: true });
            if (pending == 0) CloseBatchPlan(plan);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            pending = plan.Entries.Count(item => exportJobs.ReadBatchItem(plan, item) is not { Finished: true });
            detail = exportActivity.BatchStopped ? "Batch stopped." : "Batch canceled.";
        }
        catch (Exception error)
        {
            detail = error.GetBaseException().Message;
            StartupHook.Log(error.ToString());
        }
        finally
        {
            var summary = approvedRecovery ? $"Resumed batch · {saved} saved"
                + (alreadySaved > 0 ? $" · {alreadySaved} already saved" : "")
                + $" · {occupied} skipped · {pending} pending"
                : "Interrupted batch · Review pending exports.";
            exportActivity.EndBatch(detail is null ? summary : summary + Environment.NewLine + detail);
        }
        return saved;
    }

    private static OutputPathGuard.ApprovedDestination ApproveNewBatchDestination(string folder, string filename, OutputPathGuard.OriginalFile original,
        IReadOnlySet<string> reserved, CancellationToken token)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        for (var suffix = 0; suffix <= 10_000; suffix++)
        {
            token.ThrowIfCancellationRequested();
            var name = suffix == 0 ? filename : $"{stem}-{suffix}{extension}";
            var path = Path.Combine(folder, name);
            if (reserved.Contains(path) || File.Exists(path) || Directory.Exists(path)) continue;
            var approved = original.Approve(path);
            if (approved.AlreadyExists) continue;
            return approved;
        }
        throw new IOException("There are too many files with the same export name in this folder.");
    }

    private static async Task<FileStream> OpenBatchOriginalAsync(OutputPathGuard.OriginalFile original, CancellationToken token)
    {
        var open = Task.Run(original.OpenRead, token);
        try { return await open.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Windows can still return a file handle after the pending open was abandoned.
            _ = ReleaseWhenOpenedAsync(open);
            throw;
        }

        static async Task ReleaseWhenOpenedAsync(Task<FileStream> open)
        {
            try { await using var stream = await open.ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                // No handle was obtained.
            }
        }
    }

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

    internal Task<bool> ExportAsync(Window? owner) =>
        RunExportWithDestinationAsync(true, owner, ChooseExportDestination, null, useRecipeDestination: true);

    private Task<bool> ExportWithDestinationAsync(bool useRecipeSize, Window? owner,
        Func<SaveFileDialog, Window?, string?> chooseDestination) =>
        RunExportWithDestinationAsync(useRecipeSize, owner, chooseDestination, null);

    private Task<ExportReceipt?> CompletePublishedAsync(ExportReceipt receipt, CancellationToken stop) =>
        Task.Run(() => exportJobs.CompletePublished(receipt, stop), stop).WaitAsync(stop);

    private async Task<bool> RetryInterruptedExportAsync(Func<SaveFileDialog, Window?, string?> chooseDestination)
    {
        if (disposed || OutputRenderActive || interruptedExport is not { } interrupted) return false;
        if (interrupted.Publication is not null)
        {
            using var check = new ExportOperation(interrupted.VariantId);
            exportActivity.Begin(check);
            try
            {
                exportActivity.Report(check, "Checking the interrupted export’s output file…");
                if (await CompletePublishedAsync(interrupted, lifetime.Token) is { } completed)
                {
                    interruptedExport = null;
                    exportActivity.Report(check, DescribePreviousExport(completed));
                    return true;
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
            {
                exportActivity.Report(check, "Could not confirm the export’s output file. " + error.GetBaseException().Message);
                return false;
            }
            finally { exportActivity.End(check); }
        }
        if (interrupted.Retry is not { } retry)
        {
            exportActivity.Report(null, "Could not confirm the export’s output file. Start a new export.");
            return false;
        }
        if (!enabled)
        {
            exportActivity.Report(null, "Enable Spektrafilm to retry the interrupted render.");
            return false;
        }
        return await RunExportWithDestinationAsync(retry.FollowRecipe, Window.GetWindow(retryExport), chooseDestination, interrupted);
    }

    private async Task<bool> RunExportWithDestinationAsync(bool useRecipeSize, Window? owner,
        Func<SaveFileDialog, Window?, string?> chooseDestination, ExportReceipt? interrupted, BatchExport? batch = null,
        bool useRecipeDestination = false)
    {
        if (exportActivity.Active is not null || (batch is null && (disposed || !enabled || exportActivity.BatchActive)))
            return false;
        if (lutRenderActive)
        {
            exportStatus.Text = "Wait for the Spektrafilm LUT export to finish.";
            return false;
        }
        ExportReceipt? receipt = null;
        var form = (System.Windows.Forms.Form)window;
        System.Windows.Forms.FormClosedEventHandler? closed = null;
        try
        {
            var look = batch is null ? getSettings() ?? throw new InvalidOperationException("Wait for the Spektrafilm controls to finish loading.") : null;
            var strength = batch?.Strength ?? look!.Strength;
            var target = batch?.Target ?? HostProfile.CaptureTarget(window);
            if (interrupted is not null && interrupted.VariantId != target.VariantId)
                throw new InvalidOperationException("Select the image from the interrupted export and try again.");
            var input = batch?.Input ?? (OpticalInput)inputSpace.SelectedItem;
            var encoding = batch?.Encoding ?? ((OpticalOption)outputSpace.SelectedItem).Id;
            var args = batch?.Arguments ?? (strength > 0 ? RendererOptions(look!, input.Id, encoding, export: true) : Array.Empty<string>());
            // Reserve the export before yielding so the Viewer cannot start another
            // development while Capture One settles the source for the Save request.
            var work = exportWork = new ExportOperation(target.VariantId);
            exportActivity.Begin(work);
            closed = (_, _) => work.Cancel();
            form.FormClosed += closed;
            return await RunExportAsync();

            async Task<bool> RunExportAsync()
            {
                // Capture the native recipe and source at confirmation, before
                // yielding to the Viewer drain or the filesystem.
                var acceptedSnapshot = useRecipeDestination
                    ? HostImageSource.CaptureForExport(window, target, input.Space, captureRecipe: true) : null;
                var drainStarted = Stopwatch.GetTimestamp();
                await DrainPreviewWorkersAsync(work.Token);
                await HostImageSource.WaitForNativeDevelopmentAsync(work.Token);
                work.Token.ThrowIfCancellationRequested();
                if (batch is null && (disposed || owner is { IsVisible: false }))
                    return false;
                var viewerDrainMs = Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds;
                var original = batch?.Original ?? await Task.Run(() => new OutputPathGuard.OriginalFile(target.SourcePath), work.Token);
                work.Token.ThrowIfCancellationRequested();
                if (batch is null && (disposed || owner is { IsVisible: false }))
                    return false;
                var snapshot = batch?.Snapshot ?? acceptedSnapshot ?? HostImageSource.CaptureForExport(window, target,
                    input.Space, captureRecipe: useRecipeSize);
                var recipe = snapshot.Recipe;
                var deliveryProfile = batch?.DeliveryProfile ?? ExportColorProfile.ForRecipe(recipe?.ColorProfileName, recipe?.ColorProfilePath);
                await using var heldSource = interrupted is null ? null : await Task.Run(original.OpenRead, work.Token);
                ExportRetryDescriptor? retry = null;
                string? verifiedNativeModel = null;
                if (interrupted is not null)
                {
                    exportActivity.Report(work, "Checking the interrupted export…");
                    verifiedNativeModel = snapshot.ReadRetryModel();
                    retry = await CaptureRetryDescriptorAsync(snapshot, verifiedNativeModel, original, args, strength, encoding,
                        deliveryProfile, useRecipeSize, work.Token, heldSource);
                    var expected = interrupted.Retry!;
                    if (retry is null)
                        throw new InvalidOperationException("Spektrafilm could not verify the interrupted export. Start a new export.");
                    if (retry.Original != expected.Original)
                        throw new InvalidOperationException("The original image contents changed. Restore the original or start a new export.");
                    if (retry != expected)
                    {
                        StartupHook.Log("export.retry.settings-changed.v1", new
                        {
                            NativeModelChanged = retry.NativeModel != expected.NativeModel,
                            DeliveryChanged = retry.Delivery != expected.Delivery || retry.FollowRecipe != expected.FollowRecipe,
                        });
                        throw new InvalidOperationException("Capture One settings, the Spektrafilm look or the Export Recipe changed. Restore them or start a new export.");
                    }
                }
                work.Token.ThrowIfCancellationRequested();
                var delivery = new SpektraExport.RecipeDelivery(recipe?.FormatId, recipe?.TiffBits ?? 0, recipe?.CropMode,
                    recipe?.Omissions ?? SpektraExport.RecipeOmission.None, HasPsdCropPath: recipe?.CropPath is not null,
                    HasPsdGuides: recipe?.Guides is { Length: > 0 });
                var extension = interrupted is null ? delivery.Extension : Path.GetExtension(interrupted.Destination);
                var suggestedFormat = SpektraExport.FormatFor(extension);
                var dpi = recipe is null ? ExportResolution.DefaultDpi : ExportResolution.DotsPerInch(recipe.Resolution, recipe.ResolutionUnit);
                var suggestedName = SpektraExport.SuggestedOutputName(recipe?.SuggestedFilename, target.SourcePath, extension,
                    recipe?.SuggestedDirectory, recipe?.AddSuffixToExistingFileName == true);
                var skipPath = batch is null && interrupted is null && delivery.CanSkipRecipeOutput && recipe is { SkipExistingOutput: true,
                    SuggestedFilename: { } stem, ExactDirectory: { } exactDirectory }
                    ? Path.Combine(exactDirectory, stem + extension) : null;

                if (skipPath is not null && ExistingRecipeOutput(skipPath, original) is { } existing)
                    return Skip(existing);

                var approved = batch?.Destination;
                var offerLargeDocument = useRecipeDestination && recipe is { FormatId: "Psd" }
                    && !PsdExport.FitsRecipe(recipe.Width, recipe.Height,
                        OverlayPixels(recipe.Watermark) + OverlayPixels(recipe.TransparencyOverlay) + OverlayPixels(recipe.Annotations),
                        Encoding.UTF8.GetByteCount(recipe.Metadata.CreateXmp(recipe.Width, recipe.Height)));
                if (useRecipeDestination && delivery.CanWriteRecipeDestination && !offerLargeDocument
                    && recipe is { SuggestedFilename: not null, ExactDirectory: { } destinationDirectory })
                {
                    var proposed = original.Approve(Path.Combine(destinationDirectory, suggestedName));
                    if (!proposed.AlreadyExists)
                        approved = proposed;
                    else if (StringComparer.OrdinalIgnoreCase.Equals(skipPath, proposed.Path))
                        return Skip(proposed);
                }
                if (approved is null)
                {
                    var picker = new SaveFileDialog
                    {
                        Title = (offerLargeDocument ? "Choose PSB for a large Photoshop document · " : "")
                            + delivery.SaveTitle(recipe?.Width ?? 0, recipe?.Height ?? 0, deliveryProfile.Label),
                        Filter = interrupted is null ? SpektraExport.FileFilter : SpektraExport.FilterFor(suggestedFormat),
                        FilterIndex = interrupted is null ? (int)suggestedFormat + 1 : 1,
                        FileName = interrupted is null ? suggestedName : Path.GetFileName(interrupted.Destination),
                        DefaultExt = extension,
                        AddExtension = true,
                        OverwritePrompt = true,
                        InitialDirectory = interrupted is null ? recipe?.SuggestedDirectory ?? ""
                            : Path.GetDirectoryName(interrupted.Destination) ?? "",
                    };
                    var destination = chooseDestination(picker, owner);
                    work.Token.ThrowIfCancellationRequested();
                    if (destination is null)
                    {
                        if (interrupted is not null)
                            exportActivity.Report(work, DescribePreviousExport(interrupted));
                        return false;
                    }
                    approved = original.Approve(destination);
                }
                work.Token.ThrowIfCancellationRequested();
                var outputContainer = SpektraExport.FormatFor(approved.Path);
                if (interrupted is not null && suggestedFormat != outputContainer)
                    throw new InvalidOperationException("Use the original file format when retrying an interrupted export.");
                if (approved.AlreadyExists && StringComparer.OrdinalIgnoreCase.Equals(skipPath, approved.Path))
                    return Skip(approved);
                if (batch is null)
                {
                    if (verifiedNativeModel is not null)
                        snapshot.VerifyRetryModel(verifiedNativeModel);
                    deliveryProfile.Prepare();
                }
                var outputFormat = SpektraExport.PixelFormatFor(outputContainer);
                var deliveryNote = delivery.SavedNote(outputContainer);
                receipt = batch is { Plan: { } plan, Entry: { } entry }
                    ? exportJobs.BeginBatchItem(plan, entry)
                    : exportJobs.AcceptRetry(work.VariantId, approved.Path, interrupted?.JobId);
                work.IsAccepted = true;
                interruptedExport = null;
                exportActivity.Report(work, "Preparing the accepted export…");
                if (owner is { IsVisible: true } && owner.Title.EndsWith(ExportFailureTitleSuffix, StringComparison.Ordinal))
                    owner.Title = owner.Title[..^ExportFailureTitleSuffix.Length];
                var jobId = receipt.JobId;
                var started = Stopwatch.GetTimestamp();
                if (batch is not null)
                    deliveryProfile.Prepare();
                await using var exportSource = heldSource is null ? await Task.Run(original.OpenRead, work.Token) : null;
                if (batch is null)
                {
                    retry ??= await CaptureRetryDescriptorAsync(snapshot, snapshot.ReadRetryModel(), original, args, strength, encoding,
                        deliveryProfile, useRecipeSize, work.Token, exportSource);
                    if (retry is not null)
                        receipt = exportJobs.RecordRetry(receipt, retry);
                }
                Advance(ExportStage.DevelopingSource, "Developing export pixels in Capture One…");
                var result = await Task.Run(async () =>
                {
                    var developStarted = Stopwatch.GetTimestamp();
                    var rendered = verifiedNativeModel is null
                        ? snapshot.RenderExport(null, work.Token, useRecipeSize)
                        : snapshot.RenderVerifiedExport(work.Token, useRecipeSize, verifiedNativeModel);
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
                    if (strength > 0)
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
                                        if (receipt?.Stage == ExportStage.RenderingFilm)
                                            exportActivity.Report(work, text);
                                    });
                            },
                        };
                        var renderStarted = Stopwatch.GetTimestamp();
                        pixels = await exporter.RenderAsync(source.Width, source.Height, source.FloatBytes, args, work.Token).ConfigureAwait(false);
                        renderMs = Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
                    }
                    work.Token.ThrowIfCancellationRequested();
                    Advance(ExportStage.Encoding, "Writing the 16-bit export…", rendered.Crop.Width, rendered.Crop.Height);
                    var watermark = recipe?.Watermark;
                    var transparencyOverlay = recipe?.TransparencyOverlay;
                    var annotations = recipe?.Annotations;
                    var hasOverlays = watermark is not null || transparencyOverlay is not null || annotations is not null;
                    var convertsProfile = deliveryProfile.RequiresConversion;
                    var layeredFormat = SpektraExport.SupportsLayers(outputContainer);
                    var editableOverlays = hasOverlays && layeredFormat;
                    var deferOverlays = convertsProfile || editableOverlays;
                    var allocation = deferOverlays && hasOverlays ? RenderAllocation.ProfileImageEncodingWithOverlay
                        : !convertsProfile ? RenderAllocation.ImageEncoding
                        : RenderAllocation.ProfileImageEncoding;
                    using var memory = RenderMemoryBudget.SystemMemory.Reserve(allocation,
                        (long)rendered.Crop.Width * rendered.Crop.Height, "Free memory or choose a smaller Export Recipe.");
                    using var watermarkMemory = watermark is null ? null : RenderMemoryBudget.SystemMemory.Reserve(
                        RenderAllocation.ImageEncoding, (long)watermark.Width * watermark.Height, "Choose a smaller recipe watermark.");
                    using var transparencyMemory = transparencyOverlay is null ? null : RenderMemoryBudget.SystemMemory.Reserve(
                        RenderAllocation.ImageEncoding, (long)transparencyOverlay.Width * transparencyOverlay.Height,
                        "Choose a smaller export size or transparency overlay.");
                    using var annotationsMemory = annotations is null ? null : RenderMemoryBudget.SystemMemory.Reserve(
                        RenderAllocation.ImageEncoding, (long)annotations.Width * annotations.Height, "Choose a smaller export size for the recipe annotations.");
                    var overlays = new List<ExportOverlay.Prepared>(3);
                    if (watermark is not null)
                        overlays.Add(new("Watermark", watermark, watermark.Prepare(deliveryProfile, work.Token)));
                    if (transparencyOverlay is not null)
                        overlays.Add(new("Overlay", transparencyOverlay, transparencyOverlay.Prepare(deliveryProfile, work.Token)));
                    if (annotations is not null)
                        overlays.Add(new("Annotations", annotations, annotations.Prepare(deliveryProfile, work.Token)));
                    var deliveryFormat = convertsProfile ? PixelFormats.Rgba64 : outputFormat;
                    var encodingStarted = Stopwatch.GetTimestamp();
                    var image = DecodeExport(source, pixels, strength, input.Space, encoding, deliveryFormat, rendered.Crop, work.Token, dpi,
                        deferOverlays ? null : overlays);
                    var blendAndCropMs = Stopwatch.GetElapsedTime(encodingStarted).TotalMilliseconds;
                    var profileSetupStarted = Stopwatch.GetTimestamp();
                    var output = deliveryProfile.ConvertSrgb(image, outputFormat);
                    List<PsdLayer>? layers = editableOverlays ? [new PsdLayer("Background", 0, 0, output)] : null;
                    if (deferOverlays && hasOverlays)
                        output = ExportOverlay.Composite(output, rendered.Crop, overlays, work.Token, layers);
                    var profileSetupMs = Stopwatch.GetElapsedTime(profileSetupStarted).TotalMilliseconds;
                    var guides = layeredFormat && recipe?.Guides is { Length: > 0 } availableGuides
                        ? PsdGuide.InOutput(availableGuides, source.Width, source.Height, rendered.Crop) : null;
                    var writeStarted = Stopwatch.GetTimestamp();
                    SpektraExport.SaveApproved(approved, output, work.Token, recipe?.Metadata, deliveryProfile,
                        recipe?.TiffCompression ?? TiffCompressOption.Zip, work.ReportEncoderWrite,
                        publication => receipt = exportJobs.RecordPublication(receipt!, publication with { DeliveryNote = deliveryNote }),
                        cropPath: recipe?.CropPath, guides: guides, layers: layers is { Count: > 1 } ? layers : null);
                    StartupHook.Log("export.performance.v2", new
                    {
                        JobId = jobId,
                        SourceFormat = target.SourceFormat,
                        SourceBitDepth = source.BitDepth,
                        InputEncoding = input.Id,
                        FilmEncoding = encoding,
                        OutputFormat = outputContainer.ToString().ToUpperInvariant(),
                        TiffCompression = outputContainer == SpektraExport.Format.Tiff ? (recipe?.TiffCompression ?? TiffCompressOption.Zip).ToString() : null,
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
                var counterStatus = batch is null && form is { IsDisposed: false, Disposing: false } && recipe?.Naming?.AdvanceCounter is { } advance
                    && StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileNameWithoutExtension(suggestedName),
                        Path.GetFileNameWithoutExtension(approved.Path))
                    ? AdvanceNativeExportCounter(advance, jobId) : "";
                var noteText = deliveryNote is null ? "" : Environment.NewLine + deliveryNote;
                Advance(ExportStage.Completed, $"Saved 16-bit Spektrafilm export · {result.PixelWidth} × {result.PixelHeight}{counterStatus}{noteText}",
                    result.PixelWidth, result.PixelHeight, deliveryNote: deliveryNote);
                StartupHook.Log($"Spektrafilm 16-bit export saved at {result.PixelWidth}x{result.PixelHeight}: {approved.Path}");
                return true;

                static long OverlayPixels(ExportOverlay? overlay) => overlay is null ? 0 : (long)overlay.Width * overlay.Height;

                bool Skip(OutputPathGuard.ApprovedDestination existingOutput)
                {
                    receipt = exportJobs.Accept(work.VariantId, existingOutput.Path);
                    interruptedExport = null;
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
        }
        catch (OperationCanceledException error) when (exportWork?.Token.IsCancellationRequested == true)
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
            if (disposed || batch is not null) return false;
            if (interrupted is not null && receipt is null) return false;
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
        finally
        {
            if (closed is not null)
                form.FormClosed -= closed;
            if (exportWork is { } work)
            {
                exportWork = null;
                try { exportActivity.End(work); }
                finally { work.Dispose(); }
            }
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
            var current = exportWork;
            if (exportStatus.Dispatcher.CheckAccess())
                exportActivity.Report(current, message);
            else if (!exportStatus.Dispatcher.HasShutdownStarted)
                _ = exportStatus.Dispatcher.InvokeAsync(() =>
                {
                    if (receipt?.Stage == stage) exportActivity.Report(current, message);
                });
        }
    }

    private static async Task<ExportRetryDescriptor?> CaptureRetryDescriptorAsync(HostImageSource.Snapshot snapshot, string? model, OutputPathGuard.OriginalFile original,
        string[] filmArguments, double strength, string encoding, ExportColorProfile profile, bool followRecipe, CancellationToken token,
        FileStream? heldSource = null)
    {
        try
        {
            if (model is null)
                return null;
            var recipe = snapshot.Recipe;
            var source = snapshot.SourceIdentity;
            var settings = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Panel = typeof(SpatialPreview).Module.ModuleVersionId,
                source.HostAssemblySha256, source.ProfileSha256, source.InputEncoding, source.SourceFormat,
                Film = filmArguments, strength, encoding, OutputICC = profile.Sha256, profile.SrgbSha256,
                Recipe = recipe is null ? null : new
                {
                    recipe.FormatId, recipe.CropMode, recipe.CropPath, recipe.Guides, recipe.TiffBits, recipe.Width, recipe.Height, recipe.Scale, recipe.IgnoreCrop,
                    recipe.Resolution, recipe.ResolutionUnit, recipe.TiffCompression, recipe.Sharpening, recipe.Omissions,
                    Watermark = recipe.Watermark?.Identity,
                    TransparencyOverlay = recipe.TransparencyOverlay?.Identity,
                    Annotations = recipe.Annotations?.Identity,
                    Metadata = recipe.Metadata.CreateXmp(1, 1),
                },
            });
            return await Task.Run(async () =>
            {
                using var delivery = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                delivery.AppendData(settings);
                if (strength > 0)
                {
                    var root = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
                    if (root is null) return null;
                    foreach (var relative in RuntimeAssets.RequiredFiles)
                        delivery.AppendData(await HashFile(Path.Combine(root, relative), token).ConfigureAwait(false));
                }
                await using var openedSource = heldSource is null ? original.OpenRead() : null;
                var sourceHash = await SHA256.HashDataAsync(heldSource ?? openedSource!, token).ConfigureAwait(false);
                return new ExportRetryDescriptor(followRecipe, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model))),
                    Convert.ToHexString(sourceHash),
                    Convert.ToHexString(delivery.GetHashAndReset()));
            }, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupHook.Log("Could not record Spektrafilm export retry identity: " + error.GetType().Name);
            return null;
        }

        static async Task<byte[]> HashFile(string path, CancellationToken stop)
        {
            await using var file = OpenForFingerprint(path);
            return await SHA256.HashDataAsync(file, stop).ConfigureAwait(false);
        }
    }

    private static FileStream OpenForFingerprint(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static OutputPathGuard.ApprovedDestination? ExistingRecipeOutput(string path, OutputPathGuard.OriginalFile original)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var approved = original.Approve(path);
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
        ResetViewportQuality();
        failedRevision = null;
        retryViewer.Visibility = Visibility.Collapsed;
        failedSpace = null;
        failedSize = null;
        ReleaseSourceBuffers(keepDraftRenderer);
    }

    private void ResetViewportQuality()
    {
        viewportQuality.Reset();
        viewportSeedPending = true;
    }

    private void ReleaseSourceBuffers(bool keepDraftRenderer)
    {
        ResetDraft(keepRenderer: keepDraftRenderer);
        source = null;
        sourceCropIdentity = null;
        sourceSize = null;
        ClearDisplayCache(ref sourceDisplay, ref sourceDisplayLease);
        ClearRenderedPixels();
        renderedArguments = null;
        ClearDisplayCache(ref renderedDisplay, ref renderedDisplayLease);
    }

    private void ClearRenderedPixels()
    {
        renderedPixels = null;
        renderedPixelsLease?.Dispose();
        renderedPixelsLease = null;
    }

    private static void ClearDisplayCache(ref BitmapSource? cache, ref IDisposable? lease)
    {
        cache = null;
        lease?.Dispose();
        lease = null;
    }

    private static void RememberDisplay(BitmapSource bitmap, ref BitmapSource? cache, ref IDisposable? lease)
    {
        var stride = ((long)bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        lease = ViewerCacheBudget.Shared.TryRetain(stride * bitmap.PixelHeight);
        if (lease is not null)
            cache = bitmap;
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
        if (automatic && pending is null && (viewportSeedPending || pendingDraft is null && !dragging))
        {
            var projection = viewer.ProjectedLongEdge(selectedVariant);
            var budget = RenderMemoryBudget.SystemMemory.AvailableForNewAllocation;
            var now = Environment.TickCount64;
            var ratio = source is { } frame ? Math.Min(frame.Width, frame.Height) / (double)Math.Max(frame.Width, frame.Height) : 1;
            if (viewportSeedPending)
            {
                // Initial sizing belongs to settled development even while an old draft is retiring.
                projection ??= viewer.MeasureBeforePaint(selectedVariant);
                viewportSeedPending = projection is null;
                if (viewportQuality.Update(projection, ratio, budget, now, firstFrame: true))
                    RequestRefresh();
            }
            else if (viewportQuality.Update(projection, ratio, budget, now))
                RequestRefresh();
        }
        var retryKey = (selectedVariant, inputRevision, revision);
        if (rendererRetry.IsWaiting(retryKey, Environment.TickCount64)) return;
        var size = ViewerRenderSize;
        var nativeSize = size == "native";
        var longEdge = nativeSize ? 0 : int.Parse(size, CultureInfo.InvariantCulture);
        var dragEdge = DragLongEdge;
        var needsSource = !HasCurrentSource(selectedVariant, revision, space, size);
        var cropReused = needsSource && pending is null && (frameCurrent || queued)
            && TryReuseAfterCrop(selectedVariant, revision, space, size);
        if (cropReused) needsSource = false;
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
                ShowFrame(selectedVariant, viewerFrame, refreshPresentation: cropReused);
            if (!needsSource && frameCurrent && !dragging && dragEdge > 0
                && !draftWarmupAttempted && pendingDraft is null
                && source is { } fullSource && Math.Max(fullSource.Width, fullSource.Height) > dragEdge
                && getSettings() is { } warmLook)
                await RefreshDraftAsync(selectedVariant, revision, fullSource, selectedSpace, encoding, warmLook, size, dragEdge, warmup: true);
            return;
        }
        queued = false;
        InvalidateSettledFrame();
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
                // The last visible bitmap owns its display pixels. The obsolete
                // float buffers can leave before Capture One develops a replacement.
                ReleaseSourceBuffers(keepDraftRenderer: true);
                var developStarted = Stopwatch.GetTimestamp();
                var target = HostProfile.CaptureTarget(window);
                var snapshot = HostImageSource.Capture(window, target, space);
                var cropIdentity = snapshot.ReadPreviewCropIdentity();
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
                sourceCropIdentity = cropIdentity;
                sourceSize = size;
                nativeDevelopmentMs = Stopwatch.GetElapsedTime(developStarted).TotalMilliseconds;
                StartupHook.Log($"Capture One developed {target.SourceFormat} input at {developed.Width} × {developed.Height}, {developed.BitDepth}-bit {space} in {nativeDevelopmentMs:F0} ms.");
            }
            if (settingsVersion != inputRevision)
                return;

            var input = source!;
            BitmapSource output;
            if (strength <= 0)
            {
                var display = sourceDisplay;
                if (display is null)
                {
                    var bitmapStarted = Stopwatch.GetTimestamp();
                    display = await Task.Run(() => ViewerDisplayBitmap.Convert(input, null, 0, space, encoding.Id, job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    RememberDisplay(display, ref sourceDisplay, ref sourceDisplayLease);
                    bitmapMs = Stopwatch.GetElapsedTime(bitmapStarted).TotalMilliseconds;
                }
                output = display;
            }
            else
            {
                if (renderedArguments is null || !args.SequenceEqual(renderedArguments))
                {
                    renderedArguments = args;
                    ClearRenderedPixels();
                    ClearDisplayCache(ref renderedDisplay, ref renderedDisplayLease);
                }

                var display = renderedDisplay;
                if (display is null || renderedStrength != strength)
                {
                    ClearDisplayCache(ref renderedDisplay, ref renderedDisplayLease);
                    display = null;
                    var completedPixels = renderedPixels;
                    var displayPixels = strength == 1 && encoding.Id == "srgb" && completedPixels is null;
                    byte[]? packed = null;
                    if (completedPixels is null)
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
                        if (settingsVersion != inputRevision)
                            return;
                        if (displayPixels)
                            packed = reply;
                        else
                        {
                            completedPixels = reply;
                            renderedPixelsLease = ViewerCacheBudget.Shared.TryRetain(reply.LongLength);
                            if (renderedPixelsLease is not null)
                                renderedPixels = reply;
                        }
                    }
                    if (settingsVersion != inputRevision)
                        return;

                    var bitmapStarted = Stopwatch.GetTimestamp();
                    display = await Task.Run(() => packed is not null
                        ? CreateDisplayBitmap(input, packed)
                        : ViewerDisplayBitmap.Convert(input, completedPixels, strength, space, encoding.Id, job.Token), job.Token);
                    job.Token.ThrowIfCancellationRequested();
                    RememberDisplay(display, ref renderedDisplay, ref renderedDisplayLease);
                    renderedStrength = strength;
                    bitmapMs = Stopwatch.GetElapsedTime(bitmapStarted).TotalMilliseconds;
                }
                output = display;
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
            ShowFrame(selectedVariant, output, refreshPresentation: cropReused);
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

    private bool HasCurrentSource(Guid variant, NativeImageRevision revision, PreviewInputSpace space, string size) =>
        source is { Description: { } description } current && description.Source.VariantId == variant
        && description.Source.AdjustmentKey == revision && description.Source.InputEncoding == space && sourceSize == size
        && string.Equals(description.Source.ProfileSha256, checkedProfileHash, StringComparison.Ordinal)
        && description.Describes(current.Width, current.Height, current.BitDepth, current.FloatBytes.Length);

    private bool TryReuseAfterCrop(Guid variant, NativeImageRevision revision, PreviewInputSpace space, string size)
    {
        if (source is not { Description: { } description } current || sourceCropIdentity is not { } before)
            return false;
        var previous = description.Source.AdjustmentKey;
        if (revision.Geometry <= previous.Geometry || revision.HighRes != previous.HighRes || revision.LowRes != previous.LowRes
            || !HasCurrentSource(variant, previous, space, size)
            || HostImageSource.ReadPreviewCropIdentity(window, variant, revision) is not { } after || !after.IsCropChangeFrom(before))
            return false;

        // Native development covers the full canvas. The existing bitmap is
        // presented using the native Viewer's new transform and crop clip.
        source = current with { Description = description with { Source = description.Source with { AdjustmentKey = revision } } };
        sourceCropIdentity = after;
        StartupHook.Log("viewer.crop.reused.v1", new { Previous = previous.Geometry, Current = revision.Geometry });
        return true;
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
        ClearDraftResult();
        draftWarmupAttempted = false;
        lastDraftRevision = -1;
    }

    private void ClearDraftResult()
    {
        draftResult = null;
        draftResultLease?.Dispose();
        draftResultLease = null;
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
                ClearDraftResult();
                var worker = draftRenderer ??= new OpticalRenderer(nativePreview: true);
                var filmStarted = Stopwatch.GetTimestamp();
                var rendered = await (warmup
                    ? worker.WarmPreviewAsync(frame.Width, frame.Height, frame.FloatBytes, args, job.Token, pack)
                    : worker.RenderAsync(frame.Width, frame.Height, frame.FloatBytes, args, job.Token, pack));
                filmMs = Stopwatch.GetElapsedTime(filmStarted).TotalMilliseconds;
                receipt = worker.LastRender;
                job.Token.ThrowIfCancellationRequested();
                cached = (args, rendered, pack);
                draftResultLease = ViewerCacheBudget.Shared.TryRetain(rendered.LongLength);
                if (draftResultLease is not null)
                    draftResult = cached;
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

    private void ShowFrame(Guid variant, BitmapSource frame, bool refreshPresentation = false)
    {
        nextViewerCheck = Environment.TickCount64 + 350;
        viewer.Show(variant, frame, refreshPresentation);
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

    internal string[] RendererOptions(LookSettings look, string inputColor, string outputColor, bool export = false, bool draft = false) =>
        BuildRendererOptions(look, inputColor, outputColor, export, draft, frozen: null);

    private string[] BuildRendererOptions(LookSettings look, string inputColor, string outputColor, bool export, bool draft, Settings? frozen)
    {
        if (look.Mode is "process_negative" or "process_negatives")
            throw new InvalidOperationException("Process Negative requires an encoded negative scan as input.");
        var args = new List<string>();
        string Selected(string key) => frozen is null ? SelectedOpticalChoice(key)
            : frozen.Selections.GetValueOrDefault(key, ((OpticalChoice)OpticalControls.Parameters[key].Parameter).Default);
        double Number(string key) => frozen is null ? values[key].Value
            : frozen.Numbers.GetValueOrDefault(key, ((OpticalSlider)OpticalControls.Parameters[key].Parameter).Initial);
        bool IsOn(string key) => frozen is null ? OpticalToggleEnabled(key)
            : frozen.Switches.GetValueOrDefault(key, ((OpticalToggle)OpticalControls.Parameters[key].Parameter).Initial);
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
        FilmControls.AppendRendererOptions(look, !scanning, args);

        bool Send(string key) => OpticalControls.Parameters[key].ShouldSend(!scanning, IsOn, Selected);
        foreach (var key in choices.Keys)
            if (Send(key))
                Set(key, Selected(key));
        var fastGrain = !export && (draft || fastSynthesis.IsChecked == true) && Selected("grain-model") == "synthesis";
        var grainQuality = Math.Min(Number("grain-synthesis-quality"), 2);
        foreach (var key in values.Keys)
        {
            if (!Send(key))
                continue;
            var value = Number(key);
            if (fastGrain && key == "grain-synthesis-samples")
                value = Math.Min(value, Math.Floor(2 / grainQuality));
            if (fastGrain && key == "grain-synthesis-quality")
                value = grainQuality;
            Numeric(key, value);
        }
        foreach (var key in toggles.Keys)
            if (Send(key))
                Set(key, IsOn(key) ? "true" : "false");
        return args.ToArray();
    }

    private static BitmapSource CreateDisplayBitmap(HostImageSource.Frame frame, byte[] pixels)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, pixels, frame.Width * 4);
        image.Freeze();
        return image;
    }

    private static BitmapSource DecodeExport(HostImageSource.Frame frame, byte[]? rendered, double strength,
        PreviewInputSpace space, string outputEncoding, PixelFormat format, System.Drawing.Rectangle crop, CancellationToken token, double dpi,
        IReadOnlyList<ExportOverlay.Prepared>? overlays)
    {
        var transform = new SrgbDelivery(space, outputEncoding, strength);
        var hasAlpha = format == PixelFormats.Rgba64;
        var channels = hasAlpha ? 4 : 3;
        var stride = checked(crop.Width * channels * sizeof(ushort));
        var pixels = ExportPixels.Encode(frame.Width, crop, frame.FloatBytes, rendered, transform, hasAlpha, token);
        if (overlays is not null)
            foreach (var overlay in overlays)
                overlay.Source.Composite(pixels, channels, crop, overlay.Pixels, token);
        var image = BitmapSource.Create(crop.Width, crop.Height, dpi, dpi, format, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private sealed record Choice(string Id, string Label);
    private sealed record DragSize(int LongEdge, string Label);
    internal sealed record Settings(Dictionary<string, double> Numbers, Dictionary<string, string> Selections, Dictionary<string, bool> Switches);
}
