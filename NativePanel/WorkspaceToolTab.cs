using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Threading;
using P1.C1.Components.InspectorTools;
using P1.C1.Controls.Inspector;
using P1.C1.Controls.Inspector.Messaging;
using P1.C1.Controls.Inspector.WPF;
using P1.C1.Framework.Messaging;
using P1.C1.ViewModels.Controls.Inspector;
using P1.C1.ViewModels.Workspace;
using P1.C1.ViewModels.Workspace.Serialization.Primitives;

internal sealed class WorkspaceToolTab : IHandleMessage<TabRemovedMessage>, IDisposable
{
    private readonly IWorkspaceManager manager;
    private readonly IMessageBus bus;
    private readonly Dispatcher dispatcher;
    private readonly InspectorPaletteViewModel palette;
    private readonly IInspectorPaletteTabViewModel tab;
    private readonly InspectorToolWpf tool;
    private readonly IInspectorPaletteTabViewModel? previous;
    private readonly INotifyCollectionChanged? tabChanges;
    private (string Workspace, WorkspaceTabPlacement Placement)? removal;
    private string? hiddenWorkspace;
    private bool disposed;

    internal WorkspaceToolTab(IWorkspaceManager manager, IMessageBus bus, InspectorPaletteViewModel palette,
        IInspectorPaletteTabViewModel tab, InspectorToolWpf tool, IInspectorPaletteTabViewModel? previous, string? hiddenWorkspace)
    {
        this.manager = manager;
        this.bus = bus;
        dispatcher = Dispatcher.CurrentDispatcher;
        this.palette = palette;
        this.tab = tab;
        this.tool = tool;
        this.previous = previous;
        this.hiddenWorkspace = hiddenWorkspace;
        tabChanges = palette.Tabs as INotifyCollectionChanged;
        if (tabChanges is not null) tabChanges.CollectionChanged += OnTabsChanged;
        manager.SavingActiveWorkspace += OnSavingWorkspace;
        bus.Subscribe<TabRemovedMessage>(this);
    }

    public Guid Id { get; } = Guid.NewGuid();
    internal bool OwnsHiddenTab => hiddenWorkspace is not null;
    internal bool IsHidden => hiddenWorkspace is { } key && key == Key(manager.ActiveWorkspace);

    internal static string Key(IWorkspaceSettings workspace) =>
        PanelPreferenceData.WorkspaceKey(workspace.SettingsType, workspace.Name, workspace.Text);

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        var tabs = palette.Tabs;
        if (args.Action == NotifyCollectionChangedAction.Remove && args.OldItems?.Contains(tab) == true)
        {
            var indexBefore = args.OldStartingIndex;
            var removedPlacement = new WorkspaceTabPlacement(indexBefore > 0 ? tabs[indexBefore - 1].Name : null,
                indexBefore >= 0 && indexBefore < tabs.Count ? tabs[indexBefore].Name : null,
                indexBefore >= 0 ? indexBefore : tabs.Count, Hidden: true);
            removal = (Key(manager.ActiveWorkspace), removedPlacement);
            return;
        }
        if (args.Action != NotifyCollectionChangedAction.Move) return;
        var index = tabs.IndexOf(tab);
        if (index < 0) return;
        var key = Key(manager.ActiveWorkspace);
        var placement = new WorkspaceTabPlacement(index > 0 ? tabs[index - 1].Name : null,
            index + 1 < tabs.Count ? tabs[index + 1].Name : null, index);
        PanelPreferences.Update(preferences => preferences.WithWorkspaceTab(key, placement));
    }

    public void Handle(TabRemovedMessage message)
    {
        if (message.Name != tab.Text) return;
        if (dispatcher.CheckAccess()) RememberRemoval();
        else if (!dispatcher.HasShutdownStarted) _ = dispatcher.InvokeAsync(RememberRemoval);
    }

    private void RememberRemoval()
    {
        // The native command confirms, removes and disposes its tool before posting.
        // Workspace rebuilds can remove the same tab without posting this message.
        if (disposed || hiddenWorkspace is not null || removal is not { } removed
            || palette.Tabs.Contains(tab) || !tool.IsDisposed || !OwnsActivePalette()) return;
        var key = Key(manager.ActiveWorkspace);
        if (key != removed.Workspace) return;
        hiddenWorkspace = key;
        removal = null;
        PanelPreferences.Update(preferences => preferences.WithWorkspaceTab(key, removed.Placement));
        StartupHook.Log("Capture One removed the Spektrafilm tab. The live look and Export remain active in this workspace.");
    }

    private bool OwnsActivePalette() => HostDocumentAccess.Current()?.ActiveDocument() is { } document
        && ReferenceEquals(HostDocumentAccess.Palette(HostDocumentAccess.MainForm(document)), palette);

    private void OnSavingWorkspace(object? sender, WorkspaceManagerEventArgs args)
    {
        try
        {
            // The main form has just serialized its palette. Strip only our live
            // tab and only when it contains no other tools.
            if (!OwnsActivePalette() || !palette.Tabs.Contains(tab) || !tab.AllTools.SequenceEqual(new[] { tool })
                || args.WorkspaceSettings.InspectorPaletteSettings is not InspectorPaletteSettings settings) return;
            var tabs = settings.PaletteTabs;
            for (var index = 0; index < tabs.Count; index++)
            {
                if (tabs[index].Name != tab.Name || tabs[index].Text != tab.Text) continue;
                tabs.RemoveAt(index);
                var selected = ReferenceEquals(palette.Current, tab) ? previous : palette.Current;
                for (var remaining = 0; remaining < tabs.Count; remaining++)
                {
                    if (selected is null || tabs[remaining].Name != selected.Name || tabs[remaining].Text != selected.Text
                        || !tabs[remaining].Visible) continue;
                    settings.SelectedTab = remaining;
                    return;
                }
                var neighbor = tabs.Skip(index).FirstOrDefault(item => item.Visible)
                    ?? tabs.Take(index).LastOrDefault(item => item.Visible);
                settings.SelectedTab = neighbor is null ? 0 : tabs.IndexOf(neighbor);
                return;
            }
        }
        catch (Exception error)
        {
            StartupHook.Log("Spektrafilm could not update the saved workspace palette: " + error.GetBaseException().Message);
        }
    }

    public void Dispose()
    {
        disposed = true;
        bus.Unsubscribe<TabRemovedMessage>(this);
        if (tabChanges is not null) tabChanges.CollectionChanged -= OnTabsChanged;
        manager.SavingActiveWorkspace -= OnSavingWorkspace;
    }
}
