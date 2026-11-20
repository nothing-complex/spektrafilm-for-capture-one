using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using P1.C1.Components;
using P1.C1.Components.CommandsLogic;
using P1.C1.Components.ExportDialog;
using P1.C1.Core.Interfaces;
using P1.C1.Core.ModelEntities;
using P1.C1.Core.Messaging;
using P1.C1.ViewModels.Commands;
using Forms = System.Windows.Forms;

internal enum ExportBridgeState { Checking, Connected, Unavailable, Detached }

internal sealed class ExportHook : IDisposable
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Forms.Form window;
    private readonly ICaptureOneDocument document;
    private readonly object manager;
    private readonly PropertyInfo activeDocument;
    private readonly PropertyInfo activePath;
    private readonly PropertyInfo exportWindow;
    private readonly Func<IDestinationDocumentManager> destinations;
    private readonly Func<bool> isActive;
    private readonly Func<Guid, bool> wouldLoseSavedLook;
    private readonly Func<Task<bool>> export;
    private readonly Action<string> reportUnavailable;
    private readonly CommandBinding normal;
    private readonly CommandBinding previous;
    private ExportDialog? dialog;
    private IDisposable? confirmation;
    private bool choosingDestination;
    private Task<bool>? activeExport;
    private string? confirmationError;
    private bool disposed;

    internal ExportHook(Forms.Form window, Func<bool> isActive, Func<Task<bool>> export, Action<string> reportUnavailable,
        Func<Guid, bool> wouldLoseSavedLook)
    {
        this.window = window;
        this.isActive = isActive;
        this.wouldLoseSavedLook = wouldLoseSavedLook;
        this.export = export;
        this.reportUnavailable = reportUnavailable;
        var program = window.GetType().Assembly.GetType("P1.C1.Program", throwOnError: true)!;
        manager = program.GetProperty("DocumentManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("Capture One’s document manager is unavailable.");
        activeDocument = FindProperty(manager, "ActiveDocument");
        activePath = FindProperty(manager, "ActiveDocumentPath");
        exportWindow = FindProperty(window, "ExportDialogWindow");
        document = FindProperty(window, "Document").GetValue(window) as ICaptureOneDocument
            ?? throw new InvalidOperationException("The selected catalog is unavailable.");
        if (!IsApprovedDocument())
            throw new InvalidOperationException("Activate the selected catalog before binding Export.");
        var commands = manager.GetType().GetMethod("GetDocumentCommands", Instance, null,
                new[] { typeof(ICaptureOneDocument) }, null)?.Invoke(manager, new object[] { document }) as Commands
            ?? throw new InvalidOperationException("Capture One’s export commands are unavailable.");
        var getDestinationManager = manager.GetType().GetMethod("GetDestinationDocumentManager", Instance, null,
            new[] { typeof(ICaptureOneDocument) }, null)
            ?? throw new MissingMethodException(manager.GetType().FullName, "GetDestinationDocumentManager");
        var readDestinations = getDestinationManager.CreateDelegate<Func<ICaptureOneDocument, IDestinationDocumentManager>>(manager);
        destinations = () => readDestinations(document);
        normal = new CommandBinding(commands.Export, window, "OnExportOnOnExecuteMultiple", OpenDialog);
        try
        {
            previous = new CommandBinding(commands.ExportWithPreviousSettings, window,
                "OnExportWithPreviousOnOnExecuteMultiple", ExportWithPrevious);
        }
        catch
        {
            normal.Dispose();
            throw;
        }
        StartupHook.Log("Spektrafilm bound the selected catalog’s native Export commands.");
    }

    internal Window? Dialog => dialog;
    private bool Busy => choosingDestination || activeExport is { } job && (!job.IsCompleted || dialog is not null);

    private bool IsApprovedDocument() =>
        !window.IsDisposed && ReferenceEquals(activeDocument.GetValue(manager), document)
        && ApprovedCatalog.IsApproved(activePath.GetValue(manager) as string, document.Path);

    private bool ShouldIntercept(bool editMultiple) => !disposed && IsApprovedDocument() && RequiresSpektrafilm(editMultiple);

    private bool RequiresSpektrafilm(bool editMultiple)
    {
        if (isActive()) return true;
        if (document is not ISelectedCollectionsProvider
        {
            PrimarySelectedCollectionSelection: { PrimaryVariant: { } primary } selection,
        }) return false;

        // An enabled saved look can exist while the panel is still loading it.
        return wouldLoseSavedLook(primary.UUID) || editMultiple && selection.SelectedVariants is { Count: > 1 } selected
            && selected.Any(variant => variant.UUID != primary.UUID && wouldLoseSavedLook(variant.UUID));
    }

    private void OpenDialog(Command command, bool editMultiple)
    {
        try
        {
            if (!ShouldIntercept(editMultiple))
            {
                normal.Original(command, editMultiple);
                return;
            }
            if (Busy || dialog is not null || !CheckConfirmationSupport() || !CheckLookReady(editMultiple))
                return;

            var factory = PrepareDialog(editMultiple);
            try { factory.Show(window); }
            finally { ReleaseDialog(); }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private bool UseNativeConfirmation(bool editMultiple)
    {
        if (disposed || !IsApprovedDocument() || Busy)
            return false;
        if (!RequiresSpektrafilm(editMultiple))
            return true;
        if (CheckLookReady(editMultiple) && CheckRecipe())
            _ = ExportAsync();
        // Spektrafilm closes the native dialog only after its accepted file is saved.
        return false;
    }

    private void ExportWithPrevious(Command command, bool editMultiple)
    {
        try
        {
            if (!ShouldIntercept(editMultiple))
            {
                previous.Original(command, editMultiple);
                return;
            }
            if (!Busy && dialog is null && CheckConfirmationSupport() && CheckLookReady(editMultiple) && CheckRecipe())
                _ = ExportAsync();
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private bool HasSingleSelection(bool editMultiple) =>
        document is ISelectedCollectionsProvider { PrimarySelectedCollectionSelection: { PrimaryVariant: not null } selection }
        && (!editMultiple || selection.SelectedVariants?.Count == 1);

    private bool CheckLookReady(bool editMultiple)
    {
        if (!HasSingleSelection(editMultiple))
        {
            Warn("Select one variant to export with Spektrafilm.");
            return false;
        }
        if (isActive()) return true;
        Warn("Wait for the saved Spektrafilm look to load. Open the tool to see its status.");
        return false;
    }

    private bool CheckConfirmationSupport()
    {
        if (confirmationError is null)
            return true;
        Warn(confirmationError);
        return false;
    }

    private IDisposable BindConfirmation(System.Windows.Input.ICommand command, bool editMultiple)
    {
        try { return NativeExportConfirmation.Attach(command, () => UseNativeConfirmation(editMultiple), Fail); }
        catch (Exception error)
        {
            confirmationError = "Capture One's Export confirmation is unavailable. Use Export with Spektrafilm in the tool panel.";
            reportUnavailable("The Export confirmation could not be connected.");
            StartupHook.Log($"Native Export unsupported [confirmation-adapter]: {error}");
            throw new InvalidOperationException(confirmationError, error);
        }
    }

    private bool CheckRecipe()
    {
        var manager = destinations();
        var enabled = manager.EnabledDestinations;
        if (enabled.Count == 1 && StringComparer.Ordinal.Equals(enabled[0].Name, manager.SelectedDestination?.Name))
            return true;
        Warn("Enable and select one Export Recipe to use Spektrafilm.");
        return false;
    }

    private ExportDialogFactory PrepareDialog(bool editMultiple)
    {
        if (exportWindow.GetValue(window) is not null)
            throw new InvalidOperationException("Close Capture One’s other Export window before exporting with Spektrafilm.");
        var factory = new ExportDialogFactory(document, MessageBusProvider.DefaultInstance, editMultiple);
        try
        {
            confirmation = BindConfirmation(factory.DialogVM.ExportCommand, editMultiple);
            dialog = factory.ExportDialogWindow;
            dialog.Title += " – " + StartupHook.ProductName;
            exportWindow.SetValue(window, dialog);
            StartupHook.Log("Spektrafilm bound the native Export confirmation button.");
            return factory;
        }
        catch
        {
            try { ReleaseDialog(); }
            finally { factory.ExportDialogWindow.Close(); }
            throw;
        }
    }

    private void ReleaseDialog()
    {
        var binding = confirmation;
        var owner = dialog;
        confirmation = null;
        dialog = null;
        try { binding?.Dispose(); }
        finally
        {
            if (owner is not null && !window.IsDisposed && ReferenceEquals(exportWindow.GetValue(window), owner))
                exportWindow.SetValue(window, null);
        }
    }

    private async Task<bool> ExportAsync()
    {
        var owner = dialog;
        Task<bool>? job = null;
        try
        {
            StartupHook.Log("Native Export routed to the Spektrafilm renderer.");
            // The destination picker runs a nested message loop before the task
            // is returned. Once the window has closed and the task completes,
            // the next command can proceed before this continuation resumes.
            choosingDestination = true;
            try { activeExport = job = export(); }
            finally { choosingDestination = false; }
            if (!await job)
                return false;
            if (!disposed && owner is { IsVisible: true } && ReferenceEquals(owner, dialog))
                _ = owner.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!disposed && owner.IsVisible && ReferenceEquals(owner, dialog))
                        owner.Close();
                }));
            return true;
        }
        catch (Exception error)
        {
            if (disposed)
                StartupHook.Log(error.ToString());
            else
                Fail(error);
            return false;
        }
        finally
        {
            if (ReferenceEquals(activeExport, job))
                activeExport = null;
        }
    }

    private void Fail(Exception error)
    {
        StartupHook.Log(error.ToString());
        Warn($"Spektrafilm export could not start. {error.GetBaseException().Message}");
    }

    private void Warn(string message)
    {
        if (dialog is { IsVisible: true } owner)
            System.Windows.MessageBox.Show(owner, message, StartupHook.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
        else if (!window.IsDisposed)
            Forms.MessageBox.Show(window, message, StartupHook.ProductName, Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Warning);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            try { dialog?.Close(); }
            finally { ReleaseDialog(); }
        }
        finally
        {
            try { previous.Dispose(); }
            finally { normal.Dispose(); }
        }
    }

    private static PropertyInfo FindProperty(object target, string name) =>
        target.GetType().GetProperty(name, Instance) ?? throw new MissingMemberException(target.GetType().FullName, name);

    internal sealed class CommandBinding : IDisposable
    {
        private static readonly FieldInfo Callbacks = typeof(EditMultipleCommand).GetField("OnExecuteMultiple", Instance)
            ?? throw new MissingFieldException(typeof(EditMultipleCommand).FullName, "OnExecuteMultiple");
        private readonly EditMultipleCommand command;
        private readonly EditMultipleCommandEventHandler wrapper;

        internal EditMultipleCommandEventHandler Original { get; }

        internal CommandBinding(EditMultipleCommand command, object window, string method,
            EditMultipleCommandEventHandler wrapper)
        {
            this.command = command;
            this.wrapper = wrapper;
            var callbacks = (EditMultipleCommandEventHandler?)Callbacks.GetValue(command)
                ?? throw new InvalidOperationException($"Capture One did not register {command.Name}.");
            Original = callbacks.GetInvocationList().OfType<EditMultipleCommandEventHandler>()
                .Single(callback => ReferenceEquals(callback.Target, window) && callback.Method.Name == method);
            Replace(callbacks, Original, wrapper);
        }

        public void Dispose()
        {
            if (Callbacks.GetValue(command) is EditMultipleCommandEventHandler current)
                Replace(current, wrapper, Original);
        }

        private void Replace(EditMultipleCommandEventHandler callbacks, EditMultipleCommandEventHandler before,
            EditMultipleCommandEventHandler after)
        {
            var invocation = callbacks.GetInvocationList();
            var index = Array.IndexOf(invocation, before);
            if (index < 0)
                return;
            invocation[index] = after;
            Callbacks.SetValue(command, Delegate.Combine(invocation));
        }
    }

}
