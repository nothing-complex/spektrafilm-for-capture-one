using System;
using System.Collections.Specialized;
using System.Linq;
using P1.C1.Controls.Inspector;
using P1.C1.Controls.Inspector.WPF;
using P1.C1.ViewModels.Controls.Inspector;
using P1.C1.ViewModels.Docking;
using P1.C1.ViewModels.Workspace;

internal sealed class WorkspaceToolTab : IDisposable
{
    private readonly IWorkspaceManager manager;
    private readonly InspectorPaletteViewModel palette;
    private readonly IInspectorPaletteTabViewModel tab;
    private readonly IDockableControl tool;
    private readonly IInspectorPaletteTabViewModel? previous;
    private readonly INotifyCollectionChanged? tabChanges;

    internal WorkspaceToolTab(IWorkspaceManager manager, InspectorPaletteViewModel palette,
        IInspectorPaletteTabViewModel tab, IDockableControl tool, IInspectorPaletteTabViewModel? previous)
    {
        this.manager = manager;
        this.palette = palette;
        this.tab = tab;
        this.tool = tool;
        this.previous = previous;
        tabChanges = palette.Tabs as INotifyCollectionChanged;
        if (tabChanges is not null) tabChanges.CollectionChanged += OnTabsChanged;
        manager.SavingActiveWorkspace += OnSavingWorkspace;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action != NotifyCollectionChangedAction.Move) return;
        var tabs = palette.Tabs;
        var index = tabs.IndexOf(tab);
        if (index < 0) return;
        var active = manager.ActiveWorkspace;
        var key = PanelPreferenceData.WorkspaceKey(active.SettingsType, active.Name, active.Text);
        var placement = new WorkspaceTabPlacement(index > 0 ? tabs[index - 1].Name : null,
            index + 1 < tabs.Count ? tabs[index + 1].Name : null, index);
        PanelPreferences.Update(preferences => preferences.WithWorkspaceTab(key, placement));
    }

    private void OnSavingWorkspace(object? sender, WorkspaceManagerEventArgs args)
    {
        try
        {
            // The main form has just serialized its palette. Strip only our live
            // tab and only when it contains no other tools.
            if (HostDocumentAccess.Current()?.ActiveDocument() is not { } document
                || !ReferenceEquals(HostDocumentAccess.Palette(HostDocumentAccess.MainForm(document)), palette)
                || !palette.Tabs.Contains(tab) || !tab.AllTools.SequenceEqual(new[] { tool })
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
        if (tabChanges is not null) tabChanges.CollectionChanged -= OnTabsChanged;
        manager.SavingActiveWorkspace -= OnSavingWorkspace;
    }
}
