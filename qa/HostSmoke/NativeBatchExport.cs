using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

public static partial class StartupHook
{
    private static async Task TestNativeBatchExport(object bridge, object panel, object spatial, object window)
    {
        var select = new FixtureSelection(panel);
        Check(select.Variants.Length == 4, "batch export uses the four-RAW private catalog");
        var variants = select.Variants.Take(3).ToArray();
        var outside = select.Variants[3];
        var profile = panel.GetType().Assembly.GetType("HostProfile")!;
        var readSelection = profile.GetMethod("CaptureSelectionSet", Flags)!;
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var exposure = Field(panel, "exposure")!;
        var edit = exposure.GetType().GetProperty("Value", Flags)!;
        var jobFolder = Path.Combine(folder, "Export jobs");
        var firstFolder = Directory.CreateDirectory(Path.Combine(folder, "batch-isolated-failure")).FullName;
        var secondFolder = Directory.CreateDirectory(Path.Combine(folder, "batch-isolated-cancel")).FullName;
        var stopFolder = Directory.CreateDirectory(Path.Combine(folder, "batch-stop")).FullName;
        var status = (TextBlock)Field(spatial, "exportStatus")!;
        var latest = Field(spatial, "exportJobs")!;
        var acceptedOutput = new byte[] { 16, 67, 82, 0, 17 };

        select.Select(variants[0], variants);
        var order = ((Array)readSelection.Invoke(null, new[] { window, Field(panel, "document") })!).Cast<object>()
            .Select(item => (Guid)Prop(Prop(item, "Variant")!, "UUID")!).ToArray();
        for (var i = 0; i < order.Length; i++)
        {
            select.Select(order[i], order[i]);
            edit.SetValue(exposure, i == 0 ? .94 : -.74);
            toggle.IsChecked = i != 2;
        }
        select.Select(variants[0], variants);
        await StopDuringPreparation();
        var declined = await Start((_, _) => null);
        Check(declined == 0 && !Directory.Exists(jobFolder), "declining the batch folder creates no job and writes no output");

        select.Select(order[0], order[0], order[1], order[2]);
        var started = Start((picker, _) =>
        {
            Check(picker.Title.Contains("3 images", StringComparison.Ordinal) && !picker.Multiselect,
                "one folder approval covers the three frozen native variants");
            select.Select(outside, outside);
            return firstFolder;
        });
        var middle = await WaitFor(order[1], firstFolder, started);
        var collision = (string)Prop(middle, "Destination")!;
        using (var added = new FileStream(collision, FileMode.CreateNew, FileAccess.Write))
            added.Write(acceptedOutput);
        Check(await started.WaitAsync(TimeSpan.FromSeconds(90)) == 2
            && status.Text.Contains("Saved 2 of 3 images", StringComparison.Ordinal)
            && status.Text.Contains("1 failed", StringComparison.Ordinal),
            "a changed destination fails only the middle image and the batch completes the remaining one");
        Check(File.ReadAllBytes(collision).SequenceEqual(acceptedOutput) && Equals(Field(panel, "displayedVariant"), outside),
            "batch preserves the file that appeared after approval and does not move the Viewer selection");
        var receipts = ReadReceipts(firstFolder);
        Check(receipts.Count(item => item.Stage == "Completed") == 2 && receipts.Single(item => item.Id == order[1]).Stage == "Failed",
            "each accepted image records its own native variant, immutable destination and terminal outcome");
        foreach (var item in receipts.Where(item => item.Stage == "Completed"))
            _ = VerifyOutput(item.Path);
        AssertFilmStage(receipts.Single(item => item.Id == order[0]).Job, expected: true);
        AssertFilmStage(receipts.Single(item => item.Id == order[2]).Job, expected: false);

        select.Select(order[0], order[0], order[1], order[2]);
        var cancelBatch = Start((_, _) => secondFolder);
        _ = await WaitFor(order[0], secondFolder, cancelBatch);
        var cancel = (Button)Field(spatial, "cancelExport")!;
        Check(cancel.Visibility == Visibility.Visible, "the native panel offers Cancel for the current queued image");
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(await cancelBatch.WaitAsync(TimeSpan.FromSeconds(90)) == 2
            && status.Text.Contains("1 canceled", StringComparison.Ordinal),
            "canceling an image leaves the rest of the batch running");
        var repeat = ReadReceipts(secondFolder);
        Check(repeat.Single(item => item.Id == order[0]).Stage == "Canceled"
            && !File.Exists(repeat.Single(item => item.Id == order[0]).Path),
            "the canceled variant has its own receipt and publishes no file");
        var comparison = ComparePixels(receipts.Single(item => item.Id == order[2]).Path, repeat.Single(item => item.Id == order[2]).Path);
        Check(comparison.Max <= 256 && comparison.Mean <= .5,
            $"the disabled image remains within native 16-bit repeatability (max {comparison.Max}, mean {comparison.Mean:F4} codes)");
        _ = VerifyOutput(repeat.Single(item => item.Id == order[1]).Path);
        AssertFilmStage(repeat.Single(item => item.Id == order[1]).Job, expected: true);

        select.Select(order[0], order[0], order[1], order[2]);
        var occupied = Path.Combine(stopFolder, Path.GetFileName(receipts.Single(item => item.Id == order[0]).Path));
        File.WriteAllBytes(occupied, acceptedOutput);
        var stopBatch = Start((_, _) => stopFolder);
        var current = await WaitFor(order[0], stopFolder, stopBatch);
        var stop = (Button)Field(spatial, "stopExportBatch")!;
        Check(stop.Visibility == Visibility.Visible && !Equals(Prop(current, "Destination"), occupied),
            "the batch allocates a new suffix for an existing file and exposes Stop after this image");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(await stopBatch.WaitAsync(TimeSpan.FromSeconds(90)) == 1 && File.ReadAllBytes(occupied).SequenceEqual(acceptedOutput)
            && ReadReceipts(stopFolder).Length == 1 && status.Text.Contains("2 canceled", StringComparison.Ordinal),
            "Stop completes the current image, leaves the existing file intact and starts no further job");

        var nativePrevious = Directory.CreateDirectory(Path.Combine(folder, "batch-native-previous")).FullName;
        var nativeDialog = Directory.CreateDirectory(Path.Combine(folder, "batch-native-dialog")).FullName;
        var routes = await TestNativeEntryPoints(nativePrevious, nativeDialog);
        await VerifyRecipeFilenamesAndWatermarks();
        File.WriteAllText(Path.Combine(folder, "native-batch-export.json"), JsonSerializer.Serialize(new
        {
            order, comparison = new { comparison.Max, comparison.Mean },
            failedBatch = receipts.Select(item => new { item.Id, item.Job, item.Path, item.Stage }),
            canceledBatch = repeat.Select(item => new { item.Id, item.Job, item.Path, item.Stage }),
            stoppedBatch = ReadReceipts(stopFolder).Select(item => new { item.Id, item.Job, item.Path, item.Stage }),
            nativeRoutes = routes,
        }));
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native batch export assertions");

        Task<int> Start(Func<OpenFolderDialog, Window?, string?> choose) =>
            (Task<int>)Call(panel, "ExportSelectedImagesAsync", choose)!;

        async Task VerifyRecipeFilenamesAndWatermarks()
        {
            var recipe = ReadSelectedRecipeForTest(bridge, out var suppress);
            var type = recipe.GetType();
            var originalValues = new[] { "FileType", "TiffBits", "DestinationFilename", "FileOverwriteAction", "WatermarkType",
                "WatermarkText", "WatermarkTextFont", "WatermarkTextColor", "WatermarkTextScale", "WatermarkTextOpacity",
                "WatermarkHTextPlacement", "WatermarkVTextPlacement" }
                .Select(name => type.GetProperty(name, Flags)!).ToDictionary(p => p.Name, p => (Property: p, Value: p.GetValue(recipe)));
            void Set(string name, object value) => originalValues[name].Property.SetValue(recipe, value);
            void Choose(string name, string value) => Set(name, Enum.Parse(originalValues[name].Property.PropertyType, value));
            var host = panel.GetType().Assembly.GetType("HostImageSource", true)!;
            object ReadRecipe() => host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
            var selected = new[] { order[1], order[2] };
            var marks = new System.Collections.Generic.Dictionary<Guid, (string Stem, string Hash, Int32Rect Bounds)>();
            using var font = new System.Drawing.Font("Segoe UI", 24, System.Drawing.GraphicsUnit.Pixel);
            var watermarkedFolder = Directory.CreateDirectory(Path.Combine(folder, "batch-source-name-watermarks")).FullName;
            var allSkippedFolder = Directory.CreateDirectory(Path.Combine(folder, "batch-shared-skip")).FullName;
            var mixedFolder = Directory.CreateDirectory(Path.Combine(folder, "batch-mixed-skip")).FullName;
            var ordinary = ReadReceipts(nativePrevious);
            suppress.SetValue(recipe, true);
            try
            {
                Choose("FileType", "Png");
                Set("DestinationFilename", "");
                Choose("WatermarkType", "Text");
                Set("WatermarkText", "Proof " + NativeNameToken("OriginalName"));
                Set("WatermarkTextFont", font);
                Set("WatermarkTextColor", System.Drawing.Color.White);
                Set("WatermarkTextScale", 1.2f);
                Set("WatermarkTextOpacity", .85f);
                Set("WatermarkHTextPlacement", .40f);
                Set("WatermarkVTextPlacement", .33f);

                foreach (var id in selected)
                {
                    select.Select(id, id);
                    toggle.IsChecked = id == order[1];
                    var item = Prop(profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!, "Variant")!;
                    var image = Prop(item, "CoreImage")!;
                    var source = Path.GetFileNameWithoutExtension((string)Prop(image, "FullpathFilename")!);
                    var snapshot = ReadRecipe();
                    var mark = Prop(snapshot, "Watermark");
                    Check(mark is not null && Prop(snapshot, "SuggestedFilename") is null,
                        "the native selected variant supplies a token watermark and no filename suggestion");
                    marks.Add(id, (source, (string)Prop(mark!, "PixelsSha256")!,
                        new Int32Rect((int)Prop(mark!, "X")!, (int)Prop(mark!, "Y")!, (int)Prop(mark!, "Width")!, (int)Prop(mark!, "Height")!)));
                }
                Check(marks.Values.Select(mark => mark.Hash).Distinct().Count() == 2,
                    "Capture One renders distinct Original Name watermarks for the two approved RAWs");
                select.Select(selected[0], selected);
                Check(await Start((picker, _) =>
                {
                    Check(picker.Title.Contains("16-bit PNG", StringComparison.Ordinal), "the batch approves the selected PNG recipe");
                    Set("WatermarkText", "Changed after capturing both images");
                    select.Select(outside, outside);
                    return watermarkedFolder;
                }).WaitAsync(TimeSpan.FromSeconds(90)) == 2,
                    "both source-based PNG fallback names export after selection and recipe text change at folder approval");
                foreach (var item in ReadReceipts(watermarkedFolder))
                {
                    var mark = marks[item.Id];
                    Check(item.Stage == "Completed" && Path.GetFileName(item.Path) == mark.Stem + "-Spektrafilm.png",
                        "the durable output uses the selected extension with the original image's fallback name");
                    var baseline = VerifyOutput(ordinary.Single(entry => entry.Id == item.Id).Path);
                    var marked = VerifyOutput(item.Path);
                    Check(marked.Format == PixelFormats.Rgba64 && marked.PixelWidth == baseline.PixelWidth && marked.PixelHeight == baseline.PixelHeight,
                        "the batch watermark result retains 16-bit PNG geometry");
                    var difference = Difference(baseline, marked, mark.Bounds);
                    Check(difference.MarkPixels > 35 && difference.UnmarkedPixels == 0,
                        $"the variant retains its captured native watermark after approval ({difference.MarkPixels} marked pixels)");
                }

                select.Select(selected[0], selected);
                var orderedVariants = ((Array)readSelection.Invoke(null, new[] { window, Field(panel, "document") })!).Cast<object>()
                    .Select(item => Prop(item, "Variant")!).ToArray();
                var nativeTitles = orderedVariants.Select(item =>
                {
                    var field = item.GetType().GetProperty("IptcStatusTitle", Flags)!;
                    return (Variant: item, Field: field, Original: field.GetValue(item));
                }).ToArray();
                try
                {
                    nativeTitles[0].Field.SetValue(nativeTitles[0].Variant, "Batch watermark proof");
                    nativeTitles[1].Field.SetValue(nativeTitles[1].Variant, "");
                    Set("WatermarkText", NativeNameToken("MetadataStatusTitle"));
                    for (var index = 0; index < orderedVariants.Length; index++)
                    {
                        var id = (Guid)Prop(orderedVariants[index], "UUID")!;
                        select.Select(id, id);
                        Check(Prop(ReadRecipe(), "Omissions")?.ToString() == (index == 0 ? "None" : "Watermark"),
                            "only the variant with an empty Title token lacks a watermark");
                    }
                    select.Select(selected[0], selected);
                    var offered = false;
                    var receiptsBefore = Directory.GetFiles(jobFolder, "*.json", SearchOption.AllDirectories).Length;
                    Check(await Start((picker, _) =>
                    {
                        offered = true;
                        Check(picker.Title.Contains("Omits recipe watermark", StringComparison.Ordinal),
                            "folder approval discloses a watermark omission on a later selected image");
                        return null;
                    }).WaitAsync(TimeSpan.FromSeconds(40)) == 0 && offered
                        && Directory.GetFiles(jobFolder, "*.json", SearchOption.AllDirectories).Length == receiptsBefore,
                        "declining the partially supported recipe creates no batch job");
                }
                finally
                {
                    foreach (var title in nativeTitles) title.Field.SetValue(title.Variant, title.Original);
                }

                Choose("FileType", "Tiff");
                Set("TiffBits", 16);
                Choose("WatermarkType", "None");
                Choose("FileOverwriteAction", "Skip");
                Set("DestinationFilename", "Already delivered");
                var existing = Path.Combine(allSkippedFolder, "Already delivered.tif");
                File.WriteAllBytes(existing, acceptedOutput);
                select.Select(selected[0], selected);
                Check(await Start((picker, _) =>
                {
                    Check(picker.Title.Contains("Skip existing recipe files", StringComparison.Ordinal), "folder approval discloses the native Skip policy");
                    return allSkippedFolder;
                }).WaitAsync(TimeSpan.FromSeconds(40)) == 0 && status.Text.Contains("2 skipped", StringComparison.Ordinal)
                    && File.ReadAllBytes(existing).SequenceEqual(acceptedOutput) && Directory.GetFiles(allSkippedFolder).Length == 1,
                    "both images keep the same existing exact recipe filename without creating a suffix");
                var skipped = ReadReceipts(allSkippedFolder);
                Check(skipped.Length == 2 && skipped.All(item => item.Stage == "Skipped" && item.Path == existing),
                    "both read-only skips retain distinct terminal receipts");
                var skippedJobs = skipped.Select(item => item.Job.ToString()).ToArray();
                Check(!File.ReadLines(Path.Combine(folder, "panel.log")).Any(line => line.Contains("export.performance.v2")
                    && skippedJobs.Any(id => line.Contains(id, StringComparison.OrdinalIgnoreCase))),
                    "skipped images never enter the renderer or encoder");

                Set("DestinationFilename", NativeNameToken("OriginalName"));
                var retained = Path.Combine(mixedFolder, marks[selected[0]].Stem + ".tif");
                File.WriteAllBytes(retained, acceptedOutput);
                Check(await Start((_, _) => mixedFolder).WaitAsync(TimeSpan.FromSeconds(75)) == 1
                    && status.Text.Contains("1 skipped", StringComparison.Ordinal)
                    && File.ReadAllBytes(retained).SequenceEqual(acceptedOutput),
                    "an existing recipe file skips its image and leaves the next export running");
                var mixed = ReadReceipts(mixedFolder);
                Check(mixed.Single(item => item.Id == selected[0]).Stage == "Skipped"
                    && mixed.Single(item => item.Id == selected[1]).Stage == "Completed",
                    "the batch journals the skipped and exported variants independently");
                _ = VerifyOutput(mixed.Single(item => item.Id == selected[1]).Path);
                Set("TiffBits", 8);
                var offeredSubstitute = false;
                Check(await Start((picker, _) =>
                {
                    offeredSubstitute = true;
                    Check(picker.Title.Contains("Recipe requests 8-bit TIFF", StringComparison.Ordinal)
                        && !picker.Title.Contains("Skip existing recipe files", StringComparison.Ordinal),
                        "a required depth substitution is disclosed before approval and keeps its own filename");
                    return null;
                }).WaitAsync(TimeSpan.FromSeconds(40)) == 0 && offeredSubstitute && File.ReadAllBytes(retained).SequenceEqual(acceptedOutput),
                    "declining the 16-bit substitute leaves the occupied recipe output intact");
            }
            finally
            {
                try { foreach (var (member, value) in originalValues.Values) member.SetValue(recipe, value); }
                finally { suppress.SetValue(recipe, false); }
            }
        }

        static (int MarkPixels, int UnmarkedPixels) Difference(BitmapSource before, BitmapSource after, Int32Rect region)
        {
            var leftChannels = before.Format.BitsPerPixel / 16;
            var rightChannels = after.Format.BitsPerPixel / 16;
            var left = new ushort[before.PixelWidth * leftChannels];
            var right = new ushort[after.PixelWidth * rightChannels];
            var marked = 0;
            var unmarked = 0;
            for (var y = 0; y < before.PixelHeight; y++)
            {
                var includesMark = y >= region.Y && y < region.Y + region.Height;
                if (!includesMark && y % 23 != 0) continue;
                var row = new Int32Rect(0, y, before.PixelWidth, 1);
                before.CopyPixels(row, left, left.Length * sizeof(ushort), 0);
                after.CopyPixels(row, right, right.Length * sizeof(ushort), 0);
                for (var x = 0; x < before.PixelWidth; x++)
                {
                    var coversMark = includesMark && x >= region.X && x < region.X + region.Width;
                    if (!coversMark && x % 19 != 0) continue;
                    if (Math.Abs(left[x * leftChannels] - right[x * rightChannels]) <= 1024) continue;
                    if (coversMark) marked++;
                    else unmarked++;
                }
            }
            return (marked, unmarked);
        }

        async Task StopDuringPreparation()
        {
            var activity = Field(spatial, "exportActivity")!;
            var stop = (Button)Field(spatial, "stopExportBatch")!;
            var scheduler = (SemaphoreSlim)panel.GetType().Assembly.GetType("HostImageSource", true)!
                .GetField("SchedulerSlot", Flags)!.GetValue(null)!;
            Call(spatial, "SetEnabled", false);
            await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
            Check(await scheduler.WaitAsync(TimeSpan.FromSeconds(10)), "the disposable fixture holds the actual native development scheduler");
            try
            {
                var offered = false;
                var preparing = Start((_, _) => { offered = true; return firstFolder; });
                Check(stop.Visibility == Visibility.Visible && Equals(stop.Content, "Stop batch") && !preparing.IsCompleted,
                    "Stop batch is available while the batch waits for Capture One");
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(await preparing.WaitAsync(TimeSpan.FromSeconds(4)) == 0 && !offered && scheduler.CurrentCount == 0
                    && !Directory.Exists(jobFolder) && status.Text == "Batch export canceled.",
                    "Stop releases the preparation wait before Capture One's scheduler is available");
            }
            finally
            {
                scheduler.Release();
                Call(spatial, "SetEnabled", toggle.IsChecked == true);
            }

            var changed = activity.GetType().GetEvent("Changed", Flags)!;
            var approval = Directory.CreateDirectory(Path.Combine(folder, "batch-stopped-during-checks")).FullName;
            var preserved = Path.Combine(approval, "existing.png");
            File.WriteAllBytes(preserved, acceptedOutput);
            var approved = false;
            var stopped = false;
            Action stopOnFirstCheck = () =>
            {
                if (stopped || !approved || !status.Text.StartsWith("Checking batch image ", StringComparison.Ordinal)) return;
                stopped = true;
                stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            changed.GetAddMethod(nonPublic: true)!.Invoke(activity, new object[] { stopOnFirstCheck });
            try
            {
                Check(await Start((_, _) => { approved = true; return approval; }).WaitAsync(TimeSpan.FromSeconds(15)) == 0
                    && stopped && Prop(activity, "BatchActive") is false && !Directory.Exists(jobFolder)
                    && Directory.EnumerateFiles(approval).SequenceEqual(new[] { preserved })
                    && File.ReadAllBytes(preserved).SequenceEqual(acceptedOutput),
                    "stopping after folder approval cancels verification before accepting a plan or touching existing files");
            }
            finally { changed.GetRemoveMethod(nonPublic: true)!.Invoke(activity, new object[] { stopOnFirstCheck }); }
        }

        async Task<int> TestNativeEntryPoints(string previousFolder, string dialogFolder)
        {
            var routeProperty = bridge.GetType().GetProperty("Batch", Flags)!;
            var originalRoute = routeProperty.GetValue(bridge) ?? throw new InvalidOperationException("The native batch route was not attached.");
            var previous = Field(Field(bridge, "previous")!, "command")!;
            var normal = Field(Field(bridge, "normal")!, "command")!;
            string? destination = null;
            var offers = 0;
            Task<int>? job = null;
            Window? offeredOwner = null;
            Task<int> Route(Window? owner)
            {
                return job = (Task<int>)Call(panel, "ExportSelectedImagesWithOwnerAsync", owner,
                    (Func<OpenFolderDialog, Window?, string?>)((picker, dialogOwner) =>
                    {
                        Check(ReferenceEquals(owner, dialogOwner) && picker.Title.Contains("2 images", StringComparison.Ordinal),
                            "the native command offers one folder for its actual two-image selection");
                        offeredOwner = dialogOwner;
                        offers++;
                        return destination;
                    }))!;
            }
            var route = Activator.CreateInstance(originalRoute.GetType(), Flags, null,
                new[] { (object)(Func<Window?, Task<int>>)Route, Prop(originalRoute, "Ready"), Prop(originalRoute, "NeedsLook") }, null)!;
            routeProperty.SetValue(bridge, route);
            try
            {
                // The primary has film switched off. The second image must still route through Spektrafilm.
                select.Select(order[2], order[2], order[1]);
                Check(!toggle.IsChecked.GetValueOrDefault() && Call(bridge, "RequiresSpektrafilm", true) is true,
                    "a non-primary enabled look claims the batch even when the primary is disabled");
                ExecuteMultiple(previous);
                Check(job is not null && await job.WaitAsync(TimeSpan.FromSeconds(50)) == 0 && offeredOwner is null && offers == 1
                    && ReadReceipts(previousFolder).Length == 0, "canceling folder approval from Previous Settings creates no output");
                await WaitForBridge();

                destination = previousFolder;
                job = null;
                ExecuteMultiple(previous);
                Check(job is not null && await job.WaitAsync(TimeSpan.FromSeconds(60)) == 2 && offers == 2,
                    "Capture One Export with Previous Settings queues both selected images once");
                await WaitForBridge();
                VerifyNativeJobs(previousFolder);

                destination = dialogFolder;
                job = null;
                offeredOwner = null;
                Window? actualOwner = null;
                Exception? failure = null;
                using var poll = new System.Windows.Forms.Timer { Interval = 40 };
                var deadline = Stopwatch.StartNew();
                poll.Tick += (_, _) =>
                {
                    try
                    {
                        if (deadline.Elapsed > TimeSpan.FromSeconds(60))
                            throw new TimeoutException("The real multi-selection Export dialog did not complete.");
                        if (job is { IsCompletedSuccessfully: true } task && task.Result != 2)
                            throw new InvalidOperationException("The real native batch did not save both files. " + status.Text);
                        if (actualOwner is not null || Prop(bridge, "Dialog") is not Window { IsVisible: true } open
                            || open.DataContext is not { } model || Prop(model, "ExportCommand") is not ICommand confirm
                            || !confirm.CanExecute(null)) return;
                        actualOwner = open;
                        confirm.Execute(null);
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        poll.Stop();
                        (actualOwner ?? Prop(bridge, "Dialog") as Window)?.Close();
                    }
                };
                poll.Start();
                try { ExecuteMultiple(normal); }
                finally { poll.Stop(); }
                if (failure is not null) throw failure;
                Check(job is not null && await job.WaitAsync(TimeSpan.FromSeconds(60)) == 2 && offers == 3
                    && actualOwner is not null && ReferenceEquals(offeredOwner, actualOwner)
                    && Prop(bridge, "Dialog") is null && Prop(window, "ExportDialogWindow") is null,
                    "the actual native Export window owns its folder picker and releases confirmation after both results");
                await WaitForBridge();
                VerifyNativeJobs(dialogFolder);
                select.Select(order[1], order[1]);
                toggle.IsChecked = false;
                select.Select(order[2], order[2], order[1]);
                Check(Call(bridge, "RequiresSpektrafilm", true) is false,
                    "a selection with every saved master switch off remains eligible for ordinary Capture One export");
                return offers;
            }
            finally { routeProperty.SetValue(bridge, originalRoute); }

            static void ExecuteMultiple(object command)
            {
                var execute = command.GetType().GetMethod("Execute", Flags, null, new[] { typeof(bool) }, null)
                    ?? throw new MissingMethodException(command.GetType().FullName, "Execute(Boolean)");
                execute.Invoke(command, new object[] { true });
            }

            async Task WaitForBridge()
            {
                var wait = Stopwatch.StartNew();
                while (Prop(bridge, "Busy") is true && wait.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
                Check(Prop(bridge, "Busy") is false, "the native route releases its in-flight batch reservation");
            }

            void VerifyNativeJobs(string folderPath)
            {
                var items = ReadReceipts(folderPath);
                Check(items.Length == 2 && items.All(item => item.Stage == "Completed")
                    && items.Select(item => item.Id).ToHashSet().SetEquals(new[] { order[1], order[2] }),
                    "the native command journals one completed output per selected variant");
                foreach (var item in items) _ = VerifyOutput(item.Path);
                AssertFilmStage(items.Single(item => item.Id == order[1]).Job, expected: true);
                AssertFilmStage(items.Single(item => item.Id == order[2]).Job, expected: false);
            }
        }

        async Task<object> WaitFor(Guid variant, string outputFolder, Task<int> running)
        {
            var watch = Stopwatch.StartNew();
            while (!running.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(75))
            {
                if (Call(latest, "ReadLatest") is { } item && Equals(Prop(item, "VariantId"), variant)
                    && Prop(item, "Destination") is string path && Path.GetDirectoryName(path) == outputFolder
                    && Prop(item, "Finished") is false)
                    return item;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("The selected native variant did not reach its accepted batch job. " + status.Text);
        }

        (Guid Id, Guid Job, string Path, string Stage)[] ReadReceipts(string outputFolder) =>
            Directory.GetFiles(jobFolder, "*.json", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != "pending.json").Select(file =>
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(file));
                var root = json.RootElement;
                return (root.GetProperty("VariantId").GetGuid(), root.GetProperty("JobId").GetGuid(),
                    root.GetProperty("Destination").GetString()!, root.GetProperty("Stage").GetString()!);
            }).Where(row => Path.GetDirectoryName(row.Item3) == outputFolder).ToArray();

        BitmapFrame VerifyOutput(string path)
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames.Single();
            Check((frame.Format == PixelFormats.Rgb48 || frame.Format == PixelFormats.Rgba64)
                && frame.ColorContexts is { Count: 1 }, "the queued output contains native high-depth pixels and a recipe ICC");
            return frame;
        }

        (int Max, double Mean) ComparePixels(string first, string second)
        {
            var a = VerifyOutput(first);
            var b = VerifyOutput(second);
            using var pa = a.ColorContexts![0].OpenProfileStream();
            using var pb = b.ColorContexts![0].OpenProfileStream();
            Check(a.Format == b.Format && a.PixelWidth == b.PixelWidth && a.PixelHeight == b.PixelHeight
                && SHA256.HashData(pa).SequenceEqual(SHA256.HashData(pb)), "repeat exports retain exact format, geometry and ICC bytes");
            var stride = a.PixelWidth * a.Format.BitsPerPixel / 8;
            var ar = new byte[stride];
            var br = new byte[stride];
            int max = 0;
            long total = 0;
            for (var y = 0; y < a.PixelHeight; y++)
            {
                var rect = new Int32Rect(0, y, a.PixelWidth, 1);
                a.CopyPixels(rect, ar, stride, 0);
                b.CopyPixels(rect, br, stride, 0);
                var av = MemoryMarshal.Cast<byte, ushort>(ar);
                var bv = MemoryMarshal.Cast<byte, ushort>(br);
                for (var i = 0; i < av.Length; i++)
                {
                    var difference = Math.Abs(av[i] - bv[i]);
                    total += difference;
                    max = Math.Max(max, difference);
                }
            }
            return (max, total / ((double)a.PixelHeight * stride / 2));
        }

        void AssertFilmStage(Guid job, bool expected)
        {
            var film = File.ReadLines(Path.Combine(folder, "panel.log")).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var details = film.Select(record => record.RootElement)
                    .Where(record => record.GetProperty("Message").GetString() == "export.performance.v2")
                    .Select(record => record.GetProperty("Details")).Single(record => record.GetProperty("JobId").GetGuid() == job);
                Check((details.GetProperty("FilmRenderAndTransportMs").ValueKind == JsonValueKind.Number) == expected,
                    expected ? "the enabled image renders its film look" : "the disabled image retains Capture One development without film");
            }
            finally { foreach (var document in film) document.Dispose(); }
        }
    }
}
