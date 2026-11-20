using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

// A separate, opt-in test startup hook. Never distributed in the product package.
public static partial class StartupHook
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Forms.Timer? timer;
    static string folder = "";
    static DateTime deadline;
    static int checks;
    public static void Initialize()
    {
        folder = Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_SMOKE") ?? "";
        if (folder.Length == 0 || !File.Exists(Path.Combine(folder, "disposable-catalog.marker"))) return;
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder,"host-smoke.log"), "Test startup hook initialized." + Environment.NewLine);
        deadline = DateTime.UtcNow.AddMinutes(3);
        Forms.Application.Idle += Start;
    }
    static void Start(object? sender, EventArgs args)
    {
        File.AppendAllText(Path.Combine(folder,"host-smoke.log"), "Test UI loop started." + Environment.NewLine);
        Forms.Application.Idle -= Start;
        timer = new Forms.Timer { Interval = 1000 };
        timer.Tick += Tick;
        timer.Start();
    }
    static object? Field(object value, string name) => value.GetType().GetField(name, Flags)?.GetValue(value);
    static object? Prop(object value, string name) => value.GetType().GetProperty(name, Flags)!.GetValue(value);
    static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Flags)!.Invoke(value, args);
    static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        checks++;
        File.AppendAllText(Path.Combine(folder, "host-smoke.log"), "PASS " + description + Environment.NewLine);
    }
    static async void Tick(object? sender, EventArgs args)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_NATIVE_VIEWER_BASELINE") == "1")
            {
                var count = int.Parse(Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_VIEWER_CHURN")!);
                if (!await TryRunNativeViewerBaseline(count) && DateTime.UtcNow > deadline)
                    throw new TimeoutException("Capture One did not open the copied catalog with its native Viewer.");
                return;
            }
            var panelAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "NativePanel");
            var hook = panelAssembly?.GetType("StartupHook");
            var hosted = hook?.GetField("hostedTool", Flags)?.GetValue(null);
            if (hosted is null || Field(hosted, "panel") is not { } panel || Prop(panel, "StocksReady") is not true)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Panel did not attach with loaded stocks.");
                return;
            }
            timer!.Stop();
            TestWorkspaceAttachment(hook!);
            var window = Field(hosted, "mainWindow")!;
            TestHostLookups(panelAssembly!, panel, (Forms.Form)window);
            var hookField = hook!.GetField("exportHook", Flags)!;
            var exportHook = hookField.GetValue(null);
            Check(exportHook is not null, "native Export hook attached");
            var catalog = Environment.GetEnvironmentVariable("SPEKTRAFILM_CATALOG")!;
            Check(Path.GetFullPath(catalog).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "catalog is disposable and contained in test folder");
            var preferences = panelAssembly!.GetType("PanelPreferences")!;
            Check(Equals(preferences.GetMethod("UserDataDirectory", Flags)!.Invoke(null, null), Path.GetFullPath(folder)),
                "test looks and panel preferences use the disposable workspace directory from startup");
            var store = Field(panel, "looks")!;
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_VARIANT_LINEAGE") == "1")
            {
                TestNativeVariantLineage(hosted, panel, window, store);
                return;
            }
            Call(panel, "SyncSelection");
            Check(Field(panel, "displayedVariant") is Guid, "selected image bound to panel");
            var variant = (Guid)Field(panel, "displayedVariant")!;
            var spatial = Field(panel, "opticalPreview")!;
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_BATCH_EXPORT") == "1")
            {
                await TestNativeBatchExport(exportHook!, panel, spatial, window);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_BATCH_EXPORT") == "crash")
            {
                await TestNativeBatchRecovery(panel, spatial, window);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_FLOATING_WORKSPACE") == "1")
            {
                await TestFloatingWorkspaceLayout(hosted, panel);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_NATIVE_WORKSPACE_SWITCH") == "1")
            {
                await TestNativeWorkspaceSwitch(hook!, hosted);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_MOVED_LOOK_BACKUP") is { Length: > 0 } movedLookBackup)
            {
                await TestMovedCatalogLook(panel, spatial, store, variant, movedLookBackup);
                return;
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_NATIVE_LIFETIME"), out var cycles)
                && cycles is > 0 and <= 200)
            {
                await TestNativeLifetimeSoak(hook, (FrameworkElement)panel, cycles);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_CALIBRATION") is { Length: > 0 } chart)
            {
                if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_NATIVE_EFFECT_EXPORT") == "profile-sidecars")
                    await TestCalibratedChart(panelAssembly, panel, spatial, window, chart, async () =>
                    {
                        Call(spatial, "SetEnabled", true);
                        await TestNativeEffectExport(hookField, hosted, panel, spatial, window, "first", compareLccSidecar: true);
                        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native effects export assertions");
                    });
                else await TestCalibratedChart(panelAssembly, panel, spatial, window, chart);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_METADATA_FIXTURE") is { Length: > 0 } metadataFixture)
            {
                TestExportRecipeMetadata(panelAssembly!, window, exportHook!, metadataFixture);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_DURABLE_EDITS") == "1")
            {
                await TestDurableSliderEdits(panel, spatial, store);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_LOOK_BATCH") is "1" or "crash")
            {
                await TestNativeLookBatch(panel, store);
                return;
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_VIEWER_CHURN"), out var visits) && visits is > 0 and <= 200)
            {
                await MeasureViewerChurn(panel, spatial, store, visits);
                return;
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_VIEWER_TELEMETRY_SECONDS"), out var duration)
                && duration is >= 20 and <= 600)
            {
                await MeasureViewerTelemetry(hook, panel, spatial, duration);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_VIEWER_LAYOUTS") == "1")
            {
                await TestViewerLayouts(panel, spatial, window);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_OTHER_CATALOG") is { Length: > 0 } otherCatalog)
            {
                await TestConcurrentNativeCatalogs(panelAssembly!, hook!, hosted, panel, spatial, window, store, variant, catalog, otherCatalog);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_CATALOG_ISOLATION") == "1")
            {
                await TestCatalogIsolation(panelAssembly!, panel, spatial, window, store, variant, catalog);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_SMOKE_COLOR_MATRIX") == "1")
            {
                await RunColorMatrix(panelAssembly!, panel, window, spatial);
                var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
                File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native RAW colour assertions");
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_SMOKE_COLOR_MATRIX") == "source-mutations")
            {
                await TestNativeSourceMutations(panelAssembly!, panel, spatial, window);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_VIEWER_STRESS") == "1")
            {
                ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
                await TestViewportQuality(spatial, variant);
                await TestViewerWorkerRestart(hosted, panel, spatial, store, variant);
                var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
                File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native Viewer stress assertions");
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_PANEL_INVENTORY") == "1")
            {
                SavePanelControlInventory(panel, spatial);
                VerifyNativeSliderTypes(panel, spatial, store, variant);
                VerifyOpticalModeDependencies(panel, spatial);
                File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native control inventory assertions");
                return;
            }
            var exportFixture = Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_NATIVE_EFFECT_EXPORT");
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_EXPORT_INTERRUPTION") is { } interruption
                && interruption is "DevelopingSource" or "RenderingFilm" or "Encoding")
            {
                await TestNativeExportInterruption(panel, spatial, interruption);
                return;
            }
            if (exportFixture is "1" or "matrix" or "grain" or "clone" or "compression" or "profiles" or "folders" or "panel-modes" or "source-revision" or "masked-source" or "profile-sidecars" or "recipe-restart")
            {
                var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
                if (exportFixture == "matrix")
                    await TestNativeEffectExportMatrix(hookField, hosted, panel, spatial, window, phase);
                else
                    await TestNativeEffectExport(hookField, hosted, panel, spatial, window, phase,
                        compareGrain: exportFixture is "grain" or "clone", compareClone: exportFixture == "clone",
                        compareCompression: exportFixture == "compression", compareProfiles: exportFixture == "profiles",
                        compareFolders: exportFixture == "folders",
                        comparePanelModes: exportFixture == "panel-modes", changeSourceRevision: exportFixture == "source-revision",
                        compareRecipeRestart: exportFixture == "recipe-restart", compareMaskedSource: exportFixture == "masked-source",
                        compareProfileSidecars: exportFixture == "profile-sidecars");
                File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native effects export assertions");
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_NATIVE_DEVELOPMENT_DEADLINE") == "1")
            {
                await TestNativeDevelopmentDeadline(panel, spatial);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_DEVELOPMENT_LEASE") == "1")
            {
                await TestNativeDevelopmentLease(panelAssembly!, spatial, window);
                return;
            }
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_DEVICE_LOSS") == "1")
            {
                await TestNativeDeviceLossRecovery(panel, spatial, store, variant);
                return;
            }
            var exposure = Field(panel, "exposure")!;
            var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_SUPPORT_DIAGNOSTICS") == "1")
            {
                await TestNativeSupportDiagnostics(hookField, exportHook!, hosted, panel, window, spatial, store, variant, exposure, toggle);
                return;
            }
            await TestLaunchHealth(hosted, spatial, toggle);
            await TestAdaptiveWatchdog(hook!, panel, variant);
            await TestFloatingWorkArea(panelAssembly!, hosted, panel);
            var restart = File.Exists(Path.Combine(folder, "restart.expected"));
            if (restart)
            {
                Check(Math.Abs((double)Prop(exposure, "Value")! - 1.2) < 1e-9, "exposure restored after process restart");
                Check(toggle.IsChecked == false, "disabled master switch restored without hiding tool");
                Check(((TextBlock)Field(spatial, "exportStatus")!).Text == "Previous Spektrafilm export canceled.",
                    "the last terminal export status was loaded after restarting Capture One");
            }
            TestExportBinding(hookField, exportHook!, hosted, panel, window, exposure, toggle);
            toggle.IsChecked = true;
            foreach (var value in new[] { 0.3, 0.7, 1.2 })
            {
                exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, value);
                var saved = Call(store, "Read", variant)!;
                Check(Math.Abs((double)Prop(Prop(saved, "Look")!, "Exposure")! - value) < 1e-9, "slider movement durably saved: " + value);
            }
            TestProductionUndo(panel, spatial, window, store, variant, exposure);
            TestPresetHistory(panel, window, store, variant, exposure, toggle);
            await TestPresetExport(panelAssembly, panel, store, variant);
            await TestViewportQuality(spatial, variant);
            await TestSpectralReconstruction(panel, spatial, store, variant);
            TestPanelAccessibility(panel, spatial, exposure);
            await TestNativeSliderInput(hook, panel, store, variant, exposure);
            TestStockFavorites(panel, store, variant, restart);
            await TestViewerHistogram(hook, spatial, restart);
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_PROOF_TEST") == "1")
                await TestProofing(panel, spatial);
            await TestLockedLook(panel, store, variant, exposure, toggle);
            TestBlockedLookInputs(panel, store, variant, exposure, toggle);
            var optics = Call(spatial, "CaptureSettings")!;
            Call(spatial, "ValidateSettings", optics);
            var selections = (System.Collections.Generic.Dictionary<string,string>)Prop(optics, "Selections")!;
            var switches = (System.Collections.Generic.Dictionary<string,bool>)Prop(optics, "Switches")!;
            selections["preview-size"] = "1200";
            selections["drag-preview-size"] = "720";
            switches["show-source"] = false;
            switches["draft-synthesis"] = true;
            Call(panel, "ValidateLoadedSettings", Call(panel, "CurrentSettings"), optics);
            Check(true, "legacy quality preferences accepted without blocking existing looks");
            selections.Remove("preview-size"); selections.Remove("drag-preview-size"); switches.Remove("show-source"); switches.Remove("draft-synthesis");
            Call(spatial, "SetEnabled", false);
            var host = panelAssembly.GetType("HostImageSource")!;
            var profile = panelAssembly.GetType("HostProfile")!;
            var target = profile.GetMethod("CaptureTarget", Flags)!.Invoke(null, new[] { window });
            var space = Enum.ToObject(panelAssembly.GetType("PreviewInputSpace")!, 0);
            await TestNativeGeometry(host, profile, window, target!, space);
            await TestInputProfileReplacement(host, window, target!, spatial);
            await TestViewerWorkerRestart(hosted, panel, spatial, store, variant);
            var snapshot = host.GetMethod("Capture", Flags)!.Invoke(null, new[] { window, target, space, false })!;
            object? lastFrame = null;
            foreach (var edge in new[] { 720, 1200 })
            {
                var frame = await Task.Run(() => Call(snapshot, "RenderScaledPreview", edge, CancellationToken.None));
                lastFrame = frame;
                Check((int)Prop(frame!, "BitDepth")! == 16, "native development at " + edge + " uses 16-bit input");
                Check(Math.Max((int)Prop(frame!, "Width")!, (int)Prop(frame!, "Height")!) <= edge + 1, "scaled development respects " + edge + " bound");
            }
            var rendererType = panelAssembly.GetType("OpticalRenderer")!;
            using (var renderer = (IDisposable)Activator.CreateInstance(rendererType, Flags, null, new object[] { true, false }, null)!)
            {
                var renderArgs = Call(spatial,"RendererOptions",Call(panel,"CurrentSettings"),"srgb","srgb",true,false)!;
                var width = (int)Prop(lastFrame!,"Width")!;
                var height = (int)Prop(lastFrame!,"Height")!;
                var renderTask = (Task<byte[]>)Call(renderer,"RenderAsync",width,height,Prop(lastFrame!,"FloatBytes"),renderArgs,CancellationToken.None,false)!;
                var pixels = await renderTask;
                Check(pixels.Length == width * height * 16,"native source rendered through float32 GPU export transport");
                var diagnostic = await (Task<JsonElement?>)Call(renderer, "ReadDiagnosticsAsync", CancellationToken.None)!;
                Check(diagnostic?.GetProperty("gpuMemoryBudgetBytes").GetInt64() > 0
                    && diagnostic?.GetProperty("estimatedFullFrameGpuBytes").GetInt64() > 0,
                    "Vulkan budget and frame estimate available on the real host export worker");

                var bitmap = (BitmapSource)spatial.GetType().GetMethod("DecodeExport",Flags)!.Invoke(null,new object[] {
                    lastFrame!,pixels,1d,space,"srgb",PixelFormats.Rgb48,new System.Drawing.Rectangle(0,0,width,height),CancellationToken.None,96d })!;
                var output = Path.Combine(folder,"host-export.tif");
                panelAssembly.GetType("SpektraExport")!.GetMethod("Save",Flags)!.Invoke(null,new object[] {
                    output,bitmap,CancellationToken.None,Prop(target!,"SourcePath")! });
                using var data=File.OpenRead(output);
                var decoded=BitmapDecoder.Create(data,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                Check(decoded.Format == PixelFormats.Rgb48,"actual developed and processed TIFF retains 16 bits per RGB channel");
                Check(decoded.PixelWidth == width && decoded.PixelHeight == height,"actual TIFF keeps intended image dimensions");
            }
            await TestWorkerQuiescence(panel, spatial, rendererType);
            await TestNativeLutCancel(panel, spatial);
            var grain = panelAssembly.GetType("NativeGrain")!;
            TestNativeUndoCapability(Field(panel, "document")!);
            TestNativeGrain(grain, window, panel, store, variant, toggle, restart);
            TestNativeGrainFaultBoundaries(grain, window, panel, store, variant);
            var recipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window });
            File.AppendAllText(Path.Combine(folder,"host-smoke.log"), "RECIPE " + recipe + Environment.NewLine);
            await TestRecipeExport(panel, spatial, recipe!, variant, toggle);
            await TestNativeExportCommands(hookField, hosted, panel, spatial, window);
            await TestFullDirectExport(spatial);
            TestUnprotectedClose(hook, store, variant, exposure);
            await TestDetach(hook, hosted, panel, spatial, window, toggle);
            Check(Prop(Call(store, "Read", variant)!, "Enabled") is false, "master switch persisted after all tests");
            if (!restart) File.WriteAllText(Path.Combine(folder, "restart.expected"), "1.2 disabled");
            File.WriteAllText(Path.Combine(folder, restart ? "restart.passed" : "first.passed"), $"PASS {checks} host assertions");
        }
        catch (Exception error)
        {
            timer?.Stop();
            File.WriteAllText(Path.Combine(folder, "failed.txt"), error.ToString());
        }
    }

    static void TestWorkspaceAttachment(Type hook)
    {
        var findSlot = hook.GetMethod("FindFreeTabSlot", Flags);
        if (findSlot is null) return;
        var allSlots = Enumerable.Range(1, 18).Select(index => $"Custom{index}PaletteTab").ToArray();
        Check(findSlot.Invoke(null, new object[] { allSlots }) is null,
            "all occupied native custom slots are refused without replacing any workspace tab");
        Check(Equals(findSlot.Invoke(null, new object[] { allSlots.Where((_, index) => index != 7) }), allSlots[7]),
            "native slot selection uses a free registered position only");
        var owner = hook.GetField("owner", Flags)!.GetValue(null)!;
        var previous = hook.GetField("previousTab", Flags)!.GetValue(null);
        var tool = hook.GetField("tab", Flags)!.GetValue(null)!;
        var actual = Prop(owner, "Current");
        var requested = Environment.GetEnvironmentVariable("SPEKTRAFILM_SHOW_TOOL") == "1";
        Check(ReferenceEquals(actual, requested || previous is null ? tool : previous),
            requested ? "explicit tool launch selects Spektrafilm once" : "startup preserves the workspace's selected native tab");
        Check(((IEnumerable)Prop(owner, "Tabs")!).Cast<object>().Count(tab => ReferenceEquals(tab, tool)) == 1,
            "the live Capture One workspace has one Spektrafilm tab");
        hook.GetMethod("SelectTab", Flags)!.Invoke(null, new[] { owner, tool });
    }

    static JsonElement ReadLaunchHealth()
    {
        var session = Environment.GetEnvironmentVariable("SPEKTRAFILM_LAUNCH_ID")!;
        var path = Path.Combine(folder, "Launch status", session + ".json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.Clone();
    }

    static async Task TestLaunchHealth(object hosted, object spatial, CheckBox toggle)
    {
        Call(hosted, "ReportLaunchHealth");
        var report = ReadLaunchHealth();
        Check(report.GetProperty("SessionId").GetString() == Environment.GetEnvironmentVariable("SPEKTRAFILM_LAUNCH_ID")
            && report.GetProperty("ProcessId").GetInt32() == Environment.ProcessId
            && report.GetProperty("HostVersion").GetString() == "16.4.6.2414", "startup report is bound to this exact process and session");
        Check(report.GetProperty("Phase").GetString() == "Ready" && report.GetProperty("Export").GetString() == "Connected",
            "actual host panel readiness and native Export binding are visible");

        // Disable the live Viewer before swapping the fixture resource root. The
        // regular installation is outside this path and no file is replaced.
        Call(spatial, "SetEnabled", false);
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", drain.Token)!;
        Call(hosted, "ReportLaunchHealth");
        Check(ReadLaunchHealth().GetProperty("Viewer").GetString() == "Disabled",
            "disabled Viewer still reports a ready native tool");
        var originalEnabled = toggle.IsChecked;
        var existing = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!;
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.Combine(folder, "unavailable-runtime"));
        try
        {
            toggle.IsChecked = true;
            Call(spatial, "SetEnabled", true);
            var until = DateTime.UtcNow.AddSeconds(15);
            while (Prop(spatial, "Health")!.ToString() != "ProcessingError" && DateTime.UtcNow < until)
                await Task.Delay(100);
            Call(hosted, "ReportLaunchHealth");
            report = ReadLaunchHealth();
            Check(report.GetProperty("Phase").GetString() == "Ready" && report.GetProperty("Viewer").GetString() == "ProcessingError",
                "real renderer failure appears separately from the available tool");
        }
        finally
        {
            Call(spatial, "SetEnabled", false);
            Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", existing);
            toggle.IsChecked = originalEnabled;
            Call(spatial, "SetEnabled", originalEnabled == true);
        }
    }

    static void TestUnprotectedClose(Type hook, object store, Guid variant, object exposure)
    {
        var saved = (string)Call(store, "GetPath", variant)!;
        var recovery = (string)Call(store, "GetRecoveryPath", variant)!;
        if (!Path.GetFullPath(recovery).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || File.Exists(recovery) || Directory.Exists(recovery))
            throw new InvalidOperationException("The fixture requires an untouched disposable recovery path.");
        var value = exposure.GetType().GetProperty("Value", Flags)!;
        var prior = value.GetValue(exposure);
        var close = hook.GetMethod("CanCloseAfterSaving", Flags)!;
        bool CanClose(Forms.CloseReason reason, Func<bool> decide) => (bool)close.Invoke(null, new object[] { reason, decide })!;
        var decisions = 0;
        Directory.CreateDirectory(recovery);
        try
        {
            using (var locked = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                value.SetValue(exposure, 2.125);
                Check(Prop(store, "HasUnprotectedPending") is true, "real blocked primary and recovery destinations leave a visible in-memory edit");
                CheckUnprotectedClosePrompt(hook, store);
                Check(!CanClose(Forms.CloseReason.UserClosing, () => { decisions++; return false; }) && decisions == 1,
                    "user-close protection can keep Capture One open when neither location can save");
                Check(CanClose(Forms.CloseReason.UserClosing, () => { decisions++; return true; }) && decisions == 2,
                    "the user can explicitly close despite completely unavailable storage");
                Check(!CanClose(Forms.CloseReason.UserClosing, () => throw new InvalidOperationException("Fixture prompt failure")),
                    "a failed close prompt keeps the unprotected edit in memory");
                Check(CanClose(Forms.CloseReason.WindowsShutDown, () => throw new InvalidOperationException("OS shutdown displayed a prompt")),
                    "OS shutdown performs a save attempt without a modal prompt");
                Directory.Delete(recovery);
                Check(CanClose(Forms.CloseReason.UserClosing, () => throw new InvalidOperationException("Durable recovery displayed a prompt"))
                    && File.Exists(recovery) && Prop(store, "HasUnprotectedPending") is false,
                    "a verified fallback automatically allows close while the primary is locked");
            }
            Call(store, "Flush");
            Check(Prop(Prop(Call(store, "Read", variant)!, "Look")!, "Exposure") is 2.125,
                "restoring disk access publishes the exact pending edit");
        }
        finally
        {
            if (Directory.Exists(recovery)) Directory.Delete(recovery);
            value.SetValue(exposure, prior);
        }
    }

    private static void CheckUnprotectedClosePrompt(Type hook, object store)
    {
        var owner = (Forms.Form)hook.GetField("window", Flags)!.GetValue(null)!;
        var caption = (string)hook.GetField("ProductName", Flags)!.GetRawConstantValue()!;
        var closing = new Forms.FormClosingEventArgs(Forms.CloseReason.UserClosing, false);
        var prompt = DismissOwnedMessageBox(owner, caption, Forms.DialogResult.No,
            () => hook.GetMethod("OnFormClosing", Flags)!.Invoke(null, new object[] { owner, closing }));
        Check(prompt && closing.Cancel && Prop(store, "HasUnprotectedPending") is true,
            "the real owned Windows confirmation defaults to staying open and its No button preserves the unsaved edit");
    }

    private static bool DismissOwnedMessageBox(Forms.Form owner, string caption, Forms.DialogResult choice, Action show,
        string? expectedMessage = null)
    {
        const uint ownerWindow = 4;
        const uint getDefaultId = 0x0400;
        const uint command = 0x0111;
        const int hasDefaultId = 0x534b0000;
        const uint abortIfHung = 2;
        var defaultButtonId = (int)choice;
        var ownerHandle = owner.Handle;
        var opened = 0;
        var matched = 0;
        using var watcher = new System.Threading.Timer(_ =>
        {
            if (Volatile.Read(ref opened) != 0) return;
            nint dialog = 0;
            while ((dialog = FindWindowExW(0, dialog, "#32770", caption)) != 0)
            {
                if (GetWindow(dialog, ownerWindow) != ownerHandle) continue;
                var buttonId = defaultButtonId;
                var button = GetDlgItem(dialog, buttonId);
                // An OK-only Win32 box can give the OK control the Cancel ID
                // for Escape handling while reporting IDOK as its default.
                if (button == 0 && choice == Forms.DialogResult.OK)
                {
                    buttonId = (int)Forms.DialogResult.Cancel;
                    button = GetDlgItem(dialog, buttonId);
                }
                if (button == 0) continue;
                if (Interlocked.Exchange(ref opened, 1) != 0) return;
                var defaultMatches = SendMessageTimeoutW(dialog, getDefaultId, 0, 0, abortIfHung, 250,
                    out var defaultId) != 0 && defaultId == (hasDefaultId | defaultButtonId);
                if (defaultMatches && (expectedMessage is null || ReadOwnedMessage(dialog, expectedMessage)))
                    Volatile.Write(ref matched, 1);
                PostMessageW(dialog, command, buttonId, button);
                return;
            }
        }, null, 25, 25);
        show();
        return Volatile.Read(ref matched) != 0;
    }

    private static bool ReadOwnedMessage(nint dialog, string expected)
    {
        const uint getText = 0x000d;
        const uint abortIfHung = 2;
        var body = new StringBuilder(expected.Length + 2);
        nint label = 0;
        while ((label = FindWindowExW(dialog, label, "Static", null)) != 0)
        {
            body.Clear();
            if (ReadWindowText(label, getText, body.Capacity, body, abortIfHung, 250, out _) != 0
                && body.ToString() == expected)
                return true;
        }
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint handle, uint relationship);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDlgItem(nint dialog, int id);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageTimeoutW(nint handle, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint ReadWindowText(nint handle, uint message, nint wParam, StringBuilder text,
        uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint handle, uint message, nint wParam, nint lParam);

    static async Task TestViewportQuality(object spatial, Guid variant)
    {
        var picker = (ComboBox)Field(spatial, "previewSize")!;
        var options = picker.ItemsSource.Cast<object>().ToArray();
        var previous = picker.SelectedItem;
        var setLoading = spatial.GetType().GetField("loading", Flags)!;
        var preferences = spatial.GetType().Assembly.GetType("PanelPreferences")!.GetProperty("Current", Flags)!;
        var savedPreferences = preferences.GetValue(null);
        void Select(object item)
        {
            // The test changes this disposable control without writing the user's preference.
            setLoading.SetValue(spatial, true);
            try { picker.SelectedItem = item; }
            finally { setLoading.SetValue(spatial, false); }
            Call(spatial, "RequestRefresh");
        }
        var view = Field(spatial, "viewer")!;
        async Task<object> WaitForSource(Func<bool> expected)
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                if (Field(spatial, "source") is { } result && Field(spatial, "frameCurrent") is true && expected())
                    return result;
                await Task.Delay(100);
            }
            throw new InvalidOperationException("The disposable Viewer did not reach its requested settled quality.");
        }

        try
        {
            Call(spatial, "SetEnabled", false);
            Select(options.Single(item => Prop(item, "Id") is "auto"));
            var memory = spatial.GetType().Assembly.GetType("RenderMemoryBudget")!.GetProperty("SystemMemory", Flags)!.GetValue(null)!;
            var budget = (long?)Prop(memory, "AvailableForNewAllocation");
            var coldProjection = Call(view, "MeasureBeforePaint", variant) as double?;
            Check(Call(view, "ProjectedLongEdge", variant) is null && coldProjection is > 0,
                "the real native canvas exposes its physical projection before Spektrafilm paints any frame");
            var qualityType = spatial.GetType().Assembly.GetType("ViewportQuality")!;
            var expectedQuality = Activator.CreateInstance(qualityType)!;
            Call(expectedQuality, "Update", coldProjection, 1d, budget, Environment.TickCount64, true);
            Call(spatial, "SetEnabled", true);
            var firstSource = await WaitForSource(() => Call(view, "ProjectedLongEdge", variant) is double edge && edge > 0);
            Check(Equals(Field(spatial, "sourceSize"), Prop(expectedQuality, "SizeId")) && Prop(firstSource, "BitDepth") is 16,
                $"Auto develops the first RGB16 source at the tier selected before overlay paint ({Field(spatial, "sourceSize")} px)");
            await Task.Delay(1400);
            var automatic = await WaitForSource(() => Field(spatial, "sourceSize") is string size
                && Equals(Prop(spatial, "ViewerRenderSize"), size));
            Check(ReferenceEquals(firstSource, automatic), "a stable cold Auto viewport keeps its first developed frame through the settle interval");
            var projection = (double)Call(view, "ProjectedLongEdge", variant)!;
            var automaticEdge = Math.Max((int)Prop(automatic, "Width")!, (int)Prop(automatic, "Height")!);
            Check(automaticEdge is >= 720 and <= 4096 && (int)Prop(automatic, "BitDepth")! == 16,
                $"auto uses the actual {projection:F0} px Viewer geometry and settles at {automaticEdge} px high-depth source");
            await TestNativeAutoQualityMotion(spatial, view, variant);
            Select(options.Single(item => Prop(item, "Id") is "2048"));
            var manual = await WaitForSource(() => Field(spatial, "sourceSize") is "2048");
            Check(Math.Max((int)Prop(manual, "Width")!, (int)Prop(manual, "Height")!) == 2048 && (int)Prop(manual, "BitDepth")! == 16,
                "manual quality applies its exact high-depth resolution after Auto");
            await TestNativePresentationMotion(spatial, view, variant, manual);
            await TestSaveViewerPng(spatial);
            await TestNativeDraftPyramid(spatial, manual);
            Check(ReferenceEquals(savedPreferences, preferences.GetValue(null)), "fixture quality changes leave the user's stored preference unchanged");
        }
        finally { Select(previous); }
    }

    private static async Task TestSaveViewerPng(object spatial)
    {
        var visible = (BitmapSource)Field(spatial, "viewerFrame")!;
        var button = (Button)Field(spatial, "savePreview")!;
        var path = Path.Combine(folder, "viewer-preview-" + Guid.NewGuid().ToString("N") + ".png");
        Check(button.IsEnabled, "the settled Viewer exposes its preview PNG command");
        var offersPng = false;
        await (Task)Call(spatial, "SavePreviewWithDestinationAsync", (Func<SaveFileDialog, Window?, string?>)((picker, _) =>
        {
            offersPng = picker.Filter == "PNG image|*.png" && picker.DefaultExt.TrimStart('.') == "png";
            return path;
        }))!;
        Check(offersPng, "the preview PNG command declares its own display-image format");
        using var input = File.OpenRead(path);
        var png = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames.Single();
        Check(png.PixelWidth == visible.PixelWidth && png.PixelHeight == visible.PixelHeight && png.ColorContexts?.Count == 1,
            "saved Viewer PNG retains the displayed dimensions and embeds a colour profile");
        var pixel = new Int32Rect(visible.PixelWidth / 3, visible.PixelHeight / 3, 1, 1);
        var original = new byte[4];
        var saved = new byte[4];
        visible.CopyPixels(pixel, original, 4, 0);
        new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0).CopyPixels(pixel, saved, 4, 0);
        Check(original.SequenceEqual(saved) && button.IsEnabled,
            "saved Viewer PNG reopens with the exact displayed BGRA sample and the command is available again");
    }

    static async Task TestSpectralReconstruction(object panel, object spatial, object store, Guid variant)
    {
        var picker = (ComboBox)Field(panel, "spectralMethod")!;
        var prior = picker.SelectedItem;
        var newer = picker.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "hanatos2026");
        string? SavedMethod() => Prop(Prop(Call(store, "Read", variant)!, "Look")!, "SpectralMethod") as string;
        Check(Prop(prior, "Id") is "hanatos2025" && SavedMethod() == "hanatos2025",
            "a prior Hanatos 2025 look stays on the same reconstruction method");
        try
        {
            picker.SelectedItem = newer;
            Check(SavedMethod() == "hanatos2026", "selecting Hanatos 2026 commits the exact method to the current variant");
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                if (Field(spatial, "frameCurrent") is true && Field(spatial, "renderedArguments") is string[] arguments
                    && Field(spatial, "source") is { } frame && Prop(frame, "BitDepth") is 16)
                {
                    var index = Array.IndexOf(arguments, "--spectral-method");
                    if (index >= 0 && index + 1 < arguments.Length && arguments[index + 1] == "hanatos2026")
                    {
                        Check(true, "Hanatos 2026 rendered the selected image from the real 16-bit Capture One source");
                        return;
                    }
                }
                await Task.Delay(100);
            }
            throw new InvalidOperationException("Hanatos 2026 did not settle in the disposable Viewer.");
        }
        finally { picker.SelectedItem = prior; }
    }

    static async Task TestViewerHistogram(Type hook, object spatial, bool restart)
    {
        var histogram = Field(spatial, "histogram")!;
        var section = (Expander)Prop(histogram, "Section")!;
        var marking = (CheckBox)Field(histogram, "markHighlights")!;
        var viewer = Field(spatial, "viewer")!;
        var comparison = (CheckBox)Field(spatial, "showSource")!;
        var palette = hook.GetField("owner", Flags)!.GetValue(null)!;
        var priorTab = Prop(palette, "Current");
        var ownTab = hook.GetField("tab", Flags)!.GetValue(null)!;
        var select = hook.GetMethod("SelectTab", Flags)!;
        var priorComparison = comparison.IsChecked;
        async Task<object> WaitForHistogram()
        {
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                if (Prop(histogram, "Histogram") is { } result && Field(spatial, "frameCurrent") is true)
                    return result;
                await Task.Delay(100);
            }
            throw new InvalidOperationException("The disposable Viewer histogram did not settle.");
        }
        try
        {
            select.Invoke(null, new[] { palette, ownTab });
            comparison.IsChecked = false;
            section.IsExpanded = true;
            var film = await WaitForHistogram();
            var displayed = (BitmapSource)Field(spatial, "viewerFrame")!;
            var pixels = (int)Prop(film, "PixelCount")!;
            Check(ReferenceEquals(Field(histogram, "frame"), displayed) && pixels == displayed.PixelWidth * displayed.PixelHeight,
                "histogram reads every visible pixel of the exact settled film Viewer frame");
            Check(((int[])Prop(film, "Red")!).Sum() == pixels && ((int[])Prop(film, "Green")!).Sum() == pixels
                && ((int[])Prop(film, "Blue")!).Sum() == pixels, "real film histogram channel counts match the view dimensions");

            marking.IsChecked = true;
            film = await WaitForHistogram();
            var mask = (BitmapSource)Prop(film, "HighlightMask")!;
            Check(ReferenceEquals(Field(viewer, "clippingSource"), displayed) && ReferenceEquals(Field(viewer, "clippingMask"), mask)
                && mask.Format == PixelFormats.Indexed1 && mask.PixelWidth == displayed.PixelWidth && mask.PixelHeight == displayed.PixelHeight,
                "real Viewer clipping mask is bound to the exact displayed film bitmap");

            var attachment = Field(viewer, "attachment")!;
            var geometry = Prop(attachment, "Geometry")!;
            var painter = (Visual)Prop(attachment, "Painter")!;
            var transform = (Matrix)Prop(geometry, "Transform")!;
            var imageClip = (Rect)Prop(geometry, "Clip")!;
            var dpi = VisualTreeHelper.GetDpi(painter);
            var paintStarted = Stopwatch.GetTimestamp();
            int imageIndex, maskIndex;
            do
            {
                var images = ImageLayers(painter).ToArray();
                imageIndex = Array.FindIndex(images, image => ReferenceEquals(image, displayed));
                maskIndex = Array.FindIndex(images, image => ReferenceEquals(image, mask));
                if (imageIndex >= 0 && maskIndex > imageIndex) break;
                await Task.Delay(50);
            } while (Stopwatch.GetElapsedTime(paintStarted) < TimeSpan.FromSeconds(5));
            Check(imageIndex >= 0 && maskIndex > imageIndex, "native WPF paint recorded the clipping mask after the film image");
            var coordinate = (
                from x in new[] { displayed.PixelWidth / 2, displayed.PixelWidth / 4, displayed.PixelWidth * 3 / 4 }
                from y in new[] { displayed.PixelHeight / 2, displayed.PixelHeight / 4, displayed.PixelHeight * 3 / 4 }
                let physical = transform.Transform(new Point(x + .5, y + .5))
                where imageClip.Contains(physical)
                select (x, y, physical)).First();
            var selected = Call(viewer, "ReadPixel", new Point(coordinate.physical.X / dpi.DpiScaleX, coordinate.physical.Y / dpi.DpiScaleY))!;
            var expected = new byte[4];
            displayed.CopyPixels(new Int32Rect(coordinate.x, coordinate.y, 1, 1), expected, 4, 0);
            Check(ReferenceEquals(Prop(selected, "Frame"), displayed) && Equals(Prop(selected, "X"), coordinate.x)
                && Equals(Prop(selected, "Y"), coordinate.y) && Equals(Prop(selected, "Red"), expected[2])
                && Equals(Prop(selected, "Green"), expected[1]) && Equals(Prop(selected, "Blue"), expected[0]),
                "picker inverts the actual native paint coordinates and returns the exact sRGB pixel");
            Check(Call(viewer, "ReadPixel", new Point((imageClip.Right + 1) / dpi.DpiScaleX, imageClip.Y / dpi.DpiScaleY)) is null,
                "picker rejects locations outside the native image clip");
            Call(histogram, "SetSample", selected);
            Check(((TextBlock)Field(histogram, "sample")!).Text.StartsWith($"sRGB · R {expected[2]} · G {expected[1]} · B {expected[0]}",
                StringComparison.Ordinal), "real panel labels the picker values with their sRGB encoding");

            var graphic = (FrameworkElement)Field(histogram, "graph")!;
            var width = (int)Math.Ceiling(graphic.ActualWidth);
            var height = (int)Math.Ceiling(graphic.ActualHeight);
            Check(width > 10 && height > 10, "histogram is laid out in the real Capture One panel");
            var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(graphic);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rendered));
            using (var output = File.Create(Path.Combine(folder, restart ? "viewer-histogram-restart.png" : "viewer-histogram.png")))
                encoder.Save(output);

            comparison.IsChecked = true;
            var unprocessed = await WaitForHistogram();
            Check(!ReferenceEquals(film, unprocessed) && !((int[])Prop(film, "Red")!).SequenceEqual((int[])Prop(unprocessed, "Red")!),
                "Show source produces a fresh histogram from the actual source Viewer pixels");
            Check(ReferenceEquals(Field(viewer, "clippingSource"), Field(spatial, "viewerFrame"))
                && !ReferenceEquals(Field(viewer, "clippingSource"), displayed), "Show source refreshes the clipping overlay for the visible frame");
            if (spatial.GetType().GetField("holdCompare", Flags)?.GetValue(spatial) is Button hold)
            {
                var sourceFrame = Field(spatial, "viewerFrame");
                comparison.IsChecked = false;
                await WaitForHistogram();
                var originalFilm = Field(spatial, "viewerFrame");
                var readSettings = (Delegate)Field(spatial, "getSettings")!;
                var before = JsonSerializer.SerializeToNode(readSettings.DynamicInvoke());
                var press = (DependencyPropertyKey)typeof(System.Windows.Controls.Primitives.ButtonBase)
                    .GetField("IsPressedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                hold.SetValue(press, true);
                try
                {
                    await WaitForHistogram();
                    Check(comparison.IsChecked == false && ReferenceEquals(Field(spatial, "viewerFrame"), sourceFrame),
                        "holding the native compare button paints the cached source without setting Show source");
                }
                finally { hold.SetValue(press, false); }
                await WaitForHistogram();
                Check(ReferenceEquals(Field(spatial, "viewerFrame"), originalFilm)
                    && JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(readSettings.DynamicInvoke())),
                    "releasing compare restores the same settled film bitmap and creative settings");
            }
            section.IsExpanded = false;
            Check(Prop(histogram, "Histogram") is null && Field(viewer, "clippingMask") is null
                && Field(viewer, "inspectionEnabled") is false && File.Exists(Path.Combine(folder, "Panel preferences.json")),
                "closing the histogram clears analysis and saves layout only to the disposable preferences");
        }
        finally
        {
            section.IsExpanded = false;
            marking.IsChecked = false;
            comparison.IsChecked = priorComparison;
            select.Invoke(null, new[] { palette, priorTab });
        }
    }

    static void TestPanelAccessibility(object panel, object spatial, object exposure)
    {
        if (spatial.GetType().GetField("holdCompare", Flags) is null) return;
        var slider = (UIElement)Field(exposure, "control")!;
        Check(AutomationProperties.GetName(slider) == "Film exposure" && AutomationProperties.GetHelpText(slider).Contains("Double-click", StringComparison.Ordinal),
            "actual native film slider exposes its full label and reset action to automation");
        var film = (ComboBox)Field(panel, "film")!;
        var paper = (ComboBox)Field(panel, "paper")!;
        Check(film.IsTextSearchEnabled && paper.IsTextSearchEnabled && TextSearch.GetTextPath(film) == "Label" && TextSearch.GetTextPath(paper) == "Label",
            "the actual stock pickers search their displayed names");
    }

    static async Task TestProofing(object panel, object spatial)
    {
        if (!File.Exists(Path.Combine(folder, "Capture One user.config.before-proofing")))
            throw new InvalidOperationException("A verified Capture One preferences backup is required for the proofing fixture.");
        var app = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "CaptureOne");
        var type = app.GetType("P1.C1.ApplicationSettings", true)!;
        var settings = type.GetProperty("Default", Flags)!.GetValue(null)!;
        var proofing = type.GetProperty("IsProofingRecipeSettings", Flags)!;
        var original = (bool)proofing.GetValue(settings)!;
        var viewer = Field(spatial, "viewer")!;

        void CheckReport(bool expected)
        {
            using var report = JsonDocument.Parse((string)Call(panel, "BuildSupportDetails")!);
            var presentation = report.RootElement.GetProperty("ViewerPresentation");
            Check(presentation.GetProperty("RecipeProofing").GetBoolean() == expected,
                "support details reflect native recipe proofing " + (expected ? "on" : "off"));
        }

        async Task WaitUntil(Func<bool> semaphore, string description)
        {
            var started = Stopwatch.GetTimestamp();
            do
            {
                if (semaphore()) { Check(true, description); return; }
                await Task.Delay(100);
            } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20));
            throw new InvalidOperationException(description);
        }

        try
        {
            proofing.SetValue(settings, false);
            CheckReport(false);
            await WaitUntil(() => Prop(viewer, "HasPainted") is true && Field(spatial, "frameCurrent") is true,
                "real Viewer displays the settled film frame with recipe proofing off");
            var bitmap = Field(spatial, "viewerFrame");
            var painter = (Visual)Prop(Field(viewer, "attachment")!, "Painter")!;
            Check(ImageLayers(painter).Any(image => ReferenceEquals(image, bitmap)),
                "native painter contains the unproofed Spektrafilm frame");
            proofing.SetValue(settings, true);
            CheckReport(true);
            await WaitUntil(() => (Prop(viewer, "UnavailableReason") as string)?.Contains("Recipe Proofing", StringComparison.Ordinal) == true
                    && !ImageLayers(painter).Any(image => ReferenceEquals(image, bitmap)),
                "native recipe proofing hides the Spektrafilm layer and explains the suspended Viewer");
            proofing.SetValue(settings, false);
            await WaitUntil(() => Prop(viewer, "HasPainted") is true && ImageLayers(painter)
                    .Any(image => ReferenceEquals(image, Field(spatial, "viewerFrame"))),
                "leaving native recipe proofing restores the current film overlay");
        }
        finally { proofing.SetValue(settings, original); }
        Check(Equals(proofing.GetValue(settings), original), "native proofing preference restored after the disposable test");
    }

    static System.Collections.Generic.IEnumerable<ImageSource> ImageLayers(Visual visual)
    {
        foreach (var image in FromDrawing(VisualTreeHelper.GetDrawing(visual))) yield return image;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(visual); index++)
            if (VisualTreeHelper.GetChild(visual, index) is Visual child)
                foreach (var image in ImageLayers(child)) yield return image;

        static System.Collections.Generic.IEnumerable<ImageSource> FromDrawing(Drawing? drawing)
        {
            if (drawing is ImageDrawing { ImageSource: { } image }) yield return image;
            else if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var source in FromDrawing(child)) yield return source;
        }
    }

    static void TestHostLookups(Assembly panelAssembly, object panel, Forms.Form window)
    {
        const int calls = 5_000;
        var access = panelAssembly.GetType("HostDocumentAccess")!;
        var current = access.GetMethod("Current", Flags)!.CreateDelegate<Func<object?>>();
        var active = current()!;
        var path = (Func<string?>)Prop(active, "ActivePath")!;
        var readDocumentCallback = (Delegate)Prop(active, "ActiveDocument")!;
        var readDocument = (Func<object?>)readDocumentCallback;
        var formMethod = access.GetMethod("MainForm", Flags)!;
        var documented = System.Linq.Expressions.Expression.Parameter(typeof(object), "document");
        var formLookup = System.Linq.Expressions.Expression.Lambda<Func<object, Forms.Form?>>(
            System.Linq.Expressions.Expression.Call(formMethod,
                System.Linq.Expressions.Expression.Convert(documented, formMethod.GetParameters()[0].ParameterType)), documented).Compile();
        var approve = panelAssembly.GetType("ApprovedCatalog")!.GetMethod("IsApproved", Flags)!.CreateDelegate<Func<string?, string?, bool>>();
        var paletteFor = access.GetMethod("Palette", Flags)!.CreateDelegate<Func<Forms.Form, object?>>();
        var document = Field(panel, "document")!;
        var pathProperty = formMethod.GetParameters()[0].ParameterType.GetProperty("Path")!;
        var readDocumentPath = System.Linq.Expressions.Expression.Lambda<Func<object, string>>(
            System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Convert(documented,
                formMethod.GetParameters()[0].ParameterType), pathProperty), documented).Compile();
        var nativePath = readDocumentPath(document);
        var another = Path.Combine(folder, "different.cocatalogdb");
        var palette = paletteFor(window);
        Check(ReferenceEquals(readDocument(), document) && palette is not null && approve(path(), nativePath),
            "cached adapter matches the active native document, form palette and catalog authorization");
        Check(ReferenceEquals(active, current()) && !approve(another, nativePath),
            "binding retains its stable manager and rejects a different database path");
        Check(string.Equals(nativePath, Environment.GetEnvironmentVariable("SPEKTRAFILM_CATALOG"), StringComparison.OrdinalIgnoreCase),
            "native document reports its exact selected database path");
        Check(approve(Path.GetDirectoryName(nativePath), nativePath) && !approve(Path.GetDirectoryName(nativePath), another),
            "directory-form manager identity requires the exact opted-in database");
        Check(!approve(nativePath, another) && !approve(@"\\example.invalid\catalog-share", nativePath),
            "document switch and unrelated network paths are rejected");

        // Compare just the lookup work on the same UI thread and stable catalog.
        object? Legacy()
        {
            var program = AppDomain.CurrentDomain.GetAssemblies()
                .First(assembly => assembly.GetName().Name == "CaptureOne").GetType("P1.C1.Program")!;
            var manager = program.GetProperty("DocumentManager", Flags)!.GetValue(null)!;
            var activePath = (string?)manager.GetType().GetProperty("ActiveDocumentPath")!.GetValue(manager);
            var activeDoc = manager.GetType().GetProperty("ActiveDocument")!.GetValue(manager);
            if (activeDoc is null || !approve(activePath, readDocumentPath(activeDoc)))
                return null;
            var form = program.GetMethod("GetMainForm", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new[] { activeDoc });
            return form!.GetType().GetProperty("InspectorPaletteViewModel")!.GetValue(form);
        }

        object? Cached() => ReferenceEquals(current(), active) && readDocument() is { } currentDocument
            && approve(path(), readDocumentPath(currentDocument))
            && formLookup(currentDocument) is { } form ? paletteFor(form) : null;
        Check(ReferenceEquals(Legacy(), palette) && ReferenceEquals(Cached(), palette), "lookup benchmark produced identical live palette identity");
        double Measure(Func<object?> lookup)
        {
            var start = Stopwatch.GetTimestamp();
            object? result = null;
            for (var index = 0; index < calls; index++)
                result = lookup();
            if (!ReferenceEquals(palette, result))
                throw new InvalidOperationException("Lookup benchmark changed palette identity.");
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        var reflectionMilliseconds = Measure(Legacy);
        var cachedMilliseconds = Measure(Cached);
        File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
            $"Host lookup benchmark: {calls} calls; repeated discovery {reflectionMilliseconds:F2} ms, cached lookup {cachedMilliseconds:F2} ms." + Environment.NewLine);
    }

    static async Task TestFullDirectExport(object spatial)
    {
        var output = Path.Combine(folder, "full-precision-export.tif");
        var started = Stopwatch.GetTimestamp();
        var export = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", false, null,
            (Func<SaveFileDialog, Window?, string?>)((_, _) => output))!;
        var jobs = Field(spatial, "exportJobs")!;
        var observedStages = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        while (!export.IsCompleted)
        {
            if (Call(jobs, "ReadLatest") is { } progress && Prop(progress, "Destination") is string path && path == output)
                observedStages.Add(Prop(progress, "Stage")!.ToString()!);
            await Task.WhenAny(export, Task.Delay(30));
        }
        var exported = await export;
        Check(exported && File.Exists(output), "direct export processed the full-sized RAW source");
        Check(observedStages.Contains("RenderingFilm") && observedStages.Contains("Encoding"),
            "the real full RAW export published its film-render and encoding stages while it was working");
        var receipt = Call(jobs, "ReadLatest")!;
        Check(Prop(receipt, "Stage")!.ToString() == "Completed" && Equals(Prop(receipt, "Destination"), output)
            && Prop(receipt, "Width") is 6000 && Prop(receipt, "Height") is 4000,
            "completed output receipt kept its immutable destination and exact exported dimensions");
        using var input = File.OpenRead(output);
        var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnDemand).Frames[0];
        Check(frame.Format == PixelFormats.Rgb48 && frame.PixelWidth == 6000 && frame.PixelHeight == 4000,
            "full unscaled DNG export reopens at 6000 x 4000 in RGB48");
        Check(Math.Abs(frame.DpiX - 96) < 0.03 && Math.Abs(frame.DpiY - 96) < 0.03
            && frame.ColorContexts?.Count > 0, "full direct export retained 96 DPI and embedded ICC");
        const int stride = 16 * 6;
        var sample = new byte[16 * stride];
        frame.CopyPixels(new Int32Rect(3000, 2000, 16, 16), sample, stride, 0);
        Check(Enumerable.Range(0, sample.Length / 2).Select(index => BitConverter.ToUInt16(sample, index * 2))
            .Any(code => code % 257 != 0), "full RAW export contains codes between 8-bit steps");
        File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
            $"Full export benchmark: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms incl. one decoded sample, {input.Length} bytes." + Environment.NewLine);
        input.Dispose();
        await TestNativeEncoderCancellation();

        async Task TestNativeEncoderCancellation()
        {
            string HashOutput()
            {
                using var bytes = File.OpenRead(output);
                return Convert.ToHexString(SHA256.HashData(bytes));
            }
            var previousHash = HashOutput();
            var previousId = Prop(receipt, "JobId");
            var cancel = (Button)Field(spatial, "cancelExport")!;
            var writing = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", false, null,
                (Func<SaveFileDialog, Window?, string?>)((_, _) => output))!;
            var queued = Stopwatch.GetTimestamp();
            long stagedBytes = 0;
            while (!writing.IsCompleted && Stopwatch.GetElapsedTime(queued) < TimeSpan.FromSeconds(30))
            {
                if (Call(jobs, "ReadLatest") is { } progress && !Equals(Prop(progress, "JobId"), previousId)
                    && Prop(progress, "Stage")?.ToString() == "Encoding")
                {
                    if (Field(spatial, "exportWork") is { } active)
                        stagedBytes = (long)Prop(active, "EncoderBytesWritten")!;
                    if (stagedBytes >= 1024 * 1024) break;
                }
                await Task.WhenAny(writing, Task.Delay(20));
            }
            var cancellable = !writing.IsCompleted && cancel.Visibility == Visibility.Visible && cancel.IsEnabled;
            var signaled = Stopwatch.GetTimestamp();
            if (!writing.IsCompleted) cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(stagedBytes >= 1024 * 1024 && cancellable,
                "Capture One's Spektrafilm Cancel export button is active during actual TIFF writes");
            var result = await writing.WaitAsync(TimeSpan.FromSeconds(15));
            var cancelMs = Stopwatch.GetElapsedTime(signaled).TotalMilliseconds;
            Check(!result, "native Cancel export stopped the WIC encoder");
            var after = Call(jobs, "ReadLatest")!;
            Check(Prop(after, "Stage")?.ToString() == "Canceled" && !Equals(Prop(after, "JobId"), previousId)
                && Equals(Prop(after, "Destination"), output), "the encoding cancellation records its accepted job as canceled");
            Check(HashOutput() == previousHash && !Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(),
                "native encoding cancellation preserves the prior full-size TIFF and removes the staging file");
            var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
            File.WriteAllText(Path.Combine(folder, phase + "-encoder-cancellation.json"), JsonSerializer.Serialize(new
            {
                stagedBytes, cancelMs, previousHash,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static async Task TestWorkerQuiescence(object panel, object spatial, Type rendererType)
    {
        Call(spatial, "SetEnabled", false);
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        object NewRenderer() => Activator.CreateInstance(rendererType, Flags, null, new object[] { false, true }, null)!;
        var full = NewRenderer();
        var draft = NewRenderer();
        using var disposeFull = (IDisposable)full;
        using var disposeDraft = (IDisposable)draft;
        var arguments = Call(spatial, "RendererOptions", Call(panel, "CurrentSettings"), "srgb", "srgb", true, false)!;
        var samples = Enumerable.Range(0, 8 * 8 * 4).Select(index => index % 4 == 3 ? 1f : 0.4f).ToArray();
        var input = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, input, 0, input.Length);
        foreach (var worker in new[] { full, draft })
            await (Task<byte[]>)Call(worker, "RenderAsync", 8, 8, input, arguments, CancellationToken.None, false)!;
        using var fullProcess = Process.GetProcessById(((Process)Field(full, "process")!).Id);
        using var draftProcess = Process.GetProcessById(((Process)Field(draft, "process")!).Id);
        spatial.GetType().GetField("renderer", Flags)!.SetValue(spatial, full);
        spatial.GetType().GetField("draftRenderer", Flags)!.SetValue(spatial, draft);
        Check(!fullProcess.HasExited && !draftProcess.HasExited, "real Vulkan preview workers running before output lease");

        var nativeLut = rendererType.Assembly.GetType("NativeLutExporter")!;
        var plan = nativeLut.GetMethod("Prepare", Flags)!.Invoke(null, new[] { arguments })!;
        Task<string> Bake() => (Task<string>)Call(spatial, "RenderLutAsync", plan, 33, 1d, CancellationToken.None)!;
        var bake = Bake();
        try
        {
            await Bake();
            throw new InvalidOperationException("A second Vulkan output acquired the same lease.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("current Spektrafilm export", StringComparison.Ordinal))
        {
            Check(true, "second LUT render rejected while first owns the output lease");
        }

        Call(spatial, "SetEnabled", true);
        var image = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, null,
            (Func<SaveFileDialog, Window?, string?>)((_, _) => throw new InvalidOperationException("Image export should not open Save during LUT rendering.")))!;
        Check(await image == false && Field(spatial, "exportWork") is null, "image export is blocked during the active LUT without opening a dialog");
        var cube = await bake;
        Check(cube.Contains("LUT_3D_SIZE 33", StringComparison.Ordinal) && fullProcess.HasExited && draftProcess.HasExited,
            "Vulkan LUT completed after both Viewer worker processes exited");
        Check(Field(spatial, "lutRenderActive") is false && Field(spatial, "queued") is true,
            "latest Viewer request resumes automatically when the LUT lease releases");
    }

    static void TestProductionUndo(object panel, object spatial, object window, object store, Guid variant, object exposure)
    {
        var document = Field(panel, "document")!;
        var undoManager = Field(panel, "nativeUndo")!;
        var model = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.ModelEntities");
        var managerType = model.GetType("P1.C1.ModelEntities.UndoInterfaces.IUndoManager", true)!;
        var undo = managerType.GetMethod("Undo", new[] { typeof(bool) })!;
        var redo = managerType.GetMethod("Redo")!;
        var name = managerType.GetProperty("TryGetUndoActionName")!;
        var value = exposure.GetType().GetProperty("Value", Flags)!;
        double Saved() => (double)Prop(Prop(Call(store, "Read", variant)!, "Look")!, "Exposure")!;
        bool Is(double expected) => Math.Abs(Saved() - expected) < 1e-9;
        void Undo(bool allowRedo) => undo.Invoke(undoManager, new object[] { allowRedo });
        void Redo() => redo.Invoke(undoManager, null);
        var previousEntry = name.GetValue(undoManager);
        Call(spatial, "BeginSliderDrag");
        foreach (var motion in new[] { 0.3, 0.5, 0.8 })
        {
            value.SetValue(exposure, motion);
            Check(Is(motion) && Equals(name.GetValue(undoManager), previousEntry),
                "each motion saves durably inside one unfinished undo gesture: " + motion);
        }
        Call(spatial, "EndSliderDrag");
        Check(Equals(name.GetValue(undoManager), "Spektrafilm slider"), "finished slider gesture registered one native action");

        var profile = panel.GetType().Assembly.GetType("HostProfile")!;
        var selected = (System.Runtime.CompilerServices.ITuple)profile.GetMethod("SelectedVariant", Flags)!.Invoke(null, new[] { window })!;
        var gain = ((Type)selected[1]!).GetProperty("FilmGrainAmount")!;
        var priorNative = (float)gain.GetValue(selected[0])!;
        var newNative = priorNative < 60 ? priorNative + 11 : priorNative - 11;
        gain.SetValue(selected[0], newNative);
        Undo(true);
        Check((float)gain.GetValue(selected[0])! == priorNative && Is(0.8),
            "Undo removes the later native adjustment before the film gesture");
        Undo(true);
        Check(Is(1.2) && Equals(value.GetValue(exposure), 1.2) && Equals(name.GetValue(undoManager), previousEntry),
            "one native Undo restores the entire slider gesture and refreshes controls");
        Redo();
        Check(Is(0.8) && Equals(value.GetValue(exposure), 0.8), "native Redo persists the film look and updates controls");
        Redo();
        Check((float)gain.GetValue(selected[0])! == newNative && Is(0.8), "native adjustment remains above the film gesture in Redo history");
        Undo(false);
        Undo(false);
        Check((float)gain.GetValue(selected[0])! == priorNative && Is(1.2), "undo integration fixture restored native and film settings");

        // A second image can be active while Undo still belongs to the captured variant.
        var core = document.GetType().Assembly.GetName().Name == "P1.C1.Core"
            ? document.GetType().Assembly
            : AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.Core");
        var provider = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
        var collection = provider.GetProperty("PrimarySelectedCollection")!.GetValue(document)!;
        var variants = (IEnumerable)core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!
            .GetMethod("GetAllFilteredAndVisibleVariants")!.Invoke(collection, null)!;
        var uuid = core.GetType("P1.C1.Core.ICoreVariant", true)!.GetProperty("UUID")!;
        var other = variants.Cast<object>().First(item => !Equals(uuid.GetValue(item), variant));
        var selection = provider.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
        var variantSelection = core.GetType("P1.C1.Core.Selection.ISelection", true)!.GetProperty("SelectedVariants")!.GetValue(selection)!;
        var select = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!.GetMethod("SelectAndSetAsPrimary")!;
        var noUndo = model.GetType("P1.C1.ModelEntities.UndoInterfaces.IGroupingUndoManager", true)!.GetMethod("NoUndoScope")!;
        void SelectWithoutHistory(object target)
        {
            var scope = noUndo.Invoke(undoManager, new object[] { "Spektrafilm fixture selection" })!;
            try
            {
                select.Invoke(variantSelection, new[] { target, (object)false });
                Call(panel, "SyncSelection");
            }
            finally { noUndo.ReturnType.GetMethod("EndScope")!.Invoke(scope, null); }
        }
        value.SetValue(exposure, 2.1);
        try
        {
            SelectWithoutHistory(other);
            var shown = value.GetValue(exposure);
            Check(Equals(Field(panel, "displayedVariant"), uuid.GetValue(other)), "a different native variant is active for scoped Undo");
            Check(Equals(name.GetValue(undoManager), "Spektrafilm settings"), "captured film edit is next in native Undo");
            Undo(false);
            Check(Is(1.2) && Equals(shown, value.GetValue(exposure)), "Undo writes only the original variant while another remains selected");
        }
        finally { SelectWithoutHistory(selected[0]!); }
    }

    static async Task TestPresetExport(Assembly assembly, object panel, object store, Guid variant)
    {
        if (panel.GetType().GetMethod("SavePresetWithDestinationAsync", Flags) is null) return;
        var path = Path.Combine(folder, "Presets", "verified-look.spektra.json");
        var before = Call(store, "Read", variant)!;
        await (Task)Call(panel, "SavePresetWithDestinationAsync", (Func<SaveFileDialog, string?>)(picker =>
        {
            Check(picker.InitialDirectory == Path.GetDirectoryName(path) && Directory.Exists(picker.InitialDirectory),
                "the real preset save dialog defaults to the isolated user presets folder");
            return path;
        }))!;
        Check(File.Exists(path), "real panel saved the selected variant through the normal preset action");
        var file = JsonNode.Parse(File.ReadAllText(path))!;
        var root = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!;
        var build = assembly.GetType("RuntimeAssets")!.GetMethod("ReadPresetIdentity", Flags)!.Invoke(null, new[] { root });
        Check(build is null
            ? file["SchemaVersion"]!.GetValue<int>() == 2 && file["Engine"] is null
            : file["SchemaVersion"]!.GetValue<int>() == 3 && file["Engine"]!["AssetSha256"]!.GetValue<string>() == (string)Prop(build, "AssetSha256")!,
            "saved native preset reflects the exact verified renderer provenance available in this launch");
        var presetType = assembly.GetType("LookPreset")!;
        var stored = JsonSerializer.Deserialize(File.ReadAllText(path), presetType)!;
        Call(stored, "Validate");
        if (panel.GetType().GetField("panelChoices", Flags)?.GetValue(panel) is { } choices)
            Check(((IEnumerable)Prop(choices, "RecentPresets")!).Cast<string>().FirstOrDefault() == path,
                "the real saved preset becomes the first recent preset in this catalog test");
        Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(before, before.GetType()),
            JsonSerializer.SerializeToNode(Call(store, "Read", variant), before.GetType())),
            "saving a portable preset leaves the selected variant's durable look unchanged");
    }

    static void TestStockFavorites(object panel, object store, Guid variant, bool restart)
    {
        if (panel.GetType().GetField("panelChoices", Flags)?.GetValue(panel) is not { } choices) return;
        var picker = (ComboBox)Field(panel, "film")!;
        var selected = picker.SelectedItem!;
        var id = (string)Prop(selected, "Id")!;
        if (restart)
        {
            Check(Call(choices, "IsFavorite", false, id) is true && (string)Prop(picker.Items[0], "Id")! == id,
                "the stock favorite is restored and visibly ordered first after restart");
        }
        else
        {
            var before = Call(store, "Read", variant)!;
            var menu = (MenuItem)picker.ContextMenu!.Items[0];
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, menu));
            Check(Call(choices, "IsFavorite", false, id) is true && (string)Prop(picker.Items[0], "Id")! == id
                && ReferenceEquals(picker.SelectedItem, selected), "favoriting a stock moves its stable ID first and preserves the current selection");
            Check(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(before, before.GetType()),
                JsonSerializer.SerializeToNode(Call(store, "Read", variant), before.GetType())),
                "favoriting a stock preserves the durable look");
        }
    }

    static void TestPresetHistory(object panel, object window, object store, Guid variant, object exposure, CheckBox toggle)
    {
        var assembly = panel.GetType().Assembly;
        var presetType = assembly.GetType("LookPreset")!;
        var baseline = Call(panel, "CaptureLook")!;
        var grainType = assembly.GetType("NativeGrain")!;
        object NativeGrain() => grainType.GetMethod("Read", Flags)!.Invoke(null, new[] { window })!;
        var originalGrain = NativeGrain();
        var preset = presetType.GetMethod("From", Flags)!.Invoke(null, new[] { baseline });
        var json = JsonSerializer.SerializeToNode(preset, presetType)!.AsObject();
        json["Enabled"] = false;
        json["Look"]!.AsObject()["Exposure"] = 2.4;
        json["Optics"]!["Numbers"]!.AsObject()["halation-amount"] = 0.45;
        json["Grain"] = JsonSerializer.SerializeToNode(new { Type = 3, Impact = 19, Granularity = 59 });
        var path = Path.Combine(folder, "native-look-roundtrip.spektra.json");
        File.WriteAllText(path, json.ToJsonString());
        var disk = JsonSerializer.Deserialize(File.ReadAllText(path), presetType)!;
        bool SameLook(object look) => JsonNode.DeepEquals(JsonSerializer.SerializeToNode(look, look.GetType()),
            JsonSerializer.SerializeToNode(baseline, baseline.GetType()));
        object Saved() => Call(store, "Read", variant)!;

        Check(Call(panel, "ApplyPreset", Guid.NewGuid(), disk) is false && SameLook(Saved()), "preset keeps its captured target after navigation");
        Check(Call(panel, "ApplyPreset", variant, disk) is true, "full saved preset applied its captured native variant");
        var saved = JsonSerializer.SerializeToNode(Saved(), baseline.GetType())!;
        Check(toggle.IsChecked == false && Prop(exposure, "Value") is 2.4
            && saved["Optics"]!["Numbers"]!["halation-amount"]!.GetValue<double>() == 0.45
            && saved["Grain"]!["Impact"]!.GetValue<double>() == 19,
            "preset applied master, film, optics and staged grain together");
        Check(((FrameworkElement)Field(panel, "fileControls")!).IsEnabled
            && !((UIElement)Field(panel, "exportButtons")!).IsEnabled,
            "disabled look can still load or save presets while export actions are disabled");
        Check(originalGrain.Equals(NativeGrain()), "preset apply leaves actual Capture One Grain unchanged");

        var undo = Field(panel, "nativeUndo")!;
        var model = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.ModelEntities");
        var contract = model.GetType("P1.C1.ModelEntities.UndoInterfaces.IUndoManager", true)!;
        var revert = contract.GetMethod("Undo", new[] { typeof(bool) })!;
        Check(contract.GetProperty("TryGetUndoActionName")!.GetValue(undo) is "Spektrafilm preset", "preset recorded one named native action");
        revert.Invoke(undo, new object[] { true });
        Check(toggle.IsChecked == true && SameLook(Saved()), "one Undo restores the complete previous preset state");
        contract.GetMethod("Redo")!.Invoke(undo, null);
        Check(toggle.IsChecked == false && JsonNode.DeepEquals(saved, JsonSerializer.SerializeToNode(Saved(), baseline.GetType())),
            "one Redo restores the complete imported preset state");
        revert.Invoke(undo, new object[] { false });
        Check(SameLook(Saved()) && originalGrain.Equals(NativeGrain()), "preset fixture restored saved look and native grain exactly");

        var sections = assembly.GetType("LookSections")!;
        Check(Call(panel, "ApplyPresetSections", variant, disk, Enum.Parse(sections, "Film"), "Spektrafilm preset sections") is true,
            "the host applied just the selected film and print section");
        var filmOnly = JsonSerializer.SerializeToNode(Saved(), baseline.GetType())!;
        var baselineNode = JsonSerializer.SerializeToNode(baseline, baseline.GetType())!;
        Check(filmOnly["Look"]!["Exposure"]!.GetValue<double>() == 2.4 && toggle.IsChecked == true
            && JsonNode.DeepEquals(filmOnly["Optics"], baselineNode["Optics"])
            && JsonNode.DeepEquals(filmOnly["Grain"], baselineNode["Grain"]), "unchecked sections survive selective preset application");
        revert.Invoke(undo, new object[] { false });
        Check(SameLook(Saved()), "one native Undo restores the selective preset change");

        var spatial = Field(panel, "opticalPreview")!;
        var options = Field(panel, "options")!;
        var rendererManifest = assembly.GetType("RendererParameters")!.GetMethod("Read", Flags)!.Invoke(null,
            new[] { Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")! })!;
        var modes = ((IEnumerable)Prop(options, "Modes")!).Cast<object>();
        foreach (var mode in modes)
        {
            var modeName = (string)Prop(mode, "Id")!;
            foreach (var boundary in new[] { "Minimum", "Maximum" })
            {
                var exhaustive = json.DeepClone().AsObject();
                exhaustive["Look"]!["Mode"] = modeName;
                exhaustive["Look"]!["Exposure"] = (double)Prop(exposure, boundary)!;
                exhaustive["Look"]!["PrintExposure"] = (double)Prop(Field(panel, "printExposure")!, boundary)!;
                exhaustive["Look"]!["Strength"] = (double)Prop(Field(panel, "strength")!, boundary)! / 100;
                var development = exhaustive["Look"]!["Development"]!.AsObject();
                foreach (DictionaryEntry entry in (IDictionary)Field(panel, "development")!)
                    development[(string)entry.Key] = (double)Prop(Field(entry.Value!, "Item1")!, boundary)!;
                var numbers = exhaustive["Optics"]!["Numbers"]!.AsObject();
                foreach (DictionaryEntry entry in (IDictionary)Field(spatial, "values")!)
                    numbers[(string)entry.Key] = (double)Prop(entry.Value!, boundary)!;
                var switches = exhaustive["Optics"]!["Switches"]!.AsObject();
                foreach (var key in switches.Select(entry => entry.Key).ToArray())
                    switches[key] = boundary == "Maximum";
                exhaustive["Look"]!["ScanInvert"] = boundary == "Maximum";
                var loaded = JsonSerializer.Deserialize(exhaustive.ToJsonString(), presetType)!;
                Check(Call(panel, "ApplyPreset", variant, loaded) is true, $"{modeName} absorbs all {boundary.ToLowerInvariant()} preset values");
                var captured = JsonSerializer.SerializeToNode(Saved(), baseline.GetType())!;
                Check(JsonNode.DeepEquals(captured["Look"], exhaustive["Look"])
                    && JsonNode.DeepEquals(captured["Optics"], exhaustive["Optics"])
                    && JsonNode.DeepEquals(captured["Grain"], exhaustive["Grain"])
                    && JsonNode.DeepEquals(captured["Enabled"], exhaustive["Enabled"]),
                    $"{modeName} preserves every serialized control at the {boundary.ToLowerInvariant()} boundary");
                var renderable = captured["Look"]!.DeepClone();
                renderable["Development"]!["print_exhaustion"] = 0;
                var supported = JsonSerializer.Deserialize(renderable.ToJsonString(), Prop(baseline, "Look")!.GetType());
                var arguments = (string[])Call(spatial, "RendererOptions", supported, "srgb", "srgb", false, false)!;
                Call(rendererManifest, "VerifyArguments", (object)arguments);
                Check(arguments.Length != 0, $"{modeName} {boundary.ToLowerInvariant()} controls map to the compiled renderer description");
                revert.Invoke(undo, new object[] { false });
            }
        }
        Check(SameLook(Saved()) && originalGrain.Equals(NativeGrain()), "every-mode preset probes restore the original state and native grain");

        var legacy = json.DeepClone().AsObject();
        legacy.Remove("SchemaVersion");
        legacy.Remove("Enabled");
        legacy.Remove("Grain");
        legacy["Look"]!["Development"]!["print_exhaustion"] = .4;
        var legacyPath = Path.Combine(folder, "legacy-exhaustion.spektra.json");
        var legacyText = legacy.ToJsonString();
        File.WriteAllText(legacyPath, legacyText);
        var legacyPreset = JsonSerializer.Deserialize(File.ReadAllText(legacyPath), presetType)!;
        var printPanel = (StackPanel)Field(panel, "printing")!;
        var clear = printPanel.Children.OfType<Button>().Single(button => Equals(button.Content, "Clear unsupported developer exhaustion"));
        var legacyEntry = ((IDictionary)Field(panel, "development")!)["print_exhaustion"]!;
        var exhaustion = (FrameworkElement)Field(legacyEntry, "Item1")!;
        double SavedExhaustion() => ((IDictionary)Prop(Prop(Saved(), "Look")!, "Development")!)["print_exhaustion"] is double value
            ? value : throw new InvalidOperationException("Missing saved exhaustion value.");
        Check(Call(panel, "ApplyPreset", variant, legacyPreset) is true && SavedExhaustion() == .4
            && clear.Visibility == Visibility.Visible && exhaustion.IsEnabled == false, "legacy developer exhaustion stays saved and has an explicit clear action");
        try
        {
            Call(spatial, "RendererOptions", Prop(Saved(), "Look"), "srgb", "srgb", false, false);
            throw new InvalidOperationException("Unsupported exhaustion was sent to the renderer.");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException invalid
            && invalid.Message.Contains("Developer exhaustion", StringComparison.Ordinal))
        {
            Check(Field(spatial, "frameCurrent") is false, "legacy exhaustion is refused before worker creation without presenting a current stale frame");
        }
        clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, clear));
        Check(SavedExhaustion() == 0 && clear.Visibility == Visibility.Collapsed && File.ReadAllText(legacyPath) == legacyText,
            "Clear commits only the variant setting and preserves the original legacy preset");
        var restoredArguments = (string[])Call(spatial, "RendererOptions", Prop(Saved(), "Look"), "srgb", "srgb", false, false)!;
        Call(rendererManifest, "VerifyArguments", (object)restoredArguments);
        Check(restoredArguments.Length != 0, "clearing legacy exhaustion restores a supported renderer request");
        revert.Invoke(undo, new object[] { false });
        revert.Invoke(undo, new object[] { false });
        Check(SameLook(Saved()), "native Undo restores the look before the legacy exhaustion fixture");

        var fileControls = (DependencyObject)Field(panel, "fileControls")!;
        var controls = Descendants(fileControls).OfType<ContentControl>().ToArray();
        T Control<T>(string label) where T : ContentControl => controls.OfType<T>().Single(control => Equals(control.Content, label));
        var copy = Control<Button>("Copy selected settings");
        var apply = Control<Button>("Apply copied settings");
        var film = Control<CheckBox>("Film and print");
        var optics = Control<CheckBox>("Optics and colour spaces");
        var grain = Control<CheckBox>("Staged Capture One grain");
        var master = Control<CheckBox>("Master switch");
        film.IsChecked = optics.IsChecked = grain.IsChecked = master.IsChecked = false;
        Check(!copy.IsEnabled && !apply.IsEnabled, "selective controls require a section before copying or applying");
        film.IsChecked = true;
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, copy));
        Check(apply.IsEnabled, "copying a section makes it available to apply");
        film.IsChecked = false;
        optics.IsChecked = true;
        Check(!apply.IsEnabled, "a section omitted during Copy cannot be applied later");
        film.IsChecked = true;

        var document = Field(panel, "document")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Core");
        var provider = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
        var collection = provider.GetProperty("PrimarySelectedCollection")!.GetValue(document)!;
        var all = ((IEnumerable)core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!
            .GetMethod("GetAllFilteredAndVisibleVariants")!.Invoke(collection, null)!).Cast<object>().ToArray();
        var uuid = core.GetType("P1.C1.Core.ICoreVariant", true)!.GetProperty("UUID")!;
        var source = all.Single(item => Equals(uuid.GetValue(item), variant));
        var destination = all.First(item => !Equals(uuid.GetValue(item), variant));
        var destinationId = (Guid)uuid.GetValue(destination)!;
        var selection = provider.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
        var target = core.GetType("P1.C1.Core.Selection.ISelection", true)!.GetProperty("SelectedVariants")!.GetValue(selection)!;
        var select = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!.GetMethod("SelectAndSetAsPrimary")!;
        void Select(object item)
        {
            select.Invoke(target, new[] { item, (object)false });
            Call(panel, "SyncSelection");
        }

        try
        {
            Select(destination);
            exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, -0.45);
            toggle.IsChecked = false;
            var previous = JsonSerializer.SerializeToNode(Call(panel, "CaptureLook")!, baseline.GetType())!;
            var destinationGrain = NativeGrain();
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, apply));
            var next = JsonSerializer.SerializeToNode(Call(store, "Read", destinationId)!, baseline.GetType())!;
            Check(JsonNode.DeepEquals(next["Look"], baselineNode["Look"]) && JsonNode.DeepEquals(next["Optics"], previous["Optics"])
                && JsonNode.DeepEquals(next["Grain"], previous["Grain"]) && next["Enabled"]!.GetValue<bool>() == false,
                "real Copy and Apply carry only the captured film settings to another native variant");
            Check(SameLook(Saved()) && destinationGrain.Equals(NativeGrain()), "copy leaves the source and actual destination grain intact");
            Check(contract.GetProperty("TryGetUndoActionName")!.GetValue(undo) is "Apply Spektrafilm settings",
                "copy recorded one named native history action");
            revert.Invoke(undo, new object[] { false });
            var restored = JsonSerializer.SerializeToNode(Call(store, "Read", destinationId)!, baseline.GetType());
            Check(JsonNode.DeepEquals(previous, restored), "one native Undo restores the destination after Copy and Apply");
        }
        finally
        {
            film.IsChecked = optics.IsChecked = grain.IsChecked = master.IsChecked = true;
            Select(source);
        }

        static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
                foreach (var descendant in Descendants(child))
                    yield return descendant;
        }
    }

    static void TestNativeUndoCapability(object document)
    {
        var model = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.ModelEntities");
        const string root = "P1.C1.ModelEntities.UndoInterfaces.";
        var actionType = model.GetType(root + "IUndoableAction", true)!;
        var managerType = model.GetType(root + "IUndoManager", true)!;
        var manager = model.GetType(root + "IUndo", true)!.GetProperty("UndoManager")!.GetValue(document)!;
        var action = DispatchProxy.Create(actionType, typeof(UndoProbe));
        var probe = (UndoProbe)action;
        var change = managerType.GetMethod("Do")!;
        var undo = managerType.GetMethod("Undo", new[] { typeof(bool) })!;
        var redo = managerType.GetMethod("Redo")!;
        var name = managerType.GetProperty("TryGetUndoActionName")!;
        var previous = name.GetValue(manager);
        change.Invoke(manager, new[] { action });
        Check(probe.Applied == 1 && !probe.Reversed && Equals(name.GetValue(manager), UndoProbe.ActionName),
            "native undo manager registered a named extension action");
        undo.Invoke(manager, new object[] { true });
        Check(probe.Applied == 1 && probe.Reversed, "native Undo invoked the extension callback");
        redo.Invoke(manager, null);
        Check(probe.Applied == 2 && !probe.Reversed, "native Redo invoked the extension apply callback");
        undo.Invoke(manager, new object[] { false });
        Check(probe.Reversed && Equals(name.GetValue(manager), previous), "extension probe restored the preceding history entry");
    }

    public class UndoProbe : DispatchProxy
    {
        internal const string ActionName = "Spektrafilm test action";
        internal int Applied { get; private set; }
        internal bool Reversed { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            switch (method?.Name)
            {
                case "get_Name": return ActionName;
                case "get_CanUndo": return true;
                case "Do":
                    Applied++;
                    Reversed = false;
                    return null;
                case "Undo":
                    Reversed = true;
                    return null;
                default:
                    throw new NotSupportedException(method?.Name);
            }
        }
    }

    static void TestNativeGrain(Type grain, object window, object panel, object store, Guid variant, CheckBox toggle, bool restart)
    {
        object Read() => grain.GetMethod("Read", Flags)!.Invoke(null, new[] { window })!;
        void Apply(double impact, double granularity) => grain.GetMethod("Apply", Flags)!
            .Invoke(null, new[] { window, store, variant, 2, impact, granularity });
        bool CanRestore() => (bool)grain.GetMethod("HasApplied", Flags)!.Invoke(null, new[] { window, store, variant })!;
        void Restore() => grain.GetMethod("Restore", Flags)!.Invoke(null, new[] { window, store, variant });
        string Serialize(object state) => System.Text.Json.JsonSerializer.Serialize(state, state.GetType());
        var checkRestart = File.Exists(Path.Combine(folder, "verify-grain-restart.marker"));
        var expected = Path.Combine(folder, "grain-before-restart.json");
        var effective = (TextBlock)Field(panel, "nativeGrainText")!;
        var applyButton = (Button)Field(panel, "applyGrain")!;
        var restoreButton = (Button)Field(panel, "restoreGrain")!;
        void Refresh() => Call(panel, "RefreshNativeGrain", true);
        if (restart && checkRestart)
        {
            Check(CanRestore(), "native grain and its catalog-scoped receipt survived process termination");
            Refresh();
            Check(restoreButton.IsEnabled && effective.Text.Contains("Matches the grain applied", StringComparison.Ordinal),
                "the tool presents the restored receipt as available on restart");
            Restore();
            Check(Serialize(Read()) == File.ReadAllText(expected), "second process restored exact original native grain values");
        }

        var before = Read();
        var savedStage = Prop(Call(panel, "CaptureLook")!, "Grain")!;
        var stageType = (ComboBox)Field(panel, "grainType")!;
        var stageImpact = Field(panel, "grainImpact")!;
        var stageSize = Field(panel, "grainSize")!;
        var priorMaster = toggle.IsChecked;
        stageType.SelectedIndex = 2;
        stageImpact.GetType().GetProperty("Value", Flags)!.SetValue(stageImpact, 37d);
        stageSize.GetType().GetProperty("Value", Flags)!.SetValue(stageSize, 51d);
        Check(before.Equals(Read()), "moving the staged grain controls leaves the actual native grain unchanged");
        toggle.IsChecked = false;
        Refresh();
        Check(applyButton.IsEnabled && effective.Text.Contains("Current in Capture One", StringComparison.Ordinal),
            "effective native grain and Apply remain available with Spektrafilm switched off");
        var undo = Field(panel, "nativeUndo")!;
        var model = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.ModelEntities");
        var history = model.GetType("P1.C1.ModelEntities.UndoInterfaces.IUndoManager", true)!;
        var actionName = history.GetProperty("TryGetUndoActionName")!;
        var priorAction = actionName.GetValue(undo);
        applyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, applyButton));
        Check(CanRestore(), "native grain transaction applied with verified restore receipt");
        Check(restoreButton.IsEnabled && effective.Text.Contains("Impact 37", StringComparison.Ordinal)
            && effective.Text.Contains("Matches the grain applied", StringComparison.Ordinal), "effective grain shows the applied native values");

        Check(Equals(actionName.GetValue(undo), "Spektrafilm: Capture One Grain"),
            "one successful native Grain transaction registers its named Undo entry");
        history.GetMethod("Undo", new[] { typeof(bool) })!.Invoke(undo, new object[] { true });
        Refresh();
        Check(before.Equals(Read()) && !restoreButton.IsEnabled && Equals(actionName.GetValue(undo), priorAction),
            "one native Undo restores every grain field and the previous history item");
        history.GetMethod("Redo")!.Invoke(undo, null);
        Refresh();
        Check(restoreButton.IsEnabled && effective.Text.Contains("Impact 37", StringComparison.Ordinal), "native Redo restores the effective grain and receipt match");

        var profile = grain.Assembly.GetType("HostProfile")!;
        var native = profile.GetMethod("SelectedVariant", Flags)!.Invoke(null, new[] { window })!;
        var tuple = (System.Runtime.CompilerServices.ITuple)native;
        var settings = (Type)tuple[1]!;
        var amount = settings.GetProperty("FilmGrainAmount")!;
        amount.SetValue(tuple[0], 52f);
        Check(!CanRestore(), "external native grain change invalidates ownership");
        Refresh();
        Check(!restoreButton.IsEnabled && effective.Text.Contains("Impact 52", StringComparison.Ordinal)
            && effective.Text.Contains("Capture One Grain changed", StringComparison.Ordinal), "external grain reports current values and disables restore");
        try
        {
            Restore();
            throw new InvalidOperationException("Stale grain restore was accepted.");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
        {
            Check((float)amount.GetValue(tuple[0])! == 52f, "stale restore preserved the external grain change");
        }
        finally
        {
            amount.SetValue(tuple[0], 37f);
        }
        Refresh();
        restoreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, restoreButton));
        Check(before.Equals(Read()) && !CanRestore(), "native grain restored exact earlier values and ownership cleared");
        Check(!restoreButton.IsEnabled && Prop(stageImpact, "Value") is 37d && toggle.IsChecked == false,
            "Restore keeps the staged settings and works with the master switch off");
        Call(panel, "SetGrainControls", savedStage);
        Call(panel, "SaveLook");
        toggle.IsChecked = priorMaster;
        if (!restart && checkRestart)
        {
            File.WriteAllText(expected, Serialize(before));
            Apply(46, 62);
            Check(CanRestore(), "native grain receipt exists before the host is stopped");
        }
    }

    static async Task TestNativeGeometry(Type host, Type profile, object window, object target, object space)
    {
        var selected = profile.GetMethod("ReadSelection", Flags)!.Invoke(null, new[] { window })!;
        var nativeVariant = Prop(selected, "Variant")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.Core");
        var variant = core.GetType("P1.C1.Core.ICoreVariant", true)!;
        var rotation = variant.GetProperty("Rotation")!;
        var flip = variant.GetProperty("Flip")!;
        var keystone = variant.GetProperty("KeystoneH")!;
        var amount = variant.GetProperty("KeystoneAmount")!;
        var originalRotation = rotation.GetValue(nativeVariant);
        var originalFlip = flip.GetValue(nativeVariant)!;
        var originalKeystone = keystone.GetValue(nativeVariant);
        var originalAmount = amount.GetValue(nativeVariant);

        async Task<(int Width, int Height, byte[] Pixels)> Develop()
        {
            var snap = host.GetMethod("Capture", Flags)!.Invoke(null, new[] { window, target, space, false })!;
            var frame = await Task.Run(() => Call(snap, "RenderScaledPreview", 480, CancellationToken.None));
            Check((int)Prop(frame!, "BitDepth")! == 16, "geometry case used native 16-bit development");
            return ((int)Prop(frame!, "Width")!, (int)Prop(frame!, "Height")!, (byte[])Prop(frame!, "FloatBytes")!);
        }

        try
        {
            flip.SetValue(nativeVariant, Enum.Parse(flip.PropertyType, "None"));
            var baseline = await Develop();
            variant.GetMethod("RotateRight")!.Invoke(nativeVariant, null);
            var rotated = await Develop();
            Check(baseline.Width == rotated.Height && baseline.Height == rotated.Width,
                "native rotate right swaps the developed canvas dimensions");
            var clockwise = PixelDifference(baseline, rotated, (x, y) => (baseline.Height - 1 - y, x));
            var counterClockwise = PixelDifference(baseline, rotated, (x, y) => (y, baseline.Width - 1 - x));
            Check(Math.Min(clockwise, counterClockwise) < 0.025 && Math.Abs(clockwise - counterClockwise) > 0.04,
                "rotated canvas has the expected quarter-turn pixel mapping");

            rotation.SetValue(nativeVariant, originalRotation);
            flip.SetValue(nativeVariant, Enum.Parse(flip.PropertyType, "Horizontal"));
            var mirrored = await Develop();
            Check(mirrored.Width == baseline.Width && mirrored.Height == baseline.Height, "native flip retains canvas size");
            Check(PixelDifference(baseline, mirrored, (x, y) => (baseline.Width - 1 - x, y)) < 0.025,
                "native horizontal flip preserves the mirrored sample pattern");

            flip.SetValue(nativeVariant, Enum.Parse(flip.PropertyType, "None"));
            amount.SetValue(nativeVariant, 100f);
            keystone.SetValue(nativeVariant, 14f);
            var corrected = await Develop();
            Check(corrected.Width > 0 && corrected.Height > 0 && !corrected.Pixels.AsSpan().SequenceEqual(baseline.Pixels),
                "native keystone correction changes the developed image");
        }
        finally
        {
            rotation.SetValue(nativeVariant, originalRotation);
            flip.SetValue(nativeVariant, originalFlip);
            keystone.SetValue(nativeVariant, originalKeystone);
            amount.SetValue(nativeVariant, originalAmount);
        }

        static double PixelDifference((int Width, int Height, byte[] Pixels) source,
            (int Width, int Height, byte[] Pixels) output, Func<int, int, (int X, int Y)> transform)
        {
            double error = 0;
            int samples = 0;
            for (int y = 16; y < source.Height - 16; y += 11)
            for (int x = 16; x < source.Width - 16; x += 13)
            {
                var point = transform(x, y);
                for (int channel = 0; channel < 3; channel++)
                {
                    var a = BitConverter.ToSingle(source.Pixels, ((y * source.Width + x) * 4 + channel) * 4);
                    var b = BitConverter.ToSingle(output.Pixels, ((point.Y * output.Width + point.X) * 4 + channel) * 4);
                    error += Math.Abs(a - b);
                    samples++;
                }
            }
            return error / samples;
        }
    }

    static async Task TestLockedLook(object panel, object store, Guid variant, object exposure, CheckBox toggle)
    {
        var path = (string)Call(store, "GetPath", variant)!;
        var bytes = File.ReadAllBytes(path);
        var warning = (TextBlock)Field(panel, "saveError")!;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            panel.GetType().GetField("displayedVariant", Flags)!.SetValue(panel, null);
            Call(panel, "SyncSelection");
            Check(!toggle.IsEnabled && warning.Visibility == Visibility.Visible
                && warning.Text.Contains("retry", StringComparison.OrdinalIgnoreCase),
                "temporarily inaccessible saved look pauses editing and reports retry");
            Check(Call(store, "IsBlocked", variant) is false, "sharing violation leaves corrupt-state recovery inactive");
            var watchdog = (Forms.Timer)panel.GetType().Assembly.GetType("StartupHook", true)!.GetField("timer", Flags)!.GetValue(null)!;
            Check(watchdog.Interval == 1000, "a real sharing violation wakes the fast persistence watchdog immediately");
            using var support = JsonDocument.Parse((string)Call(panel, "BuildSupportDetails")!);
            Check(support.RootElement.GetProperty("LookStorage").GetString() == "ReadUnavailable"
                && support.RootElement.GetProperty("NativeExportRoute").GetString() == "LookUnavailable",
                "redacted support details explain an unavailable look without exporting its filename");
        }

        var until = DateTime.UtcNow.AddSeconds(5);
        while (!toggle.IsEnabled && DateTime.UtcNow < until)
            await Task.Delay(100);
        Check(toggle.IsEnabled && toggle.IsChecked == true && Math.Abs((double)Prop(exposure, "Value")! - 1.2) < 1e-9,
            "host watchdog restored the original look after the sharing violation without user input");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), "automatic read retry preserved saved bytes");
    }

    static void TestExportBinding(FieldInfo hookField, object attached, object hosted, object panel, object window,
        object exposure, CheckBox toggle)
    {
        var normal = Field(attached, "normal")!;
        var previous = Field(attached, "previous")!;
        var normalCommand = Field(normal, "command")!;
        var previousCommand = Field(previous, "command")!;
        var callbacks = (FieldInfo)normal.GetType().GetField("Callbacks", Flags)!.GetValue(null)!;
        var warning = (TextBlock)Field(panel, "exportBridgeStatus")!;
        void Bind() => hookField.DeclaringType!.GetMethod("AttachExport", Flags)!.Invoke(null, new[] { window, hosted });

        ((IDisposable)attached).Dispose();
        hookField.SetValue(null, null);
        var normalBefore = callbacks.GetValue(normalCommand);
        var previousBefore = callbacks.GetValue(previousCommand);
        try
        {
            foreach (var (command, description) in new[] { (normalCommand, "first"), (previousCommand, "second") })
            {
                var original = callbacks.GetValue(command);
                callbacks.SetValue(command, null);
                try
                {
                    Bind();
                    Check(hookField.GetValue(null) is null && warning.Visibility == Visibility.Visible
                        && Prop(panel, "ExportBridgeStatus")!.ToString() == "Unavailable",
                        "missing " + description + " callback reaches the persistent startup warning");
                    Call(hosted, "ReportLaunchHealth");
                    Check(ReadLaunchHealth().GetProperty("Export").GetString() == "Unavailable",
                        "missing " + description + " callback also reaches the typed launch report");
                    Check(Equals(callbacks.GetValue(normalCommand), command == normalCommand ? null : normalBefore)
                        && Equals(callbacks.GetValue(previousCommand), command == previousCommand ? null : previousBefore),
                        "native delegate lists preserved after missing " + description + " Export binding");
                    toggle.IsChecked = true;
                    exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, 0.4);
                    Check(warning.Visibility == Visibility.Visible && Prop(panel, "IsSpektraExportActive") is true,
                        "attach warning survives an edit and direct Export stays available");
                    var details = Prop(panel, "ExportBridgeStatus")!;
                    const string privateText = @"C:\Fixture User\Do not share my raw.CR3";
                    Call(panel, "SetExportBridgeStatus", details, privateText);
                    var report = (string)Call(panel, "BuildSupportDetails")!;
                    using var support = JsonDocument.Parse(report);
                    Check(support.RootElement.GetProperty("NativeExportRoute").GetString() == "UsePanel"
                        && support.RootElement.GetProperty("NativeExportBridge").GetString() == "Unavailable"
                        && !report.Contains("Fixture User", StringComparison.Ordinal),
                        "redacted support details identify the alternate Export route without including native error paths");
                }
                finally { callbacks.SetValue(command, original); }
            }
        }
        finally { Bind(); }

        var current = hookField.GetValue(null) ?? throw new InvalidOperationException("Native Export did not rebind.");
        Check(warning.Visibility == Visibility.Collapsed && Prop(panel, "ExportBridgeStatus")!.ToString() == "Connected",
            "successful native rebind clears its own warning");
        var connected = (string)Call(panel, "BuildSupportDetails")!;
        using (var report = JsonDocument.Parse(connected))
        {
            var names = report.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var presentation = report.RootElement.GetProperty("ViewerPresentation");
            var profile = presentation.GetProperty("WindowsAssociatedIccSha256").GetString();
            Check(names.SetEquals(new[] { "SchemaVersion", "Build", "CaptureOne", "RendererProtocol", "RendererAssets",
                "Controls", "Viewer", "ViewerPresentation", "NativeExportBridge", "NativeExportRoute", "LookStorage", "DroppedLogEntries" })
                && report.RootElement.GetProperty("NativeExportRoute").GetString() == "Spektrafilm",
                "support details contain only allow-listed versions and states after native Export reconnects");
            Check(report.RootElement.GetProperty("SchemaVersion").GetInt32() == 2
                && presentation.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(new[] { "Encoding", "RecipeProofing", "WindowsAssociatedIccSha256" })
                && presentation.GetProperty("Encoding").GetString() == "sRGB"
                && (profile is null || (profile.Length == 64 && profile.All(Uri.IsHexDigit))),
                "Viewer presentation support details contain only encoding, proofing and an optional ICC fingerprint");
        }
        File.WriteAllText(Path.Combine(folder, "support-details.json"), connected);
        toggle.IsChecked = false;
        try
        {
            using var disabled = JsonDocument.Parse((string)Call(panel, "BuildSupportDetails")!);
            Check(disabled.RootElement.GetProperty("NativeExportRoute").GetString() == "CaptureOne",
                "support details identify ordinary Capture One export when the master switch is off");
        }
        finally { toggle.IsChecked = true; }
        Window? owner = null;
        try
        {
            Call(current, "PrepareDialog", false);
            owner = (Window)Prop(current, "Dialog")!;
            Check(ReferenceEquals(Prop(window, "ExportDialogWindow"), owner), "native Export confirmation bound and dialog published");
        }
        finally
        {
            try { Call(current, "ReleaseDialog"); }
            finally { owner?.Close(); }
        }
        Check(Prop(window, "ExportDialogWindow") is null, "native Export dialog registration released");

        Call(current, "PrepareDialog", false);
        var other = (Window)Prop(current, "Dialog")!;
        var slot = window.GetType().GetProperty("ExportDialogWindow", Flags)!;
        try
        {
            Call(current, "ReleaseDialog");
            slot.SetValue(window, other);
            try
            {
                Call(current, "PrepareDialog", false);
                throw new InvalidOperationException("Spektrafilm replaced an existing native Export window.");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            {
                Call(current, "ReleaseDialog");
                Check(ReferenceEquals(slot.GetValue(window), other), "foreign native Export window remains registered after rejected open and cleanup");
            }
        }
        finally
        {
            if (ReferenceEquals(slot.GetValue(window), other)) slot.SetValue(window, null);
            other.Close();
        }
    }

    static async Task TestNativeExportCommands(FieldInfo hookField, object hosted, object panel, object spatial, object window)
    {
        var original = (IDisposable)hookField.GetValue(null)!;
        original.Dispose();
        hookField.SetValue(null, null);
        var routed = 0;
        var destination = Path.Combine(folder, "native-confirmation-export.tif");
        var previousDestination = Path.Combine(folder, "native-previous-export.png");
        Task<bool>? lastExport = null;
        object? bridge = null;
        try
        {
            Task<bool> Start()
            {
                var pick = ++routed;
                var dialog = (Window?)Prop(bridge!, "Dialog");
                lastExport = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, dialog,
                    (Func<SaveFileDialog, Window?, string?>)((_, _) => pick switch { 1 => null, 2 => destination, _ => previousDestination }))!;
                return lastExport;
            }

            bridge = Activator.CreateInstance(original.GetType(), Flags, null,
                new object[] { window, (Func<bool>)(() => Prop(panel, "IsSpektraExportActive") is true), (Func<Task<bool>>)Start,
                    (Action<string>)(message => Call(panel, "SetExportBridgeStatus", Prop(panel, "ExportBridgeStatus")!.GetType()
                        .GetField("Unavailable")!.GetValue(null), message)),
                    (Func<Guid, bool>)(variant => Call(hosted, "WouldLoseSavedLook", variant) is true) }, null)!;
            var destinations = ((Delegate)Field(bridge, "destinations")!).DynamicInvoke()!;
            var enabled = ((IEnumerable)Prop(destinations, "EnabledDestinations")!).Cast<object>().ToArray();
            Check(enabled.Length == 1 && Equals(Prop(enabled[0], "Name"), Prop(Prop(destinations, "SelectedDestination")!, "Name")),
                "native command fixture has one enabled and selected recipe");

            var factory = Call(bridge, "PrepareDialog", false)!;
            var confirmation = (ICommand)Prop(Prop(factory, "DialogVM")!, "ExportCommand")!;
            var owner = (Window)Prop(bridge, "Dialog")!;
            Exception? dialogError = null;
            using var timeLimit = new Forms.Timer { Interval = 60_000 };
            timeLimit.Tick += (_, _) =>
            {
                timeLimit.Stop();
                dialogError = new TimeoutException("Native Export confirmation did not close the dialog.");
                owner.Close();
            };
            owner.Loaded += (_, _) =>
            {
                _ = owner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Check(confirmation.CanExecute(null) && owner.IsVisible, "real native Export confirmation is available in the visible dialog");
                        confirmation.Execute(null);
                        Check(routed == 1 && lastExport is { IsCompletedSuccessfully: true, Result: false } && owner.IsVisible,
                            "canceling Save leaves native Export dialog open");
                        confirmation.Execute(null);
                    }
                    catch (Exception error)
                    {
                        dialogError = error;
                        owner.Close();
                    }
                }), DispatcherPriority.ApplicationIdle);
            };
            try
            {
                timeLimit.Start();
                Call(factory, "Show", window);
                if (dialogError is not null)
                    throw dialogError;
                Check(routed == 2 && lastExport is { IsCompletedSuccessfully: true, Result: true } && !owner.IsVisible,
                    "successful Export confirmation closes the real native dialog");
                Check(File.Exists(destination), "native confirmation produced a Spektrafilm file");
            }
            finally
            {
                timeLimit.Stop();
                try { Call(bridge, "ReleaseDialog"); }
                finally { if (owner.IsVisible) owner.Close(); }
            }

            var previous = Field(Field(bridge, "previous")!, "command")!;
            if (previous is ICommand command)
            {
                Check(command.CanExecute(null), "native Export with Previous Settings command is available");
                command.Execute(null);
            }
            else
            {
                var execute = previous.GetType().GetMethod("Execute", Flags, null, new[] { typeof(bool) }, null)
                    ?? throw new MissingMethodException(previous.GetType().FullName, "Execute(Boolean)");
                execute.Invoke(previous, new object[] { false });
            }
            Check(routed == 3 && lastExport is not null && await lastExport,
                "native Export with Previous Settings routed through the same high-depth pipeline");
            foreach (var (file, format, query) in new[]
            {
                (destination, PixelFormats.Rgb48, "/ifd/xmp"),
                (previousDestination, PixelFormats.Rgba64, "/xmp"),
            })
            {
                using var output = File.OpenRead(file);
                var decoded = BitmapDecoder.Create(output, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Check(decoded.Format == format, "native command output reopens as 16-bit " + format);
                var metadata = ((BitmapMetadata)decoded.Metadata).GetQuery(query) as BitmapMetadata;
                Check(metadata?.GetQuery("/tiff:Orientation") is "1", "native command output includes the normalized XMP orientation");
            }

            foreach (var (invalidCommand, name) in new (ICommand, string)[]
            {
                (new FixtureCommand(new Func<bool>(new MissingClosureField().Execute)), "missing closure field"),
                (new FixtureCommand(new Func<bool>(new WrongClosureField().Execute)), "wrong closure field type"),
            })
            {
                try
                {
                    Call(bridge, "BindConfirmation", invalidCommand, false);
                    throw new InvalidOperationException("The confirmation adapter accepted an invalid command.");
                }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
                {
                    Check(Prop(panel, "ExportBridgeStatus")?.ToString() == "Unavailable"
                        && Prop(panel, "IsSpektraExportActive") is true
                        && ((TextBlock)Field(panel, "exportBridgeStatus")!).Visibility == Visibility.Visible,
                        name + " reports unavailable native Export while keeping direct export active");
                    Check(routed == 3 && Prop(window, "ExportDialogWindow") is null,
                        name + " did not route a job or retain a dialog owner");
                }
            }
        }
        finally
        {
            try { (bridge as IDisposable)?.Dispose(); }
            finally { hookField.DeclaringType!.GetMethod("AttachExport", Flags)!.Invoke(null, new[] { window, hosted }); }
        }
        Check(Prop(panel, "ExportBridgeStatus")?.ToString() == "Connected", "a clean reattach recovers from a confirmation-adapter failure");
    }

    private class FixtureCommandBase(Delegate action) : ICommand
    {
        protected readonly Delegate m_actionToExecute = action;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? _) => true;
        public void Execute(object? _) => m_actionToExecute.DynamicInvoke();
    }

    private sealed class FixtureCommand(Delegate action) : FixtureCommandBase(action);

    private sealed class MissingClosureField
    {
        public bool Execute() => false;
    }

    private sealed class WrongClosureField
    {
        public readonly string argExportAction = "unexpected value";
        public bool Execute() => false;
    }

    static async Task TestRecipeExport(object panel, object spatial, object recipe, Guid variant, CheckBox toggle)
    {
        var output = Path.Combine(folder, "recipe-export.tif");
        Task<bool> Start(Func<SaveFileDialog, Window?, string?> choose) =>
            (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, null, choose)!;
        var document = Field(panel, "document")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "P1.C1.Core");
        var collectionsType = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
        var collection = collectionsType.GetProperty("PrimarySelectedCollection")!.GetValue(document)!;
        var selected = collectionsType.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
        var selectionType = core.GetType("P1.C1.Core.Selection.ISelection", true)!;
        var selectedVariants = selectionType.GetProperty("SelectedVariants")!.GetValue(selected)!;
        var variantSelection = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!;
        var simpleCollection = core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!;
        var hostVariant = core.GetType("P1.C1.Core.ICoreVariant", true)!;
        var all = ((IEnumerable)simpleCollection.GetMethod("GetAllFilteredAndVisibleVariants")!.Invoke(collection, null)!).Cast<object>().ToArray();
        Guid Id(object item) => (Guid)hostVariant.GetProperty("UUID")!.GetValue(item)!;
        var originalVariant = all.Single(item => Id(item) == variant);
        var disabledVariant = all.First(item => Id(item) != variant);
        void Select(object item)
        {
            variantSelection.GetMethod("SelectAndSetAsPrimary")!.Invoke(selectedVariants, new[] { item, (object)false });
            Call(panel, "SyncSelection");
            if (!Equals(Field(panel, "displayedVariant"), Id(item)))
                throw new InvalidOperationException("Capture One did not activate the requested fixture variant.");
        }
        Select(disabledVariant);
        toggle.IsChecked = false;
        Select(originalVariant);
        Check(toggle.IsChecked == true, "Capture One variants have distinct persisted enable states");
        var result = await Start((picker, _) =>
        {
            Check(picker.Title.Contains($"{Prop(recipe, "Width")} × {Prop(recipe, "Height")}", StringComparison.Ordinal),
                "Save dialog identifies the captured recipe dimensions");
            var duplicate = Start((_, _) => throw new InvalidOperationException("Opened a second save dialog."));
            Check(duplicate.IsCompletedSuccessfully && !duplicate.Result, "duplicate Export blocked while choosing a destination");
            Select(disabledVariant);
            Check(toggle.IsChecked == false, "real Capture One selection changed to a disabled variant during destination selection");
            return output;
        });
        Check(result && File.Exists(output), "captured recipe export completed while a different disabled variant was selected");
        using (var data = File.OpenRead(output))
        {
            var frame = BitmapDecoder.Create(data, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];
            Check(frame.Format == PixelFormats.Rgb48, "recipe export saved RGB48 TIFF");
            if (Prop(recipe, "ColorProfilePath") is string profilePath && File.Exists(profilePath))
            {
                Check(frame.ColorContexts is { Count: 1 }, "recipe export embedded an RGB ICC profile");
                using var embedded = frame.ColorContexts![0].OpenProfileStream();
                using var selectedProfile = File.OpenRead(profilePath);
                Check(SHA256.HashData(embedded).SequenceEqual(SHA256.HashData(selectedProfile)),
                    "16-bit native recipe export embedded the exact selected Capture One ICC bytes");
            }
            Check(frame.PixelWidth == (int)Prop(recipe, "Width")! && frame.PixelHeight == (int)Prop(recipe, "Height")!,
                "recipe export obeyed Capture One output dimensions");
            var resolution = (double)Prop(recipe, "Resolution")!;
            var dpi = (string)Prop(recipe, "ResolutionUnit")! switch
            {
                "Inch" => resolution,
                "Centimeter" => 2.54 * resolution,
                "Millimeter" => 25.4 * resolution,
                _ => throw new InvalidOperationException("Unknown fixture recipe resolution unit."),
            };
            Check(Math.Abs(frame.DpiX - dpi) < 0.03 && Math.Abs(frame.DpiY - dpi) < 0.03,
                "recipe export recorded Capture One print resolution on both axes");
        }
        var original = File.ReadAllBytes(output);
        Select(originalVariant);
        var canceled = Start((_, _) => output);
        Call(Field(spatial, "exportWork")!, "Cancel");
        Check(!await canceled && File.ReadAllBytes(output).AsSpan().SequenceEqual(original),
            "explicit cancellation preserved the previous recipe export");
    }

    static async Task TestDetach(Type hook, object hosted, object panel, object spatial, object window, CheckBox toggle)
    {
        var output = Path.Combine(folder, "recipe-export.tif");
        var existing = File.ReadAllBytes(output);
        var export = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, null,
            (Func<SaveFileDialog, Window?, string?>)((_, _) => output))!;
        var token = (CancellationToken)Prop(Field(spatial, "exportWork")!, "Token")!;
        var closing = new Forms.FormClosingEventArgs(Forms.CloseReason.UserClosing, cancel: true);
        hook.GetMethod("OnFormClosing", Flags)!.Invoke(null, new[] { window, closing });
        var hostedField = hook.GetField("hostedTool", Flags)!;
        Check(ReferenceEquals(hostedField.GetValue(null), hosted) && !token.IsCancellationRequested,
            "canceled form close keeps the panel attached and the export live");

        toggle.IsChecked = false;
        Call(panel, "ScheduleSelectionSync");
        var operation = (DispatcherOperation)Field(panel, "selectionSync")!;
        Check(operation.Status == DispatcherOperationStatus.Pending, "real dispatcher held a pending selection callback");
        // The fixture invokes the production detach path and keeps the host message loop
        // alive to observe cancellation and the queued callback deterministically.
        var watchdog = (Forms.Timer)hook.GetField("timer", Flags)!.GetValue(null)!;
        watchdog.Stop();
        hook.GetMethod("Detach", Flags)!.Invoke(null, new object[] { false });
        Check(hostedField.GetValue(null) is null && token.IsCancellationRequested, "detach cancels the active export and releases the host owner");
        Check(operation.Status == DispatcherOperationStatus.Aborted && Field(panel, "selectionSync") is null,
            "detach aborts the late dispatcher callback");
        Check(!await export.WaitAsync(TimeSpan.FromSeconds(10)) && File.ReadAllBytes(output).AsSpan().SequenceEqual(existing),
            "detached export exits without replacing the prior output");
        var canceled = Call(Field(spatial, "exportJobs")!, "ReadLatest")!;
        Check(Prop(canceled, "Stage")!.ToString() == "Canceled" && Equals(Prop(canceled, "Destination"), output),
            "detached job records a durable cancellation for its accepted filename");
    }
}
