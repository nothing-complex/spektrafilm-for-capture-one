using System;
using System.IO;

internal static class ApprovedCatalog
{
    internal static string? SelectedDatabase { get; } =
        ParseSelection(Environment.GetEnvironmentVariable("SPEKTRAFILM_CATALOG"));

    internal static bool HasSelection => SelectedDatabase is not null;

    private static readonly string? SelectedDirectory = SelectedDatabase is { } selected ? Path.GetDirectoryName(selected) : null;

    internal static bool IsApproved(string? activePath, string? documentPath) =>
        SelectedDatabase is not null && PathMatches(documentPath, SelectedDatabase)
        && (PathMatches(activePath, SelectedDatabase) || PathMatches(activePath, SelectedDirectory));

    private static bool PathMatches(string? value, string? expected)
    {
        if (expected is null || string.IsNullOrWhiteSpace(value))
            return false;
        if (Matches(value, expected))
            return true;
        if (!Path.IsPathFullyQualified(value))
            return false;
        try { return Matches(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)), expected); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // The launcher validates and backs up the database before starting Capture One.
    // The hook authorizes it only after the actual native document and manager agree.
    // Resolve the requested path lexically here to avoid I/O during native startup.
    internal static string? ParseSelection(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
            return null;
        try
        {
            var path = Path.GetFullPath(configured);
            return IsCatalogDatabase(path) ? path : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsCatalogDatabase(string path) => Matches(Path.GetExtension(path), ".cocatalogdb");
    private static bool Matches(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
