using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
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
            TestExportBinding(hookField, exportHook!, hosted, panel, window, exposure, toggle);
            toggle.IsChecked = true;
            foreach (var value in new[] { 0.3, 0.7, 1.2 })
            {
                exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, value);
                var saved = Call(store, "Read", variant)!;
                Check(Math.Abs((double)Prop(Prop(saved, "Look")!, "Exposure")! - value) < 1e-9, "slider movement durably saved: " + value);
            }
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
            var grain = panelAssembly.GetType("NativeGrain")!;
            var before = grain.GetMethod("Read",Flags)!.Invoke(null,new[] {window})!;
            grain.GetMethod("Apply",Flags)!.Invoke(null,new object[] {window,variant,2,37d,51d});
            Check((bool)grain.GetMethod("HasApplied",Flags)!.Invoke(null,new object[] {window,variant})!,"native grain transaction applied with verified restore receipt");
            grain.GetMethod("Restore",Flags)!.Invoke(null,new object[] {window,variant});
            Check(before.Equals(grain.GetMethod("Read",Flags)!.Invoke(null,new[] {window})),"native grain restored exact prior values");
            var recipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window });
            File.AppendAllText(Path.Combine(folder,"host-smoke.log"), "RECIPE " + recipe + Environment.NewLine);
            await TestRecipeExport(panel, spatial, recipe!, variant, toggle);
            await TestNativeExportCommands(hookField, hosted, panel, spatial, window);
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
        var result = await Start((_, _) =>
        {
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
