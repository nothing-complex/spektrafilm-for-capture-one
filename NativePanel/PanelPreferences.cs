using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

internal sealed record FloatingPlacement(double Left, double Top, double Width, double Height)
{
    internal bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width)
        && double.IsFinite(Height) && Width is >= 200 and <= 5000 && Height is >= 100 and <= 10000;
}

internal sealed record FloatingToolLayout(bool Open, FloatingPlacement? Placement, double ScrollOffset);

internal sealed record WorkspaceTabPlacement(string? Left, string? Right, int Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hidden = false)
{
    internal int Resolve(IReadOnlyList<string?> tabs)
    {
        if (Right is not null)
            for (var index = 0; index < tabs.Count; index++)
                if (StringComparer.Ordinal.Equals(Right, tabs[index])) return index;
        if (Left is not null)
            for (var index = 0; index < tabs.Count; index++)
                if (StringComparer.Ordinal.Equals(Left, tabs[index])) return index + 1;
        return Math.Clamp(Index, 0, tabs.Count);
    }
}

internal sealed record PanelPreferenceData(int Version = 1, string ViewerSize = "native", int DragSize = 1200,
    bool DraftSynthesis = false, FloatingPlacement? Floating = null, Dictionary<string, bool>? Sections = null,
    double FloatingScrollOffset = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, FloatingToolLayout>? FloatingWorkspaces = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, WorkspaceTabPlacement>? WorkspaceTabs = null)
{
    internal static string WorkspaceKey(int type, string? name, string? label)
    {
        // Capture One uses the same internal name for every custom workspace.
        var identity = name == "CustomWorkspace" ? name + "|" + label : name;
        return type.ToString(CultureInfo.InvariantCulture) + "|" + identity;
    }

    internal FloatingToolLayout FloatingLayout(string? workspace) =>
        workspace is not null && FloatingWorkspaces?.GetValueOrDefault(workspace) is { } saved
            ? saved : new(false, Floating, FloatingScrollOffset);

    internal PanelPreferenceData WithFloatingLayout(string? workspace, FloatingToolLayout layout)
    {
        var updated = this with { Floating = layout.Placement, FloatingScrollOffset = layout.ScrollOffset };
        if (workspace is null || FloatingWorkspaces?.GetValueOrDefault(workspace) == layout) return updated;
        var workspaces = FloatingWorkspaces is null ? new Dictionary<string, FloatingToolLayout>(StringComparer.Ordinal)
            : new Dictionary<string, FloatingToolLayout>(FloatingWorkspaces, StringComparer.Ordinal);
        workspaces[workspace] = layout;
        return updated with { FloatingWorkspaces = workspaces };
    }

    internal PanelPreferenceData WithWorkspaceTab(string workspace, WorkspaceTabPlacement placement)
    {
        if (WorkspaceTabs?.GetValueOrDefault(workspace) == placement) return this;
        var tabs = WorkspaceTabs is null ? new Dictionary<string, WorkspaceTabPlacement>(StringComparer.Ordinal)
            : new Dictionary<string, WorkspaceTabPlacement>(WorkspaceTabs, StringComparer.Ordinal);
        tabs[workspace] = placement;
        return this with { WorkspaceTabs = tabs };
    }
}

internal static class PanelPreferences
{
    private static readonly PanelPreferenceStore Store = new(Path.Combine(UserDataDirectory(), "Panel preferences.json"), StartupHook.Log);

    internal static string UserDataDirectory()
    {
        if (Environment.GetEnvironmentVariable("SPEKTRAFILM_DATA_ROOT") is not { } directory)
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilmToolsForCaptureOne");
        if (!Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("The Spektrafilm data directory must be an absolute path.");
        return Path.GetFullPath(directory);
    }

    internal static PanelPreferenceData Current => Store.Current;
    internal static void Update(Func<PanelPreferenceData, PanelPreferenceData> edit) => Store.Update(edit);
    internal static Expander CreateSection(string key, string title, UIElement content, double topMargin) =>
        Store.CreateSection(key, title, content, topMargin);
}

internal sealed class PanelPreferenceStore
{
    private readonly string pathname;
    private readonly Action<string> log;
    private bool writable = true;

    internal PanelPreferenceStore(string pathname, Action<string> log)
    {
        this.pathname = pathname;
        this.log = log;
        Current = Read();
    }

    internal PanelPreferenceData Current { get; private set; }

    private PanelPreferenceData Read()
    {
        try
        {
            var preferences = PanelFiles.ReadPreset<PanelPreferenceData>(pathname);
            if (preferences.Version != 1) throw new InvalidDataException("Panel preferences are from a newer version.");
            return preferences;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (Exception error)
        {
            // Keep unknown/corrupt preferences untouched; image state is independent.
            writable = false;
            log("Panel preferences could not be loaded: " + error.GetBaseException().Message);
            return new();
        }
    }

    internal void Update(Func<PanelPreferenceData, PanelPreferenceData> edit)
    {
        var next = edit(Current);
        if (next == Current) return;
        Current = next;
        if (!writable) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pathname)!);
            PanelFiles.WritePreset(pathname, next);
        }
        catch (Exception error) { log("Panel preferences could not be saved: " + error.GetBaseException().Message); }
    }

    internal Expander CreateSection(string key, string title, UIElement content, double topMargin)
    {
        var expander = new Expander
        {
            Header = title, Content = content, Margin = new Thickness(0, topMargin, 0, 0),
            IsExpanded = Current.Sections?.GetValueOrDefault(key) ?? false,
        };
        expander.Expanded += OnExpansionChanged;
        expander.Collapsed += OnExpansionChanged;
        return expander;

        void OnExpansionChanged(object sender, RoutedEventArgs args)
        {
            if (!ReferenceEquals(args.OriginalSource, expander)) return;
            Update(current =>
            {
                if (current.Sections?.GetValueOrDefault(key) == expander.IsExpanded) return current;
                var sections = current.Sections is null ? new Dictionary<string, bool>(StringComparer.Ordinal)
                    : new Dictionary<string, bool>(current.Sections, StringComparer.Ordinal);
                sections[key] = expander.IsExpanded;
                return current with { Sections = sections };
            });
        }
    }
}
