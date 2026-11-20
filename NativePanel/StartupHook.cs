using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using P1.C1.Controls.Inspector.WPF;
using P1.C1.Core.Messaging;
using P1.C1.Core.ModelEntities;
using P1.C1.Framework.Messaging;
using P1.C1.ViewModels.Controls.Inspector;
using Forms = System.Windows.Forms;

internal static class StartupHook
{
    internal const string ProductName = "Spektrafilm for Capture One";
    internal const string BrandName = "Spektrafilm";
    private static readonly string[] CustomTabIds = Enumerable.Range(1, 18)
        .Select(index => $"Custom{index}PaletteTab").ToArray();
    private static bool showToolOnLaunch = Environment.GetEnvironmentVariable("SPEKTRAFILM_SHOW_TOOL") == "1";

    private static Forms.Timer? timer;
    private static Dispatcher? dispatcher;
    private static HostDocumentAccess.Manager? observedManager;
    private static IDisposable? documentChanges;
    private static int checkQueued;
    private static int quietChecks;
    private const int RecoveryWatchdogMs = 1000;
    private const int IdleWatchdogMs = 4000;

    private static InspectorPaletteViewModel? owner;
    private static InspectorPaletteTabViewModel? tab;
    private static SpektraFilmTool.HostedTool? hostedTool;
    private static ExportHook? exportHook;
    private static IInspectorPaletteTabViewModel? previousTab;
    private static Forms.Form? window;
    private static string? lastState;
    private static readonly Lazy<PanelLog?> PanelLogger = new(() =>
        Environment.GetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG") is { } path && Path.IsPathFullyQualified(path)
            ? new PanelLog(path) : null);

    public static void Initialize()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe
                || !string.Equals(Path.GetFileName(exe), "CaptureOne.exe", StringComparison.OrdinalIgnoreCase))
                return;
            LaunchHealth.Report(LaunchPhase.Starting);
            if (!ApprovedCatalog.HasSelection)
            {
                LaunchHealth.Report(LaunchPhase.CatalogUnavailable);
                return;
            }

            var hostPath = Path.Combine(Path.GetDirectoryName(exe)!, "CaptureOne.dll");
            using var host = File.OpenRead(hostPath);
            var version = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            LaunchHealth.SetHostVersion(version);
            var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            var verified = HostCompatibility.Match(version, architecture, Convert.ToHexString(SHA256.HashData(host)));
            if (verified is null)
            {
                LaunchHealth.Report(LaunchPhase.UnsupportedHost);
                Log($"{ProductName} supports verified Windows Capture One builds {HostCompatibility.SupportedVersions}. Rejected {version} ({architecture}).");
                return;
            }

            VerifiedHost = verified;

            LaunchHealth.Report(LaunchPhase.WaitingForCatalog);
            Log($"{ProductName} startup hook loaded for Capture One {verified.FileVersion} ({verified.Architecture}).");
            // Wait for Capture One's own WinForms message loop before accessing its WPF palette.
            Forms.Application.Idle += OnApplicationIdle;
            Forms.Application.ApplicationExit += OnApplicationExit;
        }
        catch (Exception error)
        {
            LaunchHealth.Report(LaunchPhase.Failed);
            Log(error.ToString());
        }
    }

    private static void OnApplicationExit(object? sender, EventArgs args)
    {
        Forms.Application.Idle -= OnApplicationIdle;
        Forms.Application.ApplicationExit -= OnApplicationExit;
        if (timer is { } check)
        {
            timer = null;
            check.Stop();
            check.Tick -= OnTick;
            check.Dispose();
        }
        dispatcher = null;
        ObserveManager(null);
        try { Detach(); }
        catch (Exception error) { Log(error.ToString()); }
        finally
        {
            LaunchHealth.Report(LaunchPhase.Closed, ExportBridgeState.Detached);
            try
            {
                if (PanelLogger.IsValueCreated)
                    PanelLogger.Value?.Complete().Wait(TimeSpan.FromMilliseconds(150));
            }
            catch (AggregateException) { }
        }
    }

    private static void OnApplicationIdle(object? sender, EventArgs args)
    {
        try
        {
            var document = HostDocumentAccess.Current()?.ActiveDocument();
            var form = HostDocumentAccess.MainForm(document);
            if (form is not { IsHandleCreated: true, IsDisposed: false })
                return;

            Forms.Application.Idle -= OnApplicationIdle;
            dispatcher = Dispatcher.CurrentDispatcher;
            timer = new Forms.Timer { Interval = RecoveryWatchdogMs };
            timer.Tick += OnTick;
            timer.Start();
            OnTick(sender, args);
        }
        catch (Exception error)
        {
            LaunchHealth.Report(LaunchPhase.Failed);
            LogState(error.ToString());
        }
    }

    private static void ObserveManager(HostDocumentAccess.Manager? manager)
    {
        if (ReferenceEquals(manager, observedManager)) return;
        var previous = documentChanges;
        documentChanges = null;
        observedManager = manager;
        try { previous?.Dispose(); }
        catch (Exception error) { Log("Could not remove Capture One document notifications: " + error.GetBaseException().Message); }
        try { documentChanges = manager?.ObserveChanges(QueueHostCheck); }
        catch (Exception error) { Log("Capture One document notifications are unavailable: " + error.GetBaseException().Message); }
    }

    private static void QueueHostCheck()
    {
        if (dispatcher is not { HasShutdownStarted: false } ownerDispatcher || Interlocked.Exchange(ref checkQueued, 1) != 0)
            return;
        _ = ownerDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref checkQueued, 0);
            if (!ReferenceEquals(dispatcher, ownerDispatcher) || timer is not { Enabled: true }) return;
            EnsureRecoveryWatchdog();
            OnTick(null, EventArgs.Empty);
        }));
    }

    internal static void EnsureRecoveryWatchdog()
    {
        quietChecks = 0;
        if (timer is { } check && check.Interval != RecoveryWatchdogMs)
            check.Interval = RecoveryWatchdogMs;
    }

    private static void AdvanceWatchdog(bool hasPendingState)
    {
        if (hasPendingState || documentChanges is null)
        {
            EnsureRecoveryWatchdog();
            return;
        }
        if (quietChecks < 3) quietChecks++;
        if (quietChecks == 3 && timer is { } check && check.Interval != IdleWatchdogMs)
            check.Interval = IdleWatchdogMs;
    }

    private static void OnTick(object? sender, EventArgs args)
    {
        try
        {
            var manager = HostDocumentAccess.Current();
            ObserveManager(manager);
            var document = manager?.ActiveDocument();
            if (document is null || !ApprovedCatalog.IsApproved(manager?.ActivePath(), document.Path))
            {
                EnsureRecoveryWatchdog();
                if (window is { IsDisposed: true })
                    Detach();
                LaunchHealth.Report(LaunchPhase.WaitingForCatalog, ExportBridgeState.Detached);
                LogState(hostedTool is null ? "Waiting for the selected catalog." : "Live Viewer paused until the selected catalog is active.");
                return;
            }

            var form = HostDocumentAccess.MainForm(document);
            var palette = HostDocumentAccess.Palette(form);
            if (document is null || form is not { IsHandleCreated: true, IsDisposed: false } || palette is null)
            {
                EnsureRecoveryWatchdog();
                LaunchHealth.Report(LaunchPhase.WaitingForPalette);
                LogState("Waiting for the selected catalog’s tool palette.");
                return;
            }

            if (ReferenceEquals(palette, owner) && tab is not null && palette.Tabs.Contains(tab)
                && hostedTool is { } existing && tab.AllTools.Contains(existing.Tool))
            {
                existing.CheckSelectionAndSave();
                AdvanceWatchdog(existing.LookRecoveryPending);
                existing.ReportLaunchHealth();
                return;
            }

            EnsureRecoveryWatchdog();
            Detach();
            Attach(form, palette, document);
            hostedTool?.ReportLaunchHealth();
        }
        catch (Exception error)
        {
            EnsureRecoveryWatchdog();
            LaunchHealth.Report(LaunchPhase.Failed);
            LogState(error.ToString());
        }
    }

    private static void Attach(Forms.Form form, InspectorPaletteViewModel palette, ICaptureOneDocument document)
    {
        var bus = MessageBusProvider.DefaultInstance;
        var lastUserTab = palette.Current;
        var id = FindFreeTabSlot(palette.Tabs.Select(item => item.Name))
            ?? throw new InvalidOperationException($"Remove an unused custom tool tab to make room for {ProductName}.");
        var newTab = new FilmToolsTab(id, bus.GlobalBus, document)
        {
            IsVisible = true,
            ParentPalette = palette,
        };

        SpektraFilmTool.HostedTool? hosted = null;
        try
        {
            hosted = SpektraFilmTool.Create(form, bus, document);
            // The menu-level AddTool helper also emits a tour event and expects
            // a factory-registered caption. Add the hosted control to its area.
            newTab.ScrollAreaViewModel.AddTool(hosted.Tool);
            palette.Tabs.Add(newTab);
            var selected = showToolOnLaunch || lastUserTab is null ? newTab : lastUserTab;
            if (!ReferenceEquals(palette.Current, selected)) SelectTab(palette, selected);
        }
        catch
        {
            if (ReferenceEquals(palette.Current, newTab))
                SelectTab(palette, lastUserTab);
            palette.Tabs.Remove(newTab);
            hosted?.Dispose();
            newTab.Dispose();
            throw;
        }

        owner = palette;
        tab = newTab;
        hostedTool = hosted;
        previousTab = lastUserTab;
        window = form;
        window.FormClosing += OnFormClosing;
        showToolOnLaunch = false;
        LogState($"Attached {ProductName} to the selected catalog as {id}.");
        AttachExport(form, hosted);
        hosted.LoadControls();
    }

    private static string? FindFreeTabSlot(IEnumerable<string?> occupied)
    {
        var used = occupied.ToHashSet(StringComparer.Ordinal);
        return CustomTabIds.FirstOrDefault(name => !used.Contains(name));
    }

    private static void AttachExport(Forms.Form form, SpektraFilmTool.HostedTool hosted)
    {
        hosted.SetExportBridgeStatus(ExportBridgeState.Checking);
        try
        {
            exportHook = new ExportHook(form, () => hosted.IsSpektraExportActive,
                () => hosted.ExportAsync(exportHook?.Dialog),
                detail => hosted.SetExportBridgeStatus(ExportBridgeState.Unavailable, detail));
            hosted.SetExportBridgeStatus(ExportBridgeState.Connected);
        }
        catch (Exception error)
        {
            hosted.SetExportBridgeStatus(ExportBridgeState.Unavailable, error.GetBaseException().Message);
            Log($"{BrandName} could not bind the native Export command [document-commands]: {error}");
        }
    }

    private sealed class FilmToolsTab(string id, IMessageBus bus, ICaptureOneDocument document)
        : InspectorPaletteTabViewModel(id, bus, ProductName, document)
    {
        private static readonly FilmstripTabInfoFactory FilmstripFactory = new();

        protected override IInspectorPaletteTabInfoFactory TabInfoFactory => FilmstripFactory;
    }

    private sealed class FilmstripTabInfoFactory : IInspectorPaletteTabInfoFactory
    {
        private readonly InspectorPaletteTabInfoFactory nativeFactory = new();

        public InspectorPaletteTabInfo Create(string name) => nativeFactory.Create(name) with
        {
            Image = DrawFilmstrip(Color.FromRgb(196, 196, 196)),
            CheckedImage = DrawFilmstrip(Color.FromRgb(253, 137, 20)),
        };

        private static ImageSource DrawFilmstrip(Color color)
        {
            // The same transparent bounds in both states keep the tab centered when selected.
            var geometry = Geometry.Parse("F0 M 6,8 H 26 V 24 H 6 Z "
                + "M 8,10 H 11 V 12 H 8 Z M 14,10 H 18 V 12 H 14 Z M 21,10 H 24 V 12 H 21 Z "
                + "M 8,14 H 24 V 18 H 8 Z "
                + "M 8,20 H 11 V 22 H 8 Z M 14,20 H 18 V 22 H 14 Z M 21,20 H 24 V 22 H 21 Z");
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
            drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, geometry));
            var icon = new DrawingImage(drawing);
            icon.Freeze();
            return icon;
        }
    }

    private static void OnFormClosing(object? sender, Forms.FormClosingEventArgs args)
    {
        if (args.Cancel)
            return;
        try
        {
            if (!CanCloseAfterSaving(args.CloseReason, () => Forms.MessageBox.Show(sender as Forms.IWin32Window,
                "Spektrafilm could not save changes to the normal location or the recovery folder. Keep Capture One open to retry when storage is available. Close Capture One and discard the pending changes?",
                ProductName, Forms.MessageBoxButtons.YesNo, Forms.MessageBoxIcon.Warning, Forms.MessageBoxDefaultButton.Button2)
                == Forms.DialogResult.Yes))
            {
                args.Cancel = true;
                return;
            }
            Detach(saveWorkspace: true);
        }
        catch (Exception error)
        {
            Log(error.ToString());
        }
    }

    private static bool CanCloseAfterSaving(Forms.CloseReason reason, Func<bool> confirmDiscard)
    {
        if (hostedTool?.ProtectPendingLooks() != false)
            return true;
        Log("Capture One close requested while a Spektrafilm edit is only in memory.");
        if (reason != Forms.CloseReason.UserClosing)
            return true;
        try { return confirmDiscard(); }
        catch (Exception error)
        {
            Log("Could not ask whether to close with unsaved Spektrafilm changes: " + error.GetBaseException().Message);
            return false;
        }
    }

    private static void Detach(bool saveWorkspace = false)
    {
        if (tab is not { } oldTab || owner is not { } oldOwner)
            return;

        var originalWindow = window;
        var oldTool = hostedTool;
        var oldExport = exportHook;
        var oldPrevious = previousTab;
        tab = null;
        owner = null;
        hostedTool = null;
        exportHook = null;
        previousTab = null;
        window = null;

        if (originalWindow is not null)
            originalWindow.FormClosing -= OnFormClosing;
        oldTool?.SetExportBridgeStatus(ExportBridgeState.Detached);
        LaunchHealth.Report(LaunchPhase.WaitingForPalette, ExportBridgeState.Detached);
        try { oldExport?.Dispose(); }
        finally
        {
            try { oldTool?.Dispose(); }
            finally
            {
                if (oldOwner.Tabs.Contains(oldTab))
                {
                    if (ReferenceEquals(oldOwner.Current, oldTab))
                    {
                        var other = oldPrevious is not null && oldOwner.Tabs.Contains(oldPrevious)
                            ? oldPrevious
                            : oldOwner.Tabs.FirstOrDefault(item => !ReferenceEquals(item, oldTab));
                        SelectTab(oldOwner, other);
                    }
                    oldOwner.Tabs.Remove(oldTab);
                    oldTab.Dispose();
                }
            }
        }
        // The main form saves its palette before raising FormClosing. Refresh the
        // in-memory workspace snapshot once the temporary Spektrafilm tab has been removed.
        if (saveWorkspace && originalWindow is { IsDisposed: false })
            oldOwner.SaveSettingsToWorkspace();
    }

    private static void SelectTab(InspectorPaletteViewModel palette, IInspectorPaletteTabViewModel? current) =>
        typeof(InspectorPaletteViewModel).GetProperty("Current")!.SetValue(palette, current);

    private static void LogState(string text)
    {
        if (string.Equals(lastState, text, StringComparison.Ordinal))
            return;
        lastState = text;
        Log(text);
    }

    internal static void Log(string text)
    {
        try { PanelLogger.Value?.Write(text); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { }
    }

    internal static CaptureOneHost? VerifiedHost { get; private set; }
    internal static int DroppedLogEntries => PanelLogger.IsValueCreated ? PanelLogger.Value?.Dropped ?? 0 : 0;
}
