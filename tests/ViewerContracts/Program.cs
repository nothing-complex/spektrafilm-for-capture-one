using System;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class HostImageSource
{
    internal sealed record Frame(int Width, int Height, byte[] FloatBytes, int BitDepth, FrameDescription? Description = null);
}

internal static class StartupHook
{
    internal static void Log(string message) => throw new InvalidOperationException(message);
}

internal static partial class Program
{
    private static int assertions;
    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new InvalidOperationException(message);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--benchmark-rgb16", var report])
        {
            BenchmarkRgb16(report);
            return;
        }
        if (args is ["--benchmark-export-pixels", var exportReport])
        {
            BenchmarkExportPixels(exportReport);
            return;
        }
        if (args is ["--large-encoding", var results])
        {
            Directory.CreateDirectory(results);
            TestLargeEncoding(results);
            Console.WriteLine($"PASS: {assertions} large 16-bit encoder checks.");
            return;
        }
        if (args is ["--cancel-file-encoding", var cancellationResults])
        {
            TestFileEncoderCancellation(cancellationResults);
            Console.WriteLine($"PASS: {assertions} native encoder cancellation checks.");
            return;
        }
        if (args is ["--psd-delivery", var psdResults])
        {
            Directory.CreateDirectory(psdResults);
            TestPsdDelivery(psdResults, writeReference: true);
            Console.WriteLine($"PASS: {assertions} PSD export assertions.");
            return;
        }
        if (args is ["--psd-from-test-tiff", var testTiff, var testPsd])
        {
            TestPsdFromTiff(testTiff, testPsd);
            Console.WriteLine($"PASS: {assertions} saved film TIFF to PSD comparisons.");
            return;
        }
        if (args is ["--hold-guarded-export-stage", var stagingDirectory])
        {
            HoldGuardedExportStage(stagingDirectory);
            return;
        }
        if (args is ["--hold-verified-export", var publicationDirectory, var point])
        {
            HoldVerifiedExport(publicationDirectory, point);
            return;
        }
        if (args is ["--hold-batch-approval", var batchDirectory, var batchPoint])
        {
            HoldBatchApproval(batchDirectory, batchPoint);
            return;
        }
        if (args is ["--display-profile-patches", var alternateIcc, var patchReport])
        {
            MeasureWpfDisplayPatches(alternateIcc, patchReport);
            return;
        }
        if (args.Length > 0)
        {
            RunContracts(Directory.CreateDirectory(Path.GetFullPath(args[0])).FullName);
        }
        else
        {
            var scratch = Directory.CreateTempSubdirectory("Spektrafilm-viewer-contracts-");
            try
            {
                RunContracts(scratch.FullName);
            }
            catch
            {
                Console.Error.WriteLine($"Viewer contract artifacts retained in {scratch.FullName}");
                throw;
            }
            scratch.Delete(recursive: true);
        }
        Console.WriteLine($"PASS: {assertions} assertions. Pure current-source probes only; no Capture One process used.");
    }

    private static void RunContracts(string folder)
    {
        TestGeometry();
        TestDpiAndCropGeometry();
        TestFloatingBounds();
        TestViewportQuality();
        TestPreviewCropModel();
        TestViewerHistogram();
        TestRgb16();
        TestFloatSource();
        TestConversionBudget();
        TestParallelExportPixels();
        TestExportWatermark();
        TestDraft();
        TestDraftPyramid();
        TestDevelopedFrameDescription();
        TestExport(folder);
        TestExportFilenameSuggestion(folder);
        TestRecipeDelivery();
        TestPsdDelivery(folder);
        TestExportFolderSuggestion(folder);
        TestTiffCompression(folder);
        TestExportProfiles(folder);
        TestExportMetadata(folder);
        TestExportResolution();
        TestExportJobJournal(folder);
        TestExportBatchJournal(folder);
        TestExportActivity();
        TestColorSampling();
        TestFractionalColorSampling();
        TestColorPrecision();
        TestLookStrengthDelivery();
        TestViewerDisplayBitmap();
        TestOutputProtection(folder);
        TestExportStorageFailures(folder);
        TestCrashOwnedExportStage(folder);
        TestInterruptedPublication(folder);
        TestInterruptedBatchApproval(folder);
        TestSectionPreferences(folder);
        TestScheduling().GetAwaiter().GetResult();
        TestNativeCallbackOwnership().GetAwaiter().GetResult();
        TestMemoryAdmission();
        TestExportFrameLimits();
    }

    private static void TestFloatingBounds()
    {
        var secondaryWorkArea = new Rectangle(-2160, -200, 2160, 1400);
        var docked = new Rectangle(-400, 100, 300, 600);
        Check(FloatingWindowBounds.Fit(docked, secondaryWorkArea) == docked, "valid negative-monitor placement stays put");
        Check(FloatingWindowBounds.Fit(new Rectangle(-20, -220, 380, 620), secondaryWorkArea) ==
            new Rectangle(-380, -200, 380, 620), "all four edges clamp to the selected monitor's work area");
        Check(FloatingWindowBounds.Fit(new Rectangle(-2800, 3000, 4000, 2000), secondaryWorkArea) == secondaryWorkArea,
            "an oversized offscreen tool shrinks into the work area");
        Check(FloatingWindowBounds.Fit(docked, Rectangle.Empty) == docked, "transient absence of a work area leaves placement undisturbed");
    }

    private static void TestRecipeDelivery()
    {
        const SpektraExport.Format tiff = SpektraExport.Format.Tiff;
        const SpektraExport.Format png = SpektraExport.Format.Png;
        const SpektraExport.Format psd = SpektraExport.Format.Psd;
        foreach (var (requested, bits, actual, expectedTitle, expectedNote, canSkipRecipeOutput) in new[]
        {
            ("Tiff", 16, tiff, "Save 16-bit TIFF, PNG or PSD", (string?)null, true),
            ("Tiff", 8, tiff, "Recipe requests 8-bit TIFF", "Recipe requested 8-bit TIFF. Saved 16-bit TIFF.", false),
            ("Png", 0, png, "Save 16-bit TIFF, PNG or PSD", null, true),
            ("Png", 0, tiff, "Save 16-bit TIFF, PNG or PSD", "Recipe requested PNG. Saved 16-bit TIFF.", true),
            ("Jpeg", 0, tiff, "Recipe requests JPEG", "Recipe requested JPEG. Saved 16-bit TIFF.", false),
            ("123", 0, png, "Recipe requests a different file format", "Recipe requested a different file format. Saved 16-bit PNG.", false),
            ("Psd", 16, psd, "Save 16-bit TIFF, PNG or PSD", null, true),
            ("Psd", 8, psd, "Recipe requests 8-bit PSD", "Recipe requested 8-bit PSD. Saved 16-bit PSD.", false),
            ("Psd", 16, tiff, "Save 16-bit TIFF, PNG or PSD", "Recipe requested 16-bit PSD. Saved 16-bit TIFF.", true),
        })
        {
            var delivery = new SpektraExport.RecipeDelivery(requested, bits, "Respect");
            var title = delivery.SaveTitle(3000, 2000, "ProPhoto RGB");
            Check(title.StartsWith(expectedTitle, StringComparison.Ordinal) && title.Contains("3000 × 2000 · ProPhoto RGB", StringComparison.Ordinal)
                && delivery.SavedNote(actual) == expectedNote
                && delivery.CanSkipRecipeOutput == canSkipRecipeOutput
                && delivery.Extension == (requested switch { "Png" => ".png", "Psd" => ".psd", _ => ".tif" }),
                $"{requested} {bits} recipe states the 16-bit delivery policy and actual saved format");
        }
        var cropPath = new SpektraExport.RecipeDelivery("Psd", 16, "AsPath");
        Check(cropPath.SaveTitle(3000, 2000, "sRGB").Contains("full image, crop path omitted", StringComparison.Ordinal)
            && cropPath.SavedNote(png) ==
                "Recipe requested 16-bit PSD. Saved 16-bit PNG. Saved the full image. The recipe’s crop path was omitted.",
            "a crop path substitution is disclosed before and after delivery");
        var direct = new SpektraExport.RecipeDelivery(null, 0, null);
        Check(direct.SavedNote(tiff) is null && direct.SaveTitle(0, 0, "sRGB").Contains("(sRGB)", StringComparison.Ordinal),
            "a direct panel save has no native recipe substitution");
        var optionalEffects = new SpektraExport.RecipeDelivery("Tiff", 16, "Respect",
            SpektraExport.RecipeOmission.Watermark | SpektraExport.RecipeOmission.Sharpening);
        Check(optionalEffects.SaveTitle(3000, 2000, "sRGB").Contains("omits recipe watermark, recipe sharpening mode", StringComparison.Ordinal)
            && optionalEffects.SavedNote(tiff) == "The export omitted recipe watermark, recipe sharpening mode.",
            "a matching output format still discloses active recipe pixel effects in Save and completed history");
        var layered = cropPath with { Omissions = SpektraExport.RecipeOmission.PsdLayers };
        Check(layered.SaveTitle(3000, 2000, "sRGB").Contains("crop path omitted · omits PSD layers", StringComparison.Ordinal)
            && layered.SavedNote(png)?.EndsWith("The export omitted PSD layers.", StringComparison.Ordinal) == true,
            "PSD layer disclosures accompany format and crop-path substitutions");
        var batch = layered with { Omissions = SpektraExport.RecipeOmission.PsdLayers | SpektraExport.RecipeOmission.Watermark };
        Check(batch.BatchTitle(2, skipExisting: false)
            == "Export 2 images as 16-bit PSD · Full image, unavailable crop paths omitted · Omits recipe watermark, PSD layers",
            "batch approval names the actual format, crop-path policy and all requested omissions before saving");
        var editable = cropPath with { HasPsdCropPath = true };
        Check(editable.SaveTitle(3000, 2000, "sRGB").Contains("editable crop path in PSD", StringComparison.Ordinal)
            && editable.SavedNote(psd) == "Saved the full image with the editable crop path."
            && editable.SavedNote(png) == cropPath.SavedNote(png)
            && editable.BatchTitle(2, skipExisting: false).Contains("editable crop paths", StringComparison.Ordinal),
            "approval and receipts distinguish a retained PSD path from an omitted path in alternate formats");
        var guides = new SpektraExport.RecipeDelivery("Psd", 16, "Ignore", HasPsdGuides: true);
        Check(guides.SaveTitle(3000, 2000, "sRGB").Contains("editable guides in PSD", StringComparison.Ordinal)
            && guides.SavedNote(psd) is null && guides.SavedNote(tiff)?.EndsWith("The export omitted recipe guides.", StringComparison.Ordinal) == true
            && (guides with { Format = "Png" }).BatchTitle(2, false).Contains("Omits recipe guides", StringComparison.Ordinal),
            "PSD guide disclosure follows the delivered file and stays specific to recipe guides");
        var unavailableGuides = guides with { HasPsdGuides = false, Omissions = SpektraExport.RecipeOmission.Guides };
        Check(unavailableGuides.SaveTitle(3000, 2000, "sRGB").Contains("omits recipe guides", StringComparison.Ordinal)
            && unavailableGuides.SavedNote(psd) == "The export omitted recipe guides.",
            "unavailable native guides are disclosed even in PSD delivery");
    }

    private static void TestExportFilenameSuggestion(string folder)
    {
        const string source = "raw.dng";
        Check(SpektraExport.SuggestedOutputName("Film proof 日 1.2", source, ".tif") == "Film proof 日 1.2.tif",
            "native naming preview preserves a Unicode and dotted film output stem");
        Check(SpektraExport.SuggestedOutputName("Film proof. ", source, ".png") == "Film proof.png",
            "native naming preview uses the selected extension after trimming Windows trailing characters");
        foreach (var unsafeName in new[] { null, "", "..\\outside", "C:\\outside", "image:stream", new string('z', 201) })
            Check(SpektraExport.SuggestedOutputName(unsafeName, source, ".tif") == "raw-Spektrafilm.tif"
                && SpektraExport.SuggestedOutputName(unsafeName, source, ".png") == "raw-Spektrafilm.png",
                "an unavailable or unusable recipe suggestion keeps the source-based name and chosen extension");
        var collisions = Directory.CreateDirectory(Path.Combine(folder, "recipe-name-collisions")).FullName;
        const string stem = "Film proof 日 1.2";
        File.WriteAllText(Path.Combine(collisions, stem + ".tif"), "previous export");
        Directory.CreateDirectory(Path.Combine(collisions, stem + " 1.tif"));
        File.WriteAllText(Path.Combine(collisions, stem + " 3.tif"), "another export");
        Check(SpektraExport.SuggestedOutputName(stem, source, ".tif", collisions, addSuffix: true) == stem + " 2.tif",
            "the native Add Suffix hint skips the occupied file and folder and uses the first available space-number suffix");
        Check(SpektraExport.SuggestedOutputName(stem, source, ".png", collisions, addSuffix: true) == stem + ".png"
            && SpektraExport.SuggestedOutputName(stem, source, ".tif", collisions, addSuffix: false) == stem + ".tif",
            "different extensions and other native collision policies retain their base suggestion");
        Check(File.ReadAllText(Path.Combine(collisions, stem + ".tif")) == "previous export"
            && Directory.GetFileSystemEntries(collisions).Length == 3, "suggesting a collision suffix leaves all destination entries untouched");
    }

    private static void TestExportFolderSuggestion(string folder)
    {
        var root = Directory.CreateDirectory(Path.Combine(folder, "recipe-root")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(root, "Prints", "Proofs")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(folder, "recipe-root-other")).FullName;
        Check(SpektraExport.ExistingRecipeLocation(root, Path.Combine("Prints", "Proofs"), true) == (nested, nested),
            "the recipe suggests an existing nested output folder");
        Check(SpektraExport.ExistingRecipeLocation(root, "Uncreated", true) == (root, null) && !Directory.Exists(Path.Combine(root, "Uncreated")),
            "an absent recipe subfolder leaves Save at the root without creating folders");
        Check(SpektraExport.ExistingRecipeLocation(root, Path.Combine("..", "recipe-root-other"), true) == (root, null)
            && SpektraExport.ExistingRecipeLocation(root, outside, true) == (root, null),
            "a recipe subfolder cannot redirect the dialog away from its output root");
        Check(SpektraExport.ExistingRecipeLocation(root, null, true) == (root, null)
            && SpektraExport.ExistingRecipeLocation(root, null, false) == (root, root),
            "an unresolved subfolder is a Save hint while a recipe with no subfolder uses the exact root");
        Check(SpektraExport.ExistingRecipeLocation("relative-root", "Prints", true) == (null, null),
            "a relative recipe root is left to Save's ordinary location selection");
    }

    private static void TestExportActivity()
    {
        var activity = new ExportActivity();
        using var first = new ExportOperation(Guid.NewGuid());
        using var second = new ExportOperation(Guid.NewGuid());
        Check(activity.Active is null, "a new native document has no active export");
        activity.Begin(first);
        first.IsAccepted = true;
        activity.Report(first, "Developing");
        Action observed = () => Check(activity.Active == first && activity.Message == "Encoding",
            "a replacement observer receives the existing accepted operation and its current stage");
        activity.Changed += observed;
        activity.Report(first, "Encoding");
        activity.Changed -= observed;
        activity.End(first);
        activity.Begin(second);
        activity.Report(second, "Rendering");
        activity.Report(first, "Late result");
        activity.End(first);
        Check(activity.Active == second && activity.Message == "Rendering", "late callbacks from the old job cannot replace the current state");
        activity.Active!.Cancel();
        Check(second.Token.IsCancellationRequested && !first.Token.IsCancellationRequested,
            "canceling the active operation cannot cancel a previous export");
        activity.End(second);
        Check(activity.Active is null, "a terminal export releases the native document's live slot");
        var preparation = activity.BeginBatch(CancellationToken.None);
        activity.Begin(first);
        activity.StopBatch();
        Check(activity.BatchActive && activity.BatchStopped && preparation.IsCancellationRequested && !first.Token.IsCancellationRequested,
            "requesting a batch stop cancels pending preparation and leaves the accepted image running");
        activity.End(first);
        Check(activity.Active is null && activity.BatchActive, "the batch owns output between consecutive images");
        activity.EndBatch("Finished batch");
        Check(!activity.BatchActive && activity.Message == "Finished batch", "batch completion publishes one visible summary");
        using var waiting = new ExportOperation(Guid.NewGuid());
        using var lifetime = new CancellationTokenSource();
        var next = activity.BeginBatch(lifetime.Token);
        Check(!next.IsCancellationRequested && !activity.BatchStopped, "the next batch has an independent preparation token");
        activity.Begin(waiting);
        activity.StopBatch();
        Check(next.IsCancellationRequested && waiting.Token.IsCancellationRequested,
            "Stop cancels a selected item that has yet to accept an output");
        activity.End(waiting);
        activity.EndBatch("Stopped");
        var closing = activity.BeginBatch(lifetime.Token);
        lifetime.Cancel();
        Check(closing.IsCancellationRequested, "closing the native document cancels batch preparation");
        activity.EndBatch("Closed");
    }

    private static void TestExportBatchJournal(string folder)
    {
        var directory = Path.Combine(folder, "batch-journal");
        var writer = new ExportJobJournal(directory, _ => { });
        var original = Path.Combine(folder, "source.raw");
        var approved = Path.Combine(folder, "approved");
        var entries = new[] { "first", "second", "third" }.Select(name =>
            new ExportBatchEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), original,
                Path.Combine(approved, name + ".tif"), new(true, new string('A', 64), new string('B', 64), new string('C', 64)))).ToArray();
        var plan = ExportBatchPlan.Create(approved, entries);
        writer.SaveBatch(plan);
        var first = writer.BeginBatchItem(plan, entries[0]);
        writer.Report(first, ExportStage.Completed);
        var second = writer.BeginBatchItem(plan, entries[1]);
        writer.Report(second, ExportStage.Encoding);
        var reader = new ExportJobJournal(directory, _ => { });
        var recovered = reader.ReadPendingBatch();
        Check(recovered is not null && recovered.Id == plan.Id && recovered.Entries.SequenceEqual(entries),
            "an atomic batch plan keeps all approved destinations and their verification data after reopen");
        Check(reader.ReadBatchItem(recovered!, entries[0])?.Stage == ExportStage.Completed
            && reader.ReadBatchItem(recovered!, entries[1])?.Stage == ExportStage.Encoding
            && reader.ReadBatchItem(recovered!, entries[2]) is null, "a reopened batch distinguishes completed, interrupted and pending jobs");
        var restarted = reader.BeginBatchItem(recovered!, entries[1]);
        Check(restarted.JobId == second.JobId && restarted.Destination == second.Destination && restarted.Stage == ExportStage.Accepted,
            "an interrupted queue entry restarts with its original ID and destination");
        reader.SaveBatch(recovered! with { Closed = true });
        Check(reader.ReadPendingBatch() is null && reader.ReadBatchItem(plan, entries[0])?.Stage == ExportStage.Completed,
            "closing the pending queue preserves its individual job receipts");

        var skips = entries.Take(2).Select(entry => entry with
            { Destination = entries[0].Destination, Retry = null, SkipExisting = true }).ToArray();
        var skipPlan = ExportBatchPlan.Create(approved, skips);
        writer.SaveBatch(skipPlan);
        Check(reader.ReadPendingBatch()?.Entries.SequenceEqual(skips) == true,
            "read-only skips for the same file survive a plan reload with their separate job IDs");
        foreach (var entry in skips)
            writer.Report(writer.BeginBatchItem(skipPlan, entry), ExportStage.Skipped);
        Check(reader.ReadPendingBatch() is null && skips.All(entry => reader.ReadBatchItem(skipPlan, entry)?.Stage == ExportStage.Skipped),
            "every approved skip records its own terminal receipt");
        var conflicting = ExportBatchPlan.Create(approved, new[] { skips[0], skips[1] with { SkipExisting = false } });
        var rejected = false;
        try { writer.SaveBatch(conflicting); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "no writing entry can share a skipped destination in the same approved batch");
    }

    private static void TestExportJobJournal(string folder)
    {
        var directory = Path.Combine(folder, "Export jobs");
        var messages = new List<string>();
        var journal = new ExportJobJournal(directory, messages.Add);
        Check(journal.ReadLatest() is null && messages.Count == 0, "a missing export history is normal");
        var variant = Guid.NewGuid();
        var firstPath = Path.GetFullPath(Path.Combine(folder, "First.tif"));
        File.WriteAllText(firstPath, "existing output");
        var first = journal.Accept(variant, firstPath);
        var retry = new ExportRetryDescriptor(false, new string('A', 64), new string('B', 64), new string('C', 64));
        first = journal.RecordRetry(first, retry);
        first = journal.Report(first, ExportStage.DevelopingSource);
        first = journal.Report(first, ExportStage.RenderingFilm);
        var restarted = new ExportJobJournal(directory, messages.Add);
        Check(restarted.ReadLatest() == first && first is { Finished: false, Stage: ExportStage.RenderingFilm } && first.Retry == retry,
            "accepted variant, destination and in-progress stage survive a fresh journal reader");

        if (!SpinWait.SpinUntil(() => DateTime.UtcNow > first.AcceptedUtc, TimeSpan.FromSeconds(1)))
            throw new InvalidOperationException("The system clock did not advance for the export fixture.");
        var second = journal.AcceptRetry(variant, Path.GetFullPath(Path.Combine(folder, "Second.png")), first.JobId);
        first = journal.Report(first, ExportStage.Encoding, 2400, 1600);
        first = journal.Report(first, ExportStage.Completed);
        Check(restarted.ReadLatest() == second && second.RetryOf == first.JobId && first.Retry is null
            && Directory.GetFiles(directory, "*.json").Length == 2,
            "a late older completion cannot replace the receipt for a newer accepted export");
        second = journal.Report(second, ExportStage.Canceled);
        Check(restarted.ReadLatest() == second && second.Finished, "canceled export publishes its distinct terminal state");
        try
        {
            journal.Report(first, ExportStage.Canceled);
            throw new InvalidDataException("A terminal export accepted a new transition.");
        }
        catch (InvalidOperationException) { Check(true, "a terminal export cannot be overwritten by a subsequent cancellation"); }
        Check(File.ReadAllText(firstPath) == "existing output", "progress tracking does not modify its exported targets");

        var malformed = Path.Combine(directory, "z-invalid.json");
        File.WriteAllText(malformed, "{}");
        Check(restarted.ReadLatest() == second && File.Exists(malformed) && messages.Count == 1,
            "malformed history is preserved and the latest valid receipt is still used");
        var unavailable = new ExportJobJournal(Path.Combine(firstPath, "journal"), messages.Add);
        Check(unavailable.Accept(variant, firstPath).Stage == ExportStage.Accepted && messages.Count == 2,
            "an unavailable progress directory does not block a separately authorized export");
        var skipped = journal.Report(journal.Accept(variant, firstPath), ExportStage.Skipped);
        Check(restarted.ReadLatest() == skipped && skipped is { Finished: true, Width: 0, Height: 0 }
            && File.ReadAllText(firstPath) == "existing output",
            "a native Skip persists a terminal outcome without describing a newly rendered file");
        const string deliveryNote = "Recipe requested JPEG. Saved 16-bit TIFF.";
        var failedAlternative = journal.Report(journal.Accept(variant, firstPath), ExportStage.Failed, deliveryNote: deliveryNote);
        Check(failedAlternative.DeliveryNote is null, "failed delivery carries no saved-format note");
        var deliveredAlternative = journal.Report(journal.Accept(variant, firstPath), ExportStage.Completed, 2400, 1600, deliveryNote: deliveryNote);
        Check(restarted.ReadLatest() == deliveredAlternative && deliveredAlternative.DeliveryNote == deliveryNote,
            "confirmed recipe substitution is visible in export history after reopening");

        var sharedDirectory = Path.Combine(folder, "Contended export jobs");
        var errors = new ConcurrentQueue<string>();
        var writer = new ExportJobJournal(sharedDirectory, errors.Enqueue);
        using var stop = new CancellationTokenSource();
        using var reading = new CountdownEvent(4);
        var readers = Enumerable.Range(0, reading.InitialCount).Select(_ => Task.Run(() =>
        {
            var reader = new ExportJobJournal(sharedDirectory, errors.Enqueue);
            reading.Signal();
            while (!stop.IsCancellationRequested)
            {
                reader.ReadLatest();
                Thread.Yield();
            }
        })).ToArray();
        try
        {
            if (!reading.Wait(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("The concurrent export journal readers did not start.");
            for (var index = 0; index < 24; index++)
            {
                var receipt = writer.Accept(variant, firstPath);
                foreach (var stage in new[] { ExportStage.DevelopingSource, ExportStage.RenderingFilm, ExportStage.Encoding, ExportStage.Completed })
                {
                    receipt = writer.Report(receipt, stage);
                    Check(writer.ReadLatest() == receipt, $"concurrent readers preserve durable export stage {stage}");
                }
            }
        }
        finally
        {
            stop.Cancel();
            Task.WaitAll(readers);
        }
        Check(errors.IsEmpty, "another in-process journal reader never blocks replacing a Windows receipt: " + string.Join(", ", errors));
    }

    private static void TestViewerHistogram()
    {
        var pixels = new byte[]
        {
            0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 99, 70, 99, 0,
            0, 0, 0, 255, 255, 255, 255, 255, 177, 169, 156, 133, 255, 255, 255, 0,
        };
        var image = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        image.Freeze();
        var bins = ViewerHistogram.Read(image, CancellationToken.None);
        Check(bins.PixelCount == 6 && bins.HighlightCount == 4, "histogram counts visible pixels with a channel at the display maximum");
        Check(bins.Red[255] == 2 && bins.Green[255] == 2 && bins.Blue[255] == 2, "red, green and blue histograms follow the display code channel order");
        Check(bins.Red[156] == 1 && bins.Green[169] == 1 && bins.Blue[177] == 1 && bins.Green[70] == 0,
            "partially transparent colour is counted and fully transparent colour is excluded");
        Check(bins.Red.Sum() == 6 && bins.Green.Sum() == 6 && bins.Blue.Sum() == 6, "histogram totals match the exact visible sample count");
        Check(bins.HighlightMask is null, "an off-state clipping overlay allocates no mask");
        var marked = ViewerHistogram.Read(image, CancellationToken.None, markHighlights: true);
        var mask = marked.HighlightMask!;
        var maskBytes = new byte[2];
        mask.CopyPixels(maskBytes, 1, 0);
        Check(mask.IsFrozen && mask.Format == PixelFormats.Indexed1 && maskBytes.SequenceEqual(new byte[] { 0xe0, 0x40 }),
            "clipping mask uses one bit per pixel and excludes transparent or below-maximum patches");
        var overlay = new FormatConvertedBitmap(mask, PixelFormats.Bgra32, null, 0);
        var display = new byte[32];
        overlay.CopyPixels(display, 16, 0);
        Check(display[3] == 148 && display[15] == 0 && display[23] == 148 && display[31] == 0,
            "clipping mask exposes exactly the clipped pixels as translucent marks");

        var transform = new Matrix(-2, .25, .75, 3, 20, 10);
        var coordinates = new ViewerGeometry(new System.Windows.Rect(12, 8, 10, 15), transform, 8);
        Check(coordinates.TryMapPixel(transform.Transform(new System.Windows.Point(2.5, 1.5)), 4, 2, out var point)
            && point == (2, 1), "pixel inspection inverts the exact mirrored and sheared Viewer transform");
        Check(!coordinates.TryMapPixel(transform.Transform(new System.Windows.Point(.1, 2)), 4, 2, out _)
            && !coordinates.TryMapPixel(new System.Windows.Point(22, 15), 4, 2, out _)
            && !coordinates.TryMapPixel(new System.Windows.Point(double.NaN, 15), 4, 2, out _),
            "pixel inspection rejects right/bottom edges, clipped and non-finite coordinates");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            ViewerHistogram.Read(image, stop.Token);
            throw new InvalidOperationException("A stale histogram ignored cancellation.");
        }
        catch (OperationCanceledException) { Check(true, "stale histogram can be cancelled before scanning the shared display frame"); }
    }

    private static void TestPreviewCropModel()
    {
        const string crop = "\"crop\":{\"x\":3240,\"y\":1800,\"width\":4320,\"height\":2840},\n";
        const string settings = "\"lensDistortionAmount\":1,\"rotation\":0,\"keystone\":0,\"opaque\":nan";
        static string Model(int counter, string rectangle, string other) => "{\n\"syncProcessCounter\":" + counter
            + ",\n\"adjustmentLayer\":{\n" + rectangle + other + "\n}\n}";
        var plain = PreviewCropModel.Read(Model(6, "", settings), 6)!;
        var cropped = PreviewCropModel.Read(Model(7, crop, settings), 7)!;
        Check(plain.Crop is null && cropped.Crop is { X: 3240, Y: 1800, Width: 4320, Height: 2840 }
            && plain.PixelSettingsSha256 == cropped.PixelSettingsSha256,
            "the native rectangle can be inserted without changing uncropped pixel identity");
        const string reset = "\"crop\":{\"x\":0,\"y\":0,\"width\":0,\"height\":0},\n";
        Check(PreviewCropModel.Read(Model(8, reset, settings), 8) == plain,
            "Capture One's explicit zero crop has the same identity as an absent crop");
        Check(PreviewCropModel.Read(Model(8, reset.Replace("\"x\":0", "\"x\":10"), settings), 8) is null
            && PreviewCropModel.Read(Model(8, reset.Replace("\"width\":0", "\"width\":100"), settings), 8) is null,
            "a partially empty rectangle remains an unsupported crop representation");
        foreach (var edited in new[] { "\"lensDistortionAmount\"", "\"rotation\"", "\"keystone\"" })
        {
            var changedSettings = settings.Replace(edited + ":", edited + ":4", StringComparison.Ordinal);
            Check(PreviewCropModel.Read(Model(7, crop, changedSettings), 7) is { } changed
                && changed.PixelSettingsSha256 != plain.PixelSettingsSha256,
                "native crop plus a pixel-affecting geometry edit changes the identity");
        }
        Check(PreviewCropModel.Read(Model(7, crop, settings), 8) is null,
            "the crop model must describe the expected native counter");
        Check(PreviewCropModel.Read(Model(7, crop + crop, settings), 7) is null,
            "duplicate crop keys fail closed");
        Check(PreviewCropModel.Read(Model(7, crop.Replace("\"width\":4320", "\"angle\":4,\"width\":4320"), settings), 7) is null,
            "an unverified crop representation with an additional geometry field fails closed");
        Check(PreviewCropModel.Read("{\n" + crop + Model(7, "", settings)[2..], 7) is null
            && PreviewCropModel.Read(Model(7, crop, settings).Replace("adjustmentLayer", "otherLayer"), 7) is null
            && PreviewCropModel.Read("broken model", 7) is null,
            "an unknown crop scope or malformed native data fails closed");
        Check(PreviewCropModel.Read(Model(7, crop, settings.Replace("nan", "inf")), 7) is { } opaque
            && opaque.PixelSettingsSha256 != plain.PixelSettingsSha256,
            "an unrelated opaque native setting remains in the pixel checksum");
    }

    private static void TestViewportQuality()
    {
        var quality = new ViewportQuality();
        Check(quality.LongEdge == 1600 && quality.SizeId == "1600", "automatic quality starts at a bounded initial size");
        Check(!quality.Update(null, 1, null, 0), "missing geometry holds the initial size");
        Check(!quality.Update(3000, 1, null, 100) && !quality.Update(3000, 1, null, 349), "an upgrade requires sustained demand");
        Check(quality.Update(3000, 1, null, 350) && quality.LongEdge == 4096, "stable zoom raises the cap without native-size rendering");
        for (var i = 0; i < 100; i++)
            Check(!quality.Update(2870 + i % 2 * 20, 1, null, 500 + i * 10), "zoom jitter near the threshold keeps the current cap");
        Check(!quality.Update(2000, 1, null, 2000) && !quality.Update(2000, 1, null, 2799), "downgrade waits for the viewport to settle");
        Check(quality.Update(2000, 1, null, 2800) && quality.LongEdge == 2560, "a stable smaller viewport reduces source work");
        Check(quality.Update(4000, 1, 320 * 1024L * 1024, 2900) && quality.LongEdge == 1200, "low system memory immediately reduces automatic demand");
        Check(!quality.Update(4000, 1, 330 * 1024L * 1024, 3200) && !quality.Update(4000, 1, 330 * 1024L * 1024, 4000),
            "a small budget rebound does not trigger a new large allocation");
        Check(!quality.Update(4000, 1, 3 * 1024L * 1024 * 1024, 4300)
            && quality.Update(4000, 1, 3 * 1024L * 1024 * 1024, 4600) && quality.LongEdge == 4096,
            "sustained demand recovers after memory pressure clears");
        Check(!quality.Update(double.NaN, .75, null, 8000) && quality.LongEdge == 4096, "temporarily hidden or invalid geometry does not drop a usable frame");
        quality.Reset();
        Check(quality.LongEdge == 1600 && !quality.Update(3200, .75, null, 10000), "navigation resets demand without changing the preference");
        Check(quality.Update(3200, 1, null, 10001, firstFrame: true) && quality.LongEdge == 4096,
            "a drawable cold Viewer starts at the measured tier without a redundant settle pass");
        quality.Reset();
        Check(quality.Update(800, 1, null, 11000, firstFrame: true) && quality.LongEdge == 900,
            "a smaller drawable cold Viewer starts at its smaller tier");
        quality.Reset();
        Check(quality.Update(4000, 1, 320 * 1024L * 1024, 12000, firstFrame: true) && quality.LongEdge == 1200,
            "a drawable cold Viewer remains bounded by managed-memory headroom");
        quality.Reset();
        Check(!quality.Update(null, 1, null, 13000, firstFrame: true) && quality.LongEdge == 1600,
            "an unmeasurable cold Viewer holds the existing fallback");

        var points = new[] { new PointF(5, 9), new PointF(1005, 9), new PointF(1005, 759), new PointF(5, 759) };
        var panned = points.Select(point => new PointF(point.X - 400, point.Y + 20)).ToArray();
        var clip = new RectangleF(0, 0, 1500, 900);
        Check(ViewerGeometry.TryCreate(clip, points, 200, 150, out var centered)
            && ViewerGeometry.TryCreate(clip, panned, 200, 150, out var translated)
            && centered.ProjectedLongEdge == 1000 && centered.ProjectedLongEdge == translated.ProjectedLongEdge,
            "panning and clip translation alone preserve the estimated full image resolution");
        var zoomed = points.Select(point => new PointF(point.X * 1.5f, point.Y * 1.5f)).ToArray();
        Check(ViewerGeometry.TryCreate(clip, zoomed, 200, 150, out var enlarged) && enlarged.ProjectedLongEdge == 1500,
            "quality uses the native physical geometry after a zoom or display scaling change");
        Console.WriteLine("Viewer Auto quality: bounded size, physical geometry, hysteresis and memory pressure passed.");
    }

    private static void TestLookStrengthDelivery()
    {
        const double tolerance = 0.000003;
        var neutral = new[] { .25f, .25f, .25f };
        var bright = new[] { 1.5f, 1.5f, 1.5f };
        var mid = new SrgbDelivery(PreviewInputSpace.LinearRec2020, "linear_rec709", .5).GetSrgb(neutral, bright, 0);
        Check(Math.Abs(mid.Red - Encode(.625)) < tolerance && Math.Abs(mid.Green - Encode(.625)) < tolerance
            && Math.Abs(mid.Blue - Encode(.625)) < tolerance, "the film's linear 1.5 channel clips before mixing with linear .25");

        var chart = new[] { 1.25f, .65f, -.35f };
        foreach (var encoding in new[] { "srgb", "linear_rec709", "rec709_gamma24" })
        {
            var output = new[] { -.2f, .5f, 1.5f };
            var atHalf = new SrgbDelivery(PreviewInputSpace.Srgb, encoding, .5).GetSrgb(chart, output, 0);
            var midFilm = encoding switch { "linear_rec709" => .5, "rec709_gamma24" => Math.Pow(.5, 2.4), _ => Decode(.5) };
            Check(Math.Abs(atHalf.Red - Encode(.5)) < tolerance && Math.Abs(atHalf.Blue - Encode(.5)) < tolerance
                && Math.Abs(atHalf.Green - Encode((Decode(chart[1]) + midFilm) / 2)) < tolerance,
                "negative and saturated channels follow the same clipping policy in " + encoding);
            foreach (var space in Enum.GetValues<PreviewInputSpace>())
            {
                var atZero = new SrgbDelivery(space, encoding, 0).GetSrgb(chart, Array.Empty<float>(), 0);
                var barelyOn = new SrgbDelivery(space, encoding, 0.0000001).GetSrgb(chart, output, 0);
                var atOne = new SrgbDelivery(space, encoding, 1).GetSrgb(Array.Empty<float>(), output, 0);
                var almostFull = new SrgbDelivery(space, encoding, 0.9999999).GetSrgb(chart, output, 0);
                Check(Close(atZero, barelyOn) && Close(atOne, almostFull), "continuous strength endpoints for " + space + " and " + encoding);
                Check(atOne.Red == 0 && Math.Abs(atOne.Green - Encode(midFilm)) < tolerance && Math.Abs(atOne.Blue - 1) < tolerance,
                    "the full processed frame has the correct transfer for " + encoding);
            }
        }

        var invalid = new SrgbDelivery(PreviewInputSpace.Srgb, "srgb", .5);
        foreach (var nonfinite in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var damaged = new[] { .5f, nonfinite, .5f };
            Check(Rejects(() => invalid.GetSrgb(damaged, bright, 0)), "a non-finite source component is rejected");
            Check(Rejects(() => invalid.GetSrgb(neutral, damaged, 0)), "a non-finite processed component is rejected");
        }
        Console.WriteLine("Look strength: clip-before-blend policy, all source/output spaces, HDR highlights and endpoint continuity passed.");

        static double Encode(double linear) => linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
        static double Decode(double encoded) => encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
        static bool Close(SrgbDelivery.LinearBgr a, SrgbDelivery.LinearBgr b) => Math.Abs(a.Red - b.Red) < tolerance
            && Math.Abs(a.Green - b.Green) < tolerance && Math.Abs(a.Blue - b.Blue) < tolerance;
        static bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
        }
    }

    private static void TestSectionPreferences(string folder)
    {
        var path = Path.Combine(folder, "panel-preferences.json");
        PanelFiles.WritePreset(path, new { Version = 1, ViewerSize = "1600", DragSize = 720, DraftSynthesis = true });
        var messages = new List<string>();
        var store = new PanelPreferenceStore(path, messages.Add);
        var panel = new System.Windows.Controls.StackPanel();
        var parent = store.CreateSection("parent", "Parent", panel, 5);
        var child = store.CreateSection("child", "Child", new System.Windows.Controls.StackPanel(), 5);
        panel.Children.Add(child);
        Check(!parent.IsExpanded && !child.IsExpanded, "Legacy preferences changed default layout");
        parent.IsExpanded = true;
        child.IsExpanded = true;
        parent.IsExpanded = false;
        Check(store.Current.Sections?["parent"] == false && store.Current.Sections?["child"] == true,
            "Routed expansion/collapse changed a different section's saved state");
        store.Update(preferences => preferences with { FloatingScrollOffset = 314.25 });
        var reopened = new PanelPreferenceStore(path, messages.Add);
        Check(reopened.CreateSection("parent", "Localized parent", new System.Windows.Controls.StackPanel(), 5).IsExpanded == false,
            "Parent section did not restore independently of its title");
        Check(reopened.CreateSection("child", "Localized child", new System.Windows.Controls.StackPanel(), 5).IsExpanded,
            "Child section did not restore independently of its title");
        Check(reopened.Current.FloatingScrollOffset == 314.25 && reopened.Current.ViewerSize == "1600"
            && reopened.Current.DragSize == 720 && reopened.Current.DraftSynthesis, "Layout persistence changed quality preferences");
        Check(reopened.Current.FloatingLayout("1|Legacy") == new FloatingToolLayout(false, null, 314.25),
            "legacy floating preferences supply placement or scroll without opening a window");
        var floating = new FloatingToolLayout(true, new FloatingPlacement(20, 40, 370, 580), 42);
        var docked = new FloatingToolLayout(false, new FloatingPlacement(-260, 75, 420, 640), 102);
        var workspaceA = PanelPreferenceData.WorkspaceKey(1, "CustomWorkspace", "Color");
        var workspaceB = PanelPreferenceData.WorkspaceKey(1, "CustomWorkspace", "Retouch");
        Check(workspaceA != workspaceB, "custom workspaces with the same native internal name have distinct saved layouts");
        Check(PanelPreferenceData.WorkspaceKey(1, "DefaultWorkspace", "Default") ==
            PanelPreferenceData.WorkspaceKey(1, "DefaultWorkspace", "Localized label"), "localized built-in labels keep their stable workspace identity");
        reopened.Update(data => data.WithFloatingLayout(workspaceA, floating));
        reopened.Update(data => data.WithFloatingLayout(workspaceB, docked));
        var tab = new WorkspaceTabPlacement("Exposure", "Color", 4);
        var otherTab = new WorkspaceTabPlacement(null, "Library", 0);
        reopened.Update(data => data.WithWorkspaceTab(workspaceA, tab).WithWorkspaceTab(workspaceB, otherTab));
        var workspacePreferences = new PanelPreferenceStore(path, messages.Add).Current;
        Check(workspacePreferences.FloatingLayout(workspaceA) == floating && workspacePreferences.FloatingLayout(workspaceB) == docked,
            "different native workspace identities retain their own placement, scroll and floating mode across reload");
        Check(workspacePreferences.FloatingLayout("3|Color") == docked && workspacePreferences.ViewerSize == "1600"
            && workspacePreferences.Sections?["child"] == true, "a new workspace inherits docked defaults without changing other preferences");
        Check(workspacePreferences.WorkspaceTabs?.GetValueOrDefault(workspaceA) == tab
            && workspacePreferences.WorkspaceTabs?.GetValueOrDefault(workspaceB) == otherTab,
            "each custom workspace reopens with its own saved tab neighbors");
        Check(tab.Resolve(new[] { "Library", "Styles", "Exposure", "Color" }) == 3
            && tab.Resolve(new[] { "Library", "Exposure", "Styles" }) == 2
            && tab.Resolve(new[] { "Library", "Styles" }) == 2
            && otherTab.Resolve(new string?[] { null, "Library" }) == 1,
            "tab placement follows stable neighbor IDs and falls back to a valid index when they disappear");
        reopened.Update(data => data.WithWorkspaceTab(workspaceA, tab with { Hidden = true }));
        var hiddenTabs = new PanelPreferenceStore(path, messages.Add).Current.WorkspaceTabs!;
        Check(hiddenTabs[workspaceA].Hidden && !hiddenTabs[workspaceB].Hidden && hiddenTabs[workspaceA].Index == tab.Index,
            "removal retains placement and is scoped to one workspace");
        reopened.Update(data => data.WithWorkspaceTab(workspaceA, hiddenTabs[workspaceA] with { Hidden = false }));
        Check(new PanelPreferenceStore(path, messages.Add).Current.WorkspaceTabs?[workspaceA] == tab,
            "restoring a tab clears the hidden flag and retains its position");
        Check(messages.Count == 0, "Reading a compatible preference file reported an error");
        const string future = "{\"Version\":99,\"Unknown\":true}";
        File.WriteAllText(path, future);
        var fallback = new PanelPreferenceStore(path, messages.Add);
        fallback.CreateSection("child", "Child", new System.Windows.Controls.StackPanel(), 5).IsExpanded = true;
        Check(File.ReadAllText(path) == future && messages.Count == 1, "Future preferences were overwritten or unreported");
    }

    private static void TestColorSampling()
    {
        var values = new float[] {0,0,0,1, 1,1,1,1};
        var bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var frame = new HostImageSource.Frame(2, 1, bytes, 16);
        foreach (var space in Enum.GetValues<PreviewInputSpace>())
        {
            var result = ViewerDraftSource.Downsample(frame,1,CancellationToken.None,space);
            var output = MemoryMarshal.Cast<byte,float>(result.FloatBytes);
            Check(Math.Abs(ColorTransfer.Decode(output[0],space)-0.5) < 2e-6, $"{space} average is not linear light");
            Check(output[3] == 1, "Resampling changed alpha");
            for(var i=-25;i<=200;i++)
            {
                var code=i/100f;
                Check(Math.Abs(ColorTransfer.Encode(ColorTransfer.Decode(code,space),space)-code)<1e-4, $"Transfer roundtrip {space} {code}");
            }
        }
        Check(Math.Abs(ColorTransfer.Encode(0.5f,PreviewInputSpace.Srgb)-0.73535698f)<2e-6,"sRGB light average reference");
        Console.WriteLine("Colour: all input encodings average in linear light and preserve extended-range roundtrips.");
    }

    private static void TestOutputProtection(string folder)
    {
        var source=Path.Combine(folder,"source.tif");
        File.WriteAllText(source,"ORIGINAL");
        var refused=false;
        try { OutputPathGuard.Validate(source.ToUpperInvariant(),source); } catch(IOException) { refused=true; }
        Check(refused,"Original path was accepted");
        Check(OutputPathGuard.Validate(Path.Combine(folder,"different.tif"),source).EndsWith("different.tif"),"Distinct output rejected");
        ExpectRefused(() => OutputPathGuard.Validate(Path.Combine(folder, ".", "source.tif"), source), "Source path with dot segment accepted");
        var link=Path.Combine(folder,"linked-source.tif");
        try
        {
            File.CreateSymbolicLink(link,source);
            refused=false;
            try { OutputPathGuard.Validate(link,source); } catch(IOException) { refused=true; }
            Check(refused,"Original symlink was accepted");
        }
        catch(UnauthorizedAccessException) { Console.WriteLine("SKIP: symbolic-link creation unavailable for alias test."); }
        catch(IOException error) when ((error.HResult & 0xffff) == 1314)
        { Console.WriteLine("SKIP: symbolic-link privilege unavailable for alias test."); }

        var hardlink = Path.Combine(folder, "hardlink-source.tif");
        if (!CreateHardLink(hardlink, source, IntPtr.Zero))
            throw new IOException($"Hardlink fixture failed: {Marshal.GetLastWin32Error()}.");
        ExpectRefused(() => OutputPathGuard.Validate(hardlink, source), "Original file identity through a hardlink was accepted");

        var junction = Path.Combine(folder, "junction");
        var junctionTarget = Path.Combine(folder, "junction-target");
        Directory.CreateDirectory(junctionTarget);
        var junctionSource = Path.Combine(junctionTarget, "source.tif");
        File.WriteAllText(junctionSource, "JUNCTION ORIGINAL");
        var mklinkInfo = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"%SPEKTRA_TEST_JUNCTION%\" \"%SPEKTRA_TEST_TARGET%\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        mklinkInfo.Environment["SPEKTRA_TEST_JUNCTION"] = junction;
        mklinkInfo.Environment["SPEKTRA_TEST_TARGET"] = junctionTarget;
        using (var mklink = Process.Start(mklinkInfo)!)
        {
            if (!mklink.WaitForExit(10000))
                throw new TimeoutException("Disposable junction creation timed out.");
            var response = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
            if (mklink.ExitCode == 0)
            {
                try
                {
                    ExpectRefused(() => OutputPathGuard.Validate(Path.Combine(junction, "source.tif"), junctionSource), "Output junction alias accepted");
                    ExpectRefused(() => OutputPathGuard.Validate(junctionSource, Path.Combine(junction, "source.tif")), "Source junction alias accepted");
                    OutputPathGuard.Approve(Path.Combine(junction, "separate.tif"), junctionSource).Write(stream => stream.WriteByte(17));
                    Check(File.ReadAllBytes(Path.Combine(junctionTarget, "separate.tif")).SequenceEqual(new byte[] { 17 }), "Separate junction output was not committed");
                    Check(File.ReadAllText(junctionSource) == "JUNCTION ORIGINAL", "Junction test altered the original");
                }
                finally
                {
                    // Remove the junction itself before cleaning up the owned test tree.
                    Directory.Delete(junction);
                }
            }
            else if (response.Contains(new Win32Exception(5).Message, StringComparison.CurrentCultureIgnoreCase)
                || response.Contains(new Win32Exception(1314).Message, StringComparison.CurrentCultureIgnoreCase))
                Console.WriteLine("SKIP: Windows denied creation of the disposable junction fixture.");
            else
                throw new IOException("Could not create the disposable junction fixture: " + response.Trim());
        }

        var late = Path.Combine(folder, "late.tif");
        var approved = OutputPathGuard.Approve(late, source);
        ExpectRefused(() => approved.Write(stream =>
        {
            stream.WriteByte(20);
            File.WriteAllText(late, "CONCURRENT");
        }), "Concurrent creation was silently replaced");
        Check(File.ReadAllText(late) == "CONCURRENT", "Concurrent destination bytes changed");

        var replacement = OutputPathGuard.Approve(late, source);
        ExpectRefused(() => replacement.Write(stream =>
        {
            stream.WriteByte(21);
            File.Move(late, Path.Combine(folder, "previous.tif"));
            File.WriteAllText(late, "DIFFERENT");
        }), "Replacement with a different identity was silently overwritten");
        Check(File.ReadAllText(late) == "DIFFERENT", "Replacement bytes changed");
        OutputPathGuard.Approve(late, source).Write(stream => stream.WriteByte(22));
        Check(File.ReadAllBytes(late).SequenceEqual(new byte[] { 22 }), "Unchanged approved destination could not be replaced");
        using var stop = new CancellationTokenSource();
        try
        {
            OutputPathGuard.Approve(late, source).Write(stream =>
            {
                stream.WriteByte(99);
                stop.Cancel();
            }, stop.Token);
            throw new InvalidOperationException("Canceled output committed after encoding.");
        }
        catch (OperationCanceledException) { assertions++; }
        Check(File.ReadAllBytes(late).SequenceEqual(new byte[] { 22 }), "Cancellation after encoding changed an existing destination");
        try
        {
            OutputPathGuard.Approve(late, source).Write(stream =>
            {
                stream.WriteByte(90);
                throw new InsufficientMemoryException("Simulated allocation failure during encoding.");
            });
            throw new InvalidOperationException("Allocation failure published an incomplete file.");
        }
        catch (InsufficientMemoryException) { assertions++; }
        Check(File.ReadAllBytes(late).SequenceEqual(new byte[] { 22 }), "Allocation failure changed an existing output");
        Check(!Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(), "Output failures left partial files behind");

        var lateAlias = Path.Combine(folder, "late-alias.tif");
        var aliasApproval = OutputPathGuard.Approve(lateAlias, source);
        ExpectRefused(() => aliasApproval.Write(stream =>
        {
            stream.WriteByte(23);
            if (!CreateHardLink(lateAlias, source, IntPtr.Zero))
                throw new InvalidOperationException("Could not create concurrent hardlink fixture.");
        }), "Concurrent original alias was accepted");
        Check(File.ReadAllText(lateAlias) == "ORIGINAL", "Concurrent original alias was altered");
        Check(File.ReadAllText(source)=="ORIGINAL","Path test altered original");

        var raw = Path.Combine(folder, "source-capture.dng");
        var moved = Path.Combine(folder, "previous-capture.dng");
        var priorOutput = Path.Combine(folder, "completed.png");
        File.WriteAllText(raw, "RAW1");
        File.WriteAllText(priorOutput, "PREVIOUS");
        var originalTime = File.GetLastWriteTimeUtc(raw);
        var original = new OutputPathGuard.OriginalFile(raw);
        File.Move(raw, moved);
        File.WriteAllText(raw, "RAW2");
        File.SetLastWriteTimeUtc(raw, originalTime);
        ExpectRefused(() => original.Approve(priorOutput), "An equal-length replacement at the same RAW path passed approval");
        Check(File.ReadAllText(priorOutput) == "PREVIOUS", "Replacement during Save changed a completed export");
        var current = new OutputPathGuard.OriginalFile(raw);
        var stable = current.Approve(priorOutput);
        using (current.OpenRead())
        {
            ExpectRefused(() => File.WriteAllText(raw, "RAW3"), "The accepted export allowed a writer through its source lease");
            ExpectRefused(() => File.Move(raw, Path.Combine(folder, "lease-replacement.dng")), "The accepted export allowed its source to be renamed");
        }
        ExpectRefused(() => stable.Write(stream =>
        {
            stream.WriteByte(93);
            File.WriteAllText(raw, "RAW3");
            File.SetLastWriteTimeUtc(raw, originalTime);
        }), "An in-place RAW rewrite with a preserved timestamp passed the final guard");
        Check(File.ReadAllText(priorOutput) == "PREVIOUS" && File.ReadAllText(moved) == "RAW1"
            && !Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(),
            "Rejecting a changed RAW altered unrelated bytes or left a staging file");
        new OutputPathGuard.OriginalFile(raw).Approve(priorOutput).Write(stream => stream.WriteByte(94));
        Check(File.ReadAllBytes(priorOutput).SequenceEqual(new byte[] { 94 }), "A new approval could not write after reviewing the current source");

        void ExpectRefused(Action action, string failure)
        {
            try { action(); }
            catch (IOException) { assertions++; return; }
            throw new InvalidOperationException(failure);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingPath, IntPtr reserved);

    private static void TestFractionalColorSampling()
    {
        const int width = 13, height = 7;
        var samples = new float[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            samples[index] = (x - 2) / 9f;
            samples[index + 1] = ((x + y) % 3) / 2f;
            samples[index + 2] = (x == 12 ? 1.4f : 0.003f) + y * 0.008f;
            samples[index + 3] = ((x + y * 2) % 7) / 6f;
        }
        var frame = new HostImageSource.Frame(width, height,
            MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), 16);

        foreach (var space in Enum.GetValues<PreviewInputSpace>())
        foreach (var (edge, columns, rows) in new[] { (12, 12, 6), (8, 8, 4), (5, 5, 2), (2, 2, 1), (1, 1, 1) })
        {
            var draft = ViewerDraftSource.Downsample(frame, edge, CancellationToken.None, space);
            Check(draft.Width == columns && draft.Height == rows && draft.BitDepth == 16, "Fractional draft dimensions or precision changed");
            var output = MemoryMarshal.Cast<byte, float>(draft.FloatBytes);
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            for (var channel = 0; channel < 4; channel++)
            {
                double weighted = 0;
                for (var sy = 0; sy < height; sy++)
                for (var sx = 0; sx < width; sx++)
                {
                    // Independent overlap in destination coordinates. Every footprint has area one.
                    double weight = Overlap(x, sx, width, columns) * Overlap(y, sy, height, rows);
                    double sample = samples[(sy * width + sx) * 4 + channel];
                    weighted += weight * (channel == 3 ? sample : DecodeReference(sample, space));
                }
                var actual = output[(y * columns + x) * 4 + channel];
                var linear = channel == 3 ? actual : DecodeReference(actual, space);
                Check(Math.Abs(linear - weighted) < 4e-6, $"{space} fractional pixel ({x}, {y}, {channel}) at {edge} disagrees with independent overlap integral");
            }
        }
        Console.WriteLine("Fractional area filtering agrees with an independent nonconstant colour and alpha integral.");

        static double Overlap(int dest, int src, int sourceSize, int destSize) =>
            Math.Max(0, Math.Min(dest + 1, (src + 1) * (double)destSize / sourceSize)
                - Math.Max(dest, src * (double)destSize / sourceSize));

        static double DecodeReference(double encoded, PreviewInputSpace space)
        {
            var value = Math.Abs(encoded);
            var linear = space switch
            {
                PreviewInputSpace.Srgb => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4),
                PreviewInputSpace.AdobeRgb => Math.Pow(value, 563.0 / 256),
                PreviewInputSpace.ProPhotoRgb => value <= 16.0 / 512 ? value / 16 : Math.Pow(value, 1.8),
                PreviewInputSpace.LinearRec2020 => value,
                _ => throw new ArgumentOutOfRangeException(nameof(space)),
            };
            return Math.CopySign(linear, encoded);
        }
    }

    private static void TestColorPrecision()
    {
        foreach (var space in Enum.GetValues<PreviewInputSpace>())
        {
            foreach (var value in new[] {float.NaN,float.PositiveInfinity,float.NegativeInfinity})
            {
                var refused=false;
                try {ColorTransfer.Decode(value,space);}catch(ArgumentOutOfRangeException){refused=true;}
                Check(refused,"Decode accepted nonfinite input");
                refused=false;
                try {ColorTransfer.Encode(value,space);}catch(ArgumentOutOfRangeException){refused=true;}
                Check(refused,"Encode accepted nonfinite input");
            }
        }
        foreach(var space in new[]{(PreviewInputSpace)(-1),(PreviewInputSpace)4,(PreviewInputSpace)int.MaxValue})
        {
            var refused=false;
            try {ColorTransfer.Encode(.5f,space);}catch(ArgumentOutOfRangeException){refused=true;}
            Check(refused,"Invalid input space accepted");
        }
        for(var i=1;i<=500;i++)
        {
            var encoded=i/100000f;
            var linear=(float)Math.Pow(encoded,563.0/256);
            var actual=ColorTransfer.Encode(linear,PreviewInputSpace.AdobeRgb);
            Check(Math.Abs(actual-encoded)<2e-8,"Adobe near-black encode precision lost");
            Check(Math.Abs(ColorTransfer.Encode(-linear,PreviewInputSpace.AdobeRgb)+encoded)<2e-8,"Signed Adobe near-black precision lost");
        }
        foreach(var linear in new[]{1f/256-1e-7f,1f/256,1f/256+1e-7f,.25f,.8f,2f})
            Check(Math.Abs(ColorTransfer.Encode(linear,PreviewInputSpace.AdobeRgb)-Math.Pow(linear,256.0/563))<2e-7,"Adobe encode reference/threshold discontinuity");
        Console.WriteLine("Colour precision: Adobe near-black reference, transfer boundary, signed range, finite inputs and enum validation passed.");
    }

    private static void TestExportFrameLimits()
    {
        foreach (var (width, height) in new[] { (6000, 5000), (9000, 5000), (10000, 6100), (10000, 10000), (16384, 8), (8, 16384) })
            Check(RenderFrameLimits.ValidateExportCanvas(width, height, 1) == (width, height), "allowed export was silently resized");
        Check(RenderFrameLimits.ValidateExportCanvas(10000, 6100, 0.5) == (5000, 3050), "recipe scale changed");
        Check(RenderFrameLimits.ValidateExportCanvas(3999.5, 2000.1, 1) == (4000, 2001), "fractional canvas must reserve its final pixel");
        foreach (var (width, height, scale) in new[] { (10001d, 10000d, 1d), (16385d, 1d, 1d), (10d, 10d, 0d), (double.NaN, 1d, 1d) })
        {
            try
            {
                RenderFrameLimits.ValidateExportCanvas(width, height, scale);
                throw new Exception("Invalid requested canvas was admitted");
            }
            catch (InvalidOperationException) { assertions++; }
        }
        Console.WriteLine("Export preflight: exact 30/45/61/100 MP, long thin edges, recipe scaling and oversized refusal passed.");
    }

    private static void TestMemoryAdmission()
    {
        const long gib = 1024L * 1024L * 1024L;
        const string alternatives = "Choose a smaller Viewer resolution.";
        var available = 4 * gib;
        var budget = new RenderMemoryBudget(() => available);
        var stage = RenderAllocation.WorkerTransport;
        bool Refused(long pixels)
        {
            try
            {
                using var lease = budget.Reserve(stage, pixels, alternatives);
                return false;
            }
            catch (InsufficientMemoryException error) { return error.Message.Contains(alternatives, StringComparison.Ordinal); }
        }

        Check(RenderMemoryBudget.EstimateBytes(stage, 24_000_000)
            > RenderMemoryBudget.EstimateBytes(RenderAllocation.NativeDevelopment, 24_000_000), "Worker reserve must include extra transport");
        var first = budget.Reserve(stage, 24_000_000, alternatives);
        Check(Refused(24_000_000), "4 GiB fresh system headroom admitted competing high-cost requests");
        available = 6 * gib;
        using (budget.Reserve(stage, 24_000_000, alternatives))
            Check(Refused(24_000_000), "6 GiB fresh headroom admitted a third expensive reserve");
        first.Dispose();
        first.Dispose();
        available = 8 * gib;
        Check(Refused(100_000_000), "unsupported memory demand must return an explicit resolution choice");
        using var request = budget.Reserve(stage, 24_000_000, alternatives);
        using var codec = budget.Reserve(RenderAllocation.ImageEncoding, 24_000_000, alternatives);
        available = 512 * 1024L * 1024L;
        Check(Refused(960_000), "headroom drop during active work did not prevent further admission");

        available = 3 * gib;
        var competing = 0;
        Parallel.For(0, 16, _ =>
        {
            try
            {
                using var lease = budget.Reserve(stage, 24_000_000, alternatives);
                Interlocked.Increment(ref competing);
            }
            catch (InsufficientMemoryException) { }
        });
        Check(competing == 0, "cross-context admission bypassed the outstanding leases");
        Console.WriteLine("System memory reserve: 4/6/8 GiB synthetic headroom, overlap, release and pressure behavior passed.");
    }

    private static async Task TestNativeCallbackOwnership()
    {
        using var slot = new SemaphoreSlim(0, 1);
        var invoked = 0;
        var queued = new QueuedRenderRequest<int, int>(value => { invoked++; return value; }, () => slot.Release(), out var queuedResult);
        queued.Abandon();
        queued.Abandon();
        queued.Run(42);
        Check(invoked == 0 && queuedResult.IsCanceled && slot.CurrentCount == 1,
            "an abandoned queued callback gives its slot back once and cannot run");

        await slot.WaitAsync();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var running = new QueuedRenderRequest<int, int>(value =>
        {
            entered.Set();
            if (!finish.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Native callback fixture did not release its gate.");
            return value;
        }, () => slot.Release(), out var runningResult);
        var thread = Task.Run(() => running.Run(88));
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "native callback obtained ownership of the slot");
        try
        {
            running.Abandon();
            running.Abandon();
            Check(runningResult.IsCanceled && slot.CurrentCount == 0,
                "caller cancellation cannot release an executing native callback's slot");
            Check(!await slot.WaitAsync(TimeSpan.FromMilliseconds(20)), "a second development could not enter during native drain");
        }
        finally { finish.Set(); }
        await thread.WaitAsync(TimeSpan.FromSeconds(10));
        Check(slot.CurrentCount == 1 && runningResult.IsCanceled,
            "native completion releases its slot without publishing a canceled frame");

        await slot.WaitAsync();
        var failure = new QueuedRenderRequest<int, int>(_ => throw new IOException("native failure"), () => slot.Release(), out var failedResult);
        failure.Run(0);
        try { await failedResult; throw new InvalidOperationException("Native failure was swallowed."); }
        catch (IOException) { Check(slot.CurrentCount == 1, "native fault releases its slot and surfaces the real error"); }

        for (var attempt = 0; attempt < 200; attempt++)
        {
            await slot.WaitAsync();
            var raced = new QueuedRenderRequest<int, int>(value => value, () => slot.Release(), out var racedResult);
            await Task.WhenAll(Task.Run(() => raced.Run(5)), Task.Run(raced.Abandon));
            Check(slot.CurrentCount == 1 && (racedResult.IsCanceled || racedResult is { IsCompletedSuccessfully: true, Result: 5 }),
                "competing native delivery/cancel leaves one slot and a terminal result");
        }
        var (retainedCallback, deliveredPixels) = DeliverSourceWithRetainedCallback();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check(!deliveredPixels.IsAlive, "a callback retained by Capture One must not keep its delivered pixel buffer alive");
        GC.KeepAlive(retainedCallback);
        Console.WriteLine("Capture One scheduler contracts: pending cancellation, running drain, native faults, callback retention and 200 delivery races passed.");

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static (Action<int> Callback, WeakReference Image) DeliverSourceWithRetainedCallback()
        {
            var request = new QueuedRenderRequest<int, byte[]>(size => new byte[size], () => { }, out var result);
            Action<int> callback = request.Run;
            callback(1024 * 1024);
            return (callback, new WeakReference(result.GetAwaiter().GetResult()));
        }
    }

    private static async Task TestScheduling()
    {
        var previewEnd=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken previewStop=default;
        var final=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var render=RenderCoordination.PreferFinalAsync(final.Task, token => {previewStop=token;return previewEnd.Task;},CancellationToken.None);
        final.SetResult(42);
        var result=await render.WaitAsync(TimeSpan.FromSeconds(2));
        Check(result==42,"Ready final source was blocked by draft");
        Check(previewStop.IsCancellationRequested,"Obsolete initial draft not cancelled");
        previewEnd.SetResult();
        var invoked=false;
        Check(await RenderCoordination.PreferFinalAsync(Task.FromResult(43),_=>{invoked=true;return Task.CompletedTask;},CancellationToken.None)==43&&!invoked,"Ready final started obsolete draft");
        var afterPreviewFailure=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered=RenderCoordination.PreferFinalAsync(afterPreviewFailure.Task,_=>throw new IOException("preview failed synchronously"),CancellationToken.None);
        afterPreviewFailure.SetResult(44);
        Check(await recovered==44,"Synchronous preview failure discarded final");
        using var export=new ExportOperation(Guid.NewGuid());
        Check(!export.Token.IsCancellationRequested,"New export already cancelled");
        var identity=export.VariantId;
        Check(identity!=Guid.Empty,"Accepted export lost target identity");
        export.Cancel();
        Check(export.Token.IsCancellationRequested,"Explicit cancellation ignored");
        var failed=Task.FromException<int>(new IOException("source failed"));
        var caught=false;
        try { await RenderCoordination.PreferFinalAsync(failed,_=>Task.CompletedTask,CancellationToken.None); } catch(IOException) {caught=true;}
        Check(caught,"Final source error was hidden");
        var retry = new PreviewRendererRetry<(Guid, int, int)>();
        var identityKey = (Guid.NewGuid(), 1, 7);
        var laterEdit = (identityKey.Item1, 2, 7);
        var changedSource = (identityKey.Item1, 2, 8);
        Check(retry.Schedule(identityKey, 1000), "First transport error receives one retry");
        Check(retry.IsWaiting(identityKey, 1249) && !retry.IsWaiting(identityKey, 1250), "Retry has a bounded delay");
        Check(!retry.Schedule(identityKey, 9000), "A repeat error cannot queue another retry for the same frame");
        Check(!retry.IsWaiting(laterEdit, 1010) && retry.Schedule(laterEdit, 1010), "A newer look bypasses the previous delay");
        Check(!retry.IsWaiting(changedSource, 1030) && retry.Schedule(changedSource, 1030), "Changed native source can schedule independently");
        retry.Reset();
        Check(retry.Schedule(changedSource, 1050), "Detaching discards the old retry budget");
        Console.WriteLine("Scheduling: final-ready priority, draft cancellation, export identity and explicit cancel passed.");
    }

    private static void BenchmarkDurableWrites(string folder)
    {
        var path=Path.Combine(folder,"synthetic-look.json");
        var numbers=Enumerable.Range(0,110).ToDictionary(i=>"optical-parameter-"+i,i=>(double)i/100);
        var settings=new {Look=new {Film="kodak_portra_400",Paper="kodak_portra_endura",Exposure=0.5,PrintExposure=0.0,Strength=1.0,Mode="print",SpectralMethod="hanatos",ScanInvert=false,Development=new Dictionary<string,double>{{"film_gamma",1.0},{"film_push_pull",0.0}},FilmPushPullMode="standard"},Optics=new {Numbers=numbers,Selections=new Dictionary<string,string>{{"input-color","linear_rec2020"},{"output-color","srgb"},{"preview-size","native"},{"drag-preview-size","1200"}},Switches=new Dictionary<string,bool>{{"show-source",false}}},Enabled=true,Version=1,Grain=new {Type=0,Impact=0.0,Granularity=0.0}};
        for(int i=0;i<5;i++) PanelFiles.WritePreset(path,settings);
        var ms=new double[200];
        for(int i=0;i<ms.Length;i++)
        {
            numbers["optical-parameter-0"]=i/100.0;
            var start=Stopwatch.GetTimestamp();PanelFiles.WritePreset(path,settings);ms[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(ms);
        Console.WriteLine(FormattableString.Invariant($"Durable-write microbenchmark: {ms.Length} warmed atomic JSON writes, payload {new FileInfo(path).Length} bytes, median {ms[ms.Length/2]:F3} ms, p95 {ms[(int)(ms.Length*.95)-1]:F3} ms, max {ms[^1]:F3} ms. This local storage probe is not a Capture One UI latency measurement."));
    }

    private static void TestGeometry()
    {
        PointF[][] cases = {
            new[] {new PointF(10,20),new PointF(210,20),new PointF(210,120),new PointF(10,120)},
            new[] {new PointF(210,20),new PointF(10,20),new PointF(10,120),new PointF(210,120)},
            new[] {new PointF(110,20),new PointF(110,220),new PointF(10,220),new PointF(10,20)},
            new[] {new PointF(20,20),new PointF(220,40),new PointF(250,140),new PointF(50,120)},
        };
        foreach(var corners in cases)
        {
            Check(ViewerGeometry.TryCreate(new RectangleF(0,0,500,500),corners,2000,1000,out var geometry),"Valid affine geometry rejected");
            var imageCorners = new[] {new System.Windows.Point(0,0),new System.Windows.Point(2000,0),new System.Windows.Point(2000,1000),new System.Windows.Point(0,1000)};
            for(int i=0;i<4;i++)
            {
                var p=geometry.Transform.Transform(imageCorners[i]);
                Check(Math.Abs(p.X-corners[i].X)<1e-9 && Math.Abs(p.Y-corners[i].Y)<1e-9,"Corner correspondence wrong");
            }
        }
        var invalid=(PointF[])cases[0].Clone();invalid[2].X+=5;
        Check(!ViewerGeometry.TryCreate(new RectangleF(0,0,500,500),invalid,2000,1000,out _),"Non-affine geometry accepted");
        Check(!ViewerGeometry.TryCreate(new RectangleF(300,300,500,500),cases[0],2000,1000,out _),"Offscreen geometry accepted");
        invalid[0].X=float.NaN;
        Check(!ViewerGeometry.TryCreate(new RectangleF(0,0,500,500),invalid,2000,1000,out _),"Nonfinite geometry accepted");
        Console.WriteLine("Geometry: affine, mirror, rotation, shear, offscreen and invalid-corner probes passed.");
    }

    private static void TestDpiAndCropGeometry()
    {
        var rectangle = new RectangleF(52, 33, 100, 61);
        var corners = new[] { new PointF(32, 24), new PointF(192, 24), new PointF(192, 104), new PointF(32, 104) };
        var color = new byte[] { 29, 68, 115, 255 };
        var bitmap = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat(color, 8).SelectMany(pixel => pixel).ToArray(), 16);
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        {
            Check(ViewerGeometry.TryCreate(rectangle, corners, bitmap.PixelWidth, bitmap.PixelHeight, out var geometry),
                "DPI fixture geometry rejected");
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                // Capture One's painter supplies this physical-pixel-to-DIP transform.
                dc.PushTransform(new ScaleTransform(1 / scale, 1 / scale));
                dc.PushClip(new RectangleGeometry(geometry.Clip));
                dc.PushTransform(new MatrixTransform(geometry.Transform));
                dc.DrawImage(bitmap, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                dc.Pop();
                dc.Pop();
                dc.Pop();
            }
            var frame = new RenderTargetBitmap(220, 140, scale * 96, scale * 96, PixelFormats.Pbgra32);
            frame.Render(drawing);
            byte[] Pixel(int x, int y)
            {
                var rgba = new byte[4];
                frame.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), rgba, 4, 0);
                return rgba;
            }
            Check(Pixel(110, 65).SequenceEqual(color), "frame center drifted at " + scale + "x DPI");
            Check(Pixel(46, 65)[3] == 0 && Pixel(156, 65)[3] == 0, "clip moved at " + scale + "x DPI");
            Check(Pixel(110, 28)[3] == 0 && Pixel(110, 101)[3] == 0, "vertical clip moved at " + scale + "x DPI");
        }

        var approximate = (PointF[])corners.Clone();
        approximate[2].X += .49f;
        Check(ViewerGeometry.TryCreate(rectangle, approximate, 4, 2, out _), "subpixel native corner rounding was rejected");
        approximate[2].X += .03f;
        Check(!ViewerGeometry.TryCreate(rectangle, approximate, 4, 2, out _), "nonclosing corner passed the rounding threshold");
        var flat = new[] { new PointF(32, 50), new PointF(192, 50), new PointF(192, 50.5f), new PointF(32, 50.5f) };
        Check(!ViewerGeometry.TryCreate(rectangle, flat, 4, 2, out _), "degenerate half-pixel rows should not paint");

        var canvas = new SizeF(6000, 4000);
        Check(ViewerGeometry.ScaleCrop(new RectangleF(444.2f, 100.4f, 3000, 2000), canvas, 3000, 2000)
            == new Rectangle(222, 50, 1500, 1000), "crop pixel rounding drifted from native canvas");
        Check(ViewerGeometry.ScaleCrop(new RectangleF(5999.8f, 3999.8f, .2f, .2f), canvas, 3000, 2000)
            == new Rectangle(2999, 1999, 1, 1), "rounded crop at canvas edges lost the final pixel");
        Check(ViewerGeometry.ScaleCrop(new RectangleF(5998, 3998, 10, 10), canvas, 3000, 2000)
            == new Rectangle(2999, 1999, 1, 1), "crop extending outside frame exceeded its delivered canvas");
        Console.WriteLine("Geometry: WPF physical coordinates at 100/150/200% DPI, closure limits and crop rounding passed.");
    }

    private static void TestDraft()
    {
        foreach(var shape in new[] {(7,5,3),(29,13,7),(1,19,3),(17,1,4),(257,263,73),(3001,1,2800)})
        {
            var (width,height,edge)=shape;
            var bytes=new byte[width*height*16];var floats=MemoryMarshal.Cast<byte,float>(bytes);
            for(int i=0;i<width*height;i++) { floats[4*i]=.25f;floats[4*i+1]=-.5f;floats[4*i+2]=1.5f;floats[4*i+3]=.75f; }
            var input = new HostImageSource.Frame(width, height, bytes, 16);
            var draft=ViewerDraftSource.Downsample(input,edge,CancellationToken.None);
            var result=MemoryMarshal.Cast<byte,float>(draft.FloatBytes);
            Check(Math.Max(draft.Width,draft.Height)<=Math.Min(edge,2560),"Draft cap exceeded");
            for(int i=0;i<result.Length;i+=4) Check(Math.Abs(result[i]-.25f)<3e-5&&Math.Abs(result[i+1]+.5f)<3e-5&&Math.Abs(result[i+2]-1.5f)<3e-5&&Math.Abs(result[i+3]-.75f)<3e-5,"Area filter changed constant or range/alpha");
            Check(ReferenceEquals(ViewerDraftSource.Downsample(draft,2560,CancellationToken.None),draft),"Same-size source copied");
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try {ViewerDraftSource.Downsample(input,edge,cancel.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){assertions++;}
        }
        Console.WriteLine("Draft: noninteger/extreme aspect ratios, constant energy, out-of-range channels, alpha, size cap, identity and cancellation passed.");
    }

    private static void TestViewerDisplayBitmap()
    {
        const int width = 3, height = 2;
        var original = new byte[width * height * 16];
        float[] source =
        {
            .12f, .24f, .49f, .80f, 1.2f, .2f, -.09f, .30f, .75f, .5f, .15f, 1.3f,
            .71f, .53f, .135f, 0, .024f, .045f, .095f, 1, -.005f, .14f, .28f, -.2f,
        };
        var rendered = new byte[original.Length];
        Buffer.BlockCopy(source, 0, original, 0, original.Length);
        var changed = source.Select(value => value * .72f + .13f).ToArray();
        Buffer.BlockCopy(changed, 0, rendered, 0, rendered.Length);
        var frame = new HostImageSource.Frame(width, height, original, 16);
        foreach (var space in Enum.GetValues<PreviewInputSpace>())
        foreach (var strength in new[] { 0d, .5d, 1d })
        {
            var image = ViewerDisplayBitmap.Convert(frame, strength == 0 ? null : rendered, strength, space,
                "linear_rec709", CancellationToken.None);
            var reference = new SrgbDelivery(space, "linear_rec709", strength);
            var pixels = new byte[width * height * 4];
            Task.Run(() => image.CopyPixels(pixels, width * 4, 0)).GetAwaiter().GetResult();
            Check(image.IsFrozen && image.Format == PixelFormats.Bgra32
                && image.PixelWidth == width && image.PixelHeight == height,
                "Viewer pixels must be copied to a frozen cross-thread bitmap before scratch is retired");
            for (var index = 0; index < pixels.Length; index += 4)
            {
                var rgb = reference.GetSrgb(source, changed, index);
                Check(pixels[index] == (byte)Math.Round(rgb.Blue * 255)
                    && pixels[index + 1] == (byte)Math.Round(rgb.Green * 255)
                    && pixels[index + 2] == (byte)Math.Round(rgb.Red * 255)
                    && pixels[index + 3] == (byte)MathF.Round(255 * Math.Clamp(source[index + 3], 0, 1)),
                    "Viewer scratch storage must preserve exact BGRA, colour encoding and source alpha");
            }
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var threw = false;
        try { ViewerDisplayBitmap.Convert(frame, null, 0, PreviewInputSpace.Srgb, "srgb", canceled.Token); }
        catch (OperationCanceledException) { threw = true; }
        Check(threw, "A canceled Viewer conversion must not publish a bitmap");

        const int wide = 4097, tall = 1025;
        var large = new HostImageSource.Frame(wide, tall, new byte[wide * tall * 16], 16);
        var channels = MemoryMarshal.Cast<byte, float>(large.FloatBytes.AsSpan());
        var rows = new[] { 0, tall / 6, tall / 2, tall - 1 };
        foreach (var row in rows)
        {
            var offset = (row * wide + wide - 1) * 4;
            channels[offset] = .5f;
            channels[offset + 1] = .25f;
            channels[offset + 2] = .125f;
            channels[offset + 3] = 1;
        }
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var parallel = ViewerDisplayBitmap.Convert(large, null, 0, PreviewInputSpace.Srgb, "srgb", CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Check(allocated < wide * tall, "Parallel Viewer conversion must avoid a full-frame managed BGRA scratch array");
        foreach (var row in rows)
        {
            var pixel = new byte[4];
            parallel.CopyPixels(new System.Windows.Int32Rect(wide - 1, row, 1, 1), pixel, 4, 0);
            Check(pixel.SequenceEqual(new byte[] { 32, 64, 128, 255 }), "Finalized parallel rows must retain exact end-of-row BGRA");
        }
    }

    private static void TestDevelopedFrameDescription()
    {
        var identity = new FrameSourceDescription(Guid.NewGuid(), "DNG", PreviewInputSpace.Srgb,
            new string('A', 64), new NativeImageRevision(10, 20, 30), "16.4.6.2414", new string('B', 64));
        (PreviewInputSpace Space, SourcePrimaries Primaries, SourceTransfer Transfer)[] encodings =
        {
            (PreviewInputSpace.Srgb, SourcePrimaries.SrgbD65, SourceTransfer.Srgb),
            (PreviewInputSpace.AdobeRgb, SourcePrimaries.AdobeRgbD65, SourceTransfer.AdobeRgb1998),
            (PreviewInputSpace.ProPhotoRgb, SourcePrimaries.ProPhotoRgbD50, SourceTransfer.ProPhotoRgb),
            (PreviewInputSpace.LinearRec2020, SourcePrimaries.Rec2020D65, SourceTransfer.Linear),
        };
        foreach (var encoding in encodings)
        {
            var item = identity with { InputEncoding = encoding.Space };
            Check(item.Primaries == encoding.Primaries && item.Transfer == encoding.Transfer
                && item.Stage == SourceStage.CaptureOneDevelopedRgb, $"Developed encoding contract for {encoding.Space}");
        }
        Check(identity != (identity with { VariantId = Guid.NewGuid() })
            && identity != (identity with { ProfileSha256 = new string('C', 64) })
            && identity != (identity with { AdjustmentKey = new NativeImageRevision(10, 20, 31) })
            && identity != (identity with { HostAssemblySha256 = new string('D', 64) }),
            "Copy identity changes with variant, ICC, native settings or verified host");

        const int width = 8, height = 4, bytes = width * height * 16;
        var original = new FrameDescription(identity, width, height, NativeSourceFormat.Rgb16);
        Check(original.Describes(width, height, 16, bytes) && original.Range == SourceSampleRange.NormalizedUnsigned
            && original.Alpha == SourceAlpha.Opaque && original.Sampling == SourceSampling.CaptureOne,
            "Native RGB16 descriptor records bounded data with opaque alpha");
        Check((original with { NativeFormat = NativeSourceFormat.Rgba16 }).Alpha == SourceAlpha.PreservedNativeChannel
            && (original with { NativeFormat = NativeSourceFormat.Rgb32Float }).Range == SourceSampleRange.FiniteExtended,
            "RGBA16 and finite float have separate sample contracts");
        Check(!original.Describes(width, height, 8, bytes) && !original.Describes(height, width, 16, bytes)
            && !original.Describes(width, height, 16, bytes - 1),
            "Descriptor rejects a different native format, orientation or byte count");
        var image = new HostImageSource.Frame(width, height, new byte[bytes], 16, original);
        var reduced = ViewerDraftSource.Downsample(image, 4, CancellationToken.None, PreviewInputSpace.Srgb);
        Check(reduced.Description is { Sampling: SourceSampling.LinearLightArea } derived
            && ReferenceEquals(derived.Source, original.Source)
            && derived.Describes(reduced.Width, reduced.Height, reduced.BitDepth, reduced.FloatBytes.Length),
            "A managed draft keeps exact input identity and declares its resized pixels");
        var rejected = false;
        try { ViewerDraftSource.Downsample(image, 4, CancellationToken.None, PreviewInputSpace.LinearRec2020); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "A draft cannot reinterpret its source profile as another input encoding");
        rejected = false;
        try { _ = (identity with { InputEncoding = (PreviewInputSpace)int.MaxValue }).Transfer; }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, "Undeclared camera or log transfer cannot enter the developed frame contract");
    }

    private static void TestDraftPyramid()
    {
        var identity = new FrameSourceDescription(Guid.NewGuid(), "DNG", PreviewInputSpace.Srgb,
            new string('A', 64), new NativeImageRevision(10, 20, 30), "16.4.6.2414", new string('B', 64));
        const int width = 31, height = 23;
        var data = new byte[width * height * 16];
        var components = MemoryMarshal.Cast<byte, float>(data);
        for (var index = 0; index < components.Length; index++)
            components[index] = (index % 47 - 7) / 36f;
        var source = new HostImageSource.Frame(width, height, data, 16,
            new FrameDescription(identity, width, height, NativeSourceFormat.Rgba16));
        const PreviewInputSpace space = PreviewInputSpace.Srgb;
        var small = ViewerDraftSource.Downsample(source, 7, CancellationToken.None, space);
        var medium = ViewerDraftSource.Downsample(source, 13, CancellationToken.None, space);
        var large = ViewerDraftSource.Downsample(source, 19, CancellationToken.None, space);
        var limit = Math.Max(large.FloatBytes.LongLength, small.FloatBytes.LongLength + medium.FloatBytes.LongLength);
        var pyramid = new ViewerDraftPyramid(limit);
        pyramid.Remember(source, 7, space, small);
        pyramid.Remember(source, 13, space, medium);
        Check(pyramid.RetainedBytes == small.FloatBytes.LongLength + medium.FloatBytes.LongLength,
            "Draft pyramid must account for every retained source level");
        Check(ReferenceEquals(pyramid.Find(source, 13, space), medium)
            && ReferenceEquals(pyramid.Find(source, 7, space), small), "Changing draft size must find the original level buffers");
        Check(pyramid.Find(source, 13, PreviewInputSpace.AdobeRgb) is null, "Encoded sources cannot share cached levels");
        Check(pyramid.Find(source with { FloatBytes = (byte[])data.Clone() }, 7, space) is null,
            "A redevelopment must miss even when the descriptor is identical");
        var revisedIdentity = identity with { AdjustmentKey = new NativeImageRevision(10, 21, 30) };
        var revised = source with { Description = source.Description! with { Source = revisedIdentity } };
        Check(pyramid.Find(revised, 7, space) is null, "Native adjustment revision must invalidate levels");
        pyramid.Remember(source, 19, space, large);
        Check(pyramid.Find(source, 13, space) is null && pyramid.RetainedBytes <= limit,
            "Least recently used levels must leave the byte budget before a large insertion");
        var retained = pyramid.Find(source, 19, space)!;
        Check(ReferenceEquals(retained, large) && retained.FloatBytes.SequenceEqual(
            ViewerDraftSource.Downsample(source, 19, CancellationToken.None, space).FloatBytes),
            "Every cached level must match direct downsampling of the full developed source exactly");
        var replacement = ViewerDraftSource.Downsample(revised, 7, CancellationToken.None, space);
        pyramid.Remember(revised, 7, space, replacement);
        Check(pyramid.Find(source, 19, space) is null && pyramid.RetainedBytes == replacement.FloatBytes.LongLength,
            "Remembering a new developed source must drop all old levels");
        pyramid.Clear();
        Check(pyramid.RetainedBytes == 0 && pyramid.Find(revised, 7, space) is null
            && retained.FloatBytes.SequenceEqual(large.FloatBytes), "Clearing cache must preserve buffers still held by rendering");

        var tinyCache = new ViewerDraftPyramid(small.FloatBytes.LongLength - 1);
        tinyCache.Remember(source, 7, space, small);
        tinyCache.Remember(source, 31, space, source);
        Check(tinyCache.RetainedBytes == 0 && tinyCache.Find(source, 7, space) is null,
            "Oversized levels and the full source must never count as retained draft levels");

        var shared = new ViewerCacheBudget(small.FloatBytes.LongLength + medium.FloatBytes.LongLength);
        var firstViewer = new ViewerDraftPyramid(limit, shared);
        var secondViewer = new ViewerDraftPyramid(limit, shared);
        firstViewer.Remember(source, 7, space, small);
        using var settledReply = shared.TryRetain(medium.FloatBytes.LongLength);
        secondViewer.Remember(source, 7, space, small);
        Check(settledReply is not null && secondViewer.Find(source, 7, space) is null
            && shared.RetainedBytes == small.FloatBytes.LongLength + medium.FloatBytes.LongLength,
            "Independent Viewer contexts and a settled reply share one optional-buffer ceiling");
        var sourceInUse = firstViewer.Find(source, 7, space)!;
        firstViewer.Clear();
        secondViewer.Remember(source, 7, space, small);
        Check(ReferenceEquals(secondViewer.Find(source, 7, space), small) && sourceInUse.FloatBytes.SequenceEqual(small.FloatBytes),
            "A retired cache releases room for another Viewer while its in-flight pixel buffer remains valid");
        secondViewer.Clear();
        settledReply!.Dispose();
        settledReply.Dispose();
        Check(shared.RetainedBytes == 0, "Clearing overlapping caches and disposing a lease twice returns its bytes once");

        var concurrent = new ViewerCacheBudget(64);
        var leases = new IDisposable?[64];
        Parallel.For(0, leases.Length, index => leases[index] = concurrent.TryRetain(4));
        Check(leases.Count(lease => lease is not null) == 16 && concurrent.RetainedBytes == 64
            && concurrent.TryRetain(65) is null, "Simultaneous cache admissions enforce the exact shared ceiling");
        Parallel.ForEach(leases, lease => lease?.Dispose());
        Check(concurrent.RetainedBytes == 0, "Independent dispatcher lifetimes release the allowance concurrently");
        Console.WriteLine("Draft pyramid: exact buffers, source and transfer isolation, byte budget, eviction and in-flight ownership passed.");
    }

    private static void TestExport(string folder)
    {
        foreach(var suffix in new[] {".tif",".png"})
        {
            var path=Path.Combine(folder,"probe"+suffix);var format=SpektraExport.PixelFormatFor(path);
            ushort[] values=format==PixelFormats.Rgba64
                ? new ushort[]{1,257,32769,65535,32770,65534,2001,14001,257,258,259,65535,62321,21234,54321,65535}
                : new ushort[]{1,257,32769,32770,65534,2001,257,258,259,62321,21234,54321};
            var expected=new byte[values.Length*2];Buffer.BlockCopy(values,0,expected,0,expected.Length);
            var stride=expected.Length/2;var image=BitmapSource.Create(2,2,150,200,format,null,values,stride);
            using var active = new CancellationTokenSource();
            SpektraExport.Save(path,image,active.Token);
            using var file=File.OpenRead(path);
            var decoded=BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat|BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad).Frames[0];
            Check(decoded.Format==format,"Wrong decoded pixel format");
            var actual=new byte[expected.Length];decoded.CopyPixels(actual,stride,0);
            Check(actual.SequenceEqual(expected),"16-bit samples changed");
            Check(decoded.ColorContexts?.Count>0,"ICC not embedded");
            var existing=File.ReadAllBytes(path);
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try {SpektraExport.Save(path,image,cancel.Token);throw new Exception("Expected canceled export");}catch(OperationCanceledException){assertions++;}
            Check(existing.SequenceEqual(File.ReadAllBytes(path)),"Canceled write changed destination");
            var eight=BitmapSource.Create(2,2,96,96,PixelFormats.Bgr24,null,new byte[12],6);
            try {SpektraExport.Save(path,eight,CancellationToken.None);throw new Exception("Expected high-depth validation");}catch(InvalidDataException){assertions++;}
            Check(existing.SequenceEqual(File.ReadAllBytes(path)),"Rejected write changed destination");
            Check(Math.Abs(decoded.DpiX - 150) < 0.03 && Math.Abs(decoded.DpiY - 200) < 0.03, "Encoder changed supplied non-square resolution");
            Console.WriteLine($"Export {suffix}: {decoded.Format}, exact samples, ICC, resolution and canceled/rejected writes passed.");
        }
    }

    private static void TestTiffCompression(string folder)
    {
        const int width = 127, height = 23, stride = width * 6;
        var source = new ushort[width * height * 3];
        for (var i = 0; i < source.Length; i++)
            source[i] = (ushort)((i * 121 + (i % 7)) & ushort.MaxValue);
        var image = BitmapSource.Create(width, height, 123, 150, PixelFormats.Rgb48, null, source, stride);
        var original = Path.Combine(folder, "compression-fixture.raw");
        File.WriteAllBytes(original, new byte[] { 42 });
        foreach (var (compression, tag) in new[] { (TiffCompressOption.None, (ushort)1), (TiffCompressOption.Lzw, (ushort)5),
                     (TiffCompressOption.Zip, (ushort)8) })
        {
            var path = Path.Combine(folder, "compression-" + compression + ".tif");
            SpektraExport.SaveApproved(OutputPathGuard.Approve(path, original), image, CancellationToken.None, tiffCompression: compression);
            using var file = File.OpenRead(path);
            var decoded = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];
            Check(Equals((decoded.Metadata as BitmapMetadata)?.GetQuery("/ifd/{ushort=259}"), tag),
                $"TIFF stores the exact {compression} lossless compression");
            var samples = new ushort[source.Length];
            decoded.CopyPixels(samples, stride, 0);
            Check(decoded.Format == PixelFormats.Rgb48 && samples.SequenceEqual(source)
                && decoded.ColorContexts is { Count: 1 } profiles && ExportColorProfile.Srgb.Matches(profiles[0]),
                $"TIFF {compression} round-trips every 16-bit code and embeds the exact ICC");
        }
    }

    private static void TestExportProfiles(string folder)
    {
        const int width = 257, height = 33;
        const int stride = width * 8;
        var original = Path.Combine(folder, "profile-fixture-original.raw");
        File.WriteAllBytes(original, new byte[] { 7, 2, 4 });
        var random = new Random(812627);
        var source = new ushort[width * height * 4];
        for (var index = 0; index < source.Length; index++) source[index] = (ushort)random.Next(65536);
        var bitmap = BitmapSource.Create(width, height, 123, 150, PixelFormats.Rgba64, null, source, stride);
        bitmap.Freeze();
        Check(ReferenceEquals(ExportColorProfile.ForRecipe(null, null).ConvertSrgb(bitmap, PixelFormats.Rgba64), bitmap),
            "the default sRGB export bypasses unnecessary profile conversion");
        foreach (var name in new[] { "ProPhoto RGB", "Linear Rec.2020" })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Profiles", name + ".icc");
            var profile = ExportColorProfile.ForRecipe(name + ".icc", path);
            Check(profile.Label == name && profile.RequiresConversion, "recipe identifies the exact RGB destination");
            var converted = Task.Run(() => profile.ConvertSrgb(bitmap, PixelFormats.Rgba64)).GetAwaiter().GetResult();
            var rgba = new ushort[source.Length];
            converted.CopyPixels(rgba, stride, 0);
            Check(Enumerable.Range(0, width * height).All(index => rgba[index * 4 + 3] == source[index * 4 + 3]),
                "ICC conversion preserved original 16-bit alpha across the background handoff");
            Check(rgba.Where((_, index) => index % 4 != 3).Distinct().Count() > 4096 && !rgba.SequenceEqual(source),
                "ICC conversion changes RGB while preserving far more than 256 levels");

            foreach (var extension in new[] { ".tif", ".png" })
            {
                var destination = Path.Combine(folder, name + extension);
                var format = SpektraExport.PixelFormatFor(destination);
                var native = profile.ConvertSrgb(bitmap, format);
                SpektraExport.SaveApproved(OutputPathGuard.Approve(destination, original), native, CancellationToken.None, outputProfile: profile);
                using var input = File.OpenRead(destination);
                var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad).Frames[0];
                Check(frame.Format == format && frame.ColorContexts is { Count: 1 } contexts && profile.Matches(contexts[0]),
                    "RGB48 TIFF and RGBA64 PNG embed precisely the chosen ICC bytes");
                var channelCount = format.BitsPerPixel / 16;
                var samples = new ushort[width * height * channelCount];
                frame.CopyPixels(samples, width * channelCount * 2, 0);
                Check(samples.Select((value, index) => value == rgba[(index / channelCount) * 4 + index % channelCount]).All(equal => equal),
                    "the output container and TIFF alpha strip preserve each converted 16-bit number");
                Check(Math.Abs(frame.DpiX - 123) < 0.03 && Math.Abs(frame.DpiY - 150) < 0.03,
                    "ICC conversion retains the supplied DPI");
            }
        }

        var snapshotPath = Path.Combine(folder, "captured-profile.icc");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Profiles", "ProPhoto RGB.icc"), snapshotPath);
        var captured = ExportColorProfile.ForRecipe("Captured RGB", snapshotPath);
        var initial = new ushort[source.Length];
        captured.ConvertSrgb(bitmap, PixelFormats.Rgba64).CopyPixels(initial, stride, 0);
        try { File.Copy(Path.Combine(AppContext.BaseDirectory, "Profiles", "Linear Rec.2020.icc"), snapshotPath, overwrite: true); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            // Some WIC builds protect a profile with an open read handle.
        }
        var unchanged = new ushort[source.Length];
        captured.ConvertSrgb(bitmap, PixelFormats.Rgba64).CopyPixels(unchanged, stride, 0);
        Check(initial.SequenceEqual(unchanged) && captured.Matches(captured.Context),
            "the captured ICC transform and bytes survive replacement of the recipe profile file");

        var approvalPath = Path.Combine(folder, "profile-awaiting-approval.icc");
        var approvedProfile = Path.Combine(AppContext.BaseDirectory, "Profiles", "ProPhoto RGB.icc");
        var differentProfile = Path.Combine(AppContext.BaseDirectory, "Profiles", "Linear Rec.2020.icc");
        File.Copy(approvedProfile, approvalPath);
        var pending = ExportColorProfile.ForRecipe("Profile awaiting approval", approvalPath);
        File.Copy(differentProfile, approvalPath, overwrite: true);
        Check(File.ReadAllBytes(approvalPath).SequenceEqual(File.ReadAllBytes(differentProfile)),
            "capturing a recipe fingerprint does not lock the external profile while approval is pending");
        ExpectProfileChange(pending.VerifyFiles, "the batch check rejects changed profile bytes before creating a plan");
        ExpectProfileChange(pending.Prepare, "the encoder refuses a profile that changed before acceptance");
        File.Copy(approvedProfile, approvalPath, overwrite: true);
        var restored = ExportColorProfile.ForRecipe("Restored profile", approvalPath);
        restored.Prepare();
        Check(restored.Sha256 == pending.Sha256 && restored.Matches(restored.Context),
            "a new approval uses the restored exact profile");
        var missing = Path.Combine(folder, "removed-profile.icc");
        File.Copy(approvedProfile, missing);
        var removed = ExportColorProfile.ForRecipe("Removed ICC", missing);
        File.Delete(missing);
        ExpectProfileChange(removed.Prepare, "an external profile removed during approval cannot be replaced by a fallback");

        var invalid = Path.Combine(folder, "non-rgb-profile.icc");
        var invalidHeader = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Profiles", "ProPhoto RGB.icc"));
        "CMYK"u8.CopyTo(invalidHeader.AsSpan(16, 4));
        File.WriteAllBytes(invalid, invalidHeader);
        try { ExportColorProfile.ForRecipe("CMYK fixture", invalid); throw new InvalidOperationException("Accepted non-RGB ICC data."); }
        catch (InvalidDataException) { assertions++; }
        Check(File.ReadAllBytes(original).SequenceEqual(new byte[] { 7, 2, 4 }), "managed ICC export left original source alone");

        static void ExpectProfileChange(Action check, string description)
        {
            try { check(); }
            catch (InvalidDataException error)
            {
                Check(error.Message.Contains("changed or became unavailable", StringComparison.Ordinal), description);
                return;
            }
            throw new InvalidOperationException(description);
        }
    }

    private static void TestLargeEncoding(string folder)
    {
        var original = Path.Combine(folder, "fixture-original.bin");
        File.WriteAllBytes(original, new byte[] { 9, 42, 101 });
        var metadata = new ExportMetadata("Fixture copyright", null, "Large high-depth synthetic image", null, null, Array.Empty<string>());
        foreach (var (width, height, suffix) in new[] { (10000, 6100, ".tif"), (9000, 5000, ".png") })
        {
            var destination = Path.Combine(folder, $"synthetic-{width}x{height}{suffix}");
            var format = SpektraExport.PixelFormatFor(destination);
            var channels = format.BitsPerPixel / 16;
            var stride = width * channels * sizeof(ushort);
            using var lease = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.ImageEncoding, (long)width * height, "");
            var row = new ushort[width * channels];
            for (var x = 0; x < width; x++)
            {
                row[x * channels] = (ushort)(1024 + x);
                row[x * channels + 1] = (ushort)(31001 + x);
                row[x * channels + 2] = (ushort)(60001 - x);
                if (channels == 4) row[x * channels + 3] = ushort.MaxValue;
            }
            var samples = new byte[checked(height * stride)];
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(row, 0, samples, y * stride, stride);
            var image = BitmapSource.Create(width, height, 300, 150, format, null, samples, stride);
            var timer = Stopwatch.StartNew();
            SpektraExport.SaveApproved(OutputPathGuard.Approve(destination, original), image, CancellationToken.None, metadata);
            using var input = File.OpenRead(destination);
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnDemand).Frames[0];
            Check(frame.Format == format && frame.PixelWidth == width && frame.PixelHeight == height,
                "Large output was resized or lost 16-bit precision");
            Check(frame.ColorContexts?.Count > 0 && Math.Abs(frame.DpiX - 300) < 0.03 && Math.Abs(frame.DpiY - 150) < 0.03,
                "Large output lost its ICC or resolution");
            foreach (var (x, y) in new[] { (0, 0), (width / 2, height / 2), (width - 1, height - 1) })
            {
                var read = new ushort[channels];
                frame.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), read, channels * sizeof(ushort), 0);
                Check(read.AsSpan().SequenceEqual(row.AsSpan(x * channels, channels)), "Large output changed a 16-bit sample");
            }
            Console.WriteLine($"Large {suffix} {width}x{height}, {format}, {input.Length} bytes in {timer.ElapsedMilliseconds} ms.");
        }
        Check(File.ReadAllBytes(original).AsSpan().SequenceEqual(new byte[] { 9, 42, 101 }),
            "Large encoders touched their source fixture");
    }

    private static void TestExportMetadata(string folder)
    {
        var original = Path.Combine(folder, "metadata-input.raw");
        File.WriteAllBytes(original, new byte[] { 1, 2, 3 });
        var metadata = new ExportMetadata("Rights \u00A9 2026", "Luka \u017D", "K\u00F8benhavn \u65E5 & portrait",
            new DateTime(2025, 3, 14, 13, 27, 6), 4, new[] { "street", "L\u00FCbecker", "street" },
            new[] { "People|street", "Places|L\u00FCbecker", "People|street" }, "Caption \u65E5", "City & people", "Spektrafilm for Capture One");
        foreach (var extension in new[] { ".tif", ".png" })
        {
            var path = Path.Combine(folder, "metadata-output" + extension);
            var format = SpektraExport.PixelFormatFor(path);
            const int width = 3, height = 2;
            var stride = width * format.BitsPerPixel / 8;
            var pixels = Enumerable.Range(0, height * stride).Select(index => (byte)index).ToArray();
            var image = BitmapSource.Create(width, height, 300, 150, format, null, pixels, stride);
            SpektraExport.SaveApproved(OutputPathGuard.Approve(path, original), image, CancellationToken.None, metadata);
            using var file = File.OpenRead(path);
            var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];
            var data = (BitmapMetadata)frame.Metadata;
            Check(frame.Format == format && frame.PixelWidth == width && frame.PixelHeight == height,
                "Metadata changed pixel format or dimensions");
            var decodedPixels = new byte[pixels.Length];
            frame.CopyPixels(decodedPixels, stride, 0);
            Check(decodedPixels.SequenceEqual(pixels) && frame.ColorContexts?.Count > 0, "Metadata changed 16-bit samples or pixel ICC");
            Check(Math.Abs(frame.DpiX - 300) < 0.03 && Math.Abs(frame.DpiY - 150) < 0.03, "Metadata changed resolution");

            if (extension == ".tif")
                Check(data.GetQuery("/ifd/{ushort=274}") is (ushort)1, "TIFF orientation is not normalized");
            var bytes = File.ReadAllBytes(path);
            var start = bytes.AsSpan().IndexOf("<?xpacket begin="u8);
            Check(start >= 0, "XMP packet missing");
            var endMessage = "<?xpacket end=\"w\"?>"u8;
            var end = bytes.AsSpan(start).IndexOf(endMessage);
            Check(end > 0, "XMP packet was truncated");
            var packet = System.Text.Encoding.UTF8.GetString(bytes, start, end + endMessage.Length);
            var xml = System.Xml.Linq.XDocument.Parse(packet);
            var fields = xml.Descendants().Single(element => element.Name.LocalName == "Description");
            Check(fields.Elements().Single(element => element.Name.LocalName == "title").Value == metadata.Title,
                "Unicode title or XML special character changed");
            Check(fields.Elements().Single(element => element.Name.LocalName == "rights").Value == metadata.Copyright,
                "Unicode rights changed");
            Check(fields.Elements().Single(element => element.Name.LocalName == "creator").Value == metadata.Creator,
                "Creator changed");
            Check(fields.Elements().Single(element => element.Name.LocalName == "subject").Descendants()
                .Where(element => element.Name.LocalName == "li").Select(element => element.Value)
                .SequenceEqual(new[] { "street", "L\u00FCbecker" }), "Keyword list changed");
            Check(fields.Elements().Single(element => element.Name.LocalName == "description").Value == metadata.Description
                && fields.Attributes().Single(attribute => attribute.Name.LocalName == "Headline").Value == metadata.Headline
                && fields.Attributes().Single(attribute => attribute.Name.LocalName == "CreatorTool").Value == metadata.Software,
                "Description, headline or software provenance changed");
            var approvedKeywords = ExportKeywordSet.ReadNativeXmp(packet);
            Check(approvedKeywords.Flat.SequenceEqual(new[] { "street", "L\u00FCbecker" })
                && approvedKeywords.Hierarchical.SequenceEqual(new[] { "People|street", "Places|L\u00FCbecker" }),
                "Native keyword parsing changed approved flat and hierarchical subjects");
            var properties = fields.Attributes().ToDictionary(attribute => (attribute.Name.NamespaceName, attribute.Name.LocalName),
                attribute => attribute.Value);
            const string tiff = "http://ns.adobe.com/tiff/1.0/", exif = "http://ns.adobe.com/exif/1.0/";
            Check(properties[(tiff, "Orientation")] == "1" && properties[(tiff, "ImageWidth")] == "3"
                && properties[(tiff, "ImageLength")] == "2" && properties[(exif, "PixelXDimension")] == "3"
                && properties[(exif, "PixelYDimension")] == "2", "Normalized orientation or dimensions missing");
            Check(properties[(exif, "DateTimeOriginal")] == "2025-03-14T13:27:06"
                && properties[("http://ns.adobe.com/xap/1.0/", "Rating")] == "4"
                && !properties.Keys.Any(key => key.LocalName.StartsWith("GPS", StringComparison.Ordinal)), "Capture date, rating or GPS policy changed");
            using var verification = File.OpenRead(path);
            var packetBytes = System.Text.Encoding.UTF8.GetBytes(packet);
            bool Verify() => extension == ".png" ? PngXmp.Contains(verification, packetBytes) : EncodedXmp.TiffContains(verification, packetBytes);
            Check(Verify(), "Container metadata validation rejected a correct packet");
            packetBytes[packetBytes.Length / 2] ^= 1;
            Check(!Verify(), "Container metadata validation accepted changed packet bytes");
        }
        Check(File.ReadAllBytes(original).SequenceEqual(new byte[] { 1, 2, 3 }), "Metadata output changed source bytes");
        Console.WriteLine("Export metadata: Unicode XMP, normalized dimensions and orientation, ICC, DPI and exact 16-bit samples passed.");
    }

    private static void TestExportResolution()
    {
        foreach (var (amount, unit) in new[] { (300d, "Inch"), (300 / 2.54, "Centimeter"), (300 / 25.4, "Millimeter") })
            Check(Math.Abs(ExportResolution.DotsPerInch(amount, unit) - 300) < 1e-9, $"Incorrect DPI conversion from {unit}");
        foreach (var amount in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.MaxValue })
        {
            try
            {
                ExportResolution.DotsPerInch(amount, "Millimeter");
                throw new InvalidOperationException("Invalid recipe resolution was accepted.");
            }
            catch (ArgumentOutOfRangeException) { assertions++; }
        }
        try
        {
            ExportResolution.DotsPerInch(300, "Unknown");
            throw new InvalidOperationException("Unknown units were accepted.");
        }
        catch (ArgumentException) { assertions++; }
    }
}
