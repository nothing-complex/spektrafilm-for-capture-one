using System;
using System.Collections.Generic;
using P1.C1.ModelEntities.UndoInterfaces;

internal sealed class NativeLookBatchUndoAction(
    VariantLookStore store, IReadOnlyList<VariantLookChange> changes, Action<IReadOnlyList<VariantLookChange>, Exception?> notify)
    : IUndoableAction
{
    private bool registered;

    public string Name => "Apply Spektrafilm settings to selected variants";
    public bool CanUndo => true;

    public void Do()
    {
        if (registered)
            Change(undo: false);
        registered = true;
    }

    public void Undo() => Change(undo: true);

    private void Change(bool undo)
    {
        Exception? error = null;
        try { VariantLookBatch.Replay(store, changes, undo); }
        catch (Exception failure) { error = failure; }
        try { notify(changes, error); }
        catch (Exception failure) { StartupHook.Log("Could not refresh Spektrafilm batch history: " + failure); }
    }
}
