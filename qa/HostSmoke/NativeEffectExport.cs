using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
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
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

public static partial class StartupHook
{
    private static async Task TestNativeEffectExportMatrix(FieldInfo hookField, object hosted, object panel, object spatial,
        object window, string phase)
    {
        var selection = new FixtureSelection(panel);
        var encodings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DNG"] = "srgb", ["CR3"] = "adobe_rgb", ["RAF"] = "prophoto", ["NEF"] = "linear_rec2020",
        };
        Check(selection.Variants.Select(selection.Format).ToHashSet(StringComparer.Ordinal).SetEquals(encodings.Keys),
            "native recipe effect matrix uses a DNG, CR3, RAF and NEF from the disposable catalogue");
        var input = (ComboBox)Field(spatial, "inputSpace")!;
        foreach (var variant in selection.Variants)
        {
            selection.Select(variant, variant);
            var prior = input.SelectedItem;
            var format = selection.Format(variant);
            try
            {
                input.SelectedItem = input.ItemsSource.Cast<object>().Single(item => Equals(Prop(item, "Id"), encodings[format]));
                await TestNativeEffectExport(hookField, hosted, panel, spatial, window, phase + "-" + format);
            }
            finally { input.SelectedItem = prior; }
        }
        TestMixedNativeExportSelection(hookField.GetValue(null)!, panel, spatial, selection, phase);
        TestSelectedVariantSave(panel, selection);
    }

    private static void TestMixedNativeExportSelection(object bridge, object panel, object spatial,
        FixtureSelection selection, string phase)
    {
        var originallyDisplayed = (Guid)Field(panel, "displayedVariant")!;
        var primary = selection.Variants[0];
        var secondary = selection.Variants[1];
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var exposure = Field(panel, "exposure")!;
        var exposureValue = exposure.GetType().GetProperty("Value", Flags)!;
        var jobs = Field(spatial, "exportJobs")!;
        var lastJob = Call(jobs, "ReadLatest");
        bool Guarded(bool editMultiple) => Call(bridge, "ShouldIntercept", editMultiple) is true;
        selection.Select(primary, primary);
        var primaryEnabled = toggle.IsChecked;
        toggle.IsChecked = false;
        selection.Select(secondary, secondary);
        var secondaryEnabled = toggle.IsChecked;
        var secondaryExposure = (double)exposureValue.GetValue(exposure)!;
        try
        {
            toggle.IsChecked = true;
            exposureValue.SetValue(exposure, 1.37);
            selection.Select(primary, primary, secondary);
            Check(Prop(panel, "IsSpektraExportActive") is false && Call(bridge, "HasSingleSelection", true) is false,
                "the real browser selection includes an enabled secondary look while the primary master is off");
            Check(Guarded(true), "both native batch export routes retain the active secondary look at preflight");
            Check(!Guarded(false), "the normal primary-only native command still delegates when its master is off");

            selection.Select(secondary, secondary);
            Check(Guarded(true) && Call(bridge, "HasSingleSelection", true) is true,
                "selecting the active variant alone can use the Spektrafilm export route");
            var options = panel.GetType().GetField("options", Flags)!;
            var loaded = options.GetValue(panel);
            try
            {
                options.SetValue(panel, null);
                Check(Prop(panel, "IsSpektraExportActive") is false && Guarded(false),
                    "the persisted enabled look keeps native Export guarded while the selected panel is loading");
                var owner = (Forms.Form)Field(bridge, "window")!;
                var caption = (string)bridge.GetType().Assembly.GetType("StartupHook", true)!
                    .GetField("ProductName", Flags)!.GetRawConstantValue()!;
                foreach (var (binding, name) in new[] { ("normal", "Export"), ("previous", "Export with Previous Settings") })
                {
                    var command = Field(Field(bridge, binding)!, "command")!;
                    var warned = DismissOwnedMessageBox(owner, caption, Forms.DialogResult.OK,
                        () => ExecuteNativeExportCommand(command),
                        "Wait for the saved Spektrafilm look to load. Open the tool to see its status.");
                    Check(warned && Prop(bridge, "Dialog") is null && Equals(lastJob, Call(jobs, "ReadLatest")),
                        $"the actual native {name} command shows the saved-look warning before accepting a job");
                }
            }
            finally { options.SetValue(panel, loaded); }
            toggle.IsChecked = false;
            selection.Select(primary, primary, secondary);
            Check(!Guarded(true), "a real multiple-variant selection with both masters off delegates to Capture One");
            var lookPath = (string)Call(Field(panel, "looks")!, "GetPath", primary)!;
            using (var locked = File.Open(lookPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!Guarded(false), "an explicitly disabled primary still delegates while its saved file is temporarily locked");
            Check(Equals(lastJob, Call(jobs, "ReadLatest")), "selection eligibility neither accepted a job nor changed the last completed export");
            File.WriteAllText(Path.Combine(folder, phase + "-export-selection.json"),
                JsonSerializer.Serialize(new { mixedBatchGuarded = true, primaryOnlyDelegated = true, allDisabledDelegated = true,
                    loadingSavedLookGuarded = true, loadingCommandsWarned = true, disabledWithLockedStateDelegated = true }));
        }
        finally
        {
            selection.Select(secondary, secondary);
            exposureValue.SetValue(exposure, secondaryExposure);
            toggle.IsChecked = secondaryEnabled;
            selection.Select(primary, primary);
            toggle.IsChecked = primaryEnabled;
            selection.Select(originallyDisplayed, originallyDisplayed);
        }
    }

    private static void TestSelectedVariantSave(object panel, FixtureSelection selection)
    {
        var originallyDisplayed = (Guid)Field(panel, "displayedVariant")!;
        var earlier = selection.Variants[0];
        var current = selection.Variants[1];
        var store = Field(panel, "looks")!;
        var exposure = Field(panel, "exposure")!;
        var value = exposure.GetType().GetProperty("Value", Flags)!;
        var previousExposure = new Dictionary<Guid, double>();
        foreach (var variant in new[] { earlier, current })
        {
            selection.Select(variant, variant);
            previousExposure[variant] = (double)value.GetValue(exposure)!;
        }
        const double earlierEdit = 2.125;
        const double currentEdit = 1.625;
        var earlierPath = (string)Call(store, "GetPath", earlier)!;
        var currentPath = (string)Call(store, "GetPath", current)!;
        var recovery = (string)Call(store, "GetRecoveryPath", earlier)!;
        var disposableRoot = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        Check(new[] { earlierPath, currentPath, recovery }.All(path =>
                Path.GetFullPath(path).StartsWith(disposableRoot, StringComparison.OrdinalIgnoreCase))
            && !File.Exists(recovery) && previousExposure[earlier] != earlierEdit && previousExposure[current] != currentEdit,
            "the selected-variant recovery fixture starts with distinct values and disposable look paths");
        static double ReadExposure(string path)
        {
            using var input = File.OpenRead(path);
            using var state = JsonDocument.Parse(input);
            return state.RootElement.GetProperty("Look").GetProperty("Exposure").GetDouble();
        }
        try
        {
            selection.Select(earlier, earlier);
            using (var locked = File.Open(earlierPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                value.SetValue(exposure, earlierEdit);
                Check(Prop(store, "HasPending") is true && Prop(store, "HasUnprotectedPending") is false && File.Exists(recovery),
                    "a locked earlier variant keeps its real slider edit in durable recovery");
                selection.Select(current, current);
                File.SetLastWriteTimeUtc(recovery, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                var checkpoint = File.ReadAllBytes(recovery);
                value.SetValue(exposure, currentEdit);
                Check(ReadExposure(currentPath) == currentEdit, "a different real Capture One variant commits its slider movement");
                Check(File.GetLastWriteTimeUtc(recovery).Year == 2019 && File.ReadAllBytes(recovery).SequenceEqual(checkpoint),
                    "saving the current variant leaves the earlier variant's recovery bytes and timestamp untouched");
            }
            Call(store, "Flush");
            Check(ReadExposure(earlierPath) == earlierEdit && ReadExposure(currentPath) == currentEdit && !File.Exists(recovery),
                "retry publishes the earlier value and preserves the current variant's committed value");
        }
        finally
        {
            foreach (var (variant, original) in previousExposure)
            {
                selection.Select(variant, variant);
                value.SetValue(exposure, original);
            }
            selection.Select(originallyDisplayed, originallyDisplayed);
        }
    }

    private static async Task TestNativeEffectExport(FieldInfo hookField, object hosted, object panel, object spatial, object window,
        string caseName, bool compareGrain = false, bool compareClone = false, bool compareCompression = false,
        bool compareFolders = false, bool comparePanelModes = false, bool compareProfiles = false,
        bool changeSourceRevision = false, bool compareRecipeRestart = false)
    {
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var inputEncoding = (string)Prop(((ComboBox)Field(spatial, "inputSpace")!).SelectedItem, "Id")!;
        var expectedVariant = (Guid)Field(panel, "displayedVariant")!;
        var strength = Field(panel, "strength")!;
        var exposure = Field(panel, "exposure")!;
        var choices = (IDictionary)Field(spatial, "choices")!;
        var diffusion = (ComboBox)choices["camera-diffusion-family"]!;
        var values = (IDictionary)Field(spatial, "values")!;
        var diffusionStrength = values["camera-diffusion-strength"]!;
        var halation = values["halation-amount"]!;
        var scatter = values["scatter-amount"]!;
        var priorEnabled = toggle.IsChecked;
        var priorDiffusion = diffusion.SelectedItem;
        var changedSliders = new[] { strength, exposure, diffusionStrength, halation, scatter }
            .Select(control => (Control: control, Property: control.GetType().GetProperty("Value", Flags)!, Before: Prop(control, "Value"))).ToArray();
        void Value(int index, double value) => changedSliders[index].Property.SetValue(changedSliders[index].Control, value);
        void Diffusion(string key) => diffusion.SelectedItem = diffusion.ItemsSource.Cast<object>().Single(item => Equals(Prop(item, "Id"), key));
        var current = (IDisposable)hookField.GetValue(null)!;
        var host = panel.GetType().Assembly.GetType("HostImageSource", true)!;
        var recipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
        var iccPath = (string)Prop(recipe, "ColorProfilePath")!;
        Check(File.Exists(iccPath), "native Export effects fixture uses a real selected recipe RGB profile");
        var iccSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(iccPath)));
        var expectedWidth = (int)Prop(recipe, "Width")!;
        var expectedHeight = (int)Prop(recipe, "Height")!;
        var compression = (TiffCompressOption)Prop(recipe, "TiffCompression")!;
        var suggestedStem = recipe.GetType().GetProperty("SuggestedFilename", Flags)?.GetValue(recipe) as string;
        var expectedSuggestedFile = suggestedStem is null ? null : suggestedStem.TrimEnd('.', ' ')
            + ((string)Prop(recipe, "FormatId")! == "Png" ? ".png" : ".tif");
        var document = Field(panel, "document")!;
        var counterProperty = document.GetType().GetProperty("OutputCounter", Flags);
        var initialCounter = counterProperty?.GetValue(document);
        var notifications = new ConcurrentQueue<(long At, string Source, string? Property)>();
        INotifyPropertyChanged? variantEvents = null;
        INotifyPropertyChanged? settingsEvents = null;
        void VariantChanged(object? _, PropertyChangedEventArgs change) => RecordChange("Variant", change.PropertyName);
        void SettingsChanged(object? _, PropertyChangedEventArgs change) => RecordChange("Settings", change.PropertyName);
        void RecordChange(string source, string? property)
        {
            if (notifications.Count < 1024)
                notifications.Enqueue((Stopwatch.GetTimestamp(), source, property));
        }
        var notificationStart = DateTimeOffset.UtcNow;
        var notificationClock = Stopwatch.GetTimestamp();
        var originalNormal = Field(current, "normal")!;
        var originalPrevious = Field(current, "previous")!;
        var normalCommand = Field(originalNormal, "command")!;
        var previousCommand = Field(originalPrevious, "command")!;
        var callbacks = (FieldInfo)originalNormal.GetType().GetField("Callbacks", Flags)!.GetValue(null)!;
        Delegate[] Invocations(object command) => ((Delegate)callbacks.GetValue(command)!).GetInvocationList();
        current.Dispose();
        hookField.SetValue(null, null);
        var normalInvocations = Invocations(normalCommand);
        var previousInvocations = Invocations(previousCommand);
        Check(normalInvocations.Count(item => item.Equals(Prop(originalNormal, "Original"))) == 1
            && previousInvocations.Count(item => item.Equals(Prop(originalPrevious, "Original"))) == 1,
            "detaching the production Export bridge restored each original command exactly once");
        object? bridge = null;
        var routeCount = 0;
        var timings = new List<JsonElement>();
        var destination = string.Empty;
        Task<bool>? job = null;
        string? offeredFilename = null;
        string? offeredDirectory = null;
        string? offeredTitle = null;
        Action? beforeDestinationApproval = null;
        var cancelDestination = false;
        Task<bool> Route()
        {
            routeCount++;
            var owner = (Window?)Prop(bridge!, "Dialog");
            job = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, owner,
                (Func<SaveFileDialog, Window?, string?>)((picker, _) =>
                {
                    offeredFilename = picker.FileName;
                    offeredDirectory = picker.InitialDirectory;
                    offeredTitle = picker.Title;
                    beforeDestinationApproval?.Invoke();
                    return cancelDestination ? null : destination;
                }))!;
            return job;
        }
        try
        {
            try
            {
                Activator.CreateInstance(originalNormal.GetType(), Flags, null,
                    new[] { normalCommand, window, "RenamedNativeExportCallback", Prop(originalNormal, "Original")! }, null);
                throw new InvalidOperationException("The native command adapter accepted a renamed callback.");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            {
                Check(Invocations(normalCommand).SequenceEqual(normalInvocations),
                    "a renamed native callback fails before changing the original Export command");
            }
            bridge = Activator.CreateInstance(current.GetType(), Flags, null,
                new object[] { window, (Func<bool>)(() => Prop(panel, "IsSpektraExportActive") is true),
                    (Func<Task<bool>>)Route, (Action<string>)(reason => throw new InvalidOperationException(reason)),
                    (Func<Guid, bool>)(variant => Call(hosted, "WouldLoseSavedLook", variant) is true) }, null)!;
            var normal = Field(Field(bridge, "normal")!, "command")!;
            var previous = Field(Field(bridge, "previous")!, "command")!;
            if (changeSourceRevision || compareRecipeRestart)
            {
                var profile = panel.GetType().Assembly.GetType("HostProfile", true)!;
                var selection = profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
                var nativeVariant = Prop(selection, "Variant")!;
                variantEvents = (INotifyPropertyChanged)nativeVariant;
                settingsEvents = (INotifyPropertyChanged)Prop(nativeVariant, "Settings")!;
                variantEvents.PropertyChanged += VariantChanged;
                settingsEvents.PropertyChanged += SettingsChanged;
            }
            toggle.IsChecked = true;
            Value(1, 1.15);
            Value(2, 1.25);
            Value(3, 0);
            Value(4, 0);
            Diffusion("off");
            Value(0, 0);
            var unprocessed = compareFolders
                ? await RenderAfterNativeDevelopment()
                : await Render("no-film-reference", ".tif", false);
            Value(0, 100);
            if (changeSourceRevision)
            {
                await CheckSourceRevision(unprocessed);
                return;
            }
            var filmOnly = await Render("film-only", ".tif", false);
            Diffusion("pro_mist");
            var filmAndDiffusion = await Render("film-and-diffusion", ".tif", false);
            Value(3, 1.5);
            Value(4, .65);
            var fullEffects = await Render("film-diffusion-halation-scatter", ".tif", false);
            var previousWithEffects = await Render("previous-with-effects", ".png", true);
            if (expectedSuggestedFile is not null)
            {
                Check(counterProperty is not null && Equals(initialCounter, counterProperty.GetValue(document)),
                    "native naming suggestions leave Capture One's output counter unchanged");
            }

            var filmDifference = Difference(unprocessed, filmOnly);
            var opticsDifference = Difference(filmOnly, filmAndDiffusion);
            var halationScatterDifference = Difference(filmAndDiffusion, fullEffects);
            var commandDifference = Difference(fullEffects, previousWithEffects);
            Check(filmDifference.Mean > .02 && filmDifference.ChangedFraction > .75,
                $"the native Export file contains a measured film look (mean RGB change {filmDifference.Mean:P2})");
            Check(opticsDifference.Mean > .0001 && opticsDifference.ChangedFraction > .15,
                $"changing only camera diffusion alters native Export pixels (mean RGB change {opticsDifference.Mean:P2})");
            Check(halationScatterDifference.Mean > .0001 && halationScatterDifference.ChangedFraction > .1,
                $"applying halation and scatter alters the native TIFF alongside the film look and diffusion (mean RGB change {halationScatterDifference.Mean:P2})");
            Check(commandDifference.Mean < 8d / 65535 && commandDifference.Maximum < .01,
                "native Export TIFF and Export with Previous Settings PNG retain the same processed RGB16 image");

            var grain = compareGrain ? await CompareGrain(fullEffects) : null;
            var compressionComparison = compareCompression ? await CompareCompression(fullEffects) : null;
            var scanMode = compareProfiles ? await CompareScanMode() : null;
            var outputProfiles = compareProfiles ? await CompareOutputProfiles() : null;
            var printDiffusion = compareProfiles ? await ComparePrintDiffusion() : null;
            if (compareFolders) await CheckFolders();
            if (compareRecipeRestart) await CheckRecipeRestart();
            var panelModes = comparePanelModes ? await ComparePanelModes(fullEffects) : null;
            if (compareGrain) await CheckReadOnlyDestination();
            var routesBeforeDisabled = routeCount;
            toggle.IsChecked = false;
            bool clicked = false;
            ShowNativeDialog(() =>
            {
                var native = Prop(window, "ExportDialogWindow") as Window;
                if (native is not { IsVisible: true }) return false;
                clicked = true;
                native.Close();
                return true;
            });
            Check(clicked && routeCount == routesBeforeDisabled && Prop(bridge, "Dialog") is null,
                "the master switch delegates to the ordinary Capture One Export dialog without a Spektrafilm job");
            ((IDisposable)bridge).Dispose();
            ((IDisposable)bridge).Dispose();
            Check(Invocations(normalCommand).SequenceEqual(normalInvocations) && Invocations(previousCommand).SequenceEqual(previousInvocations),
                "repeated detach restores the original native callback arrays without duplicate commands");
            File.WriteAllText(Path.Combine(folder, caseName + "-native-effect-export.json"), JsonSerializer.Serialize(new
            {
                width = expectedWidth, height = expectedHeight, iccSha256 = iccSha, tiffCompression = compression.ToString(),
                filmDifference, opticsDifference, halationScatterDifference, commandDifference, grain, compressionComparison,
                panelModes, outputProfiles, printDiffusion, scanMode,
                nativeExportCallbacks = routeCount, masterOffDelegated = clicked, timings,
            }, new JsonSerializerOptions { WriteIndented = true }));

            async Task<BitmapSource> RenderAfterNativeDevelopment()
            {
                var slot = (SemaphoreSlim)host.GetField("SchedulerSlot", Flags)!.GetValue(null)!;
                if (!await slot.WaitAsync(TimeSpan.FromSeconds(12)))
                    throw new TimeoutException("Could not acquire the disposable native development slot.");
                var released = false;
                var waitedBeforeSave = false;
                var release = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
                release.Tick += (_, _) =>
                {
                    release.Stop();
                    waitedBeforeSave = job is { IsCompleted: false } && offeredFilename is null;
                    slot.Release();
                    released = true;
                };
                release.Start();
                try
                {
                    var frame = await Render("no-film-reference", ".tif", true);
                    Check(waitedBeforeSave, "native Export waits for the earlier development callback before opening Save");
                    return frame;
                }
                finally
                {
                    release.Stop();
                    if (!released) slot.Release();
                }
            }

            async Task<object> ComparePanelModes(BitmapSource reference)
            {
                Check(File.Exists(Path.Combine(folder, "Capture One user.config.before-proofing")),
                    "the native workspace export test has a verified preferences backup");
                var hook = hookField.DeclaringType!;
                var palette = hook.GetField("owner", Flags)!.GetValue(null)!;
                var pluginTab = hook.GetField("tab", Flags)!.GetValue(null)!;
                var originalTab = Prop(palette, "Current")!;
                var tabs = ((IEnumerable)Prop(palette, "Tabs")!).Cast<object>();
                var ordinaryTab = tabs.First(item => !ReferenceEquals(item, pluginTab));
                var select = hook.GetMethod("SelectTab", Flags)!;
                var control = (FrameworkElement)panel;
                var viewer = Field(spatial, "viewer")!;
                var measurements = new List<object>();
                if (Field(hosted, "floating") is not null)
                    throw new InvalidOperationException("The workspace export test starts with a docked tool.");
                try
                {
                    select.Invoke(null, new[] { palette, ordinaryTab });
                    await WaitForViewer(() => !control.IsVisible, "the real Viewer keeps painting Spektrafilm while its docked tab is hidden");
                    await Measure("tab-hidden");
                    await CheckBackgroundFailure("tab-hidden", shouldAnnounce: true);
                    Call(hosted, "ShowFloating");
                    await WaitForViewer(() => control.IsVisible && Field(hosted, "floating") is Window { IsVisible: true } floating
                        && floating.GetType().Name == "DockableWindow", "the same Viewer look stays painted with the actual floating tool");
                    await Measure("tool-floating");
                    await CheckBackgroundFailure("tool-floating", shouldAnnounce: false);
                }
                finally
                {
                    if (Field(hosted, "floating") is Window floating) floating.Close();
                    select.Invoke(null, new[] { palette, originalTab });
                }
                Check(ReferenceEquals(Prop(palette, "Current"), originalTab) && Field(hosted, "floating") is null,
                    "the workspace export fixture restored its selected tab and docked tool");
                return measurements;

                async Task Measure(string placement)
                {
                    foreach (var usePrevious in new[] { false, true })
                    {
                        var kind = placement + (usePrevious ? "-previous" : "-export");
                        var delta = Difference(reference, await Render(kind, ".tif", usePrevious));
                        Check(delta.Mean < 8d / 65535 && delta.Maximum < .01,
                            $"{kind} retains the reference film, diffusion, halation and scatter pixels");
                        measurements.Add(new { placement, usePrevious, delta });
                    }
                }

                async Task CheckBackgroundFailure(string placement, bool shouldAnnounce)
                {
                    var notifier = spatial.GetType().GetField("showExportFailureNotice", Flags)!;
                    var original = notifier.GetValue(spatial);
                    Check(original is Func<bool> native && native.Method.Name == "ShowNativeExportFailureNotice",
                        "the production export notice is bound to the Capture One presenter");
                    var notices = 0;
                    destination = Path.Combine(folder, caseName + "-" + placement + "-read-only-destination.tif");
                    var protectedPath = destination;
                    File.Copy(Path.Combine(folder, caseName + "-film-only.tif"), protectedPath);
                    var attributes = File.GetAttributes(protectedPath);
                    var before = File.ReadAllBytes(protectedPath);
                    var submitTest = shouldAnnounce && caseName == "first"
                        && Environment.GetEnvironmentVariable("SPEKTRAFILM_QA_SUBMIT_EXPORT_TOAST") == "1";
                    try
                    {
                        notifier.SetValue(spatial, (Func<bool>)Present);
                        File.SetAttributes(protectedPath, attributes | FileAttributes.ReadOnly);
                        job = null;
                        var previousRouteCount = routeCount;
                        ExecuteNativeExportCommand(previous);
                        Check(routeCount == previousRouteCount + 1 && job is not null && !await job.WaitAsync(TimeSpan.FromSeconds(45)),
                            $"{placement} native previous-settings export returns its accepted failure without opening a modal dialog");
                        var failed = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        var status = (TextBlock)Field(spatial, "exportStatus")!;
                        Check(notices == (shouldAnnounce ? 1 : 0) && status.IsVisible != shouldAnnounce
                            && Prop(failed, "Stage")?.ToString() == "Failed" && Equals(Prop(failed, "Destination"), protectedPath),
                            $"{placement} delivers the failure to its visible panel or one native notification and retains the receipt");
                        Check(File.ReadAllBytes(protectedPath).AsSpan().SequenceEqual(before)
                            && !Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(),
                            $"{placement} leaves every byte of the previous protected TIFF intact");
                    }
                    finally
                    {
                        notifier.SetValue(spatial, original);
                        File.SetAttributes(protectedPath, attributes);
                    }

                    bool Present()
                    {
                        notices++;
                        if (!submitTest) return false;
                        var submit = spatial.GetType().GetMethod("SubmitNativeExportNotice", Flags)!;
                        var accepted = submit.Invoke(null, new object[] { "Spektrafilm notification test",
                            "This message came from an isolated export test." }) is true;
                        File.WriteAllText(Path.Combine(folder, "native-notice-submission.json"),
                            JsonSerializer.Serialize(new { CaptureOneAccepted = accepted }));
                        return accepted;
                    }
                }

                async Task WaitForViewer(Func<bool> placement, string description)
                {
                    var started = Stopwatch.GetTimestamp();
                    do
                    {
                        if (ReferenceEquals(Prop(palette, "Current"), ordinaryTab) && placement()
                            && Prop(viewer, "HasPainted") is true && Field(spatial, "frameCurrent") is true)
                        {
                            Check(true, description);
                            return;
                        }
                        await Task.Delay(80);
                    } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20));
                    throw new InvalidOperationException(description);
                }
            }

            async Task CheckFolders()
            {
                var selected = SelectedRecipeForTest(out var notifications);
                var type = selected.GetType();
                var mode = type.GetProperty("DestinationRootFolder", Flags)!;
                var root = type.GetProperty("DestinationCustomRootFolder", Flags)!;
                var subfolder = type.GetProperty("DestinationSubFolder", Flags)!;
                var overwrite = type.GetProperty("FileOverwriteAction", Flags)!;
                var filename = type.GetProperty("DestinationFilename", Flags)!;
                var format = type.GetProperty("FileType", Flags)!;
                var bits = type.GetProperty("TiffBits", Flags)!;
                var originalMode = mode.GetValue(selected)!;
                var originalRoot = root.GetValue(selected);
                var originalSubfolder = subfolder.GetValue(selected);
                var originalAction = overwrite.GetValue(selected)!;
                var originalFilename = filename.GetValue(selected);
                var originalFormat = format.GetValue(selected);
                var originalBits = bits.GetValue(selected);
                var fixtureRoot = Directory.CreateDirectory(Path.Combine(folder, "native-recipe-location")).FullName;
                var relative = Path.Combine("Prints", "Proofs");
                var nested = Directory.CreateDirectory(Path.Combine(fixtureRoot, relative)).FullName;
                var recipeName = (string)Prop(selected, "Name")!;
                if (string.IsNullOrWhiteSpace(recipeName) || recipeName is "." or ".."
                    || recipeName != recipeName.TrimEnd('.', ' ') || recipeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidOperationException("The folder fixture requires a selected recipe with a valid directory name.");
                var byRecipe = Directory.CreateDirectory(Path.Combine(fixtureRoot, recipeName)).FullName;
                var missing = Path.Combine(fixtureRoot, "Uncreated");
                notifications.SetValue(selected, true);
                try
                {
                    mode.SetValue(selected, Enum.Parse(mode.PropertyType, "CustomPath"));
                    root.SetValue(selected, fixtureRoot);
                    subfolder.SetValue(selected, relative);
                    await Render("existing-recipe-folder", ".tif", true, fixtureOutputDirectory: nested);
                    Check(string.Equals(offeredDirectory, nested, StringComparison.OrdinalIgnoreCase),
                        "Previous Settings opens Save in the existing nested recipe folder");
                    subfolder.SetValue(selected, NativeNameToken("RecipeName"));
                    await Render("token-recipe-folder", ".tif", false, expectedNativeLocation: byRecipe, fixtureOutputDirectory: byRecipe);
                    Check(string.Equals(offeredDirectory, byRecipe, StringComparison.OrdinalIgnoreCase),
                        "the real native Export dialog passes its resolved Recipe Name folder to Save");
                    subfolder.SetValue(selected, Path.GetFileName(missing));
                    await Render("missing-recipe-folder", ".tif", true, fixtureOutputDirectory: fixtureRoot);
                    Check(string.Equals(offeredDirectory, fixtureRoot, StringComparison.OrdinalIgnoreCase) && !Directory.Exists(missing),
                        "Previous Settings suggests the recipe root when its subfolder has not been created");
                    await CheckNumberedNames();
                    await CheckNativeCounter();
                    await CheckSkipExisting();
                    await CheckDeliveryAlternatives();
                }
                finally
                {
                    try
                    {
                        mode.SetValue(selected, originalMode);
                        root.SetValue(selected, originalRoot);
                        subfolder.SetValue(selected, originalSubfolder);
                        overwrite.SetValue(selected, originalAction);
                        filename.SetValue(selected, originalFilename);
                        format.SetValue(selected, originalFormat);
                        bits.SetValue(selected, originalBits);
                    }
                    finally { notifications.SetValue(selected, false); }
                }
                Check(Equals(mode.GetValue(selected), originalMode) && Equals(root.GetValue(selected), originalRoot)
                    && Equals(subfolder.GetValue(selected), originalSubfolder) && Equals(overwrite.GetValue(selected), originalAction)
                    && Equals(filename.GetValue(selected), originalFilename) && Equals(format.GetValue(selected), originalFormat)
                    && Equals(bits.GetValue(selected), originalBits),
                    "the live recipe location and existing-file policy are restored");

                async Task CheckNumberedNames()
                {
                    if (expectedSuggestedFile is null)
                        throw new InvalidOperationException("The native filename collision fixture requires a recipe name suggestion.");
                    var extension = Path.GetExtension(expectedSuggestedFile);
                    // Exercise repeated collisions independently of a recipe's native counter.
                    const string stem = "Repeated recipe filename";
                    filename.SetValue(selected, stem);
                    var literalName = stem + extension;
                    var collisionFolder = Directory.CreateDirectory(Path.Combine(fixtureRoot, caseName + "-collisions")).FullName;
                    var occupied = Path.Combine(collisionFolder, literalName);
                    var otherOutput = Path.Combine(collisionFolder, stem + " 3" + extension);
                    byte[] prior = [7, 2, 4];
                    File.WriteAllBytes(occupied, prior);
                    File.WriteAllBytes(otherOutput, prior);
                    var occupiedDirectory = Directory.CreateDirectory(Path.Combine(collisionFolder, stem + " 1" + extension)).FullName;
                    subfolder.SetValue(selected, Path.GetFileName(collisionFolder));
                    overwrite.SetValue(selected, Enum.Parse(overwrite.PropertyType, "Rename"));
                    var currentRecipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                    Check(Prop(currentRecipe, "AddSuffixToExistingFileName") is true, "the real recipe captures Capture One's Add Suffix policy");

                    var firstName = stem + " 2" + extension;
                    var firstFrame = await Render("suffix-native-export", extension, false, fixtureOutputDirectory: collisionFolder,
                        fixtureOutputFileName: firstName);
                    Check(offeredFilename == firstName && offeredDirectory == collisionFolder,
                        "native Export suggests the first free numbered filename without replacing an existing file or folder");
                    var secondName = stem + " 4" + extension;
                    var secondFrame = await Render("suffix-previous-settings", extension, true, fixtureOutputDirectory: collisionFolder,
                        fixtureOutputFileName: secondName);
                    Check(offeredFilename == secondName && offeredDirectory == collisionFolder,
                        "Previous Settings suggests the next free name after the first native export");
                    Check(File.ReadAllBytes(occupied).SequenceEqual(prior) && File.ReadAllBytes(otherOutput).SequenceEqual(prior)
                        && Directory.Exists(occupiedDirectory), "both numbered exports preserve the existing destination entries");
                    Check(Difference(firstFrame, secondFrame).Mean < 8d / 65535,
                        "the numbered outputs retain the same effect-processed RGB16 image across both native commands");
                }

                async Task CheckNativeCounter()
                {
                    var counter = counterProperty ?? throw new MissingMemberException(document.GetType().FullName, "OutputCounter");
                    var increment = document.GetType().GetProperty("OutputCounterIncrement", Flags)
                        ?? throw new MissingMemberException(document.GetType().FullName, "OutputCounterIncrement");
                    var initialValue = counter.GetValue(document)!;
                    var initialStep = increment.GetValue(document)!;
                    var counterFolder = Directory.CreateDirectory(Path.Combine(fixtureRoot, "native-counter")).FullName;
                    subfolder.SetValue(selected, Path.GetFileName(counterFolder));
                    filename.SetValue(selected, "Counter " + NativeNameToken("Counter"));
                    overwrite.SetValue(selected, Enum.Parse(overwrite.PropertyType, "Overwrite"));
                    var extension = Path.GetExtension(expectedSuggestedFile ?? throw new InvalidOperationException("No recipe filename was offered."));
                    try
                    {
                        SetCounter(41);
                        increment.SetValue(document, Convert.ChangeType(3, increment.PropertyType));
                        var first = InspectName();
                        var before = routeCount;
                        cancelDestination = true;
                        try
                        {
                            ExecuteNativeExportCommand(previous);
                            Check(routeCount == before + 1 && job is not null && !await job.WaitAsync(TimeSpan.FromSeconds(10))
                                && offeredFilename == first && ReadCounter() == 41,
                                "canceling the native destination picker leaves the output counter unchanged");
                        }
                        finally { cancelDestination = false; }

                        await Render("counter-custom-name", extension, true, fixtureOutputDirectory: counterFolder);
                        Check(offeredFilename == first && ReadCounter() == 41, "a manually named export keeps the native output counter");
                        var failedPath = Path.Combine(counterFolder, first);
                        byte[] prior = [1, 4, 3];
                        File.WriteAllBytes(failedPath, prior);
                        File.SetAttributes(failedPath, FileAttributes.ReadOnly);
                        try
                        {
                            destination = failedPath;
                            before = routeCount;
                            ExecuteNativeExportCommand(previous);
                            Check(routeCount == before + 1 && job is not null && !await job.WaitAsync(TimeSpan.FromSeconds(45))
                                && offeredFilename == first && ReadCounter() == 41 && File.ReadAllBytes(failedPath).SequenceEqual(prior),
                                "a failed publication leaves both the previous file and native counter unchanged");
                        }
                        finally { File.SetAttributes(failedPath, FileAttributes.Normal); }

                        var firstFrame = await Render("counter-native-export", extension, false, fixtureOutputDirectory: counterFolder,
                            fixtureOutputFileName: first);
                        Check(first == offeredFilename && ReadCounter() == 44, "native Export advances the selected counter by its native increment");
                        var second = InspectName();
                        Check(second != first && ReadCounter() == 44, "the next naming preview uses the new counter without changing it");
                        var nextFrame = await Render("counter-previous-settings", extension, true, fixtureOutputDirectory: counterFolder,
                            fixtureOutputFileName: second);
                        Check(second == offeredFilename && ReadCounter() == 47, "Previous Settings consumes exactly one native counter step");
                        Check(Difference(firstFrame, nextFrame).Mean < 8d / 65535,
                            "counter-based native outputs retain the same applied Spektrafilm effect");

                        var staleName = InspectName();
                        beforeDestinationApproval = () => SetCounter(90);
                        try
                        {
                            await Render("counter-changed-while-saving", extension, true, fixtureOutputDirectory: counterFolder,
                                fixtureOutputFileName: staleName);
                            Check(offeredFilename == staleName && ReadCounter() == 90
                                && ((TextBlock)Field(spatial, "exportStatus")!).Text.Contains("naming changed during export", StringComparison.Ordinal),
                                "an independently changed counter preserves the approved file and the host's newer counter");
                        }
                        finally { beforeDestinationApproval = null; }
                    }
                    finally
                    {
                        counter.SetValue(document, initialValue);
                        increment.SetValue(document, initialStep);
                    }
                    Check(Equals(counter.GetValue(document), initialValue) && Equals(increment.GetValue(document), initialStep),
                        "the copied catalog's native counter and increment are restored");

                    long ReadCounter() => Convert.ToInt64(counter.GetValue(document));
                    void SetCounter(int value) => counter.SetValue(document, Convert.ChangeType(value, counter.PropertyType));
                    string InspectName()
                    {
                        var capture = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                        Check(Prop(Prop(capture, "Naming")!, "AdvanceCounter") is Delegate,
                            "the native filename token declares a real output counter");
                        return (string)Prop(capture, "SuggestedFilename")! + extension;
                    }
                }

                async Task CheckSkipExisting()
                {
                    var directory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "native-skip")).FullName;
                    subfolder.SetValue(selected, Path.GetFileName(directory));
                    filename.SetValue(selected, "Skip " + NativeNameToken("Counter"));
                    format.SetValue(selected, Enum.Parse(format.PropertyType, "Tiff"));
                    bits.SetValue(selected, 16);
                    overwrite.SetValue(selected, Enum.Parse(overwrite.PropertyType, "Skip"));
                    var currentRecipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                    Check(Prop(currentRecipe, "FormatId") is "Tiff" && Prop(currentRecipe, "TiffBits") is 16
                        && Prop(currentRecipe, "SkipExistingOutput") is true && Equals(Prop(currentRecipe, "ExactDirectory"), directory),
                        "the fixture captures a precise 16-bit TIFF Skip recipe");
                    var stem = (string)Prop(currentRecipe, "SuggestedFilename")!;
                    var expectedFile = stem + ".tif";
                    var protectedPath = Path.Combine(directory, expectedFile);
                    var counterBefore = counterProperty!.GetValue(document);
                    byte[] previousBytes = [7, 9, 2];
                    File.WriteAllBytes(protectedPath, previousBytes);
                    var writeTime = File.GetLastWriteTimeUtc(protectedPath);

                    void ConfirmNative()
                    {
                        ShowNativeDialog(() =>
                        {
                            if (Prop(bridge, "Dialog") is not Window { IsVisible: true } owner || owner.DataContext is not { } model
                                || Prop(model, "ExportCommand") is not ICommand confirm || !confirm.CanExecute(null))
                                return false;
                            confirm.Execute(null);
                            return true;
                        });
                    }
                    await RunSkip("Native Export", ConfirmNative, expectedPicker: false);
                    await RunSkip("Previous Settings", () => ExecuteNativeExportCommand(previous), expectedPicker: false);
                    Check(File.GetLastWriteTimeUtc(protectedPath) == writeTime, "native Skip leaves the existing file’s timestamp intact");

                    File.Move(protectedPath, protectedPath + "." + caseName + ".before");
                    destination = protectedPath;
                    beforeDestinationApproval = () => File.WriteAllBytes(protectedPath, previousBytes);
                    try { await RunSkip("Previous Settings with a file arriving during Save", () => ExecuteNativeExportCommand(previous), expectedPicker: true); }
                    finally { beforeDestinationApproval = null; }
                    var lastJob = Prop(Call(Field(spatial, "exportJobs")!, "ReadLatest")!, "JobId");

                    subfolder.SetValue(selected, "Missing Skip Folder");
                    File.WriteAllBytes(Path.Combine(fixtureRoot, expectedFile), previousBytes);
                    cancelDestination = true;
                    try
                    {
                        offeredFilename = null;
                        ExecuteNativeExportCommand(previous);
                        Check(job is not null && !await job.WaitAsync(TimeSpan.FromSeconds(10)) && offeredFilename == expectedFile
                            && Equals(lastJob, Prop(Call(Field(spatial, "exportJobs")!, "ReadLatest")!, "JobId")),
                            "the fallback root is a Save hint and does not trigger Skip for a different recipe destination");
                    }
                    finally { cancelDestination = false; }

                    subfolder.SetValue(selected, Path.GetFileName(directory));
                    format.SetValue(selected, Enum.Parse(format.PropertyType, "Png"));
                    var pngRecipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                    Check(Prop(pngRecipe, "FormatId") is "Png" && Prop(pngRecipe, "SkipExistingOutput") is true,
                        "the selected recipe requests native PNG Skip");
                    expectedFile = (string)Prop(pngRecipe, "SuggestedFilename")! + ".png";
                    protectedPath = Path.Combine(directory, expectedFile);
                    File.WriteAllBytes(protectedPath, previousBytes);
                    await RunSkip("PNG Native Export", ConfirmNative, expectedPicker: false);
                    await RunSkip("PNG Previous Settings", () => ExecuteNativeExportCommand(previous), expectedPicker: false);

                    async Task RunSkip(string command, Action execute, bool expectedPicker)
                    {
                        offeredFilename = null;
                        job = null;
                        var before = routeCount;
                        cancelDestination = !expectedPicker;
                        try { execute(); }
                        finally { cancelDestination = false; }
                        Check(routeCount == before + 1 && job is not null && await job.WaitAsync(TimeSpan.FromSeconds(10))
                            && offeredFilename == (expectedPicker ? expectedFile : null),
                            $"{command} honors the exact native Skip policy at the expected point");
                        var receipt = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        Check(Equals(Prop(receipt, "Destination"), protectedPath) && Prop(receipt, "Stage")?.ToString() == "Skipped"
                            && Equals(Prop(receipt, "Width"), 0) && Equals(counterProperty.GetValue(document), counterBefore)
                            && File.ReadAllBytes(protectedPath).SequenceEqual(previousBytes),
                            $"{command} preserves the file and counter and records a distinct skipped outcome");
                    }
                }

                async Task CheckDeliveryAlternatives()
                {
                    var fixture = Directory.CreateDirectory(Path.Combine(fixtureRoot, "delivery-alternatives")).FullName;
                    subfolder.SetValue(selected, Path.GetFileName(fixture));
                    filename.SetValue(selected, "Recipe delivery");
                    overwrite.SetValue(selected, Enum.Parse(overwrite.PropertyType, "Rename"));
                    var currentBits = bits.GetValue(selected);
                    var compressionSetting = type.GetProperty("TiffCompression", Flags)!;
                    var originalCompression = compressionSetting.GetValue(selected);
                    try
                    {
                        format.SetValue(selected, Enum.Parse(format.PropertyType, "Jpeg"));
                        await Render("jpeg-recipe-save-tiff", ".tif", false, fixtureOutputDirectory: fixture,
                            expectedCompression: TiffCompressOption.Zip);
                        VerifyAlternative("JPEG");

                        format.SetValue(selected, Enum.Parse(format.PropertyType, "Tiff"));
                        bits.SetValue(selected, 8);
                        compressionSetting.SetValue(selected, Enum.Parse(compressionSetting.PropertyType, "Zip"));
                        await Render("eight-bit-tiff-recipe-save-tiff", ".tif", true, fixtureOutputDirectory: fixture,
                            expectedCompression: TiffCompressOption.Zip);
                        VerifyAlternative("8-bit TIFF");

                        overwrite.SetValue(selected, Enum.Parse(overwrite.PropertyType, "Skip"));
                        var capture = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                        Check(Prop(capture, "SkipExistingOutput") is true && Prop(capture, "TiffBits") is 8
                            && string.Equals(Prop(capture, "ExactDirectory") as string, fixture, StringComparison.OrdinalIgnoreCase),
                            "the native 8-bit TIFF Skip recipe resolves to an exact existing folder");
                        var occupied = Path.Combine(fixture, "Recipe delivery.tif");
                        byte[] existing = [8, 4, 1, 7];
                        File.WriteAllBytes(occupied, existing);
                        await Render("eight-bit-tiff-skip-offers-alternative", ".tif", true, fixtureOutputDirectory: fixture,
                            expectedCompression: TiffCompressOption.Zip);
                        VerifyAlternative("8-bit TIFF");
                        Check(offeredFilename == Path.GetFileName(occupied) && File.ReadAllBytes(occupied).SequenceEqual(existing),
                            "the supported 16-bit alternative remains selectable and the manually bypassed existing file is preserved");
                    }
                    finally
                    {
                        bits.SetValue(selected, currentBits);
                        compressionSetting.SetValue(selected, originalCompression);
                    }

                    void VerifyAlternative(string requested)
                    {
                        var receipt = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        var note = $"Recipe requested {requested}. Saved 16-bit TIFF.";
                        Check(offeredTitle?.StartsWith($"Recipe requests {requested} · Save 16-bit TIFF or PNG", StringComparison.Ordinal) == true,
                            $"Save discloses the {requested} substitution before publication");
                        var describe = spatial.GetType().GetMethod("DescribePreviousExport", Flags)!;
                        Check(Equals(Prop(receipt, "DeliveryNote"), note)
                            && ((TextBlock)Field(spatial, "exportStatus")!).Text.Contains(note, StringComparison.Ordinal)
                            && (describe.Invoke(null, new[] { receipt }) as string)?.Contains(note, StringComparison.Ordinal) == true,
                            $"the completed {requested} export records the actual 16-bit alternative");
                    }
                }

            }

            async Task CheckRecipeRestart()
            {
                var selected = SelectedRecipeForTest(out var notifications);
                var type = selected.GetType();
                var filename = type.GetProperty("DestinationFilename", Flags)!;
                var format = type.GetProperty("FileType", Flags)!;
                var cropMode = type.GetProperty("CropExportMethod", Flags)!;
                var watermark = type.GetProperty("WatermarkType", Flags)!;
                var watermarkText = type.GetProperty("WatermarkTextSerialized", Flags)!;
                var sharpening = type.GetProperty("SharpeningOutputControl", Flags)!;
                var annotations = type.GetProperty("IncludeAnnotations", Flags)!;
                var initialName = filename.GetValue(selected);
                var initialFormat = format.GetValue(selected);
                var initialCropMode = cropMode.GetValue(selected);
                var initialWatermark = watermark.GetValue(selected);
                var initialText = watermarkText.GetValue(selected);
                var initialSharpening = sharpening.GetValue(selected);
                var initialAnnotations = annotations.GetValue(selected);
                var counter = counterProperty ?? throw new MissingMemberException(document.GetType().FullName, "OutputCounter");
                var increment = document.GetType().GetProperty("OutputCounterIncrement", Flags)!;
                var firstLaunch = caseName == "first";
                var namingDirectory = Directory.CreateDirectory(Path.Combine(folder, "recipe-restart-outputs")).FullName;
                object CaptureRecipe() => host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                long Read(PropertyInfo property) => Convert.ToInt64(property.GetValue(document));
                void Write(PropertyInfo property, int value) => property.SetValue(document, Convert.ChangeType(value, property.PropertyType));
                (int Width, int Height) Dimensions(object state) => ((int)Prop(state, "Width")!, (int)Prop(state, "Height")!);

                notifications.SetValue(selected, true);
                try
                {
                    if (firstLaunch)
                    {
                        Write(counter, 41);
                        Write(increment, 3);
                    }
                    var before = firstLaunch ? 41 : 44;
                    Check(Read(counter) == before && Read(increment) == 3,
                        firstLaunch ? "the copy uses the explicitly set native naming counter and increment"
                            : "reopening the copy loads the counter advanced by the first native export and its original increment");
                    filename.SetValue(selected, "Restart " + NativeNameToken("Counter"));
                    var expected = $"Restart {before:D3}.tif";
                    var baseline = await Render("counter-restart", ".tif", usePrevious: !firstLaunch,
                        fixtureOutputDirectory: namingDirectory, fixtureOutputFileName: expected);
                    Check(offeredFilename == expected && Read(counter) == before + 3 && Read(increment) == 3,
                        "the actual native export accepts the expected numbered TIFF and advances the persisted counter once");
                    if (!firstLaunch)
                    {
                        Check(File.Exists(Path.Combine(namingDirectory, "Restart 041.tif")),
                            "the restart export is written beside the earlier TIFF under its own counter instead of replacing it");
                        return;
                    }

                    filename.SetValue(selected, initialName);
                    watermarkText.SetValue(selected, "Spektrafilm test watermark");
                    watermark.SetValue(selected, Enum.Parse(watermark.PropertyType, "Text"));
                    sharpening.SetValue(selected, Enum.Parse(sharpening.PropertyType, "Screen"));
                    Check(Prop(CaptureRecipe(), "Omissions")?.ToString() == "Watermark, Sharpening",
                        "the selected native recipe captures its configured text watermark and screen sharpening mode");
                    var disclosedExport = await Render("watermark-and-sharpening-omissions", ".tif", true);
                    const string omitted = "recipe watermark, recipe sharpening mode";
                    const string omissionNote = "The export omitted " + omitted + ".";
                    var omissionsReceipt = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                    Check(offeredTitle?.Contains("omits " + omitted, StringComparison.Ordinal) == true
                        && (Prop(omissionsReceipt, "DeliveryNote") as string)?.EndsWith(omissionNote, StringComparison.Ordinal) == true
                        && ((TextBlock)Field(spatial, "exportStatus")!).Text.Contains(omissionNote, StringComparison.Ordinal),
                        "Save, the completed receipt and panel status identify both omitted settings");
                    var unchanged = Difference(baseline, disclosedExport);
                    Check(unchanged.Mean < 8d / 65535 && unchanged.Maximum < .01,
                        "the disclosed alternative preserves the same RGB16 film result as the baseline");
                    watermark.SetValue(selected, initialWatermark);
                    watermarkText.SetValue(selected, initialText);
                    sharpening.SetValue(selected, initialSharpening);

                    var profile = panel.GetType().Assembly.GetType("HostProfile", true)!;
                    var selection = profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
                    var variant = Prop(selection, "Variant")!;
                    var crop = variant.GetType().GetProperty("CropRawUnconstrained", Flags)!;
                    var originalCrop = crop.GetValue(variant)!;
                    var dimensions = (System.Drawing.Size)Prop(Prop(variant, "CoreImage")!, "ImageDimensions")!;
                    var fixtureCrop = Activator.CreateInstance(crop.PropertyType,
                        new System.Drawing.PointF(dimensions.Width * .44f, dimensions.Height * .53f),
                        new System.Drawing.SizeF(dimensions.Width * .55f, dimensions.Height * .65f))!;
                    filename.SetValue(selected, initialName);
                    try
                    {
                        crop.SetValue(variant, fixtureCrop);
                        Check(!Equals(crop.GetValue(variant), originalCrop), "the disposable RAW has a real asymmetric native crop");
                        var cropped = Dimensions(CaptureRecipe());
                        cropMode.SetValue(selected, Enum.Parse(cropMode.PropertyType, "Ignore"));
                        var full = Dimensions(CaptureRecipe());
                        Check(cropped != full, "the actual native recipe computes different cropped and full output dimensions");
                        var uncut = await Render("ignore-crop-reference", ".tif", true, expectedGeometry: full);

                        format.SetValue(selected, Enum.Parse(format.PropertyType, "Psd"));
                        cropMode.SetValue(selected, Enum.Parse(cropMode.PropertyType, "AsPath"));
                        annotations.SetValue(selected, true);
                        var pathRecipe = CaptureRecipe();
                        Check(Prop(pathRecipe, "FormatId") is "Psd" && Prop(pathRecipe, "CropMode") is "AsPath"
                            && Dimensions(pathRecipe) == full && Prop(pathRecipe, "Omissions")?.ToString() == "PsdLayers",
                            "Capture One supplies a full-canvas PSD recipe with a crop path and annotation layer requested");
                        var png = await Render("psd-crop-path-alternative", ".png", false, expectedGeometry: full);
                        Check(offeredTitle?.StartsWith("Recipe requests PSD · Save 16-bit TIFF or PNG · full image, crop path omitted · omits PSD layers", StringComparison.Ordinal) == true,
                            "the native Save title discloses the format, crop path and PSD layers before approval");
                        const string note = "Recipe requested PSD. Saved 16-bit PNG. Saved the full image. The recipe’s crop path was omitted. The export omitted PSD layers.";
                        var receipt = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        Check(Equals(Prop(receipt, "DeliveryNote"), note)
                            && ((TextBlock)Field(spatial, "exportStatus")!).Text.Contains(note, StringComparison.Ordinal),
                            "the completed receipt records the actual PNG and the omitted crop path and layers");
                        var parity = Difference(uncut, png);
                        Check(parity.Mean < 8d / 65535 && parity.Maximum < .01,
                            "the PSD substitute preserves the same full-canvas effect pixels as the Ignore Crop reference TIFF");
                        Check(Read(counter) == 44, "manual substitution filenames do not consume another native counter step");
                    }
                    finally { crop.SetValue(variant, originalCrop); }
                }
                finally
                {
                    try
                    {
                        format.SetValue(selected, initialFormat);
                        filename.SetValue(selected, initialName);
                        cropMode.SetValue(selected, initialCropMode);
                        watermark.SetValue(selected, initialWatermark);
                        watermarkText.SetValue(selected, initialText);
                        sharpening.SetValue(selected, initialSharpening);
                        annotations.SetValue(selected, initialAnnotations);
                    }
                    finally { notifications.SetValue(selected, false); }
                }
            }

            static string NativeNameToken(string id)
            {
                var core = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "P1.C1.ImageCoreWrapper");
                const string ns = "P1.C1.Common.DestinationNaming.";
                var tokenType = core.GetType(ns + "NameTokenType", true)!;
                var token = id == "Counter"
                    ? Activator.CreateInstance(core.GetType(ns + "NameTokenCounter", true)!, 3)
                    : Activator.CreateInstance(core.GetType(ns + "NameToken", true)!, Enum.Parse(tokenType, id));
                var tokens = Array.CreateInstance(core.GetType(ns + "NameTokenBase", true)!, 1);
                tokens.SetValue(token, 0);
                var nativeName = Activator.CreateInstance(core.GetType(ns + "DestinationName", true)!, new object[] { tokens })!;
                return (string)Prop(nativeName, "TokenNameRepresentation")!;
            }

            object SelectedRecipeForTest(out FieldInfo notifications)
            {
                Check(File.Exists(Path.Combine(folder, "native-recipe-backup.json")),
                    "the native recipe settings test has a verified recipe backup");
                var destinations = ((Delegate)Field(bridge!, "destinations")!).DynamicInvoke()!;
                var selected = Prop(destinations, "SelectedDestination")!;
                var guard = selected.GetType().GetField("m_suppressNotifications", Flags);
                if (guard?.FieldType != typeof(bool) || guard.GetValue(selected) is not false)
                    throw new InvalidOperationException("The verified host recipe notification guard is unavailable.");
                notifications = guard;
                return selected;
            }

            async Task<object> CompareOutputProfiles()
            {
                var selected = SelectedRecipeForTest(out var notifications);
                var path = selected.GetType().GetProperty("ColorProfilePath", Flags)!;
                var original = path.GetValue(selected);
                var appDirectory = Path.GetDirectoryName(window.GetType().Assembly.Location)!;
                var profileDirectory = Path.Combine(Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!, "profiles", "preview");
                var comparisons = new List<object>();
                BitmapSource? srgbReference = null;
                notifications.SetValue(selected, true);
                try
                {
                    foreach (var (label, profile) in new[]
                    {
                        ("srgb", Path.Combine(appDirectory, "Color Profiles", "Common", "sRGB Color Space Profile.icm")),
                        ("prophoto", Path.Combine(profileDirectory, "ProPhoto RGB.icc")),
                        ("linear-rec2020", Path.Combine(profileDirectory, "Linear Rec.2020.icc")),
                    })
                    {
                        path.SetValue(selected, profile);
                        var captured = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                        Check(Equals(Prop(captured, "ColorProfilePath"), profile), $"the actual selected native recipe uses the {label} profile");
                        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profile)));
                        var tiff = await Render("profile-" + label + "-export", ".tif", false, expectedProfileSha: digest);
                        var png = await Render("profile-" + label + "-previous", ".png", true, expectedProfileSha: digest);
                        var parity = Difference(tiff, png);
                        Check(parity.Mean < 8d / 65535 && parity.Maximum < .01,
                            $"both native {label} exports preserve the same high-depth processed RGB");
                        srgbReference ??= tiff;
                        var codeDifference = Difference(srgbReference, tiff);
                        if (label != "srgb")
                            Check(codeDifference.Mean > .005, $"the native {label} result converts RGB codes away from the sRGB reference");
                        comparisons.Add(new { label, profileSha256 = digest, nativeCommandDifference = parity,
                            storedCodeDifferenceFromSrgb = codeDifference });
                    }
                }
                finally
                {
                    try { path.SetValue(selected, original); }
                    finally { notifications.SetValue(selected, false); }
                }
                Check(Equals(path.GetValue(selected), original) && notifications.GetValue(selected) is false,
                    "the native recipe's original profile and notifications are restored");
                return comparisons;
            }

            async Task<object> ComparePrintDiffusion()
            {
                var filter = (ComboBox)choices["print-diffusion-family"]!;
                var initial = filter.SelectedItem;
                var opticalValues = new[] { values["halation-boost"]!, values["print-diffusion-strength"]! }
                    .Select(control => (Control: control, Property: control.GetType().GetProperty("Value", Flags)!,
                        Before: Prop(control, "Value"))).ToArray();
                void Choose(string key) => filter.SelectedItem = filter.ItemsSource.Cast<object>()
                    .Single(item => Equals(Prop(item, "Id"), key));
                try
                {
                    opticalValues[0].Property.SetValue(opticalValues[0].Control, 1d);
                    opticalValues[1].Property.SetValue(opticalValues[1].Control, 1.5);
                    Choose("off");
                    var reference = await Render("camera-diffusion-and-halation-boost", ".tif", false);
                    Choose("black_pro_mist");
                    var stacked = await Render("camera-and-print-diffusion-export", ".tif", false);
                    var previous = await Render("camera-and-print-diffusion-previous", ".png", true);
                    var printDifference = Difference(reference, stacked);
                    var nativeParity = Difference(stacked, previous);
                    Check(printDifference.Mean > .0001 && printDifference.ChangedFraction > .1,
                        "print diffusion changes the actual native RGB16 export with camera diffusion and halation boost active");
                    Check(nativeParity.Mean < 8d / 65535 && nativeParity.Maximum < .01,
                        "both native commands preserve the same combined camera and print diffusion at 16 bits");
                    return new { printDifference, nativeParity };
                }
                finally
                {
                    foreach (var (control, property, before) in opticalValues) property.SetValue(control, before);
                    filter.SelectedItem = initial;
                }
            }

            async Task<object> CompareScanMode()
            {
                var mode = (ComboBox)Field(panel, "mode")!;
                var printing = mode.SelectedItem;
                var scan = mode.ItemsSource.Cast<object>().Single(item => Equals(Prop(item, "Id"), "scan"));
                var filter = (ComboBox)choices["print-diffusion-family"]!;
                var initialFilter = filter.SelectedItem;
                var filterOn = filter.ItemsSource.Cast<object>().Single(item => Equals(Prop(item, "Id"), "black_pro_mist"));
                var filterOff = filter.ItemsSource.Cast<object>().Single(item => Equals(Prop(item, "Id"), "off"));
                var printOnly = ((IEnumerable)Field(spatial, "printOnlyControls")!).Cast<FrameworkElement>().ToArray();
                var scanner = (CheckBox)((IDictionary)Field(spatial, "toggles")!)["scanner-enabled"]!;
                var initialScanner = scanner.IsChecked;
                var glare = (FrameworkElement)values["scanner-glare"]!;
                var glareValue = glare.GetType().GetProperty("Value", Flags)!;
                var initialGlare = glareValue.GetValue(glare);
                const double printGlare = .08;
                try
                {
                    filter.SelectedItem = filterOn;
                    scanner.IsChecked = true;
                    glareValue.SetValue(glare, printGlare);
                    Check(printOnly.Length == 2 && printOnly.All(group => group.IsEnabled),
                        "Print Simulation makes its diffusion and paper glare controls available");
                    mode.SelectedItem = scan;
                    var scanMtf = ((FrameworkElement)values["scanner-mtf"]!).IsEnabled;
                    Check(printOnly.All(group => !group.IsEnabled) && diffusion.IsEnabled && scanMtf,
                        $"Scan Negative disables only print optics (mode {Prop(mode.SelectedItem, "Id")}, print groups {string.Join(", ", printOnly.Select(group => group.IsEnabled))}, camera {diffusion.IsEnabled}, MTF {scanMtf})");
                    Check(ReferenceEquals(filter.SelectedItem, filterOn) && glareValue.GetValue(glare) is printGlare,
                        "Scan Negative retains the previously configured print settings");
                    var remembered = await Render("scan-with-remembered-print-settings", ".tif", false);
                    filter.SelectedItem = filterOff;
                    glareValue.SetValue(glare, 0d);
                    var neutral = await Render("scan-with-neutral-print-settings", ".png", true);
                    var difference = Difference(remembered, neutral);
                    Check(difference.Mean < 8d / 65535 && difference.Maximum < .01,
                        "both native Scan Negative exports agree when only inactive print settings differ");
                    filter.SelectedItem = filterOn;
                    glareValue.SetValue(glare, printGlare);
                    mode.SelectedItem = printing;
                    Check(printOnly.All(group => group.IsEnabled) && ReferenceEquals(filter.SelectedItem, filterOn)
                        && glareValue.GetValue(glare) is printGlare,
                        "returning to Print Simulation makes the retained settings available again");
                    return difference;
                }
                finally
                {
                    mode.SelectedItem = printing;
                    filter.SelectedItem = initialFilter;
                    scanner.IsChecked = initialScanner;
                    glareValue.SetValue(glare, initialGlare);
                }
            }

            async Task<object> CompareCompression(BitmapSource reference)
            {
                var selected = SelectedRecipeForTest(out var notifications);
                var format = selected.GetType().GetProperty("FileType", Flags)!;
                var originalFormat = format.GetValue(selected)!;
                var bits = selected.GetType().GetProperty("TiffBits", Flags)!;
                var originalBits = bits.GetValue(selected)!;
                var setting = selected.GetType().GetProperty("TiffCompression", Flags)!;
                var original = setting.GetValue(selected)!;
                var measurements = new List<object>();
                var comparisonSource = ReadPixelCodes(reference);
                // The same object serves the live command and the app recipe writer.
                // Keep the fixture rotation in memory, then restore every changed field.
                notifications.SetValue(selected, true);
                try
                {
                    format.SetValue(selected, Enum.Parse(format.PropertyType, "Tiff"));
                    SetCompression("Zip", TiffCompressOption.Zip);
                    var zip = await Render("export-zip", ".tif", false, expectedCompression: TiffCompressOption.Zip);
                    Measure("export-zip", TiffCompressOption.Zip, zip);
                    comparisonSource = ReadPixelCodes(zip);
                    var repeat = await Render("previous-zip-repeat", ".tif", true, expectedCompression: TiffCompressOption.Zip);
                    Measure("previous-zip-repeat", TiffCompressOption.Zip, repeat);
                    foreach (var (name, expected) in new[] { ("None", TiffCompressOption.None), ("LZW", TiffCompressOption.Lzw) })
                    {
                        SetCompression(name, expected);
                        foreach (var usePrevious in new[] { false, true })
                        {
                            var kind = (usePrevious ? "previous-" : "export-") + name.ToLowerInvariant();
                            var frame = await Render(kind, ".tif", usePrevious, expectedCompression: expected);
                            Measure(kind, expected, frame);
                        }
                    }
                    format.SetValue(selected, Enum.Parse(format.PropertyType, "Png"));
                    var pngRecipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                    Check(Equals(Prop(pngRecipe, "FormatId"), "Png"), "the live recipe reader follows the PNG file type");
                    foreach (var usePrevious in new[] { false, true })
                    {
                        var kind = (usePrevious ? "previous" : "export") + "-png-recipe";
                        var png = await Render(kind, ".png", usePrevious);
                        Check(offeredFilename?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true,
                            kind + " offers a PNG name from the native recipe");
                        var parity = Difference(zip, png);
                        Check(parity.Mean < 8d / 65535 && parity.Maximum < .01,
                            kind + " preserves the same processed 16-bit RGB as the TIFF recipe");
                        measurements.Add(new { command = kind, format = "Png", parity });
                    }
                }
                finally
                {
                    try
                    {
                        format.SetValue(selected, originalFormat);
                        bits.SetValue(selected, originalBits);
                        setting.SetValue(selected, original);
                    }
                    finally { notifications.SetValue(selected, false); }
                }
                Check(Equals(format.GetValue(selected), originalFormat) && Equals(bits.GetValue(selected), originalBits)
                    && Equals(setting.GetValue(selected), original) && notifications.GetValue(selected) is false,
                    "the live Capture One recipe format, TIFF depth, compression and notifications are restored");
                return measurements;

                void SetCompression(string name, TiffCompressOption expected)
                {
                    setting.SetValue(selected, Enum.Parse(setting.PropertyType, name));
                    var currentRecipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                    var currentFormat = Prop(currentRecipe, "FormatId");
                    var currentCompression = Prop(currentRecipe, "TiffCompression");
                    Check(Equals(currentFormat, "Tiff") && Equals(currentCompression, expected),
                        $"the live Capture One recipe selects TIFF {expected} (captured {currentFormat}/{currentCompression})");
                }

                void Measure(string kind, TiffCompressOption expected, BitmapSource frame)
                {
                    var candidate = ReadPixelCodes(frame);
                    var (mean, max, changed, aboveEight) = CodeDifferences(comparisonSource, candidate);
                    // An independent native develop plus film render has small
                    // run-to-run variation even when TIFF compression is unchanged.
                    Check(mean <= .2 && max <= 256 && aboveEight <= candidate.Length / 1000,
                        $"{kind} preserves the processed RGB16 image within native rerender tolerance ({mean:F4} mean, {max} max codes)");
                    measurements.Add(new { command = kind, compression = expected.ToString(),
                        pixelHash = Convert.ToHexString(SHA256.HashData(candidate)), mean, max, changed, aboveEight });
                }

                static byte[] ReadPixelCodes(BitmapSource image)
                {
                    var stride = checked(image.PixelWidth * 6);
                    var pixels = new byte[checked(stride * image.PixelHeight)];
                    image.CopyPixels(pixels, stride, 0);
                    return pixels;
                }

                static (double Mean, int Max, int Changed, int AboveEight) CodeDifferences(byte[] left, byte[] right)
                {
                    var baseline = MemoryMarshal.Cast<byte, ushort>(left.AsSpan());
                    var candidate = MemoryMarshal.Cast<byte, ushort>(right.AsSpan());
                    long total = 0;
                    var max = 0;
                    var changed = 0;
                    var aboveEight = 0;
                    for (var index = 0; index < baseline.Length; index++)
                    {
                        var error = Math.Abs(baseline[index] - candidate[index]);
                        total += error;
                        max = Math.Max(error, max);
                        if (error != 0) changed++;
                        if (error > 8) aboveEight++;
                    }
                    return ((double)total / baseline.Length, max, changed, aboveEight);
                }
            }

            async Task CheckReadOnlyDestination()
            {
                var before = routeCount;
                var protectedPath = Path.Combine(folder, caseName + "-read-only-destination.tif");
                var retryPath = Path.Combine(folder, caseName + "-export-after-write-failure.tif");
                destination = protectedPath;
                File.Copy(Path.Combine(folder, caseName + "-film-only.tif"), protectedPath);
                var originalAttributes = File.GetAttributes(protectedPath);
                var originalBytes = File.ReadAllBytes(protectedPath);
                File.SetAttributes(protectedPath, originalAttributes | FileAttributes.ReadOnly);
                try
                {
                    job = null;
                    ExecuteNativeExportCommand(previous);
                    Check(routeCount == before + 1 && job is not null && !await job.WaitAsync(TimeSpan.FromSeconds(45)),
                        "a native previous-settings export to a read-only file reports failure without a modal dialog");
                    CheckFailedReceipt();
                    job = null;
                    string? originalTitle = null;
                    var retrySubmitted = false;
                    ShowNativeDialog(() =>
                    {
                        if (Prop(bridge, "Dialog") is not Window { IsVisible: true } owner || owner.DataContext is not { } model
                            || Prop(model, "ExportCommand") is not ICommand confirm)
                            return false;
                        if (originalTitle is null)
                        {
                            if (!confirm.CanExecute(null)) return false;
                            originalTitle = owner.Title;
                            confirm.Execute(null);
                            return false;
                        }
                        if (!retrySubmitted)
                        {
                            if (job is not { IsCompletedSuccessfully: true } || !confirm.CanExecute(null)) return false;
                            Check(!job.Result && routeCount == before + 2 && owner.Title == originalTitle + " · Export failed",
                                "the original Capture One Export window stays usable and shows an accepted-job failure in its title");
                            CheckFailedReceipt();
                            destination = retryPath;
                            confirm.Execute(null);
                            retrySubmitted = true;
                        }
                        if (owner.Title != originalTitle) return false;
                        Check(true, "accepting a fresh destination clears the failure indication in the same window");
                        return true;
                    });
                    Check(routeCount == before + 3 && job is not null && await job.WaitAsync(TimeSpan.FromSeconds(45))
                        && File.Exists(retryPath) && Prop(bridge, "Dialog") is null,
                        "the same native dialog successfully retries with a new destination and releases its command");
                    Check(File.ReadAllBytes(protectedPath).AsSpan().SequenceEqual(originalBytes)
                        && !Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(),
                        "permission failure leaves previous 16-bit TIFF bytes intact and removes the staged file");
                }
                finally { File.SetAttributes(protectedPath, originalAttributes); }

                void CheckFailedReceipt()
                {
                    var failed = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                    var status = (TextBlock)Field(spatial, "exportStatus")!;
                    Check(Prop(failed, "Stage")?.ToString() == "Failed" && Equals(Prop(failed, "Destination"), protectedPath)
                        && Prop(failed, "Error") is string { Length: > 0 } explanation
                        && status.Text.StartsWith("Spektrafilm export failed.", StringComparison.Ordinal)
                        && status.Text.Contains(explanation, StringComparison.Ordinal)
                        && Field(spatial, "exportWork") is null,
                        "the accepted native job leaves its exported-file failure in the panel and durable receipt");
                }
            }

            async Task<object> CompareGrain(BitmapSource reference)
            {
                var nativeGrain = panel.GetType().Assembly.GetType("NativeGrain", true)!;
                var store = Field(panel, "looks")!;
                var selection = new FixtureSelection(panel);
                object ReadGrain() => nativeGrain.GetMethod("Read", Flags)!.Invoke(null, new[] { window })!;
                bool CanRestore(Guid variant) => nativeGrain.GetMethod("HasApplied", Flags)!
                    .Invoke(null, new[] { window, store, variant }) is true;
                var initialGrain = ReadGrain();
                var initialStage = Prop(Call(panel, "CaptureLook")!, "Grain")!;
                var opticalGrain = (ComboBox)choices["grain-model"]!;
                var initialChoice = opticalGrain.SelectedItem;
                var impact = Field(panel, "grainImpact")!;
                var size = Field(panel, "grainSize")!;
                var amount = values["grain-amount"]!;
                var initialAmount = Prop(amount, "Value");
                var apply = (Button)Field(panel, "applyGrain")!;
                var restore = (Button)Field(panel, "restoreGrain")!;
                void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                Check(Prop(initialChoice, "Id") is "off" && !CanRestore(expectedVariant),
                    "the disposable comparison starts with optical grain off and independent Capture One grain");
                try
                {
                    ((ComboBox)Field(panel, "grainType")!).SelectedIndex = 2;
                    impact.GetType().GetProperty("Value", Flags)!.SetValue(impact, 70d);
                    size.GetType().GetProperty("Value", Flags)!.SetValue(size, 75d);
                    Check(initialGrain.Equals(ReadGrain()), "staged native Grain settings leave the actual Capture One grain alone");
                    var staged = Difference(reference, await Render("staged-native-grain", ".tif", false));
                    Check(staged.Mean < 3d / 65535, "staging native grain controls does not change the native TIFF pixels");
                    Click(apply);
                    var applied = ReadGrain();
                    Check(CanRestore(expectedVariant) && !initialGrain.Equals(applied), "the native Apply grain button changed the actual Capture One variant");
                    var native = await Render("active-native-grain", ".tif", false);
                    var nativeDifference = Difference(reference, native);
                    opticalGrain.SelectedItem = opticalGrain.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "production");
                    amount.GetType().GetProperty("Value", Flags)!.SetValue(amount, 1.65d);
                    Check(applied.Equals(ReadGrain()), "opting into Spektrafilm optical grain leaves effective Capture One grain unchanged");
                    var combined = await Render("both-native-and-optical-grain", ".tif", false, closeAfterAcceptance: true);
                    var previous = await Render("both-grains-previous", ".png", true);
                    var opticalOverNative = Difference(native, combined);
                    var commandDifference = Difference(combined, previous);
                    if (compareClone) await CompareClonedGrain(applied);
                    Click(restore);
                    Check(!CanRestore(expectedVariant) && initialGrain.Equals(ReadGrain()), "the native Restore grain button restored the exact prior state");
                    var optical = await Render("active-optical-grain", ".tif", false);
                    var opticalDifference = Difference(reference, optical);
                    var nativeOverOptical = Difference(optical, combined);
                    Check(new[] { nativeDifference, opticalDifference, opticalOverNative, nativeOverOptical }
                            .All(diff => diff.Mean > .0001 && diff.ChangedFraction > .1),
                        "native and optical grain each affect output independently and both remain present when combined");
                    Check(commandDifference.Mean < 8d / 65535 && commandDifference.Maximum < .01,
                        "both native commands preserve the combined grain through TIFF and PNG delivery");
                    return new { staged, nativeDifference, opticalDifference, opticalOverNative, nativeOverOptical, commandDifference };
                }
                finally
                {
                    Call(panel, "SyncSelection");
                    if (!Equals(Field(panel, "displayedVariant"), expectedVariant)) selection.Select(expectedVariant, expectedVariant);
                    if (CanRestore(expectedVariant)) Click(restore);
                    opticalGrain.SelectedItem = initialChoice;
                    amount.GetType().GetProperty("Value", Flags)!.SetValue(amount, initialAmount);
                    Call(panel, "SetGrainControls", initialStage);
                    Call(panel, "SaveLook");
                }

                async Task CompareClonedGrain(object applied)
                {
                    var command = Field(Field(window, "mCommands")!, "CloneVariant")!;
                    var execute = command.GetType().GetMethod("Execute", Flags, null, new[] { typeof(bool) }, null)
                        ?? throw new MissingMethodException(command.GetType().FullName, "Execute(Boolean)");
                    var profile = panel.GetType().Assembly.GetType("HostProfile", true)!;
                    var capture = profile.GetMethod("CaptureTarget", Flags)!;
                    var sourcePath = Prop(capture.Invoke(null, new[] { window })!, "SourcePath");
                    var nativeSelection = profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
                    var documentVariant = Prop(nativeSelection, "Variant")!;
                    var core = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Core");
                    var imageProperty = core.GetType("P1.C1.Core.ICoreVariant", true)!.GetProperty("CoreImage")!;
                    var image = imageProperty.GetValue(documentVariant)!;
                    var variantsProperty = imageProperty.PropertyType.GetProperty("CoreVariants")!;
                    Guid[] ImageVariants() => ((IEnumerable)variantsProperty.GetValue(image)!).Cast<object>().Select(selection.Identity).ToArray();
                    var before = ImageVariants();
                    execute.Invoke(command, new object[] { false });
                    var started = Stopwatch.GetTimestamp();
                    Guid clone;
                    while (true)
                    {
                        Call(panel, "SyncSelection");
                        if (Field(panel, "displayedVariant") is Guid selected && selected != expectedVariant)
                        {
                            clone = selected;
                            break;
                        }
                        if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(8))
                            throw new TimeoutException("Capture One did not select a newly cloned variant.");
                        await Task.Delay(35);
                    }
                    try
                    {
                        Call(panel, "RefreshNativeGrain", true);
                        Check(ImageVariants().Except(before).SequenceEqual(new[] { clone })
                            && Equals(Prop(capture.Invoke(null, new[] { window })!, "SourcePath"), sourcePath),
                            "the native Clone command adds exactly one independent variant of the same RAW");
                        Check(Equals(Prop(applied, "Values"), Prop(ReadGrain(), "Values")),
                            "the actual clone inherits the applied Capture One grain values");
                        Check(!CanRestore(clone) && !restore.IsEnabled,
                            "the cloned variant cannot claim the original variant's native grain restore receipt");
                        Check(Prop(opticalGrain.SelectedItem, "Id") is "production",
                            "the independent cloned variant inherits the source Spektrafilm optical grain");
                    }
                    finally
                    {
                        // Keep the new variant in this disposable catalog. Capture One may not add Clone to its adjustment history.
                        selection.Select(expectedVariant, expectedVariant);
                        Call(panel, "RefreshNativeGrain", true);
                    }
                    Check(ImageVariants().Contains(clone) && CanRestore(expectedVariant)
                        && applied.Equals(ReadGrain()) && Prop(opticalGrain.SelectedItem, "Id") is "production",
                        "reselecting the source preserves its independent optical look and native grain restore action");
                }
            }

            async Task CheckSourceRevision(BitmapSource reference)
            {
                Diffusion("pro_mist");
                Value(3, 1.5);
                Value(4, .65);
                var hostProfile = panel.GetType().Assembly.GetType("HostProfile", true)!;
                var selection = hostProfile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
                var variant = Prop(selection, "Variant")!;
                var native = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Core")
                    .GetType("P1.C1.Core.ICoreVariant", true)!;
                var exposureProperty = native.GetProperty("Exposure")!;
                var initialExposure = (float)exposureProperty.GetValue(variant)!;
                var readRevision = host.GetMethod("GetRevision", Flags)!;
                object Revision() => readRevision.Invoke(null, new[] { window, (object)expectedVariant })!;
                var settings = Prop(variant, "Settings")!;
                void CheckHighResolutionOnly(object before, object after) => Check(
                    Equals(Prop(before, "LowRes"), Prop(after, "LowRes"))
                    && Equals(Prop(before, "Geometry"), Prop(after, "Geometry"))
                    && Convert.ToInt64(Prop(after, "HighRes")) == Convert.ToInt64(Prop(before, "HighRes")) + 1,
                    "the fixture presents only a high-resolution revision increment");
                var originalDestination = Path.Combine(folder, caseName + "-source-changed.tif");
                var originalFile = File.ReadAllBytes(Path.Combine(folder, caseName + "-no-film-reference.tif"));
                File.WriteAllBytes(originalDestination, originalFile);
                var retryDestination = Path.Combine(folder, caseName + "-source-retry.tif");
                var confirmations = 0;
                destination = originalDestination;
                job = null;
                beforeDestinationApproval = () =>
                {
                    var prior = Revision();
                    exposureProperty.SetValue(variant, initialExposure + .5f);
                    Check(!Equals(prior, Revision()), "the fixture changes the actual Capture One revision after snapshot capture");
                    beforeDestinationApproval = null;
                };
                try
                {
                    ShowNativeDialog(() =>
                    {
                        if (Prop(bridge, "Dialog") is not Window { IsVisible: true } owner || owner.DataContext is not { } model
                            || Prop(model, "ExportCommand") is not ICommand confirm || !confirm.CanExecute(null))
                            return false;
                        if (confirmations == 0)
                        {
                            confirmations++;
                            confirm.Execute(null);
                            return false;
                        }
                        if (job is not { IsCompleted: true } || Prop(bridge, "Busy") is true)
                            return false;
                        Check(job.GetAwaiter().GetResult() is false, "a changed native snapshot fails the accepted export");
                        var failed = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        Check(Prop(failed, "Stage")?.ToString() == "Failed"
                            && Prop(failed, "Error") is string reason && reason.Contains("before image development", StringComparison.Ordinal)
                            && reason.Contains("Try Export again", StringComparison.Ordinal),
                            "the receipt reports a source change and an explicit retry");
                        Check(owner.Title.EndsWith(" · Export failed", StringComparison.Ordinal)
                            && File.ReadAllBytes(originalDestination).SequenceEqual(originalFile),
                            "the same native dialog offers retry and the approved existing file stays byte exact");
                        exposureProperty.SetValue(variant, initialExposure);
                        destination = retryDestination;
                        confirmations++;
                        confirm.Execute(null);
                        return true;
                    });
                    Check(confirmations == 2 && job is not null && await job.WaitAsync(TimeSpan.FromSeconds(45)),
                        "confirming again in the same native dialog renders the newly approved snapshot");
                    var retried = ReadFrame(retryDestination);
                    Check(retried.Format == PixelFormats.Rgb48 && retried.PixelWidth == expectedWidth && retried.PixelHeight == expectedHeight,
                        "the retry produces a full recipe-size 16-bit TIFF");
                    using var icc = retried.ColorContexts!.Single().OpenProfileStream();
                    Check(Convert.ToHexString(SHA256.HashData(icc)) == iccSha, "the retried file retains the exact recipe ICC");
                    var accepted = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                    var timing = await WaitForTiming((Guid)Prop(accepted, "JobId")!);
                    Check(Prop(accepted, "Stage")?.ToString() == "Completed"
                        && timing.GetProperty("FilmRenderAndTransportMs").GetDouble() > 0,
                        "the fresh native export receipt completes actual Spektrafilm rendering");
                    var previousResult = await Render("previous-with-effects", ".png", true);
                    Check(Difference(reference, retried).Mean > .02 && Difference(retried, previousResult).Mean < 8d / 65535,
                        "the retried native TIFF and Previous Settings PNG have matching applied effects");

                    beforeDestinationApproval = () =>
                    {
                        var logged = Revision();
                        var priorState = (string)Prop(settings, "ModelCoreRepresentation")!;
                        Call(settings, "IncrementHighResCounter");
                        CheckHighResolutionOnly(logged, Revision());
                        var nextState = (string)Prop(settings, "ModelCoreRepresentation")!;
                        var beforeLines = priorState.Split('\n');
                        var afterLines = nextState.Split('\n');
                        var modelFieldsChanged = beforeLines.Zip(afterLines).Where(pair => pair.First != pair.Second)
                            .Select(pair => System.Text.RegularExpressions.Regex.Match(pair.Second, "^\\s*\"([A-Za-z0-9_]+)\"\\s*:")
                                .Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
                        File.WriteAllText(Path.Combine(folder, caseName + "-counter-model-evidence.json"), JsonSerializer.Serialize(new
                        {
                            identical = priorState == nextState, beforeLines = beforeLines.Length, afterLines = afterLines.Length,
                            modelFieldsChanged, localLayers = (Prop(variant, "LocalAdjustmentsLayers") as IEnumerable)?.Cast<object>().Count(),
                            lccSidecars = ((IEnumerable)native.GetMethod("GetSerializedLCCIds")!.Invoke(variant, null)!).Cast<object>().Count(),
                            iccSidecars = ((IEnumerable)native.GetMethod("GetSerializedICCIds")!.Invoke(variant, null)!).Cast<object>().Count(),
                        }));
                    };
                    var unchanged = await Render("verified-high-resolution-update", ".png", true);
                    Check(Difference(previousResult, unchanged).Mean < 8d / 65535
                        && File.ReadLines(Path.Combine(folder, "panel.log")).Any(line => line.Contains("export.source.verified.v1", StringComparison.Ordinal)),
                        "an approved counter-only change completes after checking the native model identity");

                    // Keep the new exposure but give it the same counter shape as
                    // a native high-resolution cache update. The native model must distinguish it.
                    beforeDestinationApproval = () =>
                    {
                        var before = Revision();
                        var counters = Prop(settings, "VariantProcessCounters")!;
                        exposureProperty.SetValue(variant, initialExposure + .75f);
                        var highOnly = Activator.CreateInstance(counters.GetType(),
                            Convert.ToInt64(Prop(before, "HighRes")) + 1, Prop(before, "LowRes")!, Prop(before, "Geometry")!)!;
                        Call(settings, "InitializeProcessCounters", highOnly);
                        CheckHighResolutionOnly(before, Revision());
                    };
                    destination = originalDestination;
                    job = null;
                    ExecuteNativeExportCommand(previous);
                    Check(job is not null && !await job.WaitAsync(TimeSpan.FromSeconds(45)),
                        "a pixel-changing edit with the same high-resolution-only counter shape fails the accepted export");
                    var changed = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                    Check(Prop(changed, "Stage")?.ToString() == "Failed"
                        && (Prop(changed, "Error") as string)?.Contains("before image development", StringComparison.Ordinal) == true
                        && File.ReadAllBytes(originalDestination).SequenceEqual(originalFile),
                        "the changed native model preserves the approved existing TIFF and reports verification failure");
                }
                finally
                {
                    beforeDestinationApproval = null;
                    exposureProperty.SetValue(variant, initialExposure);
                }

                static BitmapFrame ReadFrame(string path)
                {
                    using var file = File.OpenRead(path);
                    return BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.OnLoad).Frames[0];
                }

            }

            async Task<BitmapSource> Render(string kind, string extension, bool usePrevious, bool closeAfterAcceptance = false,
                TiffCompressOption? expectedCompression = null, string? expectedNativeLocation = null,
                string? fixtureOutputDirectory = null, string? fixtureOutputFileName = null, string? expectedProfileSha = null,
                (int Width, int Height)? expectedGeometry = null)
            {
                var (width, height) = expectedGeometry ?? (expectedWidth, expectedHeight);
                var before = routeCount;
                job = null;
                offeredFilename = null;
                offeredDirectory = null;
                destination = Path.Combine(fixtureOutputDirectory ?? folder, fixtureOutputFileName ?? (caseName + "-" + kind + extension));
                if (usePrevious)
                {
                    if (kind == "both-grains-previous")
                        Check(Prop(bridge, "Busy") is false,
                            "a finished job with a closed dialog releases Export before the next native command");
                    ExecuteNativeExportCommand(previous);
                }
                else
                {
                    var confirmed = false;
                    long? namingStarted = null;
                    ShowNativeDialog(() =>
                    {
                        if (Prop(bridge, "Dialog") is not Window { IsVisible: true } owner || owner.DataContext is not { } model)
                            return false;
                        var confirm = Prop(model, "ExportCommand") as ICommand
                            ?? throw new InvalidOperationException("The native Export window does not expose its confirmation command.");
                        if (!confirmed)
                        {
                            if (!confirm.CanExecute(null)) return false;
                            if (kind == "film-only" && suggestedStem is not null)
                            {
                                if (ReadNativeNamingSample(owner) is not { } sample)
                                    return false;
                                namingStarted ??= Stopwatch.GetTimestamp();
                                if (sample != suggestedStem && Stopwatch.GetElapsedTime(namingStarted.Value) < TimeSpan.FromSeconds(3))
                                    return false;
                                Check(sample == suggestedStem,
                                    $"Spektrafilm uses the same stem as the visible Capture One Naming tool sample (expected '{suggestedStem}', saw '{sample}')");
                            }
                            if (expectedNativeLocation is not null && !CheckNativeLocationSample(owner, expectedNativeLocation))
                                return false;
                            confirm.Execute(null);
                            confirmed = true;
                        }
                        if (closeAfterAcceptance)
                        {
                            var accepted = Call(Field(spatial, "exportJobs")!, "ReadLatest");
                            if (job is not { IsCompleted: false } || Field(spatial, "exportWork") is null
                                || accepted is null || !Equals(Prop(accepted, "Destination"), destination)
                                || Prop(accepted, "Finished") is not false)
                                return false;
                            Check(true, "the native dialog has an accepted immutable output before the window closes");
                            owner.Close();
                        }
                        return true;
                    }, () => job is { IsCompletedSuccessfully: true } && !job.Result
                        ? Prop(Call(Field(spatial, "exportJobs")!, "ReadLatest")!, "Error") as string ?? "The accepted export returned false."
                        : null);
                }
                if (closeAfterAcceptance)
                    Check(Prop(bridge, "Dialog") is null, "closing the actual export dialog releases its native confirmation binding");
                Check(routeCount == before + 1 && job is not null && await job.WaitAsync(TimeSpan.FromSeconds(45)),
                    $"{kind} took exactly one accepted job through Capture One's native command");
                if (expectedSuggestedFile is not null && kind is ("film-only" or "previous-with-effects"))
                    Check(offeredFilename == expectedSuggestedFile, $"{kind} offers the captured native recipe name in Save");
                using var file = File.OpenRead(destination);
                var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad).Frames[0];
                Check(frame.Format == (extension == ".tif" ? PixelFormats.Rgb48 : PixelFormats.Rgba64)
                    && frame.PixelWidth == width && frame.PixelHeight == height && frame.ColorContexts is { Count: 1 },
                    $"{kind} reopens as the expected 16-bit format at the selected recipe dimensions");
                using var icc = frame.ColorContexts![0].OpenProfileStream();
                Check(Convert.ToHexString(SHA256.HashData(icc)) == (expectedProfileSha ?? iccSha), $"{kind} embeds the exact selected recipe ICC");
                if (extension == ".tif")
                {
                    var compressionTag = (frame.Metadata as BitmapMetadata)?.GetQuery("/ifd/{ushort=259}");
                    var selectedCompression = expectedCompression ?? compression;
                    var matches = (selectedCompression, compressionTag) switch
                    {
                        (TiffCompressOption.None, (ushort)1) or (TiffCompressOption.Lzw, (ushort)5)
                            or (TiffCompressOption.Zip, (ushort)8 or (ushort)32946) => true,
                        _ => false,
                    };
                    Check(matches, $"{kind} uses the selected Capture One TIFF compression {selectedCompression}");
                }
                var receipt = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                Check(Equals(Prop(receipt, "Destination"), destination) && Equals(Prop(receipt, "VariantId"), expectedVariant)
                    && Prop(receipt, "Stage")?.ToString() == "Completed",
                    $"{kind} finished its own export receipt");
                var detail = await WaitForTiming((Guid)Prop(receipt, "JobId")!);
                var film = detail.GetProperty("FilmRenderAndTransportMs");
                var expectedFilm = kind != "no-film-reference";
                var stages = new[] { "ViewerDrainMs", "NativeDevelopmentMs", "BlendAndCropMs", "IccProfileSetupMs", "EncodeVerifyAndCommitMs" }
                    .Select(key => detail.GetProperty(key).GetDouble()).ToArray();
                var filmMs = film.ValueKind == JsonValueKind.Number ? film.GetDouble() : 0;
                Check(detail.GetProperty("SourceBitDepth").GetInt32() >= 16
                    && detail.GetProperty("InputEncoding").GetString() == inputEncoding
                    && detail.GetProperty("OutputWidth").GetInt32() == width
                    && detail.GetProperty("OutputHeight").GetInt32() == height
                    && detail.GetProperty("OutputFormat").GetString() == (extension == ".tif" ? "TIFF" : "PNG")
                    && detail.GetProperty("TiffCompression").GetString() == (extension == ".tif"
                        ? (expectedCompression ?? compression).ToString() : null)
                    && detail.GetProperty("TransformsDeliveryProfile").ValueKind is JsonValueKind.True or JsonValueKind.False
                    && stages.All(value => double.IsFinite(value) && value >= 0)
                    && detail.GetProperty("TotalMs").GetDouble() >= stages.Sum() + filmMs
                    && (expectedFilm ? filmMs > 0 : film.ValueKind == JsonValueKind.Null)
                    && !detail.TryGetProperty("Destination", out _) && !detail.TryGetProperty("SourcePath", out _),
                    $"{kind} logs the native-to-file stages for its job with high-depth geometry and without paths");
                timings.Add(detail);
                return frame;
            }

            static string? ReadNativeNamingSample(Window owner)
            {
                var namingTool = FindExportTool(owner, "ExportNaming");
                if (namingTool is null) return null;
                var content = Prop(namingTool, "ToolContent")
                    ?? throw new InvalidOperationException("The native Naming content is unavailable.");
                var naming = Field(content, "m_viewModel")
                    ?? throw new InvalidOperationException("The native Naming sample model is unavailable.");
                return Path.GetFileNameWithoutExtension(Call(naming, "GetSampleText") as string ?? "");
            }

            static bool CheckNativeLocationSample(Window owner, string expected)
            {
                var tool = FindExportTool(owner, "ExportDestination");
                if (tool is null) return false;
                var viewModel = Field(tool, "m_contentViewModel")!;
                var sample = Prop(viewModel, "SamplePath") as string;
                if (string.IsNullOrWhiteSpace(sample) || !Path.IsPathFullyQualified(sample)) return false;
                Check(string.Equals(expected, Path.TrimEndingDirectorySeparator(sample), StringComparison.OrdinalIgnoreCase),
                    "Capture One's native Location tool shows the matching nested recipe path");
                return true;
            }

            static object? FindExportTool(Window owner, string typeName)
            {
                var toolList = Field(owner, "m_exportToolsList")
                    ?? throw new InvalidOperationException("The Export dialog has no native tool list.");
                var tools = ((IEnumerable)Prop(toolList, "AllScrollTools")!).Cast<object>()
                    .Concat(((IEnumerable)Prop(toolList, "AllPinnedTools")!).Cast<object>());
                // Capture One builds its export tools asynchronously after showing the dialog.
                return tools.Select(item => Prop(item, "Tool")).FirstOrDefault(tool =>
                    tool?.GetType().FullName == "P1.C1.Components.ExportDialog.Tools." + typeName);
            }

            async Task<JsonElement> WaitForTiming(Guid jobId)
            {
                var log = Path.Combine(folder, "panel.log");
                var start = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(3))
                {
                    foreach (var line in File.ReadAllLines(log).Where(line => line.Contains("export.performance.v2", StringComparison.Ordinal)))
                    {
                        try
                        {
                            using var document = JsonDocument.Parse(line);
                            if (document.RootElement.TryGetProperty("Details", out var details)
                                && details.GetProperty("JobId").GetGuid() == jobId)
                                return details.Clone();
                        }
                        catch (JsonException) { /* The background pump may still be appending this line. */ }
                    }
                    await Task.Delay(30);
                }
                throw new TimeoutException("The export's structured timing event did not reach the panel log.");
            }

            void ShowNativeDialog(Func<bool> interact, Func<string?>? failureAfterHandling = null)
            {
                var started = Stopwatch.GetTimestamp();
                var handled = false;
                Exception? failure = null;
                using var poll = new Forms.Timer { Interval = 40 };
                poll.Tick += (_, _) =>
                {
                    try
                    {
                        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(50))
                            throw new TimeoutException("The real Capture One Export dialog did not finish.");
                        if (handled)
                        {
                            if (failureAfterHandling?.Invoke() is { } error)
                                throw new InvalidOperationException(error);
                            return;
                        }
                        handled = interact();
                    }
                    catch (Exception error)
                    {
                        poll.Stop();
                        failure = error;
                        ((Window?)Prop(bridge, "Dialog") ?? Prop(window, "ExportDialogWindow") as Window)?.Close();
                    }
                };
                poll.Start();
                try { ExecuteNativeExportCommand(normal); }
                finally { poll.Stop(); }
                if (failure is not null) throw failure;
                Check(handled, "Capture One opened and handled its real native Export dialog");
            }
        }
        finally
        {
            try
            {
                if (variantEvents is not null)
                    variantEvents.PropertyChanged -= VariantChanged;
                if (settingsEvents is not null)
                    settingsEvents.PropertyChanged -= SettingsChanged;
                if (variantEvents is not null)
                {
                    var events = notifications.Select(entry => new
                    {
                        utc = notificationStart + Stopwatch.GetElapsedTime(notificationClock, entry.At),
                        entry.Source,
                        entry.Property,
                    });
                    File.WriteAllText(Path.Combine(folder, caseName + "-native-source-events.json"), JsonSerializer.Serialize(events));
                }
            }
            finally
            {
                try { (bridge as IDisposable)?.Dispose(); }
                finally
                {
                    foreach (var slider in changedSliders) slider.Property.SetValue(slider.Control, slider.Before);
                    diffusion.SelectedItem = priorDiffusion;
                    toggle.IsChecked = priorEnabled;
                    hookField.DeclaringType!.GetMethod("AttachExport", Flags)!.Invoke(null, new[] { window, hosted });
                }
            }
        }
        Check(hookField.GetValue(null) is not null && Prop(panel, "ExportBridgeStatus")?.ToString() == "Connected",
            "the real native Export bridge reconnects after the command test");
    }

    private static void ExecuteNativeExportCommand(object command)
    {
        if (command is ICommand executable)
        {
            Check(executable.CanExecute(null), "Capture One's native Export command is available");
            executable.Execute(null);
            return;
        }
        var execute = command.GetType().GetMethod("Execute", Flags, null, new[] { typeof(bool) }, null)
            ?? throw new MissingMethodException(command.GetType().FullName, "Execute(Boolean)");
        execute.Invoke(command, new object[] { false });
    }

    private sealed record ExportDifference(int Samples, double Mean, double Maximum, double ChangedFraction);

    private static ExportDifference Difference(BitmapSource reference, BitmapSource output)
    {
        var width = reference.PixelWidth;
        var firstChannels = reference.Format.BitsPerPixel / 16;
        var secondChannels = output.Format.BitsPerPixel / 16;
        var a = new byte[width * firstChannels * 2];
        var b = new byte[width * secondChannels * 2];
        long sum = 0;
        var maximum = 0;
        var changed = 0;
        var samples = 0;
        for (var y = 11; y < reference.PixelHeight; y += 23)
        {
            var row = new Int32Rect(0, y, width, 1);
            reference.CopyPixels(row, a, a.Length, 0);
            output.CopyPixels(row, b, b.Length, 0);
            for (var x = 7; x < width; x += 19)
            for (var channel = 0; channel < 3; channel++)
            {
                var distance = Math.Abs(BitConverter.ToUInt16(a, (x * firstChannels + channel) * 2)
                    - BitConverter.ToUInt16(b, (x * secondChannels + channel) * 2));
                sum += distance;
                maximum = Math.Max(maximum, distance);
                if (distance > 4) changed++;
                samples++;
            }
        }
        return new(samples, sum / (65535d * samples), maximum / 65535d, (double)changed / samples);
    }
}
