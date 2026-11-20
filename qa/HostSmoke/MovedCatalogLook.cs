using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestMovedCatalogLook(object panel, object spatial, object store, Guid variant, string backup)
    {
        var path = (string)Call(store, "GetPath", variant)!;
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(backup + ".manifest.json"));
        var original = manifest.RootElement.GetProperty("files").EnumerateArray().Single(record =>
            record.GetProperty("path").GetString() == $"Variant looks/{variant:N}.json");
        var hash = original.GetProperty("sha256").GetString();
        double? expectedExposure = null;
        var latestPath = Path.Combine(folder, "latest-local-look.json");
        if (File.Exists(latestPath))
        {
            using var latest = JsonDocument.Parse(File.ReadAllBytes(latestPath));
            var latestHash = latest.RootElement.GetProperty("sha256").GetString();
            Check(!string.Equals(hash, latestHash, StringComparison.OrdinalIgnoreCase),
                "the disposable local edit differs from the look in the original backup");
            hash = latestHash;
            expectedExposure = latest.RootElement.GetProperty("exposure").GetDouble();
        }
        var bytes = File.ReadAllBytes(path);
        Check(string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Prop(store, "CatalogIdentity") as string, manifest.RootElement.GetProperty("catalogKey").GetString(),
                StringComparison.OrdinalIgnoreCase),
            "the selected variant has the verified look bytes at its relocated catalog key");
        using var restored = JsonDocument.Parse(bytes);
        var settings = restored.RootElement.GetProperty("Look");
        var exposure = settings.GetProperty("Exposure").GetDouble();
        var expectedFilm = settings.GetProperty("Film").GetString();
        Check(exposure != 0 && (expectedExposure is null || exposure == expectedExposure)
            && restored.RootElement.GetProperty("Enabled").GetBoolean(),
            "the fixture restores a nondefault enabled film look");
        Check(Equals(Prop(Field(panel, "exposure")!, "Value"), exposure) && Prop(panel, "IsSpektraExportActive") is true
            && ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked == true
            && Equals(Prop(((ComboBox)Field(panel, "film")!).SelectedItem, "Id"), expectedFilm),
            "native controls and the export bridge load the restored look without an edit");
        var viewer = Field(spatial, "viewer")!;
        var started = Stopwatch.GetTimestamp();
        while (Prop(viewer, "HasPainted") is not true || Field(spatial, "frameCurrent") is not true)
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(25))
                throw new TimeoutException("The moved catalog's Capture One Viewer did not paint the restored look.");
            await Task.Delay(80);
        }
        Check(Field(spatial, "source") is { } nativeFrame && Prop(nativeFrame, "BitDepth") is int depth && depth >= 16,
            "the real Viewer paints the moved catalog's saved look on Capture One high-depth development");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes),
            "loading and painting the restore does not rewrite the saved look");
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        File.WriteAllText(Path.Combine(folder, phase + ".moved-look.json"),
            JsonSerializer.Serialize(new { variant, exposure, expectedFilm, sha256 = hash, painted = true }));
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native moved-catalog checks");
    }
}
