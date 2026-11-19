using System;
using P1.C1.ModelEntities.UndoInterfaces;

internal sealed class NativeLookUndoAction(
    VariantLookStore store, Guid variant, VariantLook before, VariantLook after,
    string name, Action<Guid, Exception?> notify) : IUndoableAction
{
    private bool registered;

    public string Name => name;
    public bool CanUndo => true;

    public void Do()
    {
        // Each slider movement already committed to the variant. The first Do
        // call registers the finished edit with Capture One's history.
        if (!registered)
        {
            registered = true;
            return;
        }
        Change(before, after);
    }

    public void Undo() => Change(after, before);

    private void Change(VariantLook expected, VariantLook replacement)
    {
        Exception? error = null;
        try { store.ReplaceIfCurrent(variant, expected, replacement); }
        catch (Exception failure) { error = failure; }
        try { notify(variant, error); }
        catch (Exception failure) { StartupHook.Log("Could not refresh Spektrafilm undo state: " + failure); }
    }
}
