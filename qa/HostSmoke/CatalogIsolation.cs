using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

public static partial class StartupHook
{
    private sealed record IsolationProbe(string Catalog, string DocumentType, Guid DocumentId, string CatalogKey, Guid Variant,
        string Source, string SourceSha256, string LookPath, double Exposure, double PreviewMean, double ExportMean);

    private static async Task TestCatalogIsolation(Assembly assembly, object panel, object spatial, object window,
        object store, Guid variant, string catalog)
    {
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var catalogName = phase[0];
        var restart = phase.EndsWith("-restart", StringComparison.Ordinal);
        var expected = catalogName == 'A' ? -1.5 : 1.5;
        var lookPath = (string)Call(store, "GetPath", variant)!;
        var key = (string)Prop(store, "CatalogIdentity")!;
        Check(Path.GetDirectoryName(catalog) == Path.Combine(folder, catalogName.ToString())
            && lookPath == Path.Combine(folder, "Variant looks", key, variant.ToString("N") + ".json"),
            "native clone and its look are in the expected independent catalog scope");
        var document = Field(panel, "document")!;
        var documentInterface = document.GetType().GetInterface("P1.C1.Core.ModelEntities.ICaptureOneDocument")!;
        var documentId = (Guid)documentInterface.GetProperty("UUID")!.GetValue(document)!;
        var target = assembly.GetType("HostProfile")!.GetMethod("CaptureTarget", Flags)!.Invoke(null, new[] { window })!;
        var source = (string)Prop(target, "SourcePath")!;
        Check(Equals(Prop(target, "VariantId"), variant) && File.Exists(source), "native export target uses the selected clone's RAW variant");
        string SourceHash()
        {
            using var stream = File.OpenRead(source);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        var originalSource = SourceHash();
        var slider = Field(panel, "exposure")!;
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        if (!restart)
        {
            Check(Call(store, "Read", variant) is null && !File.Exists(lookPath),
                "opening a fresh cloned catalog does not read the identically named variant's other look");
            toggle.IsChecked = true;
            slider.GetType().GetProperty("Value", Flags)!.SetValue(slider, expected);
        }
        var saved = Call(store, "Read", variant)!;
        Check(File.Exists(lookPath) && Prop(Prop(saved, "Look")!, "Exposure") is double value && value == expected
            && Equals(Prop(slider, "Value"), expected) && toggle.IsChecked == true && Prop(saved, "Enabled") is true,
            "native slider, master and durable file agree on the clone's exposure");

        var wait = Stopwatch.StartNew();
        var viewer = Field(spatial, "viewer")!;
        BitmapSource? preview;
        while ((preview = Field(spatial, "renderedDisplay") as BitmapSource) is null
               || Field(spatial, "frameCurrent") is not true || Field(viewer, "attachment") is not { } attachment
               || Prop(viewer, "HasPainted") is not true
               || !ImageLayers((Visual)Prop(attachment, "Painter")!).Any(image => ReferenceEquals(image, preview)))
        {
            if (wait.Elapsed >= TimeSpan.FromSeconds(30)) throw new TimeoutException("No settled film drawing for the catalog clone.");
            await Task.Delay(100);
        }
        Check(preview.Format == PixelFormats.Bgra32 || preview.Format == PixelFormats.Pbgra32,
            "settled film drawing exposes its 32-bit display pixels");
        const int width = 32;
        var previewBytes = new byte[width * width * 4];
        preview.CopyPixels(new Int32Rect(preview.PixelWidth / 2, preview.PixelHeight / 2, width, width), previewBytes, width * 4, 0);
        var previewMean = previewBytes.Where((_, index) => index % 4 != 3).Average(code => (double)code) / 255;

        var output = Path.Combine(folder, phase + ".tif");
        var export = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", false, null,
            (Func<SaveFileDialog, Window?, string?>)((_, _) => output))!;
        Check(await export && File.Exists(output), "full-resolution film export from the active clone completed");
        using var input = File.OpenRead(output);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnDemand).Frames[0];
        Check(frame.Format == PixelFormats.Rgb48, "each cloned catalog uses the native 16-bit export route");
        var samples = new byte[width * width * 6];
        frame.CopyPixels(new Int32Rect(frame.PixelWidth / 2, frame.PixelHeight / 2, width, width), samples, width * 6, 0);
        var exportMean = Enumerable.Range(0, samples.Length / 2).Average(index => BitConverter.ToUInt16(samples, index * 2)) / 65535;
        Check(SourceHash() == originalSource, "the original RAW hash matches after preview and export");

        var probe = new IsolationProbe(catalog, document.GetType().FullName!, documentId, key, variant, source, originalSource,
            lookPath, expected, previewMean, exportMean);
        IsolationProbe? Read(string name)
        {
            var path = Path.Combine(folder, name + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<IsolationProbe>(File.ReadAllText(path)) : null;
        }
        if (Read((catalogName == 'A' ? 'B' : 'A') + "-first") is { } other)
        {
            Check(other.DocumentId == documentId && other.Variant == variant && other.Source == source && other.SourceSha256 == originalSource
                && Path.GetFileName(other.Catalog) == Path.GetFileName(catalog),
                "clones reuse the native document and variant UUIDs, filename, source bytes and database basename");
            Check(other.CatalogKey != key && other.LookPath != lookPath && File.Exists(other.LookPath),
                "identical fixture variants have disjoint durable paths in one shared plugin profile");
            Check(Math.Abs(other.PreviewMean - previewMean) >= .01 && Math.Abs(other.ExportMean - exportMean) >= .01,
                "the two cloned catalogs produce different measured film pixels in preview and export");
        }
        if (restart)
        {
            var first = Read(catalogName + "-first")!;
            Check(first.CatalogKey == key && first.LookPath == lookPath && first.Exposure == expected
                && Math.Abs(first.PreviewMean - previewMean) < .005 && Math.Abs(first.ExportMean - exportMean) < .005,
                "restart restores this clone's preview and export within measured native quantization tolerance");
        }
        File.WriteAllText(Path.Combine(folder, phase + ".json"), JsonSerializer.Serialize(probe, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native cloned-catalog assertions for {phase}");
    }
}
