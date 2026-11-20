using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

public static partial class StartupHook
{
    private static async Task TestConcurrentNativeCatalogs(Assembly assembly, Type hook, object hosted, object panel,
        object spatial, object firstWindow, object store, Guid variant, string firstPath, string secondPath)
    {
        Check(Path.GetDirectoryName(firstPath) == Path.Combine(folder, "A") && Path.GetDirectoryName(secondPath) == Path.Combine(folder, "B")
            && File.Exists(firstPath) && File.Exists(secondPath), "both concurrent catalogs belong to this dedicated fixture");
        var access = assembly.GetType("HostDocumentAccess", true)!;
        var manager = Prop(access.GetMethod("Current", Flags)!.Invoke(null, null)!, "Instance")!;
        var catalogA = Prop(manager, "ActiveDocument")!;
        object? Active() => Prop(manager, "ActiveDocument");
        object[] OpenDocuments() => ((IEnumerable)Call(manager, "GetOpenDocuments")!).Cast<object>().ToArray();
        Forms.Form? MainForm(object document) => (Forms.Form?)access.GetMethod("MainForm", Flags)!.Invoke(null, new[] { document });
        bool Matches(object document, string path) => string.Equals((string)Prop(document, "Path")!, path, StringComparison.OrdinalIgnoreCase);
        async Task WaitFor(Func<bool> predicate, string activity)
        {
            var started = Stopwatch.GetTimestamp();
            while (!predicate())
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(24)) throw new TimeoutException(activity);
                await Task.Delay(60);
            }
        }
        var enabled = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var exposure = Field(panel, "exposure")!;
        var value = exposure.GetType().GetProperty("Value", Flags)!;
        var viewer = Field(spatial, "viewer")!;
        var picker = (ComboBox)Field(spatial, "previewSize")!;
        picker.SelectedItem = picker.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "2048");
        enabled.IsChecked = true;
        value.SetValue(exposure, 1.25);
        await WaitFor(() => Prop(viewer, "HasPainted") is true, "A did not paint its first settled frame.");
        var lookPath = (string)Call(store, "GetPath", variant)!;
        var savedA = File.ReadAllBytes(lookPath);
        var hookField = hook.GetField("hostedTool", Flags)!;
        var creation = firstWindow.GetType().Assembly.GetType("P1.C1.Components.Document.DocumentCreationHelper", true)!;
        var open = creation.GetMethod("OpenDocument", Flags)!;
        var settingsType = firstWindow.GetType().Assembly.GetType("P1.C1.ApplicationSettings", true)!;
        var settings = settingsType.GetProperty("Default", Flags)!.GetValue(null)!;
        var allowMultiple = settingsType.GetProperty("MultipleSessions", Flags)!;
        var previous = allowMultiple.GetValue(settings);
        try
        {
            allowMultiple.SetValue(settings, true);
            // The native File > Open command uses this helper to create its window.
            var result = open.Invoke(null, new object?[] { secondPath, null, false, null, false, true, null });
            File.AppendAllText(Path.Combine(folder, "host-smoke.log"), $"Native concurrent catalog open result {result}.{Environment.NewLine}");
            await WaitFor(() => OpenDocuments().Any(document => Matches(document, secondPath)), "Capture One did not open the second disposable catalog.");
            var catalogB = OpenDocuments().Single(document => Matches(document, secondPath));
            await WaitFor(() => MainForm(catalogB) is { IsHandleCreated: true }, "Capture One did not create the second catalog window.");
            var secondWindow = MainForm(catalogB)!;
            Check(OpenDocuments().Length >= 2 && Matches(catalogA, firstPath) && !ReferenceEquals(secondWindow, firstWindow),
                "the licensed process contains two actual catalogs with separate native windows");
            for (var iteration = 0; iteration < 3; iteration++)
            {
                if (iteration == 1 && Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_DEVELOPMENT_LEASE") == "1")
                    await SwitchDuringLateDevelopment(catalogB, secondWindow);
                else
                {
                    _ = Call(manager, "SetActiveDocument", catalogB);
                    secondWindow.Activate();
                    await WaitFor(() => ReferenceEquals(Active(), catalogB), "The second catalog did not become active.");
                }
                await Task.Delay(650);
                Check(Prop(viewer, "HasPainted") is false && Prop(panel, "IsSpektraExportActive") is false,
                    "the unapproved second catalog cannot paint or export the first catalog's film look");
                Check(Directory.EnumerateDirectories(Path.Combine(folder, "Variant looks")).Count() == 1
                    && File.ReadAllBytes(lookPath).SequenceEqual(savedA), "focus switching does not create an unapproved look directory or edit the old look");
                var originalForm = MainForm(catalogA) ?? throw new InvalidOperationException("Capture One lost the original window.");
                if (!originalForm.Visible) originalForm.Show();
                originalForm.BringToFront();
                originalForm.Activate();
                var firstActivated = Call(manager, "SetActiveDocument", catalogA);
                File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
                    $"Reactivating A returned {firstActivated}; A active {ReferenceEquals(Active(), catalogA)}; visible {originalForm.Visible}; " +
                    $"enabled {originalForm.Enabled}; focused {originalForm.ContainsFocus}.{Environment.NewLine}");
                try
                {
                    await WaitFor(() => ReferenceEquals(Active(), catalogA) && Prop(viewer, "HasPainted") is true,
                        "Returning to the opted-in catalog did not restore its frame.");
                }
                catch (TimeoutException error)
                {
                    var target = assembly.GetType("HostProfile", true)!.GetMethod("CaptureTarget", Flags)!;
                    string accessState;
                    try { accessState = Prop(target.Invoke(null, new object[] { originalForm })!, "VariantId")!.ToString()!; }
                    catch (TargetInvocationException failure) { accessState = failure.GetBaseException().Message; }
                    throw new TimeoutException($"{error.Message} Active A {ReferenceEquals(Active(), catalogA)}, manager A path " +
                        $"{Equals(Prop(manager, "ActiveDocumentPath"), firstPath)}, current owner {ReferenceEquals(hookField.GetValue(null), hosted)}, " +
                        $"source {accessState}, panel variant {Field(panel, "displayedVariant")}, enabled {enabled.IsChecked}/{enabled.IsEnabled}, " +
                        $"Viewer {Prop(spatial, "Health")}/{Prop(viewer, "UnavailableReason")}, pending drain " +
                        $"{(Field(spatial, "previewWorkersExited") as Task)?.Status}.", error);
                }
                Check(ReferenceEquals(hookField.GetValue(null), hosted) && Equals(value.GetValue(exposure), 1.25),
                    "the exact native tool and film state return when the opted-in catalog resumes");
            }

            var worker = Field(spatial, "renderer") is { } renderer ? Field(renderer, "process") as Process : null;
            using var workerProbe = worker is { HasExited: false } ? Process.GetProcessById(worker.Id) : null;
            Check(workerProbe is not null, "the approved window has a live renderer to check during teardown");
            var exportField = hook.GetField("exportHook", Flags)!;
            var originalExport = exportField.GetValue(null);
            _ = Call(manager, "SetActiveDocument", catalogB);
            secondWindow.BringToFront();
            secondWindow.Activate();
            await WaitFor(() => ReferenceEquals(Active(), catalogB), "The second document was not active for approved-window teardown.");
            ((Forms.Form)firstWindow).Close();
            await WaitFor(() => ((Forms.Form)firstWindow).IsDisposed && !OpenDocuments().Any(document => Matches(document, firstPath)),
                "Native close did not remove the approved document window.");
            await WaitFor(() => hookField.GetValue(null) is null && Field(spatial, "disposed") is true
                && (workerProbe is null || workerProbe.HasExited), "The closed native window retained its tool or live worker.");
            Check(ReferenceEquals(Active(), catalogB) && exportField.GetValue(null) is null && Prop(panel, "IsSpektraExportActive") is false,
                "closing the approved window detaches its native Export bridge while the unapproved window stays open");
            Check(Field(hosted, "disposed") is true && Field(panel, "disposed") is true && Prop(viewer, "HasPainted") is false,
                "the old tool, panel and viewer are disposed after closing the approved window");
            Check(File.ReadAllBytes(lookPath).SequenceEqual(savedA), "closing the approved window preserves the exact saved look");

            _ = open.Invoke(null, new object?[] { firstPath, null, false, null, false, true, null });
            await WaitFor(() => OpenDocuments().Any(document => Matches(document, firstPath)), "Capture One did not reopen the approved catalog.");
            catalogA = OpenDocuments().Single(document => Matches(document, firstPath));
            await WaitFor(() => MainForm(catalogA) is { IsHandleCreated: true }, "The reopened catalog did not receive a native form.");
            var replacement = MainForm(catalogA)!;
            if (!replacement.Visible) replacement.Show();
            replacement.BringToFront();
            replacement.Activate();
            _ = Call(manager, "SetActiveDocument", catalogA);
            await WaitFor(() => ReferenceEquals(Active(), catalogA) && hookField.GetValue(null) is { } current
                && !ReferenceEquals(current, hosted) && Prop(current, "IsSpektraExportActive") is true,
                "The reopened catalog did not attach an independent tool and Export bridge.");
            var reopened = hookField.GetValue(null)!;
            var reopenedPanel = Field(reopened, "panel")!;
            var reopenedViewer = Field(Field(reopenedPanel, "opticalPreview")!, "viewer")!;
            await WaitFor(() => Prop(reopenedViewer, "HasPainted") is true, "The reopened catalog did not paint the stored look.");
            Check(!ReferenceEquals(firstWindow, replacement) && !ReferenceEquals(panel, reopenedPanel)
                && exportField.GetValue(null) is { } reopenedExport && !ReferenceEquals(originalExport, reopenedExport),
                "reopening the approved catalog creates an independent native form and plugin panel in the same process");
            Check(Prop(Field(reopenedPanel, "exposure")!, "Value") is 1.25
                && Equals(Field(reopenedPanel, "displayedVariant"), variant) && File.ReadAllBytes(lookPath).SequenceEqual(savedA),
                "reopening loads the exact original variant and durable film settings");
            Check(!secondWindow.IsDisposed && OpenDocuments().Contains(catalogB),
                "the unapproved window remains independent across teardown and replacement");
            if (Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_SESSION") is { Length: > 0 } sessionPath)
            {
                Call(manager, "CloseDocument", secondPath);
                await WaitFor(() => !OpenDocuments().Any(document => Matches(document, secondPath)),
                    "The second catalog did not close before the session fixture.");
                await TestNativeSessionIsolation(assembly, manager, open, access, catalogA, replacement, reopenedPanel,
                    reopenedViewer, lookPath, savedA, sessionPath);
            }
        }
        finally
        {
            try
            {
                if (OpenDocuments().Any(document => Matches(document, secondPath)))
                    Call(manager, "CloseDocument", secondPath);
            }
            finally { allowMultiple.SetValue(settings, previous); }
        }
        await WaitFor(() => !OpenDocuments().Any(document => Matches(document, secondPath)) && ReferenceEquals(Active(), catalogA),
            "Closing the second catalog did not leave the first actual document active.");
        Check(File.ReadAllBytes(lookPath).SequenceEqual(savedA), "closing a different native catalog preserves the approved saved look");
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} concurrent native catalog assertions");

        async Task SwitchDuringLateDevelopment(object catalogB, Forms.Form secondWindow)
        {
            var native = assembly.GetType("HostImageSource", true)!;
            var entered = (ManualResetEventSlim)native.GetField("DevelopmentLeaseEntered", Flags)!.GetValue(null)!;
            var release = (ManualResetEventSlim)native.GetField("DevelopmentLeaseContinue", Flags)!.GetValue(null)!;
            var slot = (SemaphoreSlim)native.GetField("SchedulerSlot", Flags)!.GetValue(null)!;
            var armed = native.GetField("developmentLeaseArmed", Flags)!;
            var draft = (ComboBox)Field(spatial, "dragPreviewSize")!;
            draft.SelectedItem = draft.ItemsSource.Cast<object>().Single(item => Prop(item, "LongEdge") is 0);
            await WaitFor(() => Field(spatial, "frameCurrent") is true && Field(spatial, "pending") is null
                && Field(spatial, "pendingDraft") is null && slot.CurrentCount == 1,
                "The original catalog did not finish its previous Viewer work.");
            var oldSource = Field(spatial, "source");
            entered.Reset();
            release.Reset();
            try
            {
                armed.SetValue(null, 1);
                picker.SelectedItem = picker.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "900");
                await WaitFor(() => entered.IsSet, "The actual native callback did not enter its disposable pause.");
                _ = Call(manager, "SetActiveDocument", catalogB);
                secondWindow.Activate();
                await WaitFor(() => ReferenceEquals(Active(), catalogB), "The second catalog did not become active during development.");
                Check(slot.CurrentCount == 0 && Prop(viewer, "HasPainted") is false
                    && Prop(panel, "IsSpektraExportActive") is false,
                    "the late callback is still owned by the inactive catalog and cannot paint its other window");
            }
            finally { release.Set(); }
            await WaitFor(() => slot.CurrentCount == 1 && Field(spatial, "pending") is null,
                "The late native callback did not settle after switching catalogs.");
            var after = Field(spatial, "source");
            Check(ReferenceEquals(Active(), catalogB) && (after is null || ReferenceEquals(after, oldSource))
                && Prop(viewer, "HasPainted") is false && Field(spatial, "frameCurrent") is false,
                "the late source from the inactive catalog never enters or paints the current window");
        }
    }
}
