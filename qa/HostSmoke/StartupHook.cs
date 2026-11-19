using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

// A separate, opt-in test startup hook. Never distributed in the product package.
public static class StartupHook
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
            var panelAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "NativePanel");
            var hook = panelAssembly?.GetType("StartupHook");
            var hosted = hook?.GetField("hostedTool", Flags)?.GetValue(null);
            if (hosted is null || Field(hosted, "panel") is not { } panel || Prop(panel, "StocksReady") is not true)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Panel did not attach with loaded stocks.");
                return;
            }
            timer!.Stop();
            var window = Field(hosted, "mainWindow")!;
            TestHostLookups(panelAssembly!, panel, (Forms.Form)window);
            var hookField = hook!.GetField("exportHook", Flags)!;
            var exportHook = hookField.GetValue(null);
            Check(exportHook is not null, "native Export hook attached");
            var catalog = Environment.GetEnvironmentVariable("SPEKTRAFILM_CATALOG")!;
            Check(Path.GetFullPath(catalog).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "catalog is disposable and contained in test folder");
            // Isolate saved looks; this does not change any production storage configuration.
            var storeType = panelAssembly!.GetType("VariantLookStore")!;
            var store = Activator.CreateInstance(storeType, Flags, null, new object[] { Path.Combine(folder, "Variant looks"), catalog }, null)!;
            panel.GetType().GetField("looks", Flags)!.SetValue(panel, store);
            panel.GetType().GetField("displayedVariant", Flags)!.SetValue(panel, null);
            Call(panel, "SyncSelection");
            Check(Field(panel, "displayedVariant") is Guid, "selected image bound to panel");
            var variant = (Guid)Field(panel, "displayedVariant")!;
            var spatial = Field(panel, "opticalPreview")!;
            var exposure = Field(panel, "exposure")!;
            var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
            await TestLaunchHealth(hosted, spatial, toggle);
            var restart = File.Exists(Path.Combine(folder, "restart.expected"));
            if (restart)
            {
                Check(Math.Abs((double)Prop(exposure, "Value")! - 1.2) < 1e-9, "exposure restored after process restart");
                Check(toggle.IsChecked == false, "disabled master switch restored without hiding tool");
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
            await TestLockedLook(panel, store, variant, exposure, toggle);
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
            var grain = panelAssembly.GetType("NativeGrain")!;
            TestNativeUndoCapability(Field(panel, "document")!);
            TestNativeGrain(grain, window, store, variant, restart);
            var recipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window });
            File.AppendAllText(Path.Combine(folder,"host-smoke.log"), "RECIPE " + recipe + Environment.NewLine);
            await TestRecipeExport(panel, spatial, recipe!, variant, toggle);
            await TestNativeExportCommands(hookField, hosted, panel, spatial, window);
            await TestFullDirectExport(spatial);
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

            Call(spatial, "SetEnabled", false);
            Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", existing);
            Call(spatial, "SetEnabled", true);
            until = DateTime.UtcNow.AddSeconds(20);
            Process? worker = null;
            while (worker is null && DateTime.UtcNow < until)
            {
                if (Field(spatial, "renderingFinal") is true && Field(spatial, "renderer") is { } candidate)
                    worker = Field(candidate, "process") as Process;
                if (worker is null) await Task.Delay(10);
            }
            if (worker is null || worker.HasExited || !string.Equals(worker.MainModule?.FileName,
                Path.Combine(existing, "renderer", "SpektraStillRenderer.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Could not identify the current disposable Viewer worker.");
            worker.Kill();

            until = DateTime.UtcNow.AddSeconds(15);
            while (Prop(spatial, "Health")!.ToString() != "ProcessingError" && DateTime.UtcNow < until)
                await Task.Delay(100);
            Call(hosted, "ReportLaunchHealth");
            report = ReadLaunchHealth();
            Check(report.GetProperty("Phase").GetString() == "Ready" && report.GetProperty("Export").GetString() == "Connected"
                && report.GetProperty("Viewer").GetString() == "ProcessingError",
                "terminating the actual in-flight Viewer process reports a rendering failure without losing Export or panel readiness");
        }
        finally
        {
            Call(spatial, "SetEnabled", false);
            Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", existing);
            toggle.IsChecked = originalEnabled;
            Call(spatial, "SetEnabled", originalEnabled == true);
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
        var exported = await (Task<bool>)Call(spatial, "ExportWithDestinationAsync", false, null,
            (Func<SaveFileDialog, Window?, string?>)((_, _) => output))!;
        Check(exported && File.Exists(output), "direct export processed the full-sized RAW source");
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
                revert.Invoke(undo, new object[] { false });
            }
        }
        Check(SameLook(Saved()) && originalGrain.Equals(NativeGrain()), "every-mode preset probes restore the original state and native grain");

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

    static void TestNativeGrain(Type grain, object window, object store, Guid variant, bool restart)
    {
        object Read() => grain.GetMethod("Read", Flags)!.Invoke(null, new[] { window })!;
        void Apply(double impact, double granularity) => grain.GetMethod("Apply", Flags)!
            .Invoke(null, new[] { window, store, variant, 2, impact, granularity });
        bool CanRestore() => (bool)grain.GetMethod("HasApplied", Flags)!.Invoke(null, new[] { window, store, variant })!;
        void Restore() => grain.GetMethod("Restore", Flags)!.Invoke(null, new[] { window, store, variant });
        string Serialize(object state) => System.Text.Json.JsonSerializer.Serialize(state, state.GetType());
        var checkRestart = File.Exists(Path.Combine(folder, "verify-grain-restart.marker"));
        var expected = Path.Combine(folder, "grain-before-restart.json");
        if (restart && checkRestart)
        {
            Check(CanRestore(), "native grain and its catalog-scoped receipt survived process termination");
            Restore();
            Check(Serialize(Read()) == File.ReadAllText(expected), "second process restored exact original native grain values");
        }

        var before = Read();
        Apply(37, 51);
        Check(CanRestore(), "native grain transaction applied with verified restore receipt");
        var profile = grain.Assembly.GetType("HostProfile")!;
        var native = profile.GetMethod("SelectedVariant", Flags)!.Invoke(null, new[] { window })!;
        var tuple = (System.Runtime.CompilerServices.ITuple)native;
        var settings = (Type)tuple[1]!;
        var amount = settings.GetProperty("FilmGrainAmount")!;
        amount.SetValue(tuple[0], 52f);
        Check(!CanRestore(), "external native grain change invalidates ownership");
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
        Restore();
        Check(before.Equals(Read()) && !CanRestore(), "native grain restored exact earlier values and ownership cleared");
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
                }
                finally { callbacks.SetValue(command, original); }
            }
        }
        finally { Bind(); }

        var current = hookField.GetValue(null) ?? throw new InvalidOperationException("Native Export did not rebind.");
        Check(warning.Visibility == Visibility.Collapsed && Prop(panel, "ExportBridgeStatus")!.ToString() == "Connected",
            "successful native rebind clears its own warning");
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
                        .GetField("Unavailable")!.GetValue(null), message)) }, null)!;
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
            var frame = BitmapDecoder.Create(data, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Check(frame.Format == PixelFormats.Rgb48, "recipe export saved RGB48 TIFF");
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
    }
}
