using System;
using System.Reflection;
using System.Threading.Tasks;

internal sealed class NativeExportConfirmation : IDisposable
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly object closure;
    private readonly FieldInfo field;
    private readonly Delegate original;
    private readonly Delegate wrapper;

    private NativeExportConfirmation(object closure, FieldInfo field, Delegate original, Delegate wrapper)
    {
        this.closure = closure;
        this.field = field;
        this.original = original;
        this.wrapper = wrapper;
        field.SetValue(closure, wrapper);
    }

    internal static NativeExportConfirmation Attach(object command, Func<bool> useNative, Action<Exception> reportError)
    {
        var commandType = command.GetType();
        FieldInfo? actionField = null;
        for (var owner = commandType; owner is not null && actionField is null; owner = owner.BaseType)
            actionField = owner.GetField("m_actionToExecute", Instance | BindingFlags.DeclaredOnly);
        if (actionField is null)
            throw new MissingFieldException(commandType.FullName, "m_actionToExecute");
        if (actionField.GetValue(command) is not Delegate { Target: { } closure })
            throw new InvalidOperationException("Capture One did not expose its Export confirmation action.");
        var field = closure.GetType().GetField("argExportAction", Instance)
            ?? throw new MissingFieldException(closure.GetType().FullName, "argExportAction");
        return field.GetValue(closure) switch
        {
            Func<bool> native when field.FieldType == typeof(Func<bool>) => new(closure, field, native,
                (Func<bool>)(() => Confirm(native, useNative, reportError))),
            Func<Action, Task<bool>> native when field.FieldType == typeof(Func<Action, Task<bool>>) => new(closure, field,
                native, (Func<Action, Task<bool>>)(callback => ConfirmAsync(native, callback, useNative, reportError))),
            _ => throw new InvalidOperationException($"Capture One’s Export confirmation type {field.FieldType} is unsupported."),
        };
    }

    private static bool Confirm(Func<bool> native, Func<bool> useNative, Action<Exception> reportError)
    {
        try { return useNative() && native(); }
        catch (Exception error)
        {
            reportError(error);
            return false;
        }
    }

    private static async Task<bool> ConfirmAsync(Func<Action, Task<bool>> native, Action callback,
        Func<bool> useNative, Action<Exception> reportError)
    {
        try { return useNative() && await native(callback); }
        catch (Exception error)
        {
            reportError(error);
            return false;
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(field.GetValue(closure), wrapper))
            field.SetValue(closure, original);
    }
}
