using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public static partial class StartupHook
{
    private sealed record ColorProbe(string Source, string InputEncoding, int Width, int Height, string NativePixels, string FilmPixels);

    private static async Task RunColorMatrix(Assembly assembly, object panel, object window, object spatial)
    {
        var directory = Path.Combine(folder, "color-matrix");
        Directory.CreateDirectory(directory);
        var matrix = new List<ColorProbe>();
        var document = Field(panel, "document")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Core");
        var provider = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
        var collection = provider.GetProperty("PrimarySelectedCollection")!.GetValue(document)!;
        var variants = ((IEnumerable)core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!
            .GetMethod("GetAllFilteredAndVisibleVariants")!.Invoke(collection, null)!).Cast<object>().ToArray();
        var selection = provider.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
        var targetSelection = core.GetType("P1.C1.Core.Selection.ISelection", true)!.GetProperty("SelectedVariants")!.GetValue(selection)!;
        var select = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!.GetMethod("SelectAndSetAsPrimary")!;
        var targetMethod = assembly.GetType("HostProfile")!.GetMethod("CaptureTarget", Flags)!;
        var capture = assembly.GetType("HostImageSource")!.GetMethod("Capture", Flags)!;
        var colorEnum = assembly.GetType("PreviewInputSpace")!;
        var workerType = assembly.GetType("OpticalRenderer")!;
        var remaining = new HashSet<string>(new[] { ".CR3", ".RAF", ".NEF", ".DNG" }, StringComparer.OrdinalIgnoreCase);
        var encodings = new[] { ("Srgb", "srgb"), ("AdobeRgb", "adobe_rgb"), ("ProPhotoRgb", "prophoto"), ("LinearRec2020", "linear_rec2020") };
        Call(spatial, "SetEnabled", false);
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        var renderer = Activator.CreateInstance(workerType, Flags, null, new object[] { false, true }, null)!;
        await using var worker = (IAsyncDisposable)renderer;
        foreach (var item in variants)
        {
            select.Invoke(targetSelection, new[] { item, (object)false });
            Call(panel, "SyncSelection");
            Call(spatial, "SetEnabled", false);
            var target = targetMethod.Invoke(null, new[] { window })!;
            var source = Path.GetFileName((string)Prop(target, "SourcePath")!);
            if (!remaining.Remove(Path.GetExtension(source)))
                continue;
            (int Width, int Height)? dimensions = null;
            var look = Call(panel, "CurrentSettings")!;
            foreach (var (color, encoding) in encodings)
            {
                var space = Enum.Parse(colorEnum, color);
                var snapshot = capture.Invoke(null, new[] { window, target, space, false })!;
                var frame = (await Task.Run(() => Call(snapshot, "RenderScaledPreview", 480, CancellationToken.None)))!;
                var width = (int)Prop(frame, "Width")!;
                var height = (int)Prop(frame, "Height")!;
                var bytes = (byte[])Prop(frame, "FloatBytes")!;
                Check(Prop(frame, "BitDepth") is 16 && Math.Max(width, height) == 480
                    && bytes.LongLength == width * height * 16L && (dimensions is null || dimensions == (width, height)),
                    $"{source} develops as native 16-bit RGBA32F in {encoding} on identical geometry");
                dimensions = (width, height);
                var options = (string[])Call(spatial, "RendererOptions", look, encoding, "linear_rec709", false, false)!;
                var film = await (Task<byte[]>)Call(renderer, "RenderAsync", width, height, bytes, options, CancellationToken.None, false)!;
                Check(ValidPixels(bytes, film), $"{source} {encoding} produces a finite, distinct Rec.709 film result with unchanged alpha");
                var rawFile = source + "-" + encoding + ".f32";
                var filmFile = source + "-" + encoding + "-rec709.f32";
                File.WriteAllBytes(Path.Combine(directory, rawFile), bytes);
                File.WriteAllBytes(Path.Combine(directory, filmFile), film);
                matrix.Add(new ColorProbe(source, encoding, width, height, rawFile, filmFile));
            }
        }
        Check(remaining.Count == 0 && matrix.Count == 16, "all four camera RAW formats exercised with all four native working profiles");
        File.WriteAllText(Path.Combine(directory, "matrix.json"), JsonSerializer.Serialize(matrix, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool ValidPixels(byte[] native, byte[] film)
    {
        if (native.Length != film.Length) return false;
        var a = MemoryMarshal.Cast<byte, float>(native.AsSpan());
        var b = MemoryMarshal.Cast<byte, float>(film.AsSpan());
        var changed = false;
        for (var index = 0; index < a.Length; index++)
        {
            if (!float.IsFinite(a[index]) || !float.IsFinite(b[index])) return false;
            if (index % 4 == 3 && a[index] != b[index]) return false;
            changed |= a[index] != b[index];
        }
        return changed;
    }
}
