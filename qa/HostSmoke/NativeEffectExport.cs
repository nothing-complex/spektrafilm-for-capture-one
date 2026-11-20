using System;
using System.Collections;
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
    private static async Task TestNativeEffectExport(FieldInfo hookField, object hosted, object panel, object spatial, object window)
    {
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var strength = Field(panel, "strength")!;
        var exposure = Field(panel, "exposure")!;
        var choices = (IDictionary)Field(spatial, "choices")!;
        var diffusion = (ComboBox)choices["camera-diffusion-family"]!;
        var values = (IDictionary)Field(spatial, "values")!;
        var diffusionStrength = values["camera-diffusion-strength"]!;
        var priorEnabled = toggle.IsChecked;
        var priorDiffusion = diffusion.SelectedItem;
        var changedSliders = new[] { strength, exposure, diffusionStrength }
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
            Diffusion("off");
            Value(0, 0);
            var unprocessed = await Render("no-film-reference", ".tif", false);
            Value(0, 100);
            var filmOnly = await Render("film-only", ".tif", false);
            Diffusion("pro_mist");
            var filmAndDiffusion = await Render("film-and-diffusion", ".tif", false);
            var previousWithEffects = await Render("previous-with-effects", ".png", true);

            var filmDifference = Difference(unprocessed, filmOnly);
            var opticsDifference = Difference(filmOnly, filmAndDiffusion);
            var commandDifference = Difference(filmAndDiffusion, previousWithEffects);
            Check(filmDifference.Mean > .02 && filmDifference.ChangedFraction > .75,
                $"the native Export file contains a measured film look (mean RGB change {filmDifference.Mean:P2})");
            Check(opticsDifference.Mean > .0001 && opticsDifference.ChangedFraction > .15,
                $"changing only camera diffusion alters native Export pixels (mean RGB change {opticsDifference.Mean:P2})");
            Check(commandDifference.Mean < 8d / 65535 && commandDifference.Maximum < .01,
                "native Export TIFF and Export with Previous Settings PNG retain the same processed RGB16 image");

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
            Check(clicked && routeCount == 4 && Prop(bridge, "Dialog") is null,
                "the master switch delegates to the ordinary Capture One Export dialog without a Spektrafilm job");
            ((IDisposable)bridge).Dispose();
            ((IDisposable)bridge).Dispose();
            Check(Invocations(normalCommand).SequenceEqual(normalInvocations) && Invocations(previousCommand).SequenceEqual(previousInvocations),
                "repeated detach restores the original native callback arrays without duplicate commands");
            File.WriteAllText(Path.Combine(folder, phase + "-native-effect-export.json"), JsonSerializer.Serialize(new
            {
                width = expectedWidth, height = expectedHeight, iccSha256 = iccSha, filmDifference, opticsDifference, commandDifference,
                nativeExportCallbacks = routeCount, masterOffDelegated = clicked,
            }, new JsonSerializerOptions { WriteIndented = true }));

            async Task<BitmapSource> Render(string kind, string extension, bool usePrevious)
            {
                var before = routeCount;
                job = null;
                destination = Path.Combine(folder, phase + "-" + kind + extension);
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
                        return true;
                    });
                }
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
                return frame;
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
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native effects export assertions");
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
