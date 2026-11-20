using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

// Native setters may throw after mutating a value. Include the active step in
// rollback, then attempt every earlier restoration before ending the undo group.
internal static class NativeAdjustmentTransaction
{
    internal sealed record Step(string Name, Action Apply, Action Restore);

    internal static void Execute(IReadOnlyList<Step> steps, Action endScope, Action<Action> compensate,
        Action? verifySelection = null, Action? verifyApplied = null)
    {
        var errors = new List<Exception>();
        var active = -1;
        try
        {
            for (var index = 0; index < steps.Count; index++)
            {
                verifySelection?.Invoke();
                active = index;
                steps[index].Apply();
            }
            verifySelection?.Invoke();
            verifyApplied?.Invoke();
        }
        catch (Exception error)
        {
            errors.Add(error);
            Restore();
        }
        var applied = errors.Count == 0;
        try { endScope(); }
        catch (Exception scopeError)
        {
            errors.Add(scopeError);
            if (applied && active >= 0)
            {
                try { compensate(Restore); }
                catch (Exception compensationError)
                {
                    errors.Add(new InvalidOperationException("Could not finish compensating for the native undo failure.", compensationError));
                }
            }
        }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("The native adjustment failed and additional rollback or undo errors occurred. Check Capture One Grain; Restore remains available when its values can be verified.", errors);

        void Restore()
        {
            for (var index = active; index >= 0; index--)
            {
                try { steps[index].Restore(); }
                catch (Exception restoreError)
                {
                    errors.Add(new InvalidOperationException($"Could not restore native '{steps[index].Name}'.", restoreError));
                }
            }
        }
    }
}
