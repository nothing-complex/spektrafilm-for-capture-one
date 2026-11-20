using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using P1.C1.ModelEntities.UndoInterfaces;
using P1.C1.Components.Docking.WPF;
using P1.C1.Components.Docking.WPF.Floating;
using P1.C1.Components.InspectorTools;
using P1.C1.Controls.Inspector.Dummy;
using P1.C1.Core.Interfaces;
using P1.C1.Core.Messaging;
using P1.C1.Core.Messaging.ModelMessages;
using P1.C1.Core.ModelEntities;
using P1.C1.Framework.Messaging;
using P1.C1.ViewModels.Menu;
using P1.C1.ViewModels.Notification;
using P1.C1.ViewModels.Tools.InspectorTool;
using P1.C1.WPF.Styles;

internal static class SpektraFilmTool
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

    public static HostedTool Create(System.Windows.Forms.Form window, IMessageBusProvider bus, ICaptureOneDocument document)
    {
        // The base class loads BAML from CaptureOne.dll, so use the host's empty tool.
        var tool = new DummyInspectorTool(bus);
        HostedTool? hosted = null;
        try
        {
            var toolType = typeof(InspectorToolWpf);
            var registration = (ILifeCycleRegistrationService?)toolType.GetProperty("LifeCycleEventRegistration", InstanceNonPublic)?.GetValue(tool)
                ?? throw new MissingMemberException(toolType.FullName, "LifeCycleEventRegistration");
            var initialize = toolType.GetMethod("Initialize", InstanceNonPublic, null,
                new[] { typeof(UserControl), typeof(IInspectorToolContentViewModel), typeof(InspectorToolHeaderViewModel) }, null)
                ?? throw new MissingMethodException(toolType.FullName, "Initialize");
            hosted = new HostedTool(window, tool, document);
            var header = hosted.CreateDockedHeader(registration);
            tool.SetProperties(new Dictionary<string, object>());
            initialize.Invoke(tool, new object[] { hosted.DockedContent, EmptyInspectorToolContentViewModel.Instance, header });
            tool.DockingLogic.DockableArea = DockableArea.ScrollableArea;
            // Move the content between windows without registering the dummy tool as a native workspace tool.
            tool.DockingLogic.CanDragDrop = false;
            return hosted;
        }
        catch
        {
            try { hosted?.Dispose(); }
            finally { tool.Dispose(); }
            throw;
        }
    }

    internal sealed class HostedTool : IDisposable
    {
        private static readonly Uri CaptureOneStyle = new("pack://application:,,,/CaptureOne;component/WPF/Styles/DefaultStyle.xaml");
        private readonly System.Windows.Forms.Form mainWindow;
        private readonly LookPanel panel;
        private DockableWindow? floating;
        private ScrollViewer? floatingScroll;
        private Rect? floatingBounds;
        private bool disposed;

        internal HostedTool(System.Windows.Forms.Form mainWindow, InspectorToolWpf tool, ICaptureOneDocument document)
        {
            this.mainWindow = mainWindow;
            Tool = tool;
            panel = new LookPanel(mainWindow, document);
            Holder = new ContentControl { Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            DockedContent = new UserControl { Content = Holder };
            if (PanelPreferences.Current.Floating is { IsValid: true } saved)
                floatingBounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
        }

        internal InspectorToolWpf Tool { get; }
        internal UserControl DockedContent { get; }
        private ContentControl Holder { get; }
        internal bool IsSpektraExportActive => !disposed && panel.IsSpektraExportActive;
        internal void LoadControls() => panel.LoadStocks();
        internal bool LookRecoveryPending => panel.HasPendingLookWrites || panel.AwaitingLookRead;
        internal void CheckSelectionAndSave()
        {
            panel.SyncSelection();
            panel.RetryPendingLookWrites();
            panel.RefreshNativeGrain(force: false);
        }
        internal void ReportLaunchHealth() => LaunchHealth.Report(
            panel.ControlsLoadPhase, panel.ExportBridgeStatus, panel.ViewerStatus);
        internal Task<bool> ExportAsync(Window? owner) => panel.ExportAsync(useRecipeSize: true, owner);
        internal bool ProtectPendingLooks() => panel.ProtectPendingLooks();
        internal void SetExportBridgeStatus(ExportBridgeState state, string? detail = null) => panel.SetExportBridgeStatus(state, detail);
        internal InspectorToolHeaderViewModel CreateDockedHeader(ILifeCycleRegistrationService registration) =>
            new InspectorToolHeaderViewModelBuilder(StartupHook.ProductName, () => Tool.Document, registration)
                .DoShowActionMenu(new PanelActionMenu(() => floating is null ? "Float Tool" : "Show Floating Tool", ShowFloating, panel))
                .Build();

        private void ShowFloating()
        {
            if (disposed)
                return;
            if (floating is not null)
            {
                if (floating.WindowState == WindowState.Minimized)
                    floating.WindowState = WindowState.Normal;
                FloatingWindowBounds.KeepVisible(new WindowInteropHelper(floating).Handle);
                floating.Activate();
                return;
            }

            DockableWindow? window = null;
            try
            {
                var scroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                };
                window = CreateFloatingWindow(scroll, panel);
                if (floatingBounds is { } bounds)
                {
                    var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                    if (!desktop.IntersectsWith(bounds))
                        bounds = new Rect(SystemParameters.WorkArea.Left + 24, SystemParameters.WorkArea.Top + 24,
                            Math.Min(bounds.Width, SystemParameters.WorkArea.Width), Math.Min(bounds.Height, SystemParameters.WorkArea.Height));
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = bounds.Left;
                    window.Top = bounds.Top;
                    window.Width = bounds.Width;
                    window.Height = bounds.Height;
                }
                else if (DockedContent.ActualWidth > 0)
                {
                    window.Width = Math.Max(window.MinWidth, DockedContent.ActualWidth);
                }

                new WindowInteropHelper(window).Owner = mainWindow.Handle;
                ElementHost.EnableModelessKeyboardInterop(window);
                window.Closing += OnFloatingClosing;
                window.Closed += OnFloatingClosed;
                floating = window;
                floatingScroll = scroll;
                Holder.Content = null;
                scroll.Content = panel;
                window.Show();
                FloatingWindowBounds.KeepVisible(new WindowInteropHelper(window).Handle);
                var offset = PanelPreferences.Current.FloatingScrollOffset;
                if (double.IsFinite(offset) && offset > 0)
                    scroll.ScrollToVerticalOffset(offset);
                StartupHook.Log($"{StartupHook.ProductName} panel is floating.");
            }
            catch (Exception error)
            {
                if (window is not null)
                {
                    if (ReferenceEquals(window, floating))
                        RestoreDock(window);
                    if (!window.IsClosed)
                        window.Close();
                }
                StartupHook.Log(error.ToString());
            }
        }

        private static DockableWindow CreateFloatingWindow(ScrollViewer scroll, LookPanel panel)
        {
            var layout = new DockPanel
            {
                Background = DefaultConstants.CaptureOneBrushPanelBackground,
                Margin = new Thickness(FloatingWindowConstants.BorderThicknessUnscaled),
            };
            // Like Capture One's FloatingToolsHostPanel, provide implicit styles to the tool's whole tree.
            layout.Resources.MergedDictionaries.Add(new SharedResourceDictionary { Source = CaptureOneStyle });
            var window = new DockableWindow
            {
                Title = StartupHook.ProductName,
                Content = layout,
                Background = DefaultConstants.CaptureOneBrushDarkSeparator,
                Width = 360,
                Height = 620,
                MinWidth = 290,
                MinHeight = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var header = new InspectorToolHeader
            {
                Background = DefaultConstants.CaptureOneBrushHeaderBackground,
                DataContext = new InspectorToolHeaderViewModel(StartupHook.ProductName,
                    argActionMenuGenerator: new PanelActionMenu(() => "Dock Tool", window.Close, panel)),
            };
            // The Inspector header handles its action-menu input before it reaches this handler.
            header.MouseLeftButtonDown += (_, args) =>
            {
                if (args.LeftButton == MouseButtonState.Pressed && args.ClickCount == 1)
                {
                    args.Handled = true;
                    window.DragMove();
                }
            };
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            layout.Children.Add(scroll);
            return window;
        }

        private void OnFloatingClosing(object? sender, System.ComponentModel.CancelEventArgs args)
        {
            if (!args.Cancel && sender is DockableWindow window && ReferenceEquals(window, floating))
                SaveFloatingPlacement(window);
        }

        private void OnFloatingClosed(object? sender, EventArgs args)
        {
            if (sender is not DockableWindow window || !ReferenceEquals(window, floating))
                return;
            RestoreDock(window);
            if (!disposed)
                StartupHook.Log($"{StartupHook.ProductName} panel is docked.");
        }

        private void SaveFloatingPlacement(DockableWindow window)
        {
            var bounds = window.RestoreBounds;
            if (bounds.IsEmpty) return;
            var placement = new FloatingPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
            if (!placement.IsValid) return;
            floatingBounds = bounds;
            var offset = floatingScroll?.VerticalOffset ?? 0;
            PanelPreferences.Update(preferences => preferences with
            {
                Floating = placement, FloatingScrollOffset = double.IsFinite(offset) && offset >= 0 ? offset : 0,
            });
        }

        private void RestoreDock(DockableWindow window)
        {
            window.Closing -= OnFloatingClosing;
            window.Closed -= OnFloatingClosed;
            if (floatingScroll is not null)
                floatingScroll.Content = null;
            floatingScroll = null;
            floating = null;
            if (!disposed)
                Holder.Content = panel;
        }

        private sealed class PanelActionMenu(Func<string> label, Action action, LookPanel panel) : IActionMenuGenerator
        {
            public bool HasItems => true;

            public IEnumerable<IMenuItemViewModel> Generate()
            {
                yield return new CommandMenuItemViewModel(label(), action);
                if (panel.HasPendingLookWrites)
                    yield return new CommandMenuItemViewModel("Retry look save", panel.RetryPendingLookWrites);
                yield return new CommandMenuItemViewModel("Copy support details", panel.CopySupportDetails);
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            panel.Dispose();
            if (floating is { } window)
            {
                SaveFloatingPlacement(window);
                RestoreDock(window);
                window.Close();
            }
            Holder.Content = null;
        }
    }

    private sealed class LookPanel : UserControl, IDisposable, IHandleMessage<PrimaryVariantChangedMessage>
    {
        private const string DefaultFilm = "kodak_portra_400";
        private const string DefaultPaper = "kodak_portra_endura";
        private const string DefaultSpectralMethod = "hanatos2025";
        private readonly object window;
        private readonly ICaptureOneDocument document;
        private readonly ISelectedCollectionsProvider selections;
        private readonly VariantLookStore looks;
        private readonly ComboBox mode = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly ComboBox film = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly ComboBox paper = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly PanelChoices panelChoices = new(Path.Combine(PanelPreferences.UserDataDirectory(), "Stock favourites and recent presets.json"), StartupHook.Log);
        private readonly ComboBox spectralMethod = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly ComboBox filmPushPullMode = new() { DisplayMemberPath = nameof(Stock.Label) };
        private readonly CheckBox scanInvert = new() { Content = "Invert negative scan", Margin = new Thickness(0, 5, 0, 2) };
        private readonly CheckBox enableSpektraFilm = new()
        {
            Content = "Enable " + StartupHook.BrandName,
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 6),
        };
        private readonly TextBlock status = new() { Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock saveError = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        private readonly TextBlock exportBridgeStatus = new() { TextWrapping = TextWrapping.Wrap, Text = "Checking Capture One Export support…" };
        private readonly StackPanel controls = new() { IsEnabled = false };
        private readonly NativeSlider exposure;
        private readonly NativeSlider printExposure;
        private readonly NativeSlider strength;
        private readonly ComboBox grainType = new()
        {
            ItemsSource = new[] { "Fine Grain", "Silver Rich", "Soft", "Cubic Grain", "Tabular Grain", "Harsh" },
            SelectedIndex = 2,
        };
        private readonly NativeSlider grainImpact = new("Grain impact", 0, 100, 25, 1);
        private readonly NativeSlider grainSize = new("Grain granularity", 0, 100, 50, 1);
        private readonly TextBlock nativeGrainText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
        private readonly Expander nativeGrainSection;
        private readonly Button applyGrain;
        private readonly Button restoreGrain;
        private static readonly GrainControls DefaultGrain = new(2, 25, 50);
        private readonly StackPanel printing = new();
        private readonly Dictionary<string, (NativeSlider Slider, double Default)> development = new();
        private readonly SpatialPreview opticalPreview;
        private readonly Button recoverSavedLook;
        private readonly FrameworkElement fileControls;
        private readonly WrapPanel exportButtons = new() { Margin = new Thickness(0, 5, 0, 0) };
        private readonly CancellationTokenSource lifetime = new();
        private readonly Button cancelLut = new() { Content = "Cancel LUT export", Visibility = Visibility.Collapsed };
        private CancellationTokenSource? lutWork;
        private bool lutExporting;
        private readonly IUndoManager? nativeUndo;
        private readonly Action<Guid, Exception?> historyChanged;
        private VariantLook? displayedLook;
        private (Guid Variant, VariantLook Before)? historyGesture;
        private Guid? displayedVariant;
        private long? retryLookReadAt;
        private VariantLook? previousSavedLook;
        private DispatcherOperation? selectionSync;
        private LookOptions? options;
        private bool controlsLoadFailed;
        private bool settingValues;
        private bool disposed;

        internal LookPanel(object window, ICaptureOneDocument document)
        {
            this.window = window;
            this.document = document;
            nativeUndo = (document as IUndo)?.UndoManager;
            var weakPanel = new WeakReference<LookPanel>(this);
            historyChanged = (variant, error) =>
            {
                if (weakPanel.TryGetTarget(out var panel) && !panel.disposed)
                    panel.OnHistoryChanged(variant, error);
                else if (error is not null)
                    StartupHook.Log("Spektrafilm Undo could not update the saved look: " + error);
            };
            selections = (ISelectedCollectionsProvider)document;
            var catalog = ApprovedCatalog.SelectedDatabase ?? throw new InvalidOperationException("No catalog selected for Spektrafilm.");
            var root = Path.Combine(PanelPreferences.UserDataDirectory(), "Variant looks");
            looks = new VariantLookStore(root, catalog);
            Foreground = Brushes.Gainsboro;
            enableSpektraFilm.IsEnabled = false;
            var layout = new StackPanel { Margin = new Thickness(8, 5, 8, 9) };
            layout.Children.Add(enableSpektraFilm);
            layout.Children.Add(saveError);
            recoverSavedLook = Button("Recover saved look…", TryRecoverSelectedLook);
            recoverSavedLook.Visibility = Visibility.Collapsed;
            layout.Children.Add(recoverSavedLook);
            layout.Children.Add(exportBridgeStatus);
            cancelLut.Click += (_, _) => lutWork?.Cancel();
            layout.Children.Add(cancelLut);
            layout.Children.Add(controls);
            Content = layout;
            AddStock(controls, "Processing mode", mode, () =>
            {
                if (settingValues)
                    return;
                SetModeControls();
                RefreshLook();
            });
            AddStock(controls, "Film stock", film, () =>
            {
                SetScanOptions();
                RefreshLook();
            });
            scanInvert.Checked += (_, _) => RefreshLook();
            scanInvert.Unchecked += (_, _) => RefreshLook();
            controls.Children.Add(scanInvert);
            AddStock(controls, "Print stock", paper);
            exposure = AddSlider(controls, "Film exposure", -8, 8, 0, 0.1, "EV");
            printExposure = AddSlider(controls, "Print exposure", -5, 5, 0, 0.1, "EV");
            printExposure.ToolTip = "More light on the print stock makes the result darker.";
            strength = AddSlider(controls, "Look strength", 0, 100, 100, 1, "%");
            var exports = new ExportJobJournal(Path.Combine(PanelPreferences.UserDataDirectory(), "Export jobs", looks.CatalogIdentity), StartupHook.Log);
            opticalPreview = SpatialPreview.BuildForPanel(window, CurrentSettings, UseSelectedLook, exports);
            layout.Children.Insert(3, opticalPreview.ExportControls);
            opticalPreview.SettingsChanged += SaveLook;
            opticalPreview.SliderDragActivity += UpdateHistoryGesture;
            opticalPreview.TrackSliderDrag(exposure);
            opticalPreview.TrackSliderDrag(printExposure);
            opticalPreview.TrackSliderDrag(strength);
            controls.Children.Add(opticalPreview.LiveControls);

            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(Button("Defaults", () =>
            {
                if (options is not null)
                {
                    UpdateHistoryGesture(false);
                    ApplyControls(null, null, grain: DefaultGrain);
                    PersistLook("Spektrafilm defaults");
                }
                return Task.CompletedTask;
            }));
            controls.Children.Add(buttons);
            controls.Children.Add(status);
            controls.Children.Add(MakeFilmControls());
            controls.Children.Add(MakePrintControls());
            controls.Children.Add(MakeAdvancedControls());
            applyGrain = Button("Apply grain", ApplyNativeGrainAsync);
            restoreGrain = Button("Restore grain", RestoreNativeGrainAsync);
            nativeGrainSection = MakeGrainControls();
            layout.Children.Add(nativeGrainSection);
            layout.Children.Add(opticalPreview.Element);
            fileControls = MakeFileControls();
            layout.Children.Add(fileControls);

            enableSpektraFilm.Checked += (_, _) => OnEnabledChanged();
            enableSpektraFilm.Unchecked += (_, _) => OnEnabledChanged();
            Loaded += OnLoaded;
            try
            {
                document.MessageBus.Subscribe<PrimaryVariantChangedMessage>(this);
                try { selections.CollectionsSelectionChanged += OnCollectionsSelectionChanged; }
                catch
                {
                    document.MessageBus.Unsubscribe<PrimaryVariantChangedMessage>(this);
                    throw;
                }
            }
            catch
            {
                Loaded -= OnLoaded;
                opticalPreview.Dispose();
                lifetime.Dispose();
                throw;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (!disposed) LoadStocks();
        }

        public Guid Id { get; } = Guid.NewGuid();

        public void Handle(PrimaryVariantChangedMessage message)
        {
            if (ReferenceEquals(message.Collection, selections.PrimarySelectedCollection))
                ScheduleSelectionSync();
        }

        private void OnCollectionsSelectionChanged(object? sender, CollectionsSelectionChangedEventArgs args) => ScheduleSelectionSync();

        private void ScheduleSelectionSync()
        {
            // Read the primary variant after the host finishes processing the selection.
            if (disposed || selectionSync is not null || Dispatcher.HasShutdownStarted)
                return;
            selectionSync = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                selectionSync = null;
                if (!disposed) SyncSelection();
            }));
        }

        private bool IsSpektraFilmEnabled => !disposed && retryLookReadAt is null && enableSpektraFilm.IsChecked == true
            && (displayedVariant is not { } variant || !looks.IsBlocked(variant));

        internal ExportBridgeState ExportBridgeStatus { get; private set; } = ExportBridgeState.Checking;

        internal void SetExportBridgeStatus(ExportBridgeState state, string? detail)
        {
            ExportBridgeStatus = state;
            exportBridgeStatus.Text = state switch
            {
                ExportBridgeState.Checking => "Checking Capture One Export support…",
                ExportBridgeState.Unavailable => "Capture One Export is unavailable for film looks. Use Export with Spektrafilm in this panel. " + detail,
                _ => string.Empty,
            };
            exportBridgeStatus.Visibility = exportBridgeStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnEnabledChanged()
        {
            if (settingValues)
                return;
            UpdateEnabledState();
            SaveLook();
        }

        private void UpdateEnabledState()
        {
            if (disposed)
                return;
            var enabled = displayedVariant is not null && IsSpektraFilmEnabled;
            recoverSavedLook.Content = previousSavedLook is null ? "Recover saved look…" : "Restore previous saved look…";
            recoverSavedLook.Visibility = displayedVariant is { } selected && looks.IsBlocked(selected)
                ? Visibility.Visible : Visibility.Collapsed;
            enableSpektraFilm.IsEnabled = displayedVariant is { } variant && StocksReady
                && retryLookReadAt is null && !looks.IsBlocked(variant);
            controls.IsEnabled = enabled && StocksReady;
            opticalPreview.Element.IsEnabled = enabled && StocksReady;
            nativeGrainSection.IsEnabled = enableSpektraFilm.IsEnabled;
            fileControls.IsEnabled = enableSpektraFilm.IsEnabled;
            exportButtons.IsEnabled = enabled && StocksReady;
            opticalPreview.SetEnabled(enabled);
            RefreshNativeGrain(force: false);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            FlushLooks();
            disposed = true;
            selectionSync?.Abort();
            selectionSync = null;
            Loaded -= OnLoaded;
            try
            {
                selections.CollectionsSelectionChanged -= OnCollectionsSelectionChanged;
                document.MessageBus.Unsubscribe<PrimaryVariantChangedMessage>(this);
            }
            finally
            {
                lifetime.Cancel();
                lifetime.Dispose();
                opticalPreview.Dispose();
            }
        }

        private FrameworkElement MakeFilmControls()
        {
            var section = new StackPanel();
            AddDevelopment(section, "film_gamma", "Negative gamma", 0.1, 2, 1, 0.05);
            filmPushPullMode.ItemsSource = new[] { new Stock("standard", "Standard"), new Stock("experimental", "Experimental") };
            filmPushPullMode.SelectedIndex = 0;
            AddStock(section, "Push / pull model", filmPushPullMode);
            AddDevelopment(section, "film_push_pull", "Film push / pull", -2, 2, 0, 0.1, "EV");
            AddDevelopment(section, "negative_bleach_bypass", "Negative bleach bypass", 0, 1, 0, 0.01, "%", 100);
            AddDevelopment(section, "negative_leuco_cyan", "Negative leuco cyan", 0, 2, 1, 0.05);
            AddDevelopment(section, "uv_cutoff", "UV cutoff", 380, 450, 380, 1, "nm");
            AddDevelopment(section, "ir_cutoff", "IR cutoff", 600, 780, 780, 1, "nm");
            return Section("film-development", "Film development", section);
        }

        private FrameworkElement MakePrintControls()
        {
            AddDevelopment(printing, "print_gamma", "Print gamma", 0.1, 2, 1, 0.05);
            AddDevelopment(printing, "print_push_pull", "Print push / pull", -2, 2, 0, 0.1, "EV");
            AddDevelopment(printing, "print_shadows", "Print shadow shape", -1, 1, 0, 0.05);
            AddDevelopment(printing, "print_highlights", "Print highlight shape", -1, 1, 0, 0.05);
            AddDevelopment(printing, "print_exhaustion", "Developer exhaustion", 0, 1, 0, 0.01, "%", 100);
            var exhaustion = development["print_exhaustion"].Slider;
            exhaustion.IsEnabled = false;
            exhaustion.ToolTip = "Developer exhaustion is not supported by the image renderer. Existing preset values are preserved until you explicitly clear them.";
            var exhaustionReset = Button("Clear unsupported developer exhaustion", () =>
            {
                exhaustion.Value = 0;
                return Task.CompletedTask;
            });
            exhaustionReset.Visibility = Visibility.Collapsed;
            exhaustion.ValueChanged += (_, _) => exhaustionReset.Visibility = exhaustion.Value == 0 ? Visibility.Collapsed : Visibility.Visible;
            printing.Children.Add(exhaustionReset);
            AddDevelopment(printing, "print_bleach_bypass", "Print bleach bypass", 0, 1, 0, 0.01, "%", 100);
            AddDevelopment(printing, "cyan_filter", "Cyan filtration", 0, 120, 0, 1, "CC");
            AddDevelopment(printing, "magenta_filter", "Magenta filter shift", -60, 60, 0, 1, "CC");
            AddDevelopment(printing, "yellow_filter", "Yellow filter shift", -60, 60, 0, 1, "CC");
            AddDevelopment(printing, "preflash_exposure", "Preflash exposure", 0, 1, 0, 0.01);
            AddDevelopment(printing, "preflash_magenta", "Preflash magenta", -60, 60, 0, 1, "CC");
            AddDevelopment(printing, "preflash_yellow", "Preflash yellow", -60, 60, 0, 1, "CC");
            return Section("enlarger-and-print", "Enlarger and print", printing);
        }

        private FrameworkElement MakeAdvancedControls()
        {
            var section = new StackPanel();
            AddStock(section, "Spectral reconstruction", spectralMethod);
            AddDevelopment(section, "dir_amount", "DIR coupler amount", 0, 2, 1, 0.05);
            AddDevelopment(section, "dir_same_layer", "Same-layer inhibition", 0, 2, 1, 0.05);
            AddDevelopment(section, "dir_interlayer", "Interlayer inhibition", 0, 2, 1, 0.05);
            return Section("spectral-dir", "Spectral reconstruction and DIR", section);
        }

        private void AddDevelopment(StackPanel section, string key, string label, double min, double max,
            double initial, double step, string unit = "", double displayScale = 1)
        {
            var slider = AddSlider(section, label, min, max, initial, step, unit, displayScale);
            development.Add(key, (slider, initial));
        }

        private static Expander Section(string key, string title, FrameworkElement content) =>
            PanelPreferences.CreateSection(key, title, content, 7);

        private FrameworkElement MakeFileControls()
        {
            var section = new StackPanel();
            var presets = new WrapPanel { Margin = new Thickness(0, 5, 0, 3) };
            presets.Children.Add(Button("Save preset…", SavePresetAsync));
            presets.Children.Add(Button("Load preset…", () => LoadPresetAsync(LookSections.All)));
            var recent = new Button { Content = "Recent presets", Margin = new Thickness(0, 4, 4, 0) };
            recent.Click += async (_, _) =>
            {
                var filenames = await Task.Run(() => panelChoices.RecentPresets.ToArray());
                if (disposed || !recent.IsVisible) return;
                var menu = new ContextMenu { PlacementTarget = recent };
                foreach (var filename in filenames)
                {
                    var entry = new MenuItem { Header = Path.GetFileName(filename), ToolTip = filename };
                    entry.Click += async (_, _) => await LoadPresetAsync(LookSections.All, filename);
                    menu.Items.Add(entry);
                }
                if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No recent presets", IsEnabled = false });
                recent.ContextMenu = menu;
                menu.IsOpen = true;
            };
            presets.Children.Add(recent);
            section.Children.Add(presets);
            section.Children.Add(MakePresetSections());

            exportButtons.Children.Add(Button("Export with Spektrafilm…", () => ExportAsync(useRecipeSize: false)));
            exportButtons.Children.Add(Button("Export 33-point LUT…", () => ExportLutAsync(33)));
            exportButtons.Children.Add(Button("Export 65-point LUT…", () => ExportLutAsync(65)));
            section.Children.Add(exportButtons);
            section.Children.Add(new TextBlock { Text = "Image export saves 16-bit TIFF or PNG. Capture One Export uses the recipe's size, crop, resolution and local RGB ICC profile. Export from this panel uses sRGB. The save dialog sets the name. Permitted credits, title, date, rating and all-keyword selections carry over.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            return Section("presets-and-lut", "Presets and LUT export", section);
        }

        private FrameworkElement MakePresetSections()
        {
            var controls = new StackPanel();
            var film = AddChoice("Film and print");
            var optics = AddChoice("Optics and colour spaces");
            var grain = AddChoice("Staged Capture One grain");
            var enabled = AddChoice("Master switch");
            var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 1) };
            var load = Button("Load selected sections…", () => LoadPresetAsync(Selected()));
            (LookPreset Preset, LookSections Included)? copied = null;
            var apply = Button("Apply copied settings", () =>
            {
                try
                {
                    if (copied is { } entry && SyncSelection() && displayedVariant is { } variant)
                        ApplyPresetSections(variant, entry.Preset, Selected() & entry.Included, "Apply Spektrafilm settings");
                }
                catch (Exception error) { Report(error); }
                return Task.CompletedTask;
            });
            var copy = Button("Copy selected settings", () =>
            {
                var sections = Selected();
                if (sections != LookSections.None && SyncSelection() && enableSpektraFilm.IsEnabled && CaptureLook() is { } look)
                {
                    copied = (LookPreset.From(look), sections);
                    status.Text = "Spektrafilm settings copied.";
                    apply.IsEnabled = true;
                }
                return Task.CompletedTask;
            });
            actions.Children.Add(load);
            actions.Children.Add(copy);
            actions.Children.Add(apply);
            controls.Children.Add(actions);
            foreach (var choice in new[] { film, optics, grain, enabled })
            {
                choice.Checked += (_, _) => RefreshButtons();
                choice.Unchecked += (_, _) => RefreshButtons();
            }
            RefreshButtons();
            return Section("preset-sections", "Select settings to copy or load", controls);

            CheckBox AddChoice(string title)
            {
                var choice = new CheckBox { Content = title, IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };
                controls.Children.Add(choice);
                return choice;
            }

            LookSections Selected() => (film.IsChecked == true ? LookSections.Film : LookSections.None)
                | (optics.IsChecked == true ? LookSections.Optics : LookSections.None)
                | (grain.IsChecked == true ? LookSections.Grain : LookSections.None)
                | (enabled.IsChecked == true ? LookSections.Enabled : LookSections.None);

            void RefreshButtons()
            {
                var selected = Selected();
                load.IsEnabled = copy.IsEnabled = selected != LookSections.None;
                apply.IsEnabled = copied is { } entry && (entry.Included & selected) != LookSections.None;
            }
        }

        internal bool IsSpektraExportActive => SyncSelection() && IsSpektraFilmEnabled;
        internal Task<bool> ExportAsync(bool useRecipeSize, Window? owner = null) =>
            IsSpektraExportActive ? opticalPreview.ExportAsync(useRecipeSize, owner) : Task.FromResult(false);
        internal bool StocksReady => !disposed && options is not null;
        internal LaunchPhase ControlsLoadPhase => controlsLoadFailed ? LaunchPhase.Failed
            : StocksReady ? LaunchPhase.Ready : LaunchPhase.LoadingControls;
        internal ViewerHealth ViewerStatus => opticalPreview.Health;
        internal bool HasPendingLookWrites => looks.HasPending;

        internal void CopySupportDetails()
        {
            try
            {
                Clipboard.SetText(BuildSupportDetails());
                status.Text = "Support details copied.";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                status.Text = "The clipboard is busy. Use Copy support details again.";
            }
        }

        private string BuildSupportDetails()
        {
            var storage = SupportStorageState();
            var version = typeof(LookPanel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // Export only the enumerated state and version identities. The log,
            // variant, paths, metadata and control values stay on the computer.
            return JsonSerializer.Serialize(new
            {
                SchemaVersion = 2,
                Build = version,
                CaptureOne = LaunchHealth.HostVersion,
                RendererProtocol = RendererProtocol.Version,
                RendererAssets = opticalPreview.VerifiedRendererAssets,
                Controls = ControlsLoadPhase.ToString(),
                Viewer = ViewerStatus.ToString(),
                ViewerPresentation = new
                {
                    Encoding = "sRGB",
                    RecipeProofing = P1.C1.ApplicationSettings.Default.IsProofingRecipeSettings,
                    WindowsAssociatedIccSha256 = DisplayProfileDiagnostic.ReadAssociatedIccSha256(window),
                },
                NativeExportBridge = ExportBridgeStatus.ToString(),
                NativeExportRoute = SupportExportRoute(storage),
                LookStorage = storage,
                DroppedLogEntries = StartupHook.DroppedLogEntries,
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        private string SupportStorageState()
        {
            if (retryLookReadAt is not null) return "ReadUnavailable";
            if (displayedVariant is not { } selected) return "NoImage";
            if (looks.IsBlocked(selected)) return "RecoveryRequired";
            if (looks.HasUnprotectedPending) return "SaveUnprotected";
            if (looks.HasPending) return "RecoveryCheckpoint";
            return looks.HasCommitted(selected) ? "Saved" : "Default";
        }

        private string SupportExportRoute(string storage)
        {
            if (!StocksReady || displayedVariant is null) return "Waiting";
            if (storage is "ReadUnavailable" or "RecoveryRequired") return "LookUnavailable";
            if (!IsSpektraFilmEnabled) return "CaptureOne";
            return ExportBridgeStatus == ExportBridgeState.Connected ? "Spektrafilm" : "UsePanel";
        }

        private Task SavePresetAsync() =>
            SavePresetWithDestinationAsync(picker => picker.ShowDialog() == true ? picker.FileName : null);

        private async Task SavePresetWithDestinationAsync(Func<SaveFileDialog, string?> chooseDestination)
        {
            if (!SyncSelection() || !enableSpektraFilm.IsEnabled || CaptureLook() is not { } settings)
                return;
            try
            {
                var stop = lifetime.Token;
                var original = HostProfile.CaptureTarget(window).SourcePath;
                var preset = LookPreset.From(settings);
                var picker = new SaveFileDialog
                {
                    Filter = "Spektrafilm preset|*.spektra.json", FileName = "Film look.spektra.json",
                    InitialDirectory = UserPresetDirectory(),
                };
                var destination = chooseDestination(picker);
                if (destination is null)
                    return;
                stop.ThrowIfCancellationRequested();
                var approved = OutputPathGuard.Approve(destination, original);
                await Task.Run(() =>
                {
                    try
                    {
                        if (RuntimeAssets.ReadPresetIdentity(Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")) is { } identity)
                            preset = preset with { SchemaVersion = 3, Engine = identity };
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    {
                        StartupHook.Log("Preset renderer provenance unavailable: " + error.GetBaseException().Message);
                    }
                    approved.Write(stream => PanelFiles.WritePreset(stream, preset), stop);
                }, stop);
                RememberPreset(approved.Path);
                if (!disposed) status.Text = "Preset saved.";
            }
            catch (OperationCanceledException) when (disposed) { }
            catch (Exception error)
            {
                if (!disposed) Report(error);
            }
        }

        private async Task LoadPresetAsync(LookSections sections, string? filename = null)
        {
            if (sections == LookSections.None || !SyncSelection() || !enableSpektraFilm.IsEnabled || displayedVariant is not { } variant)
                return;
            try
            {
                if (filename is null)
                {
                    var picker = new OpenFileDialog
                    {
                        Filter = "Spektrafilm preset|*.spektra.json", CheckFileExists = true,
                        InitialDirectory = UserPresetDirectory(),
                    };
                    if (picker.ShowDialog() != true) return;
                    filename = picker.FileName;
                }
                var preset = await Task.Run(() => PanelFiles.ReadPreset<LookPreset>(filename));
                var applied = sections == LookSections.All ? ApplyPreset(variant, preset)
                    : ApplyPresetSections(variant, preset, sections, "Spektrafilm preset sections");
                if (applied) RememberPreset(filename);
            }
            catch (Exception error)
            {
                Report(error);
            }
        }

        private void RememberPreset(string filename)
        {
            try { panelChoices.RememberPreset(filename); }
            catch (Exception error) { StartupHook.Log("Recent preset could not be saved: " + error.GetBaseException().Message); }
        }

        private static string UserPresetDirectory()
        {
            var path = Path.Combine(PanelPreferences.UserDataDirectory(), "Presets");
            try { return Directory.CreateDirectory(path).FullName; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                StartupHook.Log("The user presets directory is unavailable: " + error.GetBaseException().Message);
                return "";
            }
        }

        private bool ApplyPreset(Guid variant, LookPreset preset) =>
            ApplyPresetSections(variant, preset, LookSections.All, "Spektrafilm preset");

        private bool ApplyPresetSections(Guid variant, LookPreset preset, LookSections sections, string historyName)
        {
            preset.Validate();
            if (sections == LookSections.None || !SyncSelection() || displayedVariant != variant
                || !enableSpektraFilm.IsEnabled || CaptureLook() is not { } current)
                return false;
            UpdateHistoryGesture(false);
            var (film, optics, enabled, grain) = preset.SelectSections(current, sections);
            ApplyControls(film, optics, enabled, grain);
            PersistLook(historyName);
            return true;
        }

        private void ApplyControls(LookSettings? look, SpatialPreview.Settings? optics, bool? enabled = null,
            GrainControls? grain = null)
        {
            if (look is not null)
                ValidateLoadedSettings(look, optics);
            settingValues = true;
            try
            {
                if (look is null)
                    SetDefaults();
                else
                    SetPreset(look);
                opticalPreview.LoadSettings(optics);
                if (enabled is { } useFilmTools)
                    enableSpektraFilm.IsChecked = useFilmTools;
                if (grain is not null)
                    SetGrainControls(grain);
            }
            finally
            {
                settingValues = false;
            }
            UpdateEnabledState();
        }

        internal bool SyncSelection()
        {
            if (!StocksReady)
                return false;
            Guid variant;
            try
            {
                variant = HostProfile.ReadSelection(window).Variant.UUID;
            }
            catch (InvalidOperationException)
            {
                variant = Guid.Empty;
            }
            if (variant != Guid.Empty)
            {
                UseSelectedLook(variant);
                return true;
            }
            if (displayedVariant is not null)
            {
                UpdateHistoryGesture(false);
                displayedVariant = null;
                displayedLook = null;
                retryLookReadAt = null;
                previousSavedLook = null;
                UpdateEnabledState();
            }
            return false;
        }

        private bool UseSelectedLook(Guid variant)
        {
            if (!StocksReady || variant == Guid.Empty)
                return false;
            if (displayedVariant != variant || retryLookReadAt is long retryAt && Environment.TickCount64 >= retryAt)
            {
                UpdateHistoryGesture(false);
                displayedLook = null;
                previousSavedLook = null;
                retryLookReadAt = null;
                status.Text = string.Empty;
                FlushLooks();
                displayedVariant = variant;
                try
                {
                    var outcome = looks.ReadOutcome(variant);
                    if (outcome.State == LookLoadState.TransientIo)
                        retryLookReadAt = Environment.TickCount64 + 1000;
                    var saved = outcome.Look;
                    ApplyControls(saved?.Look, saved?.Optics, saved?.Enabled ?? !outcome.ReadOnly, saved?.Grain ?? DefaultGrain);
                    if (!outcome.ReadOnly)
                        displayedLook = CaptureLook();
                    else if (saved is not null)
                    {
                        previousSavedLook = saved;
                        UpdateEnabledState();
                    }
                    if (outcome.Detail is { } detail)
                    {
                        status.Text = detail;
                        if (outcome.ReadOnly) ShowSaveError(detail);
                    }
                }
                catch (Exception error)
                {
                    looks.ProtectUnreadable(variant, error);
                    previousSavedLook = null;
                    ApplyControls(null, null, false, DefaultGrain);
                    status.Text = $"Could not load this image’s Spektrafilm look. {error.Message}";
                    StartupHook.Log(status.Text);
                    ShowSaveError(status.Text);
                }
            }
            return IsSpektraFilmEnabled;
        }

        private VariantLook? CaptureLook()
        {
            if (CurrentSettings() is not { } look)
                return null;
            look = look with { ScanInvert = scanInvert.IsChecked == true };
            var grain = new GrainControls(grainType.SelectedIndex, grainImpact.Value, grainSize.Value);
            return new VariantLook(look, opticalPreview.CaptureSettings(), IsSpektraFilmEnabled, Grain: grain);
        }

        private void SaveLook() => PersistLook("Spektrafilm settings");

        private void PersistLook(string entryName)
        {
            if (settingValues || !StocksReady || retryLookReadAt is not null
                || displayedVariant is not { } variant || looks.IsBlocked(variant) || CaptureLook() is not { } look)
                return;
            // The key belongs to the displayed controls, even if the host selection has just changed.
            var before = displayedLook;
            try
            {
                looks.Queue(variant, look);
                displayedLook = look;
                FlushLooks();
                if (historyGesture is null)
                    RecordLookEdit(variant, before, look, entryName);
            }
            catch (Exception error)
            {
                ShowSaveError($"Spektrafilm could not save your look. {error.Message}");
                UpdateEnabledState();
            }
        }

        private void UpdateHistoryGesture(bool active)
        {
            if (nativeUndo is null)
                return;
            if (active)
            {
                if (historyGesture is null && displayedVariant is { } variant && displayedLook is { } before)
                    historyGesture = (variant, before);
                return;
            }
            var finished = historyGesture;
            historyGesture = null;
            if (finished is { } edit && displayedVariant == edit.Variant && displayedLook is { } after)
                RecordLookEdit(edit.Variant, edit.Before, after, "Spektrafilm slider");
        }

        private void RecordLookEdit(Guid variant, VariantLook? before, VariantLook after, string name)
        {
            if (nativeUndo is null || before is null || VariantLookStore.Equivalent(before, after))
                return;
            try { nativeUndo.Do(new NativeLookUndoAction(looks, variant, before, after, name, historyChanged)); }
            catch (Exception error)
            {
                status.Text = "Look saved. Capture One Undo could not record this edit.";
                StartupHook.Log("Capture One Undo could not record a Spektrafilm edit: " + error);
            }
        }

        private void OnHistoryChanged(Guid variant, Exception? error)
        {
            if (displayedVariant == variant)
            {
                displayedVariant = null;
                UseSelectedLook(variant);
            }
            if (error is not null)
                ShowSaveError(error.Message);
        }

        internal void RetryPendingLookWrites()
        {
            if (looks.HasPending)
                FlushLooks();
        }

        internal bool ProtectPendingLooks()
        {
            RetryPendingLookWrites();
            return !looks.HasUnprotectedPending;
        }

        internal void FlushLooks()
        {
            try
            {
                looks.Flush();
                if (retryLookReadAt is null && (displayedVariant is not { } selected || !looks.IsBlocked(selected)))
                    saveError.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                ShowSaveError($"Spektrafilm could not save your look. {error.Message}");
            }
        }

        internal bool AwaitingLookRead => retryLookReadAt is not null;

        private void ShowSaveError(string message)
        {
            if (looks.HasPending || AwaitingLookRead) StartupHook.EnsureRecoveryWatchdog();
            if (saveError.Visibility != Visibility.Visible || saveError.Text != message)
                StartupHook.Log(message);
            saveError.Text = message;
            saveError.Visibility = Visibility.Visible;
        }

        private Task TryRecoverSelectedLook()
        {
            if (!SyncSelection() || displayedVariant is not { } variant || !looks.IsBlocked(variant))
            {
                status.Text = "This image has no blocked saved look to reset.";
                return Task.CompletedTask;
            }
            var previous = previousSavedLook;
            var prompt = previous is null
                ? "Reset this image’s Spektrafilm look to defaults? The original file and any recovery edit will be archived before replacement."
                : "Restore this image’s previous verified Spektrafilm look? The current file and any recovery edit will be archived before replacement.";
            if (System.Windows.MessageBox.Show(prompt, StartupHook.ProductName, MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return Task.CompletedTask;
            if (!SyncSelection() || displayedVariant != variant || !looks.IsBlocked(variant))
                return Task.CompletedTask;
            try
            {
                RecoverSelectedLook(variant, previous);
            }
            catch (Exception error)
            {
                if (!looks.IsBlocked(variant))
                {
                    displayedVariant = null;
                    UseSelectedLook(variant);
                }
                ShowSaveError($"The saved look could not be recovered. {error.Message}");
                UpdateEnabledState();
            }
            return Task.CompletedTask;
        }

        private void RecoverSelectedLook(Guid variant, VariantLook? previous)
        {
            if (previous is not null)
                looks.RestorePrevious(variant, previous);
            else
            {
                ApplyControls(null, null, false, DefaultGrain);
                var look = CurrentSettings() ?? throw new InvalidOperationException("Film stocks are not ready.");
                looks.ResetBlocked(variant, new VariantLook(look, opticalPreview.CaptureSettings(), true, Grain: DefaultGrain));
            }
            displayedVariant = null;
            UseSelectedLook(variant);
            saveError.Visibility = Visibility.Collapsed;
            status.Text = previous is null
                ? "Saved look reset. The original files are preserved in the Look recovery folder."
                : "Previous saved look restored. The original files are preserved in the Look recovery folder.";
        }

        private void ValidateLoadedSettings(LookSettings look, SpatialPreview.Settings? optics)
        {
            LookStateValidation.ValidateLook(look);
            foreach (var (key, value) in look.Development)
                if (!development.TryGetValue(key, out var item) || value < item.Slider.Minimum || value > item.Slider.Maximum)
                    throw new InvalidDataException($"The saved development control '{key}' is unsupported or out of range.");
            if (look.FilmPushPullMode is not "standard" and not "experimental")
                throw new InvalidDataException("The saved push/pull model is unavailable.");
            if (optics is null) return;
            LookStateValidation.ValidateOptics(optics);
            opticalPreview.ValidateSettings(optics);
        }

        private void SetPreset(LookSettings settings)
        {
            var available = options ?? throw new InvalidOperationException("The available stocks are still loading.");
            static Stock Find(Stock[] stocks, string id) => stocks.FirstOrDefault(option => option.Id == id)
                ?? throw new LookUnsupportedException("This look uses a stock or mode that is unavailable.");
            var selectedMode = Find(available.Modes, settings.Mode);
            var selectedFilm = Find(available.Films, settings.Film);
            var selectedPaper = Find(available.Papers, settings.Paper);
            var selectedMethod = Find(available.SpectralMethods, settings.SpectralMethod);
            if (selectedMode.Id == "print" && selectedFilm.Kind == "positive")
                throw new InvalidDataException("This positive film needs scan mode.");

            var previousSetting = settingValues;
            settingValues = true;
            try
            {
                mode.SelectedItem = selectedMode;
                SetModeControls();
                film.SelectedItem = selectedFilm;
                paper.SelectedItem = selectedPaper;
                spectralMethod.SelectedItem = selectedMethod;
                filmPushPullMode.SelectedIndex = settings.FilmPushPullMode == "experimental" ? 1 : 0;
                scanInvert.IsChecked = settings.ScanInvert;
                exposure.Value = settings.Exposure;
                printExposure.Value = settings.PrintExposure;
                strength.Value = settings.Strength * 100;
                foreach (var (key, entry) in development)
                    entry.Slider.Value = settings.Development?.GetValueOrDefault(key, entry.Default) ?? entry.Default;
            }
            finally
            {
                settingValues = previousSetting;
            }
        }

        private Task ExportLutAsync(int resolution) => ExportLutWithDestinationAsync(resolution, ChooseLutDestination);

        private async Task ExportLutWithDestinationAsync(int resolution, Func<int, string[], string?> chooseDestination)
        {
            if (lutExporting || !IsSpektraFilmEnabled || CurrentSettings() is not { } settings)
                return;
            lutExporting = true;
            using var work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            lutWork = work;
            try
            {
                var original = HostProfile.CaptureTarget(window).SourcePath;
                var plan = NativeLutExporter.Prepare(opticalPreview.RendererOptions(settings, "srgb", "srgb", export: true));
                var destination = chooseDestination(resolution, plan.ExcludedEffects);
                if (destination is null)
                    return;
                work.Token.ThrowIfCancellationRequested();
                var approved = OutputPathGuard.Approve(destination, original);
                cancelLut.Visibility = Visibility.Visible;
                status.Text = "Baking the sRGB LUT…";
                var cube = await opticalPreview.RenderLutAsync(plan, resolution, settings.Strength, work.Token);
                if (!disposed)
                {
                    await Task.Run(() => approved.Write(stream =>
                    {
                        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                        const int chunkSize = 16 * 1024;
                        for (var offset = 0; offset < cube.Length; offset += chunkSize)
                        {
                            work.Token.ThrowIfCancellationRequested();
                            writer.Write(cube.AsSpan(offset, Math.Min(chunkSize, cube.Length - offset)));
                        }
                    }, work.Token), work.Token);
                    if (!disposed)
                        status.Text = "LUT exported.";
                }
            }
            catch (OperationCanceledException)
            {
                if (!disposed)
                    status.Text = "LUT export canceled.";
            }
            catch (Exception error)
            {
                if (!disposed)
                    Report(error);
            }
            finally
            {
                lutWork = null;
                lutExporting = false;
                if (!disposed) cancelLut.Visibility = Visibility.Collapsed;
            }
        }

        private static string? ChooseLutDestination(int resolution, string[] excludedEffects)
        {
            if (MessageBox.Show("A 3D LUT captures the colour transform and Look strength. It cannot include image-dependent effects or Capture One adjustments.\n\nExcluded: "
                + string.Join(", ", excludedEffects) + ".\n\nContinue with an sRGB LUT?", "Export Spektrafilm LUT",
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return null;
            var picker = new SaveFileDialog { Filter = "3D LUT|*.cube", FileName = $"Spektrafilm sRGB {resolution}.cube" };
            return picker.ShowDialog() == true ? picker.FileName : null;
        }

        private Expander MakeGrainControls()
        {
            var section = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            section.Children.Add(nativeGrainText);
            section.Children.Add(new TextBlock { Text = "Stage values here, then apply them to Capture One.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3) });
            AutomationProperties.SetName(grainType, "Staged grain type");
            section.Children.Add(grainType);
            section.Children.Add(grainImpact);
            section.Children.Add(grainSize);
            grainType.SelectionChanged += (_, _) =>
            {
                grainSize.IsEnabled = grainType.SelectedIndex != 0;
                SaveLook();
            };
            foreach (var slider in new[] { grainImpact, grainSize })
            {
                slider.ValueChanged += (_, _) => SaveLook();
                slider.DragStarted += (_, _) => UpdateHistoryGesture(true);
                slider.DragCompleted += (_, _) => UpdateHistoryGesture(false);
                slider.LostMouseCapture += (_, _) => UpdateHistoryGesture(false);
                slider.Unloaded += (_, _) => UpdateHistoryGesture(false);
            }

            var buttons = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            applyGrain.IsEnabled = restoreGrain.IsEnabled = false;
            buttons.Children.Add(applyGrain);
            buttons.Children.Add(restoreGrain);
            section.Children.Add(buttons);
            var expander = PanelPreferences.CreateSection("capture-one-grain", "Capture One Grain", section, 9);
            expander.Expanded += (_, args) =>
            {
                if (ReferenceEquals(args.OriginalSource, expander)) RefreshNativeGrain(force: true);
            };
            return expander;
        }

        internal void RefreshNativeGrain(bool force)
        {
            if (disposed || !force && !nativeGrainSection.IsExpanded)
                return;
            if (displayedVariant is not { } variant)
            {
                nativeGrainText.Text = "Select a photo to see its Capture One Grain.";
                applyGrain.IsEnabled = restoreGrain.IsEnabled = false;
                return;
            }
            try
            {
                var (current, receipt) = NativeGrain.ReadStatus(window, looks, variant);
                var label = current.Type >= 0 && current.Type < grainType.Items.Count ? grainType.Items[current.Type] : current.Type;
                var values = $"Current in Capture One · {label} · Impact {current.Impact:0.##}";
                if (current.Type != 0) values += $" · Granularity {current.Granularity:0.##}";
                var detail = receipt switch
                {
                    NativeGrain.ReceiptState.MatchesApplied => "Matches the grain applied with Spektrafilm.",
                    NativeGrain.ReceiptState.MatchesOriginal => "Matches the original grain.",
                    NativeGrain.ReceiptState.Changed => "Capture One Grain changed. Restore is unavailable while these values differ.",
                    _ => string.Empty,
                };
                var text = detail.Length == 0 ? values : values + Environment.NewLine + detail;
                if (nativeGrainText.Text != text) nativeGrainText.Text = text;
                applyGrain.IsEnabled = true;
                restoreGrain.IsEnabled = receipt == NativeGrain.ReceiptState.MatchesApplied;
            }
            catch (Exception error)
            {
                nativeGrainText.Text = "Capture One Grain is unavailable. " + error.GetBaseException().Message;
                applyGrain.IsEnabled = restoreGrain.IsEnabled = false;
            }
        }

        private Task ApplyNativeGrainAsync()
        {
            if (SyncSelection() && displayedVariant is { } variant && nativeGrainSection.IsEnabled)
                Run(() => NativeGrain.Apply(window, looks, variant,
                    grainType.SelectedIndex, grainImpact.Value, grainSize.Value), "Capture One Grain applied.");
            RefreshNativeGrain(force: true);
            return Task.CompletedTask;
        }

        private Task RestoreNativeGrainAsync()
        {
            if (SyncSelection() && displayedVariant is { } variant && nativeGrainSection.IsEnabled)
                Run(() => NativeGrain.Restore(window, looks, variant), "Prior grain restored.");
            RefreshNativeGrain(force: true);
            return Task.CompletedTask;
        }

        private void SetGrainControls(GrainControls grain)
        {
            var wasSetting = settingValues;
            settingValues = true;
            try
            {
                grainType.SelectedIndex = Math.Clamp(grain.Type, 0, grainType.Items.Count - 1);
                grainImpact.Value = grain.Impact;
                grainSize.Value = grain.Granularity;
            }
            finally
            {
                settingValues = wasSetting;
            }
        }

        private void AddStock(StackPanel section, string label, ComboBox chooser, Action? changed = null)
        {
            section.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 2) });
            AutomationProperties.SetName(chooser, label);
            if (ReferenceEquals(chooser, film) || ReferenceEquals(chooser, paper))
            {
                chooser.IsTextSearchEnabled = true;
                TextSearch.SetTextPath(chooser, nameof(Stock.Label));
                AutomationProperties.SetHelpText(chooser, "Type the start of a stock name to select it.");
                AddFavoriteMenu(chooser, ReferenceEquals(chooser, paper));
            }
            section.Children.Add(chooser);
            chooser.SelectionChanged += (_, _) => (changed ?? RefreshLook)();
        }

        private void AddFavoriteMenu(ComboBox chooser, bool printStock)
        {
            var menu = new ContextMenu();
            var favorite = new MenuItem();
            menu.Items.Add(favorite);
            chooser.ContextMenu = menu;
            menu.Opened += (_, _) =>
            {
                var stock = chooser.SelectedItem as Stock;
                favorite.Header = stock is not null && panelChoices.IsFavorite(printStock, stock.Id)
                    ? "Remove from favourites" : "Add to favourites";
                favorite.IsEnabled = stock is not null;
            };
            favorite.Click += (_, _) =>
            {
                if (chooser.SelectedItem is not Stock stock || options is null) return;
                var prior = settingValues;
                try
                {
                    panelChoices.ToggleFavorite(printStock, stock.Id);
                    var sorted = SortStocks(printStock ? options.Papers : options.Films, printStock);
                    options = printStock ? options with { Papers = sorted } : options with { Films = sorted };
                    settingValues = true;
                    chooser.ItemsSource = sorted;
                    chooser.SelectedItem = stock;
                }
                catch (Exception error) { Report(error); }
                finally { settingValues = prior; }
            };
        }

        private NativeSlider AddSlider(StackPanel section, string title, double min, double max, double initial, double step,
            string unit = "", double displayScale = 1)
        {
            var slider = new NativeSlider(title, min, max, initial, step, unit, displayScale);
            slider.ValueChanged += (_, _) => RefreshLook();
            if (opticalPreview is not null)
                opticalPreview.TrackSliderDrag(slider);
            section.Children.Add(slider);
            return slider;
        }

        internal void LoadStocks()
        {
            if (disposed || options is not null)
                return;
            try
            {
                var root = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")
                    ?? throw new InvalidOperationException("Start Capture One from the Spektrafilm launcher.");
                var available = System.Text.Json.JsonSerializer.Deserialize<LookOptions>(File.ReadAllText(Path.Combine(root, "stocks.json")))
                    ?? throw new InvalidDataException("Spektrafilm stock data is missing.");
                var renderer = RendererParameters.Read(root);
                renderer.VerifyOptions("stock", available.Films.Select(stock => stock.Id));
                renderer.VerifyOptions("paper", available.Papers.Select(stock => stock.Id));
                renderer.VerifyOptions("mode", available.Modes.Select(mode => mode.Id));
                if (!renderer.SupportsChoice("spectral-method", "hanatos2026")
                    || !available.Films.All(stock => renderer.SupportsHanatos2026(stock.Id)))
                    available = available with { SpectralMethods = available.SpectralMethods.Where(method => method.Id != "hanatos2026").ToArray() };
                renderer.VerifyOptions("spectral-method", available.SpectralMethods.Select(method => method.Id));
                opticalPreview.VerifyRendererOptions(renderer);
                options = available with { Films = SortStocks(available.Films, false), Papers = SortStocks(available.Papers, true) };
                SetDefaults();
                controlsLoadFailed = false;
                SyncSelection();
            }
            catch (Exception error)
            {
                options = null;
                controlsLoadFailed = true;
                controls.IsEnabled = false;
                Report(error);
            }
        }

        private Stock[] SortStocks(Stock[] stocks, bool printStock) => stocks
            .OrderByDescending(stock => panelChoices.IsFavorite(printStock, stock.Id))
            .ThenBy(stock => stock.Label, StringComparer.CurrentCulture).ToArray();

        private void SetModeControls()
        {
            if (options is null)
                return;

            var inPrintMode = mode.SelectedItem is Stock { Id: "print" };
            var currentFilm = (film.SelectedItem as Stock)?.Id;
            var previousSetting = settingValues;
            settingValues = true;
            try
            {
                var films = options.Films.Where(stock => !inPrintMode || stock.Kind != "positive").ToArray();
                film.ItemsSource = films;
                film.SelectedItem = films.FirstOrDefault(stock => stock.Id == currentFilm) ?? films.FirstOrDefault(stock => stock.Id == DefaultFilm);
                paper.IsEnabled = printExposure.IsEnabled = printing.IsEnabled = inPrintMode;
                SetScanOptions();
            }
            finally
            {
                settingValues = previousSetting;
            }
        }

        private void SetScanOptions()
        {
            var canInvert = mode.SelectedItem is Stock { Id: "scan" } && film.SelectedItem is Stock { Kind: "negative" };
            scanInvert.Visibility = canInvert ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetDefaults()
        {
            if (options is null)
                return;
            var previousSetting = settingValues;
            settingValues = true;
            try
            {
                mode.ItemsSource = options.Modes;
                mode.SelectedItem = options.Modes.First(stock => stock.Id == "print");
                paper.ItemsSource = options.Papers;
                paper.SelectedItem = options.Papers.First(stock => stock.Id == DefaultPaper);
                spectralMethod.ItemsSource = options.SpectralMethods;
                spectralMethod.SelectedItem = options.SpectralMethods.First(stock => stock.Id == DefaultSpectralMethod);
                SetModeControls();
                film.SelectedItem = options.Films.First(stock => stock.Id == DefaultFilm);
                scanInvert.IsChecked = false;
                filmPushPullMode.SelectedIndex = 0;
                exposure.Value = printExposure.Value = 0;
                strength.Value = 100;
                foreach (var (slider, initial) in development.Values)
                    slider.Value = initial;
            }
            finally
            {
                settingValues = previousSetting;
            }
        }

        private LookSettings? CurrentSettings()
        {
            if (mode.SelectedItem is not Stock selectedMode || film.SelectedItem is not Stock selectedFilm
                || paper.SelectedItem is not Stock selectedPaper || spectralMethod.SelectedItem is not Stock selectedMethod)
                return null;
            return new LookSettings(selectedFilm.Id, selectedPaper.Id, exposure.Value,
                printExposure.Value, strength.Value / 100, selectedMode.Id, selectedMethod.Id,
                scanInvert.Visibility == Visibility.Visible && scanInvert.IsChecked == true,
                development.ToDictionary(item => item.Key, item => item.Value.Slider.Value),
                (filmPushPullMode.SelectedItem as Stock)?.Id ?? "standard");
        }

        private void RefreshLook()
        {
            if (settingValues)
                return;
            SaveLook();
            if (IsSpektraFilmEnabled)
                opticalPreview.RequestRefresh();
        }

        private void Run(Action action, string success)
        {
            try
            {
                action();
                status.Text = success;
                StartupHook.Log(success);
            }
            catch (Exception error)
            {
                Report(error);
            }
        }

        private void Report(Exception error)
        {
            while (error is TargetInvocationException { InnerException: not null } nested)
                error = nested.InnerException!;
            status.Text = error.Message;
            StartupHook.Log(error.ToString());
        }

        private static Button Button(string title, Func<Task> action)
        {
            var button = new Button { Content = title, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 5, 0) };
            button.Click += async (_, _) => await action();
            return button;
        }

    }
}
