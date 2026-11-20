using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public static partial class StartupHook
{
    private static async Task TestCalibratedChart(Assembly assembly, object panel, object spatial, object window, string chart)
    {
        var directory = Path.Combine(folder, "Calibration");
        Check(Path.GetDirectoryName(chart) == directory && File.Exists(Path.Combine(directory, "reference.json")),
            "native calibration imports a private copy of the chart");
        Call(spatial, "SetEnabled", false);
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        var document = Field(panel, "document")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Core");
        var importerType = core.GetType("P1.C1.Core.Import.Plugins.ImageCatalogImporter", true)!;
        var importer = Activator.CreateInstance(importerType, nonPublic: true)!;
        var result = Call(importer, "ImportImage", chart, document, null, false)!;
        var status = Field(result, "Item1")?.ToString();
        if (status != "Ok" || Field(result, "Item2") is null)
            throw new InvalidOperationException($"Capture One's actual catalog importer rejected the Bayer DNG ({status}).");
        Check(true, "the native catalog importer accepted the documented Bayer DNG");
        var image = Field(result, "Item2")!;
        var variantType = core.GetType("P1.C1.Core.ICoreVariant", true)!;
        var provider = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
        var activeCollection = provider.GetProperty("PrimarySelectedCollection")!;
        var collectionVariants = core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!
            .GetMethod("GetAllFilteredAndVisibleVariants")!;
        var documentType = core.GetType("P1.C1.Core.ModelEntities.ICaptureOneDocument", true)!;
        var createVariant = documentType.GetMethod("CreateVariantForImageIfNoVariantsInserted")!;
        var imageType = createVariant.GetParameters()[0].ParameterType;
        createVariant.Invoke(document, new[] { image });
        documentType.GetMethod("PersistImageAfterImport")!.Invoke(document, new[] { image });
        var added = Array.CreateInstance(imageType, 1);
        added.SetValue(image, 0);
        var catalogType = core.GetType("P1.C1.Core.ModelEntities.ICatalogDocument", true)!;
        catalogType.GetMethod("ImagesAddedToTheCatalog")!.Invoke(document, new object[] { added });
        var collection = catalogType.GetProperty("AllImagesCollection")!.GetValue(document)!;
        var listType = core.GetType("P1.C1.Core.ModelEntities.ICollection", true)!;
        using var contents = (IDisposable)listType.GetMethod("RequestPopulateContents")!.Invoke(collection, null)!;
        provider.GetMethod("SelectOnly", new[] { activeCollection.PropertyType })!.Invoke(document, new[] { collection });
        var imported = ((IEnumerable)imageType.GetProperty("Variants")!.GetValue(image)!).Cast<object>().Single();
        var uuid = variantType.GetProperty("UUID")!;
        var importedId = uuid.GetValue(imported);
        bool HasImported() => ((IEnumerable)collectionVariants.Invoke(collection, null)!).Cast<object>().Any(variant => Equals(uuid.GetValue(variant), importedId));
        var started = Stopwatch.GetTimestamp();
        while (!HasImported() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(18))
        {
            await Task.Delay(100);
        }
        Check(HasImported(), "the actual All Images collection contains the exact imported DNG variant");
        var selection = provider.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
        var targetSelection = core.GetType("P1.C1.Core.Selection.ISelection", true)!.GetProperty("SelectedVariants")!.GetValue(selection)!;
        var select = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!.GetMethod("SelectAndSetAsPrimary")!;
        select.Invoke(targetSelection, new[] { imported, (object)true });
        Call(panel, "SyncSelection");
        Call(spatial, "SetEnabled", false);
        var target = assembly.GetType("HostProfile", true)!.GetMethod("CaptureTarget", Flags)!.Invoke(null, new[] { window })!;
        Check(Equals(Prop(target, "SourcePath"), chart), "native development selects the imported calibration original");
        var capture = assembly.GetType("HostImageSource", true)!.GetMethod("Capture", Flags)!;
        var linear = Enum.Parse(assembly.GetType("PreviewInputSpace", true)!, "LinearRec2020");
        var filmCurve = variantType.GetProperty("FilmCurve")!;
        var defaultCurve = filmCurve.GetValue(imported);
        var app = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "CaptureOne");
        var program = app.GetType("P1.C1.Program", true)!;
        var profileLibrary = program.GetProperty("ColorProfileLibrary", Flags)!.GetValue(null)!;
        var rawCurve = Call(profileLibrary, "GetFilmCurve", "GenericDngFile-Linear Response.fcrv");
        if (rawCurve is null) throw new InvalidOperationException("The native Generic DNG Linear Response curve is unavailable.");
        var exposure = variantType.GetProperty("Exposure")!;
        var oldExposure = exposure.GetValue(imported)!;
        var frames = new List<object>();
        async Task Develop(string name)
        {
            var request = capture.Invoke(null, new[] { window, target, linear, false })!;
            var frame = (await Task.Run(() => Call(request, "RenderScaledPreview", 768, CancellationToken.None)))!;
            var width = (int)Prop(frame, "Width")!;
            var height = (int)Prop(frame, "Height")!;
            var bytes = (byte[])Prop(frame, "FloatBytes")!;
            Check(width == 768 && height == 512 && Prop(frame, "BitDepth") is 16 && bytes.Length == width * height * 16,
                $"calibration {name} produces the expected geometry and native RGB16 data");
            var file = name + ".f32";
            File.WriteAllBytes(Path.Combine(directory, file), bytes);
            frames.Add(new { name, file, width, height, profile = Prop(Prop(frame, "Description")!, "ProfileSha256"),
                exposure = Convert.ToDouble(exposure.GetValue(imported), CultureInfo.InvariantCulture),
                curve = filmCurve.GetValue(imported) is { } curve ? Prop(curve, "FileName") : null });
        }
        try
        {
            var plusOne = Convert.ChangeType(Convert.ToDouble(oldExposure) + 1, oldExposure.GetType(), CultureInfo.InvariantCulture);
            await Develop("native-default");
            exposure.SetValue(imported, plusOne);
            await Develop("native-plus-one-ev");
            exposure.SetValue(imported, oldExposure);
            filmCurve.SetValue(imported, rawCurve);
            Check(Equals(filmCurve.GetValue(imported), rawCurve), "the vendor's native Linear Response curve was selected on the chart");
            await Develop("native-linear-response");
            exposure.SetValue(imported, plusOne);
            await Develop("native-linear-plus-one-ev");
        }
        finally
        {
            exposure.SetValue(imported, oldExposure);
            filmCurve.SetValue(imported, defaultCurve);
        }
        File.WriteAllText(Path.Combine(directory, "developed.json"), JsonSerializer.Serialize(new
        {
            stage = "Capture One developed RGB",
            workingSpace = "Linear Rec.2020",
            defaultCurve = defaultCurve is not null ? Prop(defaultCurve, "FileName") : null,
            frames,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} licensed calibration assertions");
    }
}
