using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    }

    private static async Task TestNativeEffectExport(FieldInfo hookField, object hosted, object panel, object spatial, object window,
        string caseName, bool compareGrain = false, bool compareClone = false)
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
        Task<bool> Route()
        {
            routeCount++;
            var owner = (Window?)Prop(bridge!, "Dialog");
            job = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, owner,
                (Func<SaveFileDialog, Window?, string?>)((_, _) => destination))!;
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
                    (Func<Task<bool>>)Route, (Action<string>)(reason => throw new InvalidOperationException(reason)) }, null)!;
            var normal = Field(Field(bridge, "normal")!, "command")!;
            var previous = Field(Field(bridge, "previous")!, "command")!;
            toggle.IsChecked = true;
            Value(1, 1.15);
            Value(2, 1.25);
            Value(3, 0);
            Value(4, 0);
            Diffusion("off");
            Value(0, 0);
            var unprocessed = await Render("no-film-reference", ".tif", false);
            Value(0, 100);
            var filmOnly = await Render("film-only", ".tif", false);
            Diffusion("pro_mist");
            var filmAndDiffusion = await Render("film-and-diffusion", ".tif", false);
            Value(3, 1.5);
            Value(4, .65);
            var fullEffects = await Render("film-diffusion-halation-scatter", ".tif", false);
            var previousWithEffects = await Render("previous-with-effects", ".png", true);

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
            if (compareGrain) await CheckReadOnlyDestination();
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
            Check(clicked && routeCount == (compareGrain ? 13 : 5) && Prop(bridge, "Dialog") is null,
                "the master switch delegates to the ordinary Capture One Export dialog without a Spektrafilm job");
            ((IDisposable)bridge).Dispose();
            ((IDisposable)bridge).Dispose();
            Check(Invocations(normalCommand).SequenceEqual(normalInvocations) && Invocations(previousCommand).SequenceEqual(previousInvocations),
                "repeated detach restores the original native callback arrays without duplicate commands");
            File.WriteAllText(Path.Combine(folder, caseName + "-native-effect-export.json"), JsonSerializer.Serialize(new
            {
                width = expectedWidth, height = expectedHeight, iccSha256 = iccSha, tiffCompression = compression.ToString(),
                filmDifference, opticsDifference, halationScatterDifference, commandDifference, grain,
                nativeExportCallbacks = routeCount, masterOffDelegated = clicked, timings,
            }, new JsonSerializerOptions { WriteIndented = true }));

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
                        if (job is not { IsCompletedSuccessfully: true } || !confirm.CanExecute(null)) return false;
                        Check(!job.Result && routeCount == before + 2 && owner.Title == originalTitle + " · Export failed",
                            "the original Capture One Export window stays usable and shows an accepted-job failure in its title");
                        CheckFailedReceipt();
                        destination = retryPath;
                        confirm.Execute(null);
                        Check(owner.Title == originalTitle, "accepting a fresh destination clears the failure indication in the same window");
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
                        Check(Prop(opticalGrain.SelectedItem, "Id") is "off",
                            "the independent cloned variant starts with its own default Spektrafilm optical grain");
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

            async Task<BitmapSource> Render(string kind, string extension, bool usePrevious, bool closeAfterAcceptance = false)
            {
                var before = routeCount;
                job = null;
                destination = Path.Combine(folder, caseName + "-" + kind + extension);
                if (usePrevious) ExecuteNativeExportCommand(previous);
                else
                {
                    ShowNativeDialog(() =>
                    {
                        if (Prop(bridge, "Dialog") is not Window { IsVisible: true } owner || owner.DataContext is not { } model)
                            return false;
                        var confirm = Prop(model, "ExportCommand") as ICommand
                            ?? throw new InvalidOperationException("The native Export window does not expose its confirmation command.");
                        if (!confirm.CanExecute(null)) return false;
                        confirm.Execute(null);
                        if (closeAfterAcceptance)
                        {
                            var accepted = Call(Field(spatial, "exportJobs")!, "ReadLatest");
                            Check(job is { IsCompleted: false } && Field(spatial, "exportWork") is not null
                                && accepted is not null && Equals(Prop(accepted, "Destination"), destination)
                                && Prop(accepted, "Finished") is false,
                                "the native dialog has an accepted immutable output before the window closes");
                            owner.Close();
                        }
                        return true;
                    });
                }
                if (closeAfterAcceptance)
                    Check(Prop(bridge, "Dialog") is null, "closing the actual export dialog releases its native confirmation binding");
                Check(routeCount == before + 1 && job is not null && await job.WaitAsync(TimeSpan.FromSeconds(45)),
                    $"{kind} took exactly one accepted job through Capture One's native command");
                using var file = File.OpenRead(destination);
                var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad).Frames[0];
                Check(frame.Format == (extension == ".tif" ? PixelFormats.Rgb48 : PixelFormats.Rgba64)
                    && frame.PixelWidth == expectedWidth && frame.PixelHeight == expectedHeight && frame.ColorContexts is { Count: 1 },
                    $"{kind} reopens as the expected 16-bit format at the selected recipe dimensions");
                using var icc = frame.ColorContexts![0].OpenProfileStream();
                Check(Convert.ToHexString(SHA256.HashData(icc)) == iccSha, $"{kind} embeds the exact selected recipe ICC");
                if (extension == ".tif")
                {
                    var compressionTag = (frame.Metadata as BitmapMetadata)?.GetQuery("/ifd/{ushort=259}");
                    var matches = (compression, compressionTag) switch
                    {
                        (TiffCompressOption.None, (ushort)1) or (TiffCompressOption.Lzw, (ushort)5)
                            or (TiffCompressOption.Zip, (ushort)8 or (ushort)32946) => true,
                        _ => false,
                    };
                    Check(matches, $"{kind} uses the selected Capture One TIFF compression {compression}");
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
                    && detail.GetProperty("OutputWidth").GetInt32() == expectedWidth
                    && detail.GetProperty("OutputHeight").GetInt32() == expectedHeight
                    && detail.GetProperty("OutputFormat").GetString() == (extension == ".tif" ? "TIFF" : "PNG")
                    && detail.GetProperty("TransformsDeliveryProfile").ValueKind is JsonValueKind.True or JsonValueKind.False
                    && stages.All(value => double.IsFinite(value) && value >= 0)
                    && detail.GetProperty("TotalMs").GetDouble() >= stages.Sum() + filmMs
                    && (expectedFilm ? filmMs > 0 : film.ValueKind == JsonValueKind.Null)
                    && !detail.TryGetProperty("Destination", out _) && !detail.TryGetProperty("SourcePath", out _),
                    $"{kind} logs the native-to-file stages for its job with high-depth geometry and without paths");
                timings.Add(detail);
                return frame;
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

            void ShowNativeDialog(Func<bool> interact)
            {
                var started = Stopwatch.GetTimestamp();
                var handled = false;
                Exception? failure = null;
                using var poll = new Forms.Timer { Interval = 40 };
                poll.Tick += (_, _) =>
                {
                    if (handled && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(50)) return;
                    try
                    {
                        if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(50))
                            throw new TimeoutException("The real Capture One Export dialog did not finish.");
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
            try { (bridge as IDisposable)?.Dispose(); }
            finally
            {
                foreach (var slider in changedSliders) slider.Property.SetValue(slider.Control, slider.Before);
                diffusion.SelectedItem = priorDiffusion;
                toggle.IsChecked = priorEnabled;
                hookField.DeclaringType!.GetMethod("AttachExport", Flags)!.Invoke(null, new[] { window, hosted });
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
