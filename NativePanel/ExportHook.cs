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
    private readonly Func<Task<bool>> export;
    private readonly CommandBinding normal;
    private readonly CommandBinding previous;
    private ExportDialog? dialog;
    private IDisposable? confirmation;
    private bool busy;
    private bool disposed;

    internal ExportHook(Forms.Form window, Func<bool> isActive, Func<Task<bool>> export)
    {
        this.window = window;
        this.isActive = isActive;
        this.export = export;
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

    private bool IsApprovedDocument() =>
        !window.IsDisposed && ReferenceEquals(activeDocument.GetValue(manager), document)
        && ApprovedCatalog.IsApproved(activePath.GetValue(manager) as string);

    private bool ShouldIntercept() => !disposed && IsApprovedDocument() && isActive();

    private void OpenDialog(Command command, bool editMultiple)
    {
        try
        {
            if (!ShouldIntercept())
            {
                normal.Original(command, editMultiple);
                return;
            }
            if (busy || dialog is not null)
                return;
            if (!HasSingleSelection(editMultiple))
            {
                Warn("Select one variant to export with Spektrafilm.");
                return;
            }

            var factory = PrepareDialog(editMultiple);
            try { factory.Show(window); }
            finally { ReleaseDialog(); }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    private bool Confirm(Func<bool> native, bool editMultiple)
    {
        try
        {
            if (disposed || !IsApprovedDocument() || busy)
                return false;
            if (!isActive())
                return native();
            if (!HasSingleSelection(editMultiple))
            {
                Warn("Select one variant to export with Spektrafilm.");
                return false;
            }
            if (!CheckRecipe())
                return false;
            _ = ExportAsync();
        }
        catch (Exception error)
        {
            Fail(error);
        }
        // The native dialog command closes itself only when its action returns true.
        return false;
    }

    private void ExportWithPrevious(Command command, bool editMultiple)
    {
        try
        {
            if (!ShouldIntercept())
            {
                previous.Original(command, editMultiple);
                return;
            }
            if (!HasSingleSelection(editMultiple))
            {
                Warn("Select one variant to export with Spektrafilm.");
                return;
            }
            if (!busy && dialog is null && CheckRecipe())
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
            confirmation = ConfirmationBinding.Attach(factory.DialogVM.ExportCommand, native => Confirm(native, editMultiple));
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
        busy = true;
        var owner = dialog;
        try
        {
            StartupHook.Log("Native Export routed to the Spektrafilm renderer.");
            if (!await export())
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
            busy = false;
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

    private sealed class CommandBinding : IDisposable
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

    private sealed class ConfirmationBinding : IDisposable
    {
        private readonly object closure;
        private readonly FieldInfo field;
        private readonly Func<bool> original;
        private readonly Func<bool> wrapper;

        private ConfirmationBinding(object closure, FieldInfo field, Func<bool> original, Func<Func<bool>, bool> confirm)
        {
            this.closure = closure;
            this.field = field;
            this.original = original;
            wrapper = () => confirm(original);
            field.SetValue(closure, wrapper);
        }

        internal static ConfirmationBinding Attach(System.Windows.Input.ICommand command, Func<Func<bool>, bool> confirm)
        {
            var actionField = command.GetType().BaseType?.GetField("m_actionToExecute", Instance)
                ?? throw new MissingFieldException(command.GetType().FullName, "m_actionToExecute");
            if (actionField.GetValue(command) is not Delegate { Target: { } closure })
                throw new InvalidOperationException("Capture One did not expose its Export confirmation action.");
            var exportAction = closure.GetType().GetField("argExportAction", Instance)
                ?? throw new MissingFieldException(closure.GetType().FullName, "argExportAction");
            if (exportAction.GetValue(closure) is not Func<bool> native)
                throw new InvalidOperationException("Capture One’s Export confirmation action has an unexpected type.");
            return new ConfirmationBinding(closure, exportAction, native, confirm);
        }

        public void Dispose()
        {
            if (ReferenceEquals(field.GetValue(closure), wrapper))
                field.SetValue(closure, original);
        }
    }
}
