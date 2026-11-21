using System;
using System.Buffers.Binary;
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
        bool changeSourceRevision = false, bool compareRecipeRestart = false, bool compareMaskedSource = false,
        bool compareProfileSidecars = false, bool compareLccSidecar = false, bool compareInputSharpening = false, bool compareWatermark = false)
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
            if (changeSourceRevision || compareRecipeRestart || compareMaskedSource || compareProfileSidecars || compareLccSidecar)
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
            if (compareInputSharpening)
            {
                await CheckInputSharpeningModes();
                return;
            }
            if (compareWatermark)
            {
                await CheckNativeRecipeWatermark();
                return;
            }
            Value(0, 0);
            var unprocessed = compareFolders
                ? await RenderAfterNativeDevelopment()
                : await Render("no-film-reference", ".tif", false);
            Value(0, 100);
            if (changeSourceRevision || compareMaskedSource || compareProfileSidecars || compareLccSidecar)
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
                        Check(offeredTitle?.StartsWith($"Recipe requests {requested} · Save 16-bit TIFF, PNG or PSD", StringComparison.Ordinal) == true,
                            $"Save discloses the {requested} substitution before publication");
                        var describe = spatial.GetType().GetMethod("DescribePreviousExport", Flags)!;
                        Check(Equals(Prop(receipt, "DeliveryNote"), note)
                            && ((TextBlock)Field(spatial, "exportStatus")!).Text.Contains(note, StringComparison.Ordinal)
                            && (describe.Invoke(null, new[] { receipt }) as string)?.Contains(note, StringComparison.Ordinal) == true,
                            $"the completed {requested} export records the actual 16-bit alternative");
                    }
                }

            }

            async Task CheckNativeRecipeWatermark()
            {
                var selected = SelectedRecipeForTest(out var suppress);
                var manager = ((Delegate)Field(bridge!, "destinations")!).DynamicInvoke()!;
                var enabled = ((IEnumerable)Prop(manager, "EnabledDestinations")!).Cast<object>().ToArray();
                Check(enabled.Length == 1 && Equals(Prop(enabled[0], "Name"), Prop(selected, "Name")),
                    "only the selected native recipe is enabled for the watermark comparisons");
                var originals = new[] { "DestinationRootFolder", "DestinationCustomRootFolder", "DestinationSubFolder",
                    "DestinationFilename", "FileType", "TiffBits", "ColorProfilePath", "CropExportMethod", "WatermarkType", "WatermarkText",
                    "WatermarkTextColor", "WatermarkTextFont",
                    "WatermarkTextOpacity", "WatermarkTextScale", "WatermarkHTextPlacement", "WatermarkVTextPlacement",
                    "WatermarkImagePath", "WatermarkImageReference", "WatermarkImageOpacity", "WatermarkImageScale",
                    "WatermarkHImagePlacement", "WatermarkVImagePlacement" }
                    .Select(name => selected.GetType().GetProperty(name, Flags)!).ToDictionary(p => p.Name, p => (p, p.GetValue(selected)));
                var directory = Directory.CreateDirectory(Path.Combine(folder, caseName + "-watermark-reference")).FullName;
                var pattern = Path.Combine(directory, "watermark-fixture.png");
                var patternPixels = new byte[96 * 48 * 4];
                for (var y = 0; y < 48; y++)
                for (var x = 0; x < 96; x++)
                {
                    var offset = (y * 96 + x) * 4;
                    patternPixels[offset] = (byte)(x < 48 ? 200 : 20);
                    patternPixels[offset + 1] = (byte)(x < 48 ? 50 : 180);
                    patternPixels[offset + 2] = 240;
                    patternPixels[offset + 3] = (byte)(y < 4 || y > 44 ? 0 : x < 48 ? 255 : 128);
                }
                var marker = BitmapSource.Create(96, 48, 96, 96, PixelFormats.Bgra32, null, patternPixels, 96 * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(marker));
                using (var stream = File.Create(pattern)) encoder.Save(stream);

                void Set(string name, object value) => originals[name].p.SetValue(selected, value);
                void Choose(string name, string value) => Set(name, Enum.Parse(originals[name].p.PropertyType, value));
                object ReadRecipe() => host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                var watermarkProfilePath = (string)host.GetProperty("WatermarkProfilePath", Flags)!.GetValue(null)!;
                using var font = new System.Drawing.Font("Segoe UI", 24, System.Drawing.GraphicsUnit.Pixel);
                var comparisons = new List<object>();
                var profile = panel.GetType().Assembly.GetType("HostProfile", true)!;
                var selection = profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
                var variant = Prop(selection, "Variant")!;
                var crop = variant.GetType().GetProperty("CropRawUnconstrained", Flags)!;
                var originalCrop = crop.GetValue(variant)!;
                var coreImage = Prop(variant, "CoreImage")!;
                var dimensions = (System.Drawing.Size)Prop(coreImage, "ImageDimensions")!;
                var sourceStem = Path.GetFileNameWithoutExtension((string)Prop(coreImage, "FullpathFilename")!);
                suppress.SetValue(selected, true);
                try
                {
                    Choose("DestinationRootFolder", "CustomPath");
                    Set("DestinationCustomRootFolder", directory);
                    Set("DestinationSubFolder", string.Empty);
                    Choose("FileType", "Tiff");
                    Set("TiffBits", 16);
                    Choose("WatermarkType", "Image");
                    Set("WatermarkImagePath", pattern);
                    Call(selected, "LoadWatermark", watermarkProfilePath);
                    Check(Prop(selected, "WatermarkImageReference") is BitmapSource,
                        "Capture One loaded the disposable transparent watermark asset");
                    Set("WatermarkHImagePlacement", .23f);
                    Set("WatermarkVImagePlacement", .38f);
                    Set("WatermarkImageScale", 1.5f);
                    Set("WatermarkImageOpacity", .65f);
                    Set("WatermarkTextColor", System.Drawing.Color.FromArgb(213, 182, 112));
                    Set("WatermarkTextFont", font);
                    Set("WatermarkHTextPlacement", .74f);
                    Set("WatermarkVTextPlacement", .25f);
                    Set("WatermarkTextOpacity", .75f);
                    Set("WatermarkTextScale", 1.2f);
                    Value(0, 0);
                    var profiles = Path.Combine(Path.GetDirectoryName(window.GetType().Assembly.Location)!, "Color Profiles", "Common");
                    foreach (var (label, name) in new[] { ("srgb", "sRGB Color Space Profile.icm"), ("adobe", "Adobe RGB (1998).icm") })
                    {
                        var path = Path.Combine(profiles, name);
                        var profileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                        Set("ColorProfilePath", path);
                        await CheckCurrentWatermark(label, profileHash, label == "srgb" ? new[] { "Image", "Text" } : new[] { "Image" });
                    }
                    Set("ColorProfilePath", Path.Combine(profiles, "Adobe RGB (1998).icm"));
                    Choose("WatermarkType", "Image");
                    var filmRecipe = ReadRecipe();
                    var filmCompression = (TiffCompressOption)Prop(filmRecipe, "TiffCompression")!;
                    var filmProfile = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes((string)Prop(filmRecipe, "ColorProfilePath")!)));
                    var imageWithoutFilm = await Render("watermark-before-film", ".tif", false, expectedCompression: filmCompression,
                        expectedProfileSha: filmProfile);
                    Value(0, 100);
                    var tiff = await Render("watermark-with-film", ".tif", false, expectedCompression: filmCompression,
                        expectedProfileSha: filmProfile);
                    var png = await Render("watermark-with-film-previous", ".png", true, expectedProfileSha: filmProfile);
                    var commandParity = Difference(tiff, png);
                    Check(Difference(imageWithoutFilm, tiff).Mean > .01 && commandParity.Mean < 8d / 65535 && commandParity.Maximum < .01,
                        "native Export and Previous Settings agree on the same processed RGB16 image and watermark");

                    Choose("WatermarkType", "None");
                    var filmOnly = await Render("film-without-watermark", ".tif", false, expectedCompression: filmCompression,
                        expectedProfileSha: filmProfile);
                    Choose("WatermarkType", "Image");
                    Choose("FileType", "Psd");
                    var layered = await Render("psd-with-film-and-watermark", ".psd", true, expectedProfileSha: filmProfile,
                        expectedPsdBackground: filmOnly);
                    var layerParity = Difference(tiff, layered);
                    Check(layerParity.Mean < 8d / 65535 && layerParity.Maximum < .01,
                        "PSD's merged film and watermark match the RGB16 TIFF from the same native recipe");
                    Choose("FileType", "Tiff");

                    Value(0, 0);
                    const float centerX = .44f, centerY = .53f, cropWidth = .55f, cropHeight = .65f;
                    crop.SetValue(variant, Activator.CreateInstance(crop.PropertyType,
                        new System.Drawing.PointF(dimensions.Width * centerX, dimensions.Height * centerY),
                        new System.Drawing.SizeF(dimensions.Width * cropWidth, dimensions.Height * cropHeight))!);
                    var cropped = ReadRecipe();
                    var croppedDimensions = ((int)Prop(cropped, "Width")!, (int)Prop(cropped, "Height")!);
                    Check(croppedDimensions != (expectedWidth, expectedHeight), "native watermark comparison has an asymmetric RAW crop");
                    var cropOrigin = ((int)Math.Round(expectedWidth * (centerX - cropWidth / 2)),
                        (int)Math.Round(expectedHeight * (centerY - cropHeight / 2)));
                    await CheckCurrentWatermark("adobe-cropped", filmProfile, new[] { "Image" }, croppedDimensions, cropOrigin);
                    Choose("CropExportMethod", "Ignore");
                    await CheckCurrentWatermark("adobe-ignore-crop", filmProfile, new[] { "Image" });
                }
                finally
                {
                    try
                    {
                        crop.SetValue(variant, originalCrop);
                        foreach (var item in originals.Values) item.p.SetValue(selected, item.Item2);
                    }
                    finally { suppress.SetValue(selected, false); }
                }

                async Task CheckCurrentWatermark(string label, string profileHash, string[] modes,
                    (int Width, int Height)? geometry = null, (int X, int Y) cropOrigin = default)
                {
                    Choose("WatermarkType", "None");
                    Set("DestinationFilename", label + "-none");
                    var baseFrame = await CaptureOrdinaryRecipeTiff(directory, label + "-none", profileHash, geometry);
                    toggle.IsChecked = true;
                    var recipeCompression = (TiffCompressOption)Prop(ReadRecipe(), "TiffCompression")!;
                    var basePlugin = await Render(label + "-no-watermark", ".tif", false, expectedCompression: recipeCompression,
                        expectedProfileSha: profileHash, expectedGeometry: geometry);
                    foreach (var mode in modes)
                    {
                        Choose("WatermarkType", mode);
                        string? literalPixels = null;
                        if (mode == "Image")
                            Call(selected, "LoadWatermark", watermarkProfilePath);
                        else
                        {
                            Set("WatermarkText", "Spektrafilm " + sourceStem);
                            var literalMark = Prop(ReadRecipe(), "Watermark");
                            Check(literalMark is not null, "Capture One rendered the literal name of the copied RAW");
                            literalPixels = (string)Prop(literalMark!, "PixelsSha256")!;
                            Set("WatermarkText", "Spektrafilm " + NativeNameToken("OriginalName"));
                        }
                        var recipeWithWatermark = ReadRecipe();
                        var captured = Prop(recipeWithWatermark, "Watermark");
                        Check(captured is not null && Prop(recipeWithWatermark, "Omissions")?.ToString() == "None",
                            $"Capture One returned an immutable {mode} watermark at {label}");
                        var approvedPixels = (string)Prop(captured!, "PixelsSha256")!;
                        if (literalPixels is not null)
                            Check(approvedPixels == literalPixels, "Capture One resolves the Original Name token to the selected RAW name");
                        var stem = label + "-" + mode.ToLowerInvariant();
                        var x = (int)Prop(captured!, "X")! - cropOrigin.X;
                        var y = (int)Prop(captured!, "Y")! - cropOrigin.Y;
                        var width = (int)Prop(captured!, "Width")!;
                        var height = (int)Prop(captured!, "Height")!;
                        Set("DestinationFilename", stem);
                        var ordinary = await CaptureOrdinaryRecipeTiff(directory, stem, profileHash, geometry);
                        toggle.IsChecked = true;
                        if (mode == "Text")
                            beforeDestinationApproval = () => Set("WatermarkText", "Changed while choosing a destination");
                        BitmapSource plugin;
                        try
                        {
                            plugin = await Render(stem + "-spektrafilm", ".tif", false, expectedCompression: recipeCompression,
                                expectedProfileSha: profileHash, expectedGeometry: geometry);
                        }
                        finally { beforeDestinationApproval = null; }
                        if (mode == "Text")
                        {
                            var changed = Prop(ReadRecipe(), "Watermark");
                            Check(changed is not null && !Equals(Prop(changed, "PixelsSha256"), approvedPixels),
                                "the recipe really changed in the Save picker while the accepted export retained its captured watermark");
                        }
                        var result = CompareWatermark(baseFrame, ordinary, basePlugin, plugin, x, y, width, height);
                        comparisons.Add(new { stem, x, y, width, height, result });
                        File.WriteAllText(Path.Combine(directory, "watermark-comparisons.json"),
                            JsonSerializer.Serialize(comparisons, new JsonSerializerOptions { WriteIndented = true }));
                        Check(result.NativeVisible > 100 && result.Mean < 400 && result.Maximum < 2500
                            && result.UnmarkedMean < Math.Max(.5, 2 * result.NativeUnmarkedMean) && result.UnmarkedMaximum < 2048,
                            $"the {label} {mode} watermark follows native placement and opacity "
                            + $"(mean {result.Mean:F1}, peak {result.Maximum}, unmarked mean {result.UnmarkedMean:F2} codes)");
                    }
                }
            }

            static WatermarkComparison CompareWatermark(BitmapSource nativeBase,
                BitmapSource nativeMarked, BitmapSource pluginBase, BitmapSource pluginMarked, int x, int y, int width, int height)
            {
                var stride = nativeBase.PixelWidth * 6;
                var frames = new[] { nativeBase, nativeMarked, pluginBase, pluginMarked };
                var rows = frames.Select(_ => new ushort[stride / 2]).ToArray();
                var count = 0;
                var sum = 0L;
                var maximum = 0;
                var unmarkedCount = 0;
                var unmarkedSum = 0L;
                var nativeUnmarkedSum = 0L;
                var unmarkedMaximum = 0;
                var nativeVisible = 0;
                for (var row = 0; row < nativeBase.PixelHeight; row++)
                {
                    var rectangle = new Int32Rect(0, row, nativeBase.PixelWidth, 1);
                    for (var i = 0; i < frames.Length; i++)
                        frames[i].CopyPixels(rectangle, rows[i], stride, 0);
                    for (var i = 0; i < rows[0].Length; i++)
                    {
                        var pluginDelta = rows[3][i] - rows[2][i];
                        var nativeDelta = rows[1][i] - rows[0][i];
                        if (row < y || row >= y + height || i / 3 < x || i / 3 >= x + width)
                        {
                            unmarkedMaximum = Math.Max(unmarkedMaximum, Math.Abs(pluginDelta));
                            unmarkedSum += Math.Abs(pluginDelta);
                            nativeUnmarkedSum += Math.Abs(nativeDelta);
                            unmarkedCount++;
                            continue;
                        }
                        if (Math.Abs(nativeDelta) > 4) nativeVisible++;
                        var error = Math.Abs(nativeDelta - pluginDelta);
                        maximum = Math.Max(maximum, error);
                        sum += error;
                        count++;
                    }
                }
                return new(sum / (double)count, maximum, unmarkedSum / (double)unmarkedCount,
                    unmarkedMaximum, nativeUnmarkedSum / (double)unmarkedCount, nativeVisible);
            }

            async Task CheckInputSharpeningModes()
            {
                var selected = SelectedRecipeForTest(out var suppress);
                var manager = ((Delegate)Field(bridge!, "destinations")!).DynamicInvoke()!;
                var enabled = ((IEnumerable)Prop(manager, "EnabledDestinations")!).Cast<object>().ToArray();
                Check(enabled.Length == 1 && Equals(Prop(enabled[0], "Name"), Prop(selected, "Name")),
                    "only the selected native recipe is enabled for the independent test exports");
                var profile = panel.GetType().Assembly.GetType("HostProfile", true)!;
                var variant = Prop(profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!, "Variant")!;
                var core = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "P1.C1.Core")
                    .GetType("P1.C1.Core.ICoreVariant", true)!;
                var originalRecipe = new[] { "DestinationRootFolder", "DestinationCustomRootFolder", "DestinationSubFolder",
                    "DestinationFilename", "FileType", "TiffBits", "ColorProfilePath", "SharpeningOutputControl", "WatermarkType" }
                    .Select(name => selected.GetType().GetProperty(name, Flags)!).ToDictionary(p => p.Name, p => (p, p.GetValue(selected)));
                var originalVariant = new[] { "UnsharpMaskAmount", "UnsharpMaskRadius", "UnsharpMaskThreshold", "LensDiffractionCorrectionEnabled" }
                    .Select(name => core.GetProperty(name)!).ToDictionary(p => p.Name, p => (p, p.GetValue(variant)));
                var nativeFolder = Directory.CreateDirectory(Path.Combine(folder, caseName + "-ordinary-sharpening")).FullName;
                void RecipeValue(string name, object value) => originalRecipe[name].p.SetValue(selected, value);
                void RecipeChoice(string name, string value) => RecipeValue(name, Enum.Parse(originalRecipe[name].p.PropertyType, value));
                void NativeValue(string name, object value) => originalVariant[name].p.SetValue(variant, value);
                object CaptureRecipe() => host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
                var workingSpace = Enum.Parse(host.Assembly.GetType("PreviewInputSpace", true)!, "Srgb");
                var (profilePath, profileSha) = ((string, string))host.GetMethod("ResolvePreviewProfile", Flags)!
                    .Invoke(null, new[] { workingSpace })!;
                var results = new Dictionary<string, object>();
                suppress.SetValue(selected, true);
                try
                {
                    RecipeChoice("DestinationRootFolder", "CustomPath");
                    RecipeValue("DestinationCustomRootFolder", nativeFolder);
                    RecipeValue("DestinationSubFolder", string.Empty);
                    RecipeChoice("FileType", "Tiff");
                    RecipeValue("TiffBits", 16);
                    // Use the same working profile so the comparison isolates
                    // sharpening from WIC versus Capture One ICC conversion.
                    RecipeValue("ColorProfilePath", profilePath);
                    RecipeChoice("WatermarkType", "None");
                    NativeValue("UnsharpMaskAmount", 500f);
                    NativeValue("UnsharpMaskRadius", 2.4f);
                    NativeValue("UnsharpMaskThreshold", 0f);
                    NativeValue("LensDiffractionCorrectionEnabled", true);
                    Value(0, 0);
                    BitmapSource? nativeDefault = null, nativeLens = null, nativeDisabled = null;
                    foreach (var mode in new[] { "LensAndSharpening", "Lens", "Disabled" })
                    {
                        RecipeChoice("SharpeningOutputControl", mode);
                        RecipeValue("DestinationFilename", mode);
                        var ordinary = await CaptureOrdinaryRecipeTiff(nativeFolder, mode, profileSha);
                        nativeDefault ??= ordinary;
                        if (mode == "Lens") nativeLens = ordinary;
                        if (mode == "Disabled") nativeDisabled = ordinary;
                        toggle.IsChecked = true;
                        var selectedRecipe = CaptureRecipe();
                        Check(Prop(selectedRecipe, "Sharpening")?.ToString() == mode
                            && Prop(selectedRecipe, "Omissions")?.ToString() == "None",
                            $"the live recipe captures {mode} as supported");
                        var filmFree = await Render("selected-" + mode, ".tif", true,
                            expectedCompression: (TiffCompressOption)Prop(selectedRecipe, "TiffCompression")!, expectedProfileSha: profileSha);
                        var agreement = Difference(ordinary, filmFree);
                        var correction = Difference(nativeDefault, ordinary);
                        Check(agreement.Mean < 8d / 65535 && agreement.Maximum < .01,
                            $"the recipe's {mode} changes match an ordinary Capture One export");
                        if (mode != "LensAndSharpening")
                            Check(correction.Mean > .00005 && correction.ChangedFraction > .08,
                                $"ordinary Capture One removes visible sharpening with {mode}");
                        results.Add(mode, new { agreement, correction });
                    }

                    Value(0, 100);
                    Diffusion("pro_mist");
                    RecipeChoice("SharpeningOutputControl", "Disabled");
                    var tiff = await Render("disabled-sharpening-with-film", ".tif", false,
                        expectedCompression: (TiffCompressOption)Prop(CaptureRecipe(), "TiffCompression")!, expectedProfileSha: profileSha);
                    var png = await Render("disabled-sharpening-with-film-previous", ".png", true, expectedProfileSha: profileSha);
                    var commandAgreement = Difference(tiff, png);
                    var lensDifference = Difference(nativeLens!, nativeDisabled!);
                    Check(commandAgreement.Mean < 8d / 65535 && commandAgreement.Maximum < .01
                        && Difference(nativeDisabled!, tiff).Mean > .02,
                        "both native routes preserve the same high-depth applied film and optics with sharpening disabled");
                    Check(Equals(core.GetProperty("UnsharpMaskAmount")!.GetValue(variant), 500f),
                        "the export never edits Capture One's saved variant sharpening");
                    File.WriteAllText(Path.Combine(folder, caseName + "-native-input-sharpening.json"),
                        JsonSerializer.Serialize(new { profileSha256 = profileSha, results, lensDifference, commandAgreement }));
                }
                finally
                {
                    try
                    {
                        foreach (var item in originalVariant.Values) item.p.SetValue(variant, item.Item2);
                        foreach (var item in originalRecipe.Values) item.p.SetValue(selected, item.Item2);
                    }
                    finally { suppress.SetValue(selected, false); }
                }

            }

            async Task<BitmapSource> CaptureOrdinaryRecipeTiff(string directory, string stem, string profileSha,
                (int Width, int Height)? dimensions = null)
            {
                var (width, height) = dimensions ?? (expectedWidth, expectedHeight);
                toggle.IsChecked = false;
                var routes = routeCount;
                var path = Path.Combine(directory, stem + ".tif");
                Check(!File.Exists(path), "the ordinary command has a new private TIFF destination");
                ShowNativeDialog(() =>
                {
                    if (Prop(window, "ExportDialogWindow") is not Window { IsVisible: true } owner
                        || owner.DataContext is not { } model || Prop(model, "ExportCommand") is not ICommand command
                        || !command.CanExecute(null) || !CheckNativeLocationSample(owner, directory)
                        || ReadNativeNamingSample(owner) != stem)
                        return false;
                    command.Execute(null);
                    return true;
                });
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(35))
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                            BitmapCacheOption.OnLoad).Frames.Single();
                        Check(routeCount == routes && frame.Format == PixelFormats.Rgb48
                            && frame.PixelWidth == width && frame.PixelHeight == height,
                            $"Capture One independently rendered {stem} at native recipe size and 16-bit depth");
                        using var profileStream = frame.ColorContexts!.Single().OpenProfileStream();
                        Check(Convert.ToHexString(SHA256.HashData(profileStream)) == profileSha,
                            $"the ordinary {stem} TIFF embeds the same recipe profile");
                        return frame;
                    }
                    catch (Exception error) when (error is IOException or FileFormatException)
                    { await Task.Delay(100); }
                }
                throw new TimeoutException("Capture One did not publish the independent recipe output at " + path);
            }

            async Task CheckRecipeRestart()
            {
                var selected = SelectedRecipeForTest(out var notifications);
                var type = selected.GetType();
                var filename = type.GetProperty("DestinationFilename", Flags)!;
                var format = type.GetProperty("FileType", Flags)!;
                var cropMode = type.GetProperty("CropExportMethod", Flags)!;
                var bits = type.GetProperty("TiffBits", Flags)!;
                var watermark = type.GetProperty("WatermarkType", Flags)!;
                var watermarkImage = type.GetProperty("WatermarkImageReference", Flags)!;
                var sharpening = type.GetProperty("SharpeningOutputControl", Flags)!;
                var annotations = type.GetProperty("IncludeAnnotations", Flags)!;
                var overlay = type.GetProperty("IncludeOverlay", Flags)!;
                var initialName = filename.GetValue(selected);
                var initialFormat = format.GetValue(selected);
                var initialBits = bits.GetValue(selected);
                var initialCropMode = cropMode.GetValue(selected);
                var initialWatermark = watermark.GetValue(selected);
                var initialImage = watermarkImage.GetValue(selected);
                var initialSharpening = sharpening.GetValue(selected);
                var initialAnnotations = annotations.GetValue(selected);
                var initialOverlay = overlay.GetValue(selected);
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
                    watermark.SetValue(selected, Enum.Parse(watermark.PropertyType, "Image"));
                    watermarkImage.SetValue(selected, null);
                    sharpening.SetValue(selected, Enum.Parse(sharpening.PropertyType, "Screen"));
                    Check(Prop(CaptureRecipe(), "Omissions")?.ToString() == "Watermark, Sharpening",
                        "the selected native recipe reports an unavailable image watermark and screen sharpening mode");
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
                    watermarkImage.SetValue(selected, initialImage);
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
                    var controller = window.GetType().Assembly.GetType("P1.C1.Program", true)!
                        .GetProperty("OverlayController", Flags)!.GetValue(null)!;
                    var originalOverlayPath = Prop(controller, "Path") as string;
                    var originalOverlaySettings = new[] { "Enabled", "FollowCrop", "Opacity", "Scaling", "HorizontalOffset", "VerticalOffset" }
                        .ToDictionary(name => controller.GetType().GetProperty(name, Flags)!, name => Prop(controller, name));
                    Check(string.IsNullOrEmpty(originalOverlayPath) || File.Exists(originalOverlayPath),
                        "the disposable preferences have a restorable original overlay selection");
                    filename.SetValue(selected, initialName);
                    try
                    {
                        Call(controller, "Clear");
                        crop.SetValue(variant, fixtureCrop);
                        Check(!Equals(crop.GetValue(variant), originalCrop), "the disposable RAW has a real asymmetric native crop");
                        var cropped = Dimensions(CaptureRecipe());
                        cropMode.SetValue(selected, Enum.Parse(cropMode.PropertyType, "Ignore"));
                        var full = Dimensions(CaptureRecipe());
                        Check(cropped != full, "the actual native recipe computes different cropped and full output dimensions");
                        var uncut = await Render("ignore-crop-reference", ".tif", true, expectedGeometry: full);

                        format.SetValue(selected, Enum.Parse(format.PropertyType, "Psd"));
                        bits.SetValue(selected, 16);
                        annotations.SetValue(selected, false);
                        overlay.SetValue(selected, false);
                        var psd = await Render("flat-psd-with-film", ".psd", true, expectedGeometry: full);
                        var psdParity = Difference(uncut, psd);
                        Check(offeredFilename?.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) == true
                            && psdParity.Mean < 8d / 65535 && psdParity.Maximum < .01,
                            "the native PSD recipe suggests a Photoshop file and preserves high-depth film pixels");

                        cropMode.SetValue(selected, Enum.Parse(cropMode.PropertyType, "AsPath"));
                        overlay.SetValue(selected, true);
                        var pathRecipe = CaptureRecipe();
                        var approvedPath = Prop(pathRecipe, "CropPath");
                        Check(Prop(pathRecipe, "FormatId") is "Psd" && Prop(pathRecipe, "CropMode") is "AsPath"
                            && Dimensions(pathRecipe) == full && approvedPath is not null
                            && Prop(pathRecipe, "Omissions")?.ToString() == "TransparencyOverlay",
                            "Capture One supplies a full-canvas PSD recipe with a crop path and transparency overlay requested");
                        var withPath = await Render("editable-psd-crop-path", ".psd", false, expectedGeometry: full,
                            expectedCropPath: approvedPath);
                        var pathParity = Difference(uncut, withPath);
                        Check(pathParity.Mean < 8d / 65535 && pathParity.Maximum < .01,
                            "the editable path preserves the complete high-depth film composite");
                        var psdReceipt = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        Check(Prop(psdReceipt, "DeliveryNote") as string
                            == "Saved the full image with the editable crop path. The export omitted recipe transparency overlay.",
                            "the native PSD receipt records the included path and omitted transparency overlay");
                        var png = await Render("psd-crop-path-alternative", ".png", false, expectedGeometry: full);
                        Check(offeredTitle?.StartsWith("Save 16-bit TIFF, PNG or PSD · full image, editable crop path in PSD · omits recipe transparency overlay", StringComparison.Ordinal) == true,
                            "the native Save title names the format that retains the crop path and discloses omitted layers");
                        const string note = "Recipe requested 16-bit PSD. Saved 16-bit PNG. Saved the full image. The recipe’s crop path was omitted. The export omitted recipe transparency overlay.";
                        var receipt = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        Check(Equals(Prop(receipt, "DeliveryNote"), note)
                            && ((TextBlock)Field(spatial, "exportStatus")!).Text.Contains(note, StringComparison.Ordinal),
                            "the completed receipt records the actual PNG and the omitted crop path and layers");
                        var parity = Difference(uncut, png);
                        Check(parity.Mean < 8d / 65535 && parity.Maximum < .01,
                            "the PSD substitute preserves the same full-canvas effect pixels as the Ignore Crop reference TIFF");
                        overlay.SetValue(selected, false);
                        await CheckNativeAnnotationLayer();
                        await CheckNativeTransparencyLayer();
                        Check(Read(counter) == 44, "manual substitution filenames do not consume another native counter step");

                        async Task CheckNativeTransparencyLayer()
                        {
                            var asset = Path.Combine(folder, "recipe-transparency-fixture.png");
                            const int imageWidth = 64, imageHeight = 48;
                            var data = new byte[imageWidth * imageHeight * 4];
                            for (var y = 8; y < 40; y++)
                            for (var x = 10; x < 54; x++)
                            {
                                var i = (y * imageWidth + x) * 4;
                                data[i] = 42;
                                data[i + 1] = 21;
                                data[i + 2] = 230;
                                data[i + 3] = (byte)(x < 32 ? 140 : 255);
                            }
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(imageWidth, imageHeight, 96, 96,
                                PixelFormats.Bgra32, null, data, imageWidth * 4)));
                            using (var stream = File.Create(asset)) encoder.Save(stream);
                            Check(Call(controller, "Load", asset, true) is true, "the native Overlay tool loads a transparent PNG");
                            SetOverlay("Enabled", false);
                            SetOverlay("FollowCrop", true);
                            SetOverlay("Scaling", 64);
                            SetOverlay("Opacity", 65);
                            SetOverlay("HorizontalOffset", 9);
                            SetOverlay("VerticalOffset", -7);
                            overlay.SetValue(selected, true);
                            var state = CaptureRecipe();
                            Check(Prop(state, "TransparencyOverlay") is { } captured && Prop(captured, "Opacity") is .65f
                                && Prop(state, "Omissions")?.ToString() == "None",
                                "recipe approval captures the transparency overlay independently of the Viewer Show switch");
                            var layers = await Render("editable-psd-transparency", ".psd", true, expectedGeometry: full,
                                expectedCropPath: approvedPath, expectedPsdBackground: uncut, expectedPsdOverlay: "Overlay");
                            var flattened = await Render("flattened-png-transparency", ".png", false, expectedGeometry: full);
                            var match = Difference(layers, flattened);
                            Check(Difference(uncut, layers).Mean > .0001 && match.Mean < 8d / 65535 && match.Maximum < .01,
                                "both native export commands carry the same overlay over the RGB16 film result");

                            void SetOverlay(string name, object value)
                            {
                                var property = controller.GetType().GetProperty(name, Flags)!;
                                property.SetValue(controller, Convert.ChangeType(value, property.PropertyType));
                            }
                        }

                        async Task CheckNativeAnnotationLayer()
                        {
                            var original = Prop(variant, "Annotations")!;
                            var fixture = Activator.CreateInstance(original.GetType())!;
                            var lines = original.GetType().GetField("Lines", Flags)!;
                            var strokes = (IList)Activator.CreateInstance(lines.FieldType)!;
                            strokes.Add(Activator.CreateInstance(lines.FieldType.GetGenericArguments()[0],
                                Color.FromArgb(255, 230, 65, 15), dimensions.Width * .025f, new List<Point>
                                {
                                    new(dimensions.Width * .26, dimensions.Height * .38),
                                    new(dimensions.Width * .54, dimensions.Height * .42),
                                    new(dimensions.Width * .66, dimensions.Height * .58),
                                }));
                            lines.SetValue(fixture, strokes);
                            Call(variant, "SetAnnotations", fixture);
                            try
                            {
                                annotations.SetValue(selected, true);
                                var state = CaptureRecipe();
                                Check(Prop(state, "Annotations") is not null && Prop(state, "Omissions")?.ToString() == "None",
                                    "the recipe freezes visible native annotation strokes at approval");
                                var annotatedPsd = await Render("editable-psd-annotations", ".psd", true, expectedGeometry: full,
                                    expectedCropPath: approvedPath, expectedPsdBackground: uncut, expectedPsdOverlay: "Annotations");
                                var annotatedPng = await Render("flattened-png-annotations", ".png", false, expectedGeometry: full);
                                var outputParity = Difference(annotatedPsd, annotatedPng);
                                Check(Difference(uncut, annotatedPsd).Mean > .0001
                                    && outputParity.Mean < 8d / 65535 && outputParity.Maximum < .01,
                                    "both commands retain the native annotations and agree on the RGB16 film composite");
                            }
                            finally
                            {
                                annotations.SetValue(selected, false);
                                Call(variant, "SetAnnotations", original);
                            }
                        }
                    }
                    finally
                    {
                        crop.SetValue(variant, originalCrop);
                        Call(controller, "Clear");
                        if (!string.IsNullOrEmpty(originalOverlayPath))
                            Check(Call(controller, "Load", originalOverlayPath, true) is true, "the disposable overlay selection is restored");
                        foreach (var (property, value) in originalOverlaySettings) property.SetValue(controller, value);
                    }
                }
                finally
                {
                    try
                    {
                        format.SetValue(selected, initialFormat);
                        bits.SetValue(selected, initialBits);
                        filename.SetValue(selected, initialName);
                        cropMode.SetValue(selected, initialCropMode);
                        watermark.SetValue(selected, initialWatermark);
                        watermarkImage.SetValue(selected, initialImage);
                        sharpening.SetValue(selected, initialSharpening);
                        annotations.SetValue(selected, initialAnnotations);
                        overlay.SetValue(selected, initialOverlay);
                    }
                    finally { notifications.SetValue(selected, false); }
                }
            }

            object SelectedRecipeForTest(out FieldInfo notifications)
                => ReadSelectedRecipeForTest(bridge!, out notifications);

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
                void KeepOnlyHighResolutionChange(object before)
                {
                    var counters = Prop(settings, "VariantProcessCounters")!;
                    var highOnly = Activator.CreateInstance(counters.GetType(),
                        Convert.ToInt64(Prop(before, "HighRes")) + 1, Prop(before, "LowRes")!, Prop(before, "Geometry")!)!;
                    Call(settings, "InitializeProcessCounters", highOnly);
                    CheckHighResolutionOnly(before, Revision());
                }
                void BumpHighResolutionCounter()
                {
                    var prior = Revision();
                    Call(settings, "IncrementHighResCounter");
                    CheckHighResolutionOnly(prior, Revision());
                }
                if (compareMaskedSource)
                {
                    var unmasked = await Render("unmasked-source-reference", ".png", true);
                    await CheckMaskedSource(unmasked);
                    return;
                }
                if (compareProfileSidecars)
                {
                    var inputProfile = Directory.EnumerateFiles(Path.Combine(folder, "ICC fixture inputs")).Single();
                    var profileProperty = native.GetProperty("ColorProfile")!;
                    var originalProfile = profileProperty.GetValue(variant);
                    var cameraProfile = Activator.CreateInstance(profileProperty.PropertyType,
                        "Spektrafilm fixture ICC", File.ReadAllBytes(inputProfile));
                    profileProperty.SetValue(variant, cameraProfile);
                    try { await CheckProfileSidecars(); }
                    finally { profileProperty.SetValue(variant, originalProfile); }
                    return;
                }
                if (compareLccSidecar)
                {
                    await CheckLensCastSidecar();
                    return;
                }
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
                        exposureProperty.SetValue(variant, initialExposure + .75f);
                        KeepOnlyHighResolutionChange(before);
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

                async Task CheckProfileSidecars()
                {
                    var profileReference = await Render("icc-sidecar-reference", ".png", true);
                    var ids = LocalIccIds(variant).ToArray();
                    var sourceProfile = native.GetProperty("ColorProfile")!.GetValue(variant)!;
                    var active = ((IEnumerable)Call(variant, "GetVariantICCIds")!).Cast<object>().ToArray();
                    File.WriteAllText(Path.Combine(folder, caseName + "-icc-resolution.json"), JsonSerializer.Serialize(new
                    {
                        localIds = ids.Select(value => new { fileName = Prop(value, "FileName"), undo = Prop(value, "UndoID") }),
                        activeIds = active.Select(value => new { fileName = Prop(value, "FileName"), undo = Prop(value, "UndoID") }),
                        activeIsLocal = Prop(sourceProfile, "isLocal"), activeByteCount = (Prop(sourceProfile, "rawData") as byte[])?.Length,
                    }));
                    var id = ids.Single();
                    var sidecarName = (string)Prop(id, "FileName")!;
                    var sidecarPath = Path.Combine(folder, "Adjustments", "ICC", sidecarName);
                    byte[] ReadIcc() => (byte[])Prop(Call(variant, "GetICCSideCarGivenID", id)!, "rawData")!;
                    var originalBytes = File.ReadAllBytes(Directory.EnumerateFiles(Path.Combine(folder, "ICC fixture inputs")).Single());
                    Check(ReadIcc().SequenceEqual(originalBytes), "the native source resolves the actual copied ICC sidecar bytes");
                    if (!File.Exists(sidecarPath))
                        File.WriteAllBytes(sidecarPath, originalBytes);
                    var changedBytes = WithDifferentCopyright(originalBytes);
                    try
                    {
                        beforeDestinationApproval = BumpHighResolutionCounter;
                        var approved = await Render("verified-icc-sidecar-update", ".png", true);
                        Check(Difference(profileReference, approved).Mean < 8d / 65535,
                            "an unchanged native ICC sidecar survives a counter-only revision update");

                        var protectedFile = Path.Combine(folder, caseName + "-protected-icc-sidecar.png");
                        var originalPng = File.ReadAllBytes(Path.Combine(folder, caseName + "-icc-sidecar-reference.png"));
                        File.WriteAllBytes(protectedFile, originalPng);
                        beforeDestinationApproval = () =>
                        {
                            var revision = Revision();
                            File.WriteAllBytes(sidecarPath, changedBytes);
                            Call(variant, "UnloadICCFromMemory", id);
                            var currentId = LocalIccIds(variant).Single();
                            Check(ReadIcc().SequenceEqual(changedBytes) && Equals(Prop(currentId, "FileName"), Prop(id, "FileName"))
                                && Equals(Prop(currentId, "UndoID"), Prop(id, "UndoID")),
                                "Capture One resolves the changed physical ICC bytes under the same sidecar ID");
                            KeepOnlyHighResolutionChange(revision);
                        };
                        destination = protectedFile;
                        job = null;
                        ExecuteNativeExportCommand(previous);
                        Check(job is not null && !await job.WaitAsync(TimeSpan.FromSeconds(45)),
                            "a real ICC sidecar change rejects the approved source despite an unchanged sidecar ID");
                        var failed = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        Check(Prop(failed, "Stage")?.ToString() == "Failed" && File.ReadAllBytes(protectedFile).SequenceEqual(originalPng),
                            "a changed native ICC sidecar cannot replace the existing approved PNG");
                    }
                    finally
                    {
                        beforeDestinationApproval = null;
                        File.WriteAllBytes(sidecarPath, originalBytes);
                        Call(variant, "UnloadICCFromMemory", id);
                    }
                    Check(ReadIcc().SequenceEqual(originalBytes), "the disposable native ICC sidecar is restored exactly");
                    File.WriteAllText(Path.Combine(folder, caseName + "-native-sidecars.json"), JsonSerializer.Serialize(new
                    {
                        originalIccSha256 = Convert.ToHexString(SHA256.HashData(originalBytes)),
                        changedIccSha256 = Convert.ToHexString(SHA256.HashData(changedBytes)), iccBytes = originalBytes.Length,
                    }));
                }

                async Task CheckLensCastSidecar()
                {
                    var flatReference = await Render("uncorrected-flat-field", ".png", true);
                    var createLcc = window.GetType().Assembly.GetType("P1.C1.Components.VariantProcessHelper", true)!
                        .GetMethod("CreateLCC", BindingFlags.Static | Flags)!;
                    object?[] arguments = [variant, Enum.ToObject(createLcc.GetParameters()[1].ParameterType, 0), null];
                    var result = createLcc.Invoke(null, arguments)?.ToString();
                    Check(result == "resOK" && arguments[2] is byte[] { Length: > 0 },
                        $"Capture One generates native lens-cast correction data from the disposable RAW ({result})");
                    var lccBytes = (byte[])arguments[2]!;
                    var modelCore = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.AppCore");
                    var profileType = modelCore.GetType("P1.ModelCoreWrapper.McLCCProfile", true)!;
                    var lcc = Activator.CreateInstance(profileType, "Spektrafilm fixture LCC", lccBytes)!;
                    var actionType = native.Assembly.GetType("P1.C1.Core.UndoSupport.SetLccAction", true)!;
                    var action = Activator.CreateInstance(actionType, lcc, variant, settings, true, true)!;
                    Call(action, "Do");
                    try
                    {
                        var lccId = ((IEnumerable)Call(settings, "GetLCCIdsThroughLayers", false)!).Cast<object>().Single();
                        var loadedLcc = (byte[])Prop(Call(variant, "GetLCCSideCarGivenID", lccId)!, "rawData")!;
                        Check(lccBytes.SequenceEqual(loadedLcc), "the active native lens-cast sidecar matches Capture One’s generated bytes");
                        var corrected = await Render("lcc-sidecar-reference", ".png", true);
                        beforeDestinationApproval = BumpHighResolutionCounter;
                        var approved = await Render("verified-lcc-sidecar-update", ".png", true);
                        Check(Difference(corrected, approved).Mean < 8d / 65535 && Difference(flatReference, corrected).Mean > .001,
                            "the verified LCC export preserves Capture One’s actual correction after a counter-only update");
                    }
                    finally
                    {
                        beforeDestinationApproval = null;
                        Call(action, "Undo");
                    }
                    File.WriteAllText(Path.Combine(folder, caseName + "-native-sidecars.json"), JsonSerializer.Serialize(new
                    {
                        nativeLccSha256 = Convert.ToHexString(SHA256.HashData(lccBytes)),
                        lccBytes = lccBytes.Length,
                    }));
                }

                async Task CheckMaskedSource(BitmapSource unmasked)
                {
                    var layers = Prop(variant, "LocalAdjustmentsLayers")!;
                    var initialCount = (int)Prop(layers, "Count")!;
                    var layer = AddFixtureAdjustmentLayer(native, variant, layers);
                    try
                    {
                        Check((int)Prop(layers, "Count")! == initialCount + 1, "the disposable RAW uses a real Capture One adjustment layer");
                        var setValue = layer.GetType().GetMethods(Flags).Single(method => method.Name == "SetValue"
                            && method.IsGenericMethodDefinition && method.GetParameters().Length == 2).MakeGenericMethod(typeof(float));
                        setValue.Invoke(layer, new object[] { "Exposure", .85f });
                        Call(layer, "FillMaskNoUndo", variant);
                        var populated = await Render("native-filled-mask", ".png", true);
                        Check(Difference(unmasked, populated).Mean > .02,
                            "the native filled mask alters the effect-processed high-depth output");

                        var before = CopyModel();
                        var prior = Revision();
                        Call(settings, "IncrementHighResCounter");
                        CheckHighResolutionOnly(prior, Revision());
                        var counterOnly = CopyModel();
                        setValue.Invoke(layer, new object[] { "Exposure", 1.4f });
                        var exposureChange = CopyModel();
                        setValue.Invoke(layer, new object[] { "Exposure", .85f });
                        var maskBefore = MaskBytes();
                        beforeDestinationApproval = () =>
                        {
                            var revision = Revision();
                            Call(settings, "IncrementHighResCounter");
                            CheckHighResolutionOnly(revision, Revision());
                        };
                        var verified = await Render("verified-masked-source-update", ".png", true);
                        Check(Difference(populated, verified).Mean < 8d / 65535,
                            "an unchanged native mask completes export after a high-resolution-only update");

                        var protectedFile = Path.Combine(folder, caseName + "-protected-masked-source.png");
                        var originalBytes = File.ReadAllBytes(Path.Combine(folder, caseName + "-native-filled-mask.png"));
                        File.WriteAllBytes(protectedFile, originalBytes);
                        string[] maskChangedLabels = [];
                        beforeDestinationApproval = () =>
                        {
                            var revision = Revision();
                            var model = CopyModel();
                            Call(layer, "InvertMask", variant);
                            KeepOnlyHighResolutionChange(revision);
                            maskChangedLabels = ChangedLabels(model, CopyModel());
                            Check(maskChangedLabels.SequenceEqual(new[] { "highresProcessCounter" }),
                                "the changed mask cannot be distinguished by the copied model and counters alone");
                        };
                        destination = protectedFile;
                        job = null;
                        ExecuteNativeExportCommand(previous);
                        Check(job is not null && !await job.WaitAsync(TimeSpan.FromSeconds(45)),
                            "the native mask byte change rejects the earlier approved export");
                        var failed = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
                        Check(Prop(failed, "Stage")?.ToString() == "Failed" && File.ReadAllBytes(protectedFile).SequenceEqual(originalBytes),
                            "the rejected mask edit preserves the existing approved file byte for byte");
                        beforeDestinationApproval = null;

                        var maskAfter = MaskBytes();
                        var cleared = await Render("native-inverted-mask", ".png", true);
                        Check(Difference(unmasked, cleared).Mean < 8d / 65535 && Difference(populated, cleared).Mean > .02,
                            "changing only the native mask changes the developed output and restores the unmasked pixels");
                        Check(before != exposureChange && !maskBefore.SequenceEqual(maskAfter),
                            "the locked model copy includes local adjustment parameters and Capture One can serialize changed mask content");
                        File.WriteAllText(Path.Combine(folder, caseName + "-mask-source-evidence.json"), JsonSerializer.Serialize(new
                        {
                            baselineModelBytes = before.Length, counterOnlyChangedLabels = ChangedLabels(before, counterOnly),
                            parameterChangedLabels = ChangedLabels(counterOnly, exposureChange), maskChangedLabels,
                            fullMaskBytes = maskBefore.Length, invertedMaskBytes = maskAfter.Length,
                        }));
                    }
                    finally
                    {
                        beforeDestinationApproval = null;
                        Call(layers, "RemoveNoUndo", layer);
                        Check((int)Prop(layers, "Count")! == initialCount, "the temporary native layer was removed from the disposable RAW");
                    }

                    string CopyModel()
                    {
                        var modelCore = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.AppCore");
                        var wrapper = modelCore.GetType("P1.ModelCoreWrapper.McVariant", true)!;
                        using var copy = (IDisposable)Call(settings, "GetMcVariantDeepCopy", true)!;
                        using var borrowed = (IDisposable)Activator.CreateInstance(wrapper, Call(copy, "GetNativePointer")!)!;
                        try { return (string)Call(borrowed, "ToJSON")!; }
                        finally { Call(borrowed, "DisownNativePointer"); }
                    }

                    byte[] MaskBytes()
                    {
                        var sidecars = native.Assembly.GetType("P1.C1.Core.SideCarsPersistency", true)!;
                        var save = sidecars.GetMethod("SaveLinkedMaskListAsBuffer", BindingFlags.Static | Flags)!;
                        var mask = Prop(layer, "Mask")!;
                        using var data = (IDisposable)save.Invoke(null, new[] { Call(mask, "GetHandle")!, (object)false })!;
                        return (byte[])Call(data, "ToArray")!;
                    }

                    static string[] ChangedLabels(string before, string after)
                    {
                        var left = before.Split('\n');
                        var right = after.Split('\n');
                        if (left.Length != right.Length) return ["Line count"];
                        return left.Zip(right).Where(pair => pair.First != pair.Second)
                            .Select(pair => System.Text.RegularExpressions.Regex.Match(pair.Second, "^\\s*\"([A-Za-z0-9_]+)\"\\s*:")
                                .Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
                    }
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
                (int Width, int Height)? expectedGeometry = null, object? expectedCropPath = null,
                BitmapSource? expectedPsdBackground = null, string expectedPsdOverlay = "Watermark")
            {
                var (width, height) = expectedGeometry ?? (expectedWidth, expectedHeight);
                var before = routeCount;
                var expectedFilm = (double)Prop(strength, "Value")! > 0;
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
                BitmapSource frame;
                string actualIcc;
                if (extension == ".psd")
                {
                    var psd = PsdFixture.Read(file);
                    if (expectedPsdBackground is not null)
                    {
                        Check(psd.Layers is [var background, var overlay] && background.Name == "Background"
                            && background.Width == width && background.Height == height && overlay.Name == expectedPsdOverlay
                            && overlay.Samples.Length == (long)overlay.Width * overlay.Height * 4
                            && overlay.Samples.Where((_, index) => (index & 3) == 3).Any(alpha => alpha is > 0 and < 65535),
                            $"the delivered PSD has a separate {expectedPsdOverlay} layer with 16-bit alpha over the full film frame");
                        var baseLayer = psd.Layers[0];
                        var baseFrame = BitmapSource.Create(baseLayer.Width, baseLayer.Height, psd.DpiX, psd.DpiY, PixelFormats.Rgb48,
                            null, baseLayer.Samples, baseLayer.Width * 6);
                        var baseParity = Difference(baseFrame, expectedPsdBackground);
                        Check(baseParity.Mean < 8d / 65535 && baseParity.Maximum < .01,
                            $"hiding the {expectedPsdOverlay} layer reveals the separate high-depth film result");
                    }
                    else
                        Check(psd.Layers.Length == 0, "no visible native watermark produces an ordinary flat PSD");
                    frame = BitmapSource.Create(psd.Width, psd.Height, psd.DpiX, psd.DpiY, PixelFormats.Rgb48,
                        null, psd.Samples, psd.Width * 6);
                    actualIcc = Convert.ToHexString(SHA256.HashData(psd.Resources[1039]));
                    if (expectedCropPath is not null)
                    {
                        var left = (double)Prop(expectedCropPath, "Left")!;
                        var top = (double)Prop(expectedCropPath, "Top")!;
                        var right = (double)Prop(expectedCropPath, "Right")!;
                        var bottom = (double)Prop(expectedCropPath, "Bottom")!;
                        (double X, double Y)[] expected = [(left, top), (right, top), (right, bottom), (left, bottom)];
                        Check(left > 0 && top > 0 && right < 1 && bottom < 1
                            && psd.ReadCropPath().Zip(expected).All(pair => Math.Abs(pair.First.X - pair.Second.X) * width < .001
                                && Math.Abs(pair.First.Y - pair.Second.Y) * height < .001),
                            "the exported PSD contains the approved asymmetric crop in full-canvas coordinates");
                    }
                    else
                        Check(!psd.Resources.ContainsKey(2000), "ordinary flattened PSD export does not add a crop path");
                }
                else
                {
                    var wic = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.OnLoad).Frames[0];
                    Check(wic.ColorContexts is { Count: 1 }, $"{kind} embeds one selected recipe ICC");
                    using var icc = wic.ColorContexts![0].OpenProfileStream();
                    actualIcc = Convert.ToHexString(SHA256.HashData(icc));
                    frame = wic;
                }
                Check(frame.Format == (extension == ".png" ? PixelFormats.Rgba64 : PixelFormats.Rgb48)
                    && frame.PixelWidth == width && frame.PixelHeight == height,
                    $"{kind} reopens as the expected 16-bit format at the selected recipe dimensions");
                Check(actualIcc == (expectedProfileSha ?? iccSha), $"{kind} embeds the exact selected recipe ICC");
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
                var stages = new[] { "ViewerDrainMs", "NativeDevelopmentMs", "BlendAndCropMs", "IccProfileSetupMs", "EncodeVerifyAndCommitMs" }
                    .Select(key => detail.GetProperty(key).GetDouble()).ToArray();
                var filmMs = film.ValueKind == JsonValueKind.Number ? film.GetDouble() : 0;
                Check(detail.GetProperty("SourceBitDepth").GetInt32() >= 16
                    && detail.GetProperty("InputEncoding").GetString() == inputEncoding
                    && detail.GetProperty("OutputWidth").GetInt32() == width
                    && detail.GetProperty("OutputHeight").GetInt32() == height
                    && detail.GetProperty("OutputFormat").GetString() == (extension switch { ".psd" => "PSD", ".tif" => "TIFF", _ => "PNG" })
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

    private static IEnumerable<object> LocalIccIds(object variant)
    {
        foreach (var id in ((IEnumerable)Call(variant, "GetVariantICCIds")!).Cast<object>())
        {
            var name = Prop(id, "FileName") as string;
            if (!string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name)
                && name.EndsWith(".icm", StringComparison.OrdinalIgnoreCase)
                && Prop(Call(variant, "GetICCSideCarGivenID", id)!, "isLocal") is true)
                yield return id;
        }
    }

    private static byte[] WithDifferentCopyright(byte[] profile)
    {
        var replacement = (byte[])profile.Clone();
        var tagCount = checked((int)BinaryPrimitives.ReadUInt32BigEndian(replacement.AsSpan(128, 4)));
        for (var index = 0; index < tagCount; index++)
        {
            var tag = replacement.AsSpan(132 + 12 * index, 12);
            if (!tag[..4].SequenceEqual("cprt"u8)) continue;
            var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(tag[4..]));
            if (!replacement.AsSpan(offset, 4).SequenceEqual("text"u8) || replacement[offset + 8] is < 32 or > 126)
                break;
            replacement[offset + 8] = replacement[offset + 8] == (byte)'S' ? (byte)'T' : (byte)'S';
            replacement.AsSpan(84, 16).Clear(); // A profile ID of zero means that no ID has been computed.
            return replacement;
        }
        throw new InvalidOperationException("The disposable ICC fixture requires an ASCII copyright tag.");
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

    private static string NativeNameToken(string id)
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

    private static object ReadSelectedRecipeForTest(object bridge, out FieldInfo notifications)
    {
        Check(File.Exists(Path.Combine(folder, "native-recipe-backup.json")),
            "the native recipe settings test has a verified recipe backup");
        var destinations = ((Delegate)Field(bridge, "destinations")!).DynamicInvoke()!;
        var selected = Prop(destinations, "SelectedDestination")!;
        var guard = selected.GetType().GetField("m_suppressNotifications", Flags);
        if (guard?.FieldType != typeof(bool) || guard.GetValue(selected) is not false)
            throw new InvalidOperationException("The verified host recipe notification guard is unavailable.");
        notifications = guard;
        return selected;
    }

    private sealed record ExportDifference(int Samples, double Mean, double Maximum, double ChangedFraction);
    private sealed record WatermarkComparison(double Mean, int Maximum, double UnmarkedMean, int UnmarkedMaximum,
        double NativeUnmarkedMean, int NativeVisible);

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
