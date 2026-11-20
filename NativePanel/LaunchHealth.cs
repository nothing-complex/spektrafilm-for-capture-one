using System;
using System.IO;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<LaunchPhase>))]
internal enum LaunchPhase
{
    Starting,
    UnsupportedHost,
    CatalogUnavailable,
    WaitingForCatalog,
    WaitingForPalette,
    LoadingControls,
    Ready,
    Closed,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<ViewerHealth>))]
internal enum ViewerHealth { Unchecked, Disabled, WaitingForImage, Rendering, Visible, ProcessingError }

internal static class LaunchHealth
{
    private static readonly Guid? Session = Guid.TryParseExact(Environment.GetEnvironmentVariable("SPEKTRAFILM_LAUNCH_ID"),
        "N", out var id) ? id : null;
    private static readonly string? Path = StatusPath();
    private static Snapshot? published;
    private static string? hostVersion;
    private static bool writeErrorLogged;

    internal static string? HostVersion => hostVersion;
    internal static void SetHostVersion(string? version) => hostVersion = version;

    internal static void Report(LaunchPhase phase, ExportBridgeState export = ExportBridgeState.Checking,
        ViewerHealth viewer = ViewerHealth.Unchecked)
    {
        if (Path is null || Session is not { } id)
            return;
        var current = new Snapshot(phase, export.ToString(), viewer, hostVersion);
        if (current == published)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            PanelFiles.WriteCompactPreset(Path, new ReportFile(1, id.ToString("N"), Environment.ProcessId,
                DateTime.UtcNow, current.Phase, current.HostVersion, current.Export, current.Viewer));
            published = current;
            writeErrorLogged = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!writeErrorLogged)
                StartupHook.Log("Could not record Spektrafilm launch status: " + error.GetBaseException().Message);
            writeErrorLogged = true;
        }
    }

    private static string? StatusPath()
    {
        if (Session is not { } id || Environment.GetEnvironmentVariable("SPEKTRAFILM_PANEL_LOG") is not { } log
            || !System.IO.Path.IsPathFullyQualified(log))
            return null;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(log));
            return directory is null ? null : System.IO.Path.Combine(directory, "Launch status", id.ToString("N") + ".json");
        }
        catch (ArgumentException) { return null; }
    }

    private sealed record Snapshot(LaunchPhase Phase, string Export, ViewerHealth Viewer, string? HostVersion);
    private sealed record ReportFile(int SchemaVersion, string SessionId, int ProcessId, DateTime UpdatedUtc,
        LaunchPhase Phase, string? HostVersion, string Export, ViewerHealth Viewer);
}
