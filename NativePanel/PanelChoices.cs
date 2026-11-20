using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

internal sealed class PanelChoices
{
    private sealed record Data([property: JsonRequired] int Version, [property: JsonRequired] string[] Films,
        [property: JsonRequired] string[] Papers, [property: JsonRequired] string[] RecentPresets);

    private const int RecentLimit = 6;
    private readonly string path;
    private Data state = new(1, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    private readonly bool writable = true;

    internal PanelChoices(string path, Action<string> log)
    {
        this.path = path;
        try
        {
            var saved = PanelFiles.ReadPreset<Data>(path);
            if (saved.Version != 1 || !ValidIds(saved.Films) || !ValidIds(saved.Papers)
                || saved.RecentPresets is not { Length: <= RecentLimit }
                || !saved.RecentPresets.All(IsPresetPath)
                || saved.RecentPresets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.RecentPresets.Length)
                throw new InvalidDataException("Unsupported stock favourites or recent presets.");
            state = saved;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error)
        {
            writable = false;
            log("Panel choices could not be loaded: " + error.GetBaseException().Message);
        }
    }

    internal IEnumerable<string> RecentPresets => state.RecentPresets.Where(File.Exists);
    internal bool IsFavorite(bool paper, string id) => (paper ? state.Papers : state.Films).Contains(id, StringComparer.Ordinal);

    internal void ToggleFavorite(bool paper, string id)
    {
        if (!ValidId(id)) throw new ArgumentException("Choose a valid stock.");
        var current = paper ? state.Papers : state.Films;
        var updated = IsFavorite(paper, id) ? current.Where(item => item != id).ToArray() : current.Append(id).ToArray();
        if (!ValidIds(updated)) throw new InvalidOperationException("There are too many favourite stocks.");
        Save(paper ? state with { Papers = updated } : state with { Films = updated });
    }

    internal void RememberPreset(string filename)
    {
        if (!IsPresetPath(filename)) return;
        filename = Path.GetFullPath(filename);
        if (string.Equals(state.RecentPresets.FirstOrDefault(), filename, StringComparison.OrdinalIgnoreCase)) return;
        var ordered = state.RecentPresets.Where(previous => !string.Equals(previous, filename, StringComparison.OrdinalIgnoreCase))
            .Prepend(filename).Take(RecentLimit).ToArray();
        Save(state with { RecentPresets = ordered });
    }

    private void Save(Data next)
    {
        if (!writable) throw new InvalidOperationException("The existing stock favourites and recent presets could not be read.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PanelFiles.WriteCompactPreset(path, next);
        state = next;
    }

    private static bool IsPresetPath(string? value) =>
        value is { Length: > 0 and <= 4096 } && Path.IsPathFullyQualified(value) && value.EndsWith(".spektra.json", StringComparison.OrdinalIgnoreCase);

    private static bool ValidIds(string[]? ids) =>
        ids is { Length: <= 128 } && ids.All(ValidId) && ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;

    private static bool ValidId(string? id) =>
        id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
