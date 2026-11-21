using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

public static partial class StartupHook
{
    private static async Task TestNativeExportSelection(FieldInfo hookField, object hosted, object panel, object spatial, object window)
    {
        var selection = new FixtureSelection(panel);
        Check(selection.Variants.Length >= 3, "the Export dialog fixture has three real native RAW variants");
        var first = selection.Variants[0];
        var second = selection.Variants[1];
        var third = selection.Variants[2];
        Check(selection.Format(first) != selection.Format(second), "the original and replacement selections have distinct RAW formats");
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var strength = Field(panel, "strength")!;
        var strengthValue = strength.GetType().GetProperty("Value", Flags)!;
        selection.Select(first, first);
        var originalEnabled = toggle.IsChecked;
        var originalStrength = strengthValue.GetValue(strength);
        var jobs = Field(spatial, "exportJobs")!;
        var beforeReceipt = Call(jobs, "ReadLatest");
        var host = panel.GetType().Assembly.GetType("HostImageSource", true)!;
        var recipe = host.GetMethod("ActiveRecipe", Flags)!.Invoke(null, new[] { window })!;
        var expectedIcc = SHA256.HashData(File.ReadAllBytes((string)Prop(recipe, "ColorProfilePath")!));
        var scheduler = (SemaphoreSlim)host.GetField("SchedulerSlot", Flags)!.GetValue(null)!;
        var path = Path.Combine(folder, "accepted-before-selection-change.png");
        var caption = (string)hookField.DeclaringType!.GetField("ProductName", Flags)!.GetRawConstantValue()!;
        var production = (IDisposable)hookField.GetValue(null)!;
        production.Dispose();
        hookField.SetValue(null, null);

        object? bridge = null;
        var allowed = false;
        var held = false;
        var routed = 0;
        var batchRouted = 0;
        Task<bool>? job = null;
        try
        {
            toggle.IsChecked = true;
            strengthValue.SetValue(strength, 100d);
            Task<bool> Route()
            {
                routed++;
                if (!allowed) return Task.FromResult(false);
                var owner = (Window)Prop(bridge!, "Dialog")!;
                job = (Task<bool>)Call(spatial, "ExportWithDestinationAsync", true, owner,
                    (Func<SaveFileDialog, Window?, string?>)((_, _) =>
                    {
                        held = scheduler.Wait(0);
                        Check(held, "the fixture holds Capture One development after destination approval");
                        return path;
                    }))!;
                return job;
            }

            bridge = Activator.CreateInstance(production.GetType(), Flags, null, new object[]
            {
                window, (Func<bool>)(() => true), (Func<Task<bool>>)Route,
                (Action<string>)(reason => throw new InvalidOperationException(reason)),
                (Func<Guid, bool>)(_ => true),
            }, null)!;
            var batch = bridge.GetType().GetProperty("Batch", Flags)!;
            batch.SetValue(bridge, Activator.CreateInstance(batch.PropertyType, new object[]
            {
                (Func<Window?, Task<int>>)(_ => { batchRouted++; return Task.FromResult(0); }),
                (Func<bool>)(() => true), (Func<Guid, bool>)(_ => true),
            }));
            hookField.SetValue(null, bridge);
            var normal = Field(Field(bridge, "normal")!, "command")!;
            var execute = normal.GetType().GetMethod("Execute", Flags, null, new[] { typeof(bool) }, null)!;

            Reject(false, second, new[] { first, second }, "primary-only Export rejects a different primary photo");
            Reject(true, first, new[] { first, third }, "batch Export rejects changed membership with the same primary photo");
            Reject(true, second, new[] { first, second }, "batch Export rejects a different primary photo with the same members");

            allowed = true;
            SelectVerified(first, first, second);
            var confirmed = false;
            var switched = false;
            RunDialog(false, (owner, command) =>
            {
                if (!confirmed)
                {
                    SelectVerified(first, first, third);
                    confirmed = true;
                    command.Execute(null);
                }
                else if (!switched && Prop(Field(spatial, "exportActivity")!, "Active") is { } work
                    && Prop(work, "IsAccepted") is true)
                {
                    var receipt = Call(jobs, "ReadLatest")!;
                    Check(Equals(Prop(receipt, "VariantId"), first) && Equals(Prop(receipt, "Destination"), path) && held,
                        "primary-only Export accepts unchanged primary selection and freezes the original destination");
                    selection.Select(second, second);
                    Check(Equals(Field(panel, "displayedVariant"), second) && owner.IsVisible,
                        "Capture One switches the browser selection while the accepted Export dialog is open");
                    switched = true;
                    held = false;
                    scheduler.Release();
                }
                return switched;
            });
            Check(switched && routed == 1 && batchRouted == 0 && job is not null && await job,
                "one accepted film export completes after selection changes");
            var completed = Call(jobs, "ReadLatest")!;
            Check(Equals(Prop(completed, "VariantId"), first) && Equals(Prop(completed, "Destination"), path)
                && Prop(completed, "Stage")?.ToString() == "Completed" && Prop(bridge, "Dialog") is null,
                "the completed receipt keeps the first photo and the approved destination and the dialog releases ownership");
            var id = (Guid)Prop(completed, "JobId")!;
            var expectedFormat = selection.Format(first);
            var started = Stopwatch.GetTimestamp();
            JsonElement? timing = null;
            while (timing is null && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                foreach (var line in File.ReadLines(Path.Combine(folder, "panel.log")).Where(line => line.Contains("export.performance.v2")))
                {
                    try
                    {
                        using var entry = JsonDocument.Parse(line);
                        var details = entry.RootElement.GetProperty("Details");
                        if (details.GetProperty("JobId").GetGuid() == id) timing = details.Clone();
                    }
                    catch (JsonException) { }
                }
                if (timing is null) await Task.Delay(50);
            }
            Check(timing is { } exported && exported.GetProperty("SourceFormat").GetString() == expectedFormat
                && exported.GetProperty("SourceBitDepth").GetInt32() == 16 && exported.GetProperty("FilmRenderAndTransportMs").GetDouble() > 0,
                "the actual film render used the original 16-bit RAW after the browser moved to a different RAW format");
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames.Single();
            Check(frame.Format == PixelFormats.Rgba64 && frame.ColorContexts is { Count: 1 },
                "the saved film result reopens as 16-bit RGBA PNG with its recipe profile");
            using (var embedded = frame.ColorContexts!.Single().OpenProfileStream())
                Check(SHA256.HashData(embedded).SequenceEqual(expectedIcc), "the saved PNG embeds the exact native recipe ICC");
            stream.Position = 0;
            File.WriteAllText(Path.Combine(folder, "native-export-selection.json"), JsonSerializer.Serialize(new
            {
                originalFormat = expectedFormat, replacementFormat = selection.Format(second),
                outputSha256 = Convert.ToHexString(SHA256.HashData(stream)), timing,
            }));

            void SelectVerified(Guid primary, params Guid[] members)
            {
                selection.Select(primary, members);
                var observed = ((Guid Primary, Guid[] Members))Call(bridge, "CaptureOffer", true)!;
                Check(observed.Primary == primary && observed.Members.Length == members.Length
                    && observed.Members.All(members.Contains), "the native selection has the exact fixture primary and members");
            }

            void Reject(bool multiple, Guid primary, Guid[] members, string description)
            {
                SelectVerified(first, first, second);
                RunDialog(multiple, (owner, command) =>
                {
                    SelectVerified(primary, members);
                    var warned = DismissOwnedMessageBox(new WindowInteropHelper(owner).Handle, caption, Forms.DialogResult.OK,
                        () => command.Execute(null), "Capture One’s selection changed. Reopen Export for the new selection.");
                    Check(warned && owner.IsVisible && routed == 0 && batchRouted == 0 && Equals(beforeReceipt, Call(jobs, "ReadLatest")),
                        description);
                    owner.Close();
                    return true;
                });
            }

            void RunDialog(bool multiple, Func<Window, ICommand, bool> interact)
            {
                using var timer = new Forms.Timer { Interval = 40 };
                var opened = Stopwatch.GetTimestamp();
                var handled = false;
                var interacting = false;
                Exception? error = null;
                timer.Tick += (_, _) =>
                {
                    if (interacting) return;
                    interacting = true;
                    try
                    {
                        if (Stopwatch.GetElapsedTime(opened) > TimeSpan.FromSeconds(45))
                            throw new TimeoutException("The native Export selection dialog did not finish.");
                        if (!handled && Prop(bridge, "Dialog") is Window { IsVisible: true } owner
                            && owner.DataContext is { } model && Prop(model, "ExportCommand") is ICommand command && command.CanExecute(null))
                            handled = interact(owner, command);
                    }
                    catch (Exception failure)
                    {
                        error = failure;
                        timer.Stop();
                        (Prop(bridge, "Dialog") as Window)?.Close();
                    }
                    finally { interacting = false; }
                };
                timer.Start();
                try { execute.Invoke(normal, new object[] { multiple }); }
                finally { timer.Stop(); }
                if (error is not null) throw error;
                Check(handled && Prop(bridge, "Dialog") is null && Prop(window, "ExportDialogWindow") is null,
                    "the real native Export window closed and released its registration");
            }
        }
        finally
        {
            if (held) scheduler.Release();
            (bridge as IDisposable)?.Dispose();
            selection.Select(first, first);
            strengthValue.SetValue(strength, originalStrength);
            toggle.IsChecked = originalEnabled;
            hookField.DeclaringType!.GetMethod("AttachExport", Flags)!.Invoke(null, new[] { window, hosted });
        }
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native Export selection assertions");
    }
}
