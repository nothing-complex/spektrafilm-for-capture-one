using System;
using System.Collections.Generic;
using System.Linq;

internal sealed record VariantLookChange(Guid Variant, VariantLook? SavedBefore, VariantLook EffectiveBefore, VariantLook After);

internal static class VariantLookBatch
{
    internal const int MaxVariants = 1000;

    internal static VariantLookChange? Prepare(VariantLookStore store, Guid variant, VariantLook defaults,
        LookPreset preset, LookSections sections)
    {
        var saved = store.ReadForBatch(variant);
        var before = saved ?? defaults;
        var (film, optics, enabled, grain) = preset.SelectSections(before, sections);
        var after = before with
        {
            Look = film,
            Optics = optics ?? before.Optics,
            Enabled = enabled ?? before.Enabled,
            Grain = grain ?? before.Grain,
        };
        return VariantLookStore.Equivalent(before, after) ? null : new(variant, saved, before, after);
    }

    internal static void Replay(VariantLookStore store, IReadOnlyList<VariantLookChange> changes, bool undo)
    {
        var ordered = undo ? changes.Reverse() : changes;
        foreach (var change in ordered)
            store.RequireBatchBaseline(change.Variant, undo ? change.After : change.EffectiveBefore);

        var committed = new Stack<VariantLookChange>();
        try
        {
            foreach (var change in ordered)
            {
                Apply(change, undo);
                committed.Push(change);
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            foreach (var change in committed)
            {
                try { Apply(change, !undo); }
                catch (Exception rollbackError) { failures.Add(rollbackError); }
            }
            if (failures.Count == 1) throw;
            throw new AggregateException("Spektrafilm could not fully restore the batch. Check the affected variants.", failures);
        }

        void Apply(VariantLookChange change, bool revert) => store.CommitBatchChange(change.Variant,
            revert ? change.After : change.EffectiveBefore, revert ? change.EffectiveBefore : change.After);
    }
}
