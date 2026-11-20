using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

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
            var description = Prop(Prop(frame, "Description")!, "Source")!;
            frames.Add(new { name, file, width, height, profile = Prop(description, "ProfileSha256"),
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
        if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_ORIGINAL_AVAILABILITY_PROBE") == "1")
            await ProbeOriginalAvailability(assembly, panel, spatial, window, chart, variantType, imported);
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} licensed calibration assertions");
    }

    private static async Task ProbeOriginalAvailability(Assembly assembly, object panel, object spatial, object window,
        string chart, Type variantType, object imported)
    {
        var sourcePath = Path.GetFullPath(chart);
        Check(Path.GetDirectoryName(sourcePath) == Path.Combine(Path.GetFullPath(folder), "Calibration")
            && (File.GetAttributes(sourcePath) & FileAttributes.ReparsePoint) == 0,
            "the temporarily disconnected original is a regular file in the private calibration fixture");
        var hash = SHA256.HashData(File.ReadAllBytes(sourcePath));
        var offlinePath = sourcePath + ".temporarily-offline";
        var picker = (ComboBox)Field(spatial, "previewSize")!;
        picker.SelectedItem = picker.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "720");
        ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
        Call(spatial, "SetEnabled", true);
        var viewer = Field(spatial, "viewer")!;
        var coreImage = variantType.GetProperty("CoreImage")!;
        var native = coreImage.GetValue(imported)!;
        var available = coreImage.PropertyType.GetInterfaces().Prepend(coreImage.PropertyType)
            .Select(type => type.GetProperty("IsFileAvailable")).First(property => property is not null)!;
        bool Painted() => Field(spatial, "frameCurrent") is true && Prop(viewer, "HasPainted") is true;
        var trace = new List<object>();
        object Snapshot(string state) => new
        {
            state, fileExists = File.Exists(sourcePath), nativeAvailable = available.GetValue(native),
            current = Field(spatial, "frameCurrent"), painted = Prop(viewer, "HasPainted"),
            status = ((TextBlock)Field(spatial, "status")!).Text,
        };
        var started = Stopwatch.GetTimestamp();
        while (!Painted())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(18))
                throw new TimeoutException("The private original did not paint before the offline availability probe.");
            await Task.Delay(75);
        }
        var initial = Field(spatial, "source");
        var initialLook = Call(panel, "CaptureLook")!;
        var initialState = JsonSerializer.Serialize(initialLook, initialLook.GetType());
        var target = assembly.GetType("HostProfile", true)!.GetMethod("CaptureTarget", Flags)!.Invoke(null, new[] { window })!;
        var host = assembly.GetType("HostImageSource", true)!;
        var capture = host.GetMethod("Capture", Flags)!;
        var encoding = Enum.Parse(assembly.GetType("PreviewInputSpace", true)!, "Srgb");
        trace.Add(Snapshot("online"));
        var moved = false;
        var suspended = false;
        var offlineStarted = Stopwatch.GetTimestamp();
        double? offlineMs = null;
        try
        {
            File.Move(sourcePath, offlinePath);
            moved = true;
            for (var sample = 0; sample < 12; sample++)
            {
                await Task.Delay(150);
                trace.Add(Snapshot("offline"));
                if (available.GetValue(native) is false && Field(spatial, "source") is null && !Painted()
                    && Field(spatial, "viewerFrame") is null && !((Button)Field(spatial, "savePreview")!).IsEnabled
                    && ((TextBlock)Field(spatial, "status")!).Text.Contains("Reconnect the original", StringComparison.Ordinal))
                {
                    suspended = true;
                    offlineMs ??= Stopwatch.GetElapsedTime(offlineStarted).TotalMilliseconds;
                }
            }
            try
            {
                capture.Invoke(null, new[] { window, target, encoding, true });
                throw new InvalidDataException("Native recipe export accepted a disconnected original.");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException failure
                && failure.Message.Contains("cannot develop", StringComparison.Ordinal))
            {
                Check(true, "native recipe export refuses to start from a disconnected original");
            }
        }
        catch (IOException error) when (!moved)
        {
            trace.Add(new { state = "move-failed", error = error.Message });
        }
        finally
        {
            if (File.Exists(offlinePath)) File.Move(offlinePath, sourcePath);
            Check(SHA256.HashData(File.ReadAllBytes(sourcePath)).SequenceEqual(hash), "the private source was restored byte for byte");
        }
        var restart = Stopwatch.GetTimestamp();
        double? restoredMs = null;
        while (Stopwatch.GetElapsedTime(restart) < TimeSpan.FromSeconds(14))
        {
            await Task.Delay(150);
            trace.Add(Snapshot("reconnected"));
            if (available.GetValue(native) is true && Painted() && Field(spatial, "source") is { } current
                && !ReferenceEquals(initial, current) && Prop(current, "BitDepth") is 16)
            {
                restoredMs = Stopwatch.GetElapsedTime(restart).TotalMilliseconds;
                break;
            }
        }
        File.WriteAllText(Path.Combine(folder, "original-availability-probe.json"),
            JsonSerializer.Serialize(new { offlineMs, restoredMs, trace }, new JsonSerializerOptions { WriteIndented = true }));
        Check(moved && suspended, "missing private RAW clears the old overlay and Save Preview with an actionable native status");
        Check(restoredMs is not null, "the reconnected original automatically repaints from a new Capture One RGB16 source");
        var finalLook = Call(panel, "CaptureLook")!;
        Check(JsonSerializer.Serialize(finalLook, finalLook.GetType()) == initialState,
            "disconnecting and restoring an original retains its exact film look");
    }
}
