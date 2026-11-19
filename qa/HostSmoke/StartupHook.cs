using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            var hookField = hook!.GetField("exportHook", Flags)!;
            var exportHook = hookField.GetValue(null);
            Check(exportHook is not null, "native Export hook attached");
            var catalog = Environment.GetEnvironmentVariable("SPEKTRAFILM_CATALOG")!;
            Check(Path.GetFullPath(catalog).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "catalog is disposable and contained in test folder");
            TestExportBinding(hookField, exportHook!, window);
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
            var restart = File.Exists(Path.Combine(folder, "restart.expected"));
            if (restart)
            {
                Check(Math.Abs((double)Prop(exposure, "Value")! - 1.2) < 1e-9, "exposure restored after process restart");
                Check(toggle.IsChecked == false, "disabled master switch restored without hiding tool");
            }
            toggle.IsChecked = true;
            foreach (var value in new[] { 0.3, 0.7, 1.2 })
            {
                exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, value);
                var saved = Call(store, "Read", variant)!;
                Check(Math.Abs((double)Prop(Prop(saved, "Look")!, "Exposure")! - value) < 1e-9, "slider movement durably saved: " + value);
            }
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
                var bitmap = (BitmapSource)spatial.GetType().GetMethod("DecodeExport",Flags)!.Invoke(null,new object[] {
                    lastFrame!,pixels,1d,space,"srgb",PixelFormats.Rgb48,new System.Drawing.Rectangle(0,0,width,height),CancellationToken.None })!;
                var output = Path.Combine(folder,"host-export.tif");
                panelAssembly.GetType("SpektraExport")!.GetMethod("Save",Flags)!.Invoke(null,new object[] {
                    output,bitmap,CancellationToken.None,Prop(target!,"SourcePath")! });
                using var data=File.OpenRead(output);
                var decoded=BitmapDecoder.Create(data,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0];
                Check(decoded.Format == PixelFormats.Rgb48,"actual developed and processed TIFF retains 16 bits per RGB channel");
                Check(decoded.PixelWidth == width && decoded.PixelHeight == height,"actual TIFF keeps intended image dimensions");
            }
            var grain = panelAssembly.GetType("NativeGrain")!;
            var before = grain.GetMethod("Read",Flags)!.Invoke(null,new[] {window})!;
            grain.GetMethod("Apply",Flags)!.Invoke(null,new object[] {window,variant,2,37d,51d});
            Check((bool)grain.GetMethod("HasApplied",Flags)!.Invoke(null,new object[] {window,variant})!,"native grain transaction applied with verified restore receipt");
            grain.GetMethod("Restore",Flags)!.Invoke(null,new object[] {window,variant});
            Check(before.Equals(grain.GetMethod("Read",Flags)!.Invoke(null,new[] {window})),"native grain restored exact prior values");
            var recipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window });
            File.AppendAllText(Path.Combine(folder,"host-smoke.log"), "RECIPE " + recipe + Environment.NewLine);
            await TestRecipeExport(spatial, recipe!);
            toggle.IsChecked = false;
            Call(panel, "FlushLooks");
            Check(Prop(Call(store, "Read", variant)!, "Enabled") is false, "master switch persisted after all tests");
            File.WriteAllText(Path.Combine(folder, restart ? "restart.passed" : "first.passed"), $"PASS {checks} host assertions");
            if (!restart) File.WriteAllText(Path.Combine(folder, "restart.expected"), "1.2 disabled");
        }
        catch (Exception error)
        {
            timer?.Stop();
            File.WriteAllText(Path.Combine(folder, "failed.txt"), error.ToString());
        }
    }

    static void TestExportBinding(FieldInfo hookField, object attached, object window)
    {
        var hookType = attached.GetType();
        var normal = Field(attached, "normal")!;
        var previous = Field(attached, "previous")!;
        var normalCommand = Field(normal, "command")!;
        var previousCommand = Field(previous, "command")!;
        var callbacks = (FieldInfo)normal.GetType().GetField("Callbacks", Flags)!.GetValue(null)!;
        var active = Field(attached, "isActive");
        var export = Field(attached, "export");
        object Bind() => Activator.CreateInstance(hookType, Flags, null, new[] { window, active, export }, null)!;

        ((IDisposable)attached).Dispose();
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
                    using var unexpected = (IDisposable)Bind();
                    throw new InvalidOperationException("Binding unexpectedly succeeded with a missing " + description + " callback.");
                }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
                {
                    Check(Equals(callbacks.GetValue(normalCommand), command == normalCommand ? null : normalBefore)
                        && Equals(callbacks.GetValue(previousCommand), command == previousCommand ? null : previousBefore),
                        "native delegate lists preserved after missing " + description + " Export binding");
                }
                finally { callbacks.SetValue(command, original); }
            }
        }
        finally { hookField.SetValue(null, Bind()); }

        var current = hookField.GetValue(null)!;
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

    static async Task TestRecipeExport(object spatial, object recipe)
    {
        var output = Path.Combine(folder, "recipe-export.tif");
        Task<bool> Start(Func<SaveFileDialog, Window?, string?> choose) =>
            (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, null, choose)!;
        Call(spatial, "SetEnabled", true);
        var result = await Start((_, _) =>
        {
            var duplicate = Start((_, _) => throw new InvalidOperationException("Opened a second save dialog."));
            Check(duplicate.IsCompletedSuccessfully && !duplicate.Result, "duplicate Export blocked while choosing a destination");
            Call(spatial, "SetEnabled", false);
            return output;
        });
        Check(result && File.Exists(output), "recipe export completed after enable state changed during destination selection");
        using (var data = File.OpenRead(output))
        {
            var frame = BitmapDecoder.Create(data, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            Check(frame.Format == PixelFormats.Rgb48, "recipe export saved RGB48 TIFF");
            Check(frame.PixelWidth == (int)Prop(recipe, "Width")! && frame.PixelHeight == (int)Prop(recipe, "Height")!,
                "recipe export obeyed Capture One output dimensions");
        }
        var original = File.ReadAllBytes(output);
        Call(spatial, "SetEnabled", true);
        var canceled = Start((_, _) => output);
        Call(Field(spatial, "exportWork")!, "Cancel");
        Check(!await canceled && File.ReadAllBytes(output).AsSpan().SequenceEqual(original),
            "explicit cancellation preserved the previous recipe export");
        Call(spatial, "SetEnabled", false);
    }
}
