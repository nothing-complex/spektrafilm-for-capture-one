using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using P1.C1.Core;
using P1.C1.ModelEntities.UndoInterfaces;

internal static class NativeGrain
{
    internal enum ReceiptState { None, MatchesApplied, MatchesOriginal, Changed }

    internal readonly record struct GrainSettings(Guid VariantId, int Type, double Impact, double Granularity)
    {
        internal GrainValues Values => new(Type, Impact, Granularity);
    }

    internal readonly record struct GrainStatus(GrainSettings Current, ReceiptState Receipt);

    internal static GrainSettings Read(object window)
    {
        return ReadValues(HostProfile.ReadSelection(window).Variant);
    }

    internal static void Apply(
        object window,
        VariantLookStore looks,
        Guid expectedVariantId,
        int type,
        double impact,
        double granularity)
    {
        if (type is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(type));
        CheckSlider(impact, nameof(impact));
        CheckSlider(granularity, nameof(granularity));

        var target = SelectedTarget(window, expectedVariantId);
        var current = ReadValues(target.Variant);
        var next = new GrainSettings(
            expectedVariantId,
            type,
            (float)impact,
            // Capture One's Granularity slider is disabled for Fine Grain.
            type == 0 ? current.Granularity : (float)granularity);
        if (current == next)
            return;

        // Persist the baseline and expected host values before touching any native setter.
        var receipt = looks.GrainReceipts.Prepare(expectedVariantId, current.Values, next.Values);
        ChangeWithReceipt(target.Variant, looks.GrainReceipts, receipt, next,
            () => SetValues(window, target, next, current, "Spektrafilm: Capture One Grain"));
    }

    internal static bool HasApplied(object window, VariantLookStore looks, Guid expectedVariantId)
        => ReadStatus(window, looks, expectedVariantId).Receipt == ReceiptState.MatchesApplied;

    internal static GrainStatus ReadStatus(object window, VariantLookStore looks, Guid expectedVariantId)
    {
        var target = SelectedTarget(window, expectedVariantId);
        var current = ReadValues(target.Variant);
        var receipt = looks.GrainReceipts.Read(expectedVariantId);
        var state = receipt switch
        {
            null => ReceiptState.None,
            _ when receipt.Matches(current.Values) => ReceiptState.MatchesApplied,
            _ when receipt.Original == current.Values => ReceiptState.MatchesOriginal,
            _ => ReceiptState.Changed,
        };
        return new GrainStatus(current, state);
    }

    internal static void Restore(object window, VariantLookStore looks, Guid expectedVariantId)
    {
        var target = SelectedTarget(window, expectedVariantId);
        var current = ReadValues(target.Variant);
        var receipt = looks.GrainReceipts.Read(expectedVariantId);
        if (receipt is null)
            throw new InvalidOperationException("There is no Spektrafilm grain adjustment to restore.");
        if (current.Values == receipt.Original)
            throw new InvalidOperationException("Capture One Grain is already at the saved original values.");
        if (!receipt.Matches(current.Values))
            throw new InvalidOperationException("Capture One Grain changed since the panel applied it.");

        var original = new GrainSettings(expectedVariantId, receipt.Original.Type, receipt.Original.Impact, receipt.Original.Granularity);
        // Keep the receipt if native persistence lags behind the successful restore.
        // A restored value invalidates ownership on both this and future launches.
        ChangeWithReceipt(target.Variant, looks.GrainReceipts, receipt, original,
            () => SetValues(window, target, original, current, "Spektrafilm: Restore Capture One Grain"));
    }

    private static void ChangeWithReceipt(ICoreVariant target, GrainReceiptStore store,
        GrainReceipt receipt, GrainSettings requested, Action change)
    {
        Exception? failure = null;
        try { change(); }
        catch (Exception error) { failure = error; }
        try
        {
            var actual = ReadValues(target);
            if (actual != requested)
            {
                if (failure is null)
                    failure = new InvalidOperationException("Capture One did not accept all grain settings.");
                if (actual.Values != receipt.Original && !receipt.Matches(actual.Values))
                    store.Save(receipt with { Applied = actual.Values, PreviousApplied = receipt.Applied });
            }
        }
        catch (Exception verificationError)
        {
            failure = failure is null ? verificationError : new AggregateException(
                "Capture One Grain could not be changed safely and its resulting values could not be verified.", failure, verificationError);
        }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static HostProfile.Selection SelectedTarget(object window, Guid expectedVariantId)
    {
        var target = HostProfile.ReadSelection(window);
        if (target.Variant.UUID != expectedVariantId)
            throw new InvalidOperationException("The selected image changed. Choose the grain settings again.");
        return target;
    }

    private static GrainSettings ReadValues(ICoreVariant variant) =>
        new(variant.UUID, (int)variant.FilmGrainType, variant.FilmGrainAmount, variant.FilmGrainGranularity);

    private static void SetValues(
        object window,
        HostProfile.Selection target,
        GrainSettings values,
        GrainSettings current,
        string operation)
    {
        var variant = target.Variant;
        var grainType = typeof(ICoreVariant).GetProperty(nameof(ICoreVariant.FilmGrainType))
            ?? throw new MissingMemberException(typeof(ICoreVariant).FullName, nameof(ICoreVariant.FilmGrainType));
        if (!grainType.CanWrite || !grainType.PropertyType.IsEnum)
            throw new InvalidOperationException("This Capture One version exposes an incompatible grain type.");
        if ((target.Document as IUndo)?.UndoManager is not IGroupingUndoManager manager)
            throw new InvalidOperationException("Capture One Undo is unavailable for this catalog.");
        var steps = new List<NativeAdjustmentTransaction.Step>();
        if (values.Type != current.Type)
            steps.Add(new("grain type",
                () => grainType.SetValue(variant, Enum.ToObject(grainType.PropertyType, values.Type)),
                () => grainType.SetValue(variant, Enum.ToObject(grainType.PropertyType, current.Type))));
        if (values.Granularity != current.Granularity)
            steps.Add(new("granularity",
                () => variant.FilmGrainGranularity = (float)values.Granularity,
                () => variant.FilmGrainGranularity = (float)current.Granularity));
        if (values.Impact != current.Impact)
            steps.Add(new("amount",
                () => variant.FilmGrainAmount = (float)values.Impact,
                () => variant.FilmGrainAmount = (float)current.Impact));
        VerifySelection();
        var scope = manager.UndoGroupingKeepAllScope(operation);
        NativeAdjustmentTransaction.Execute(steps, scope.EndScope, restore =>
        {
            var rollback = manager.NoUndoScope(operation + " rollback");
            try { restore(); }
            finally { rollback.EndScope(); }
        }, VerifySelection, () =>
        {
            if (ReadValues(variant) != values)
                throw new InvalidOperationException("Capture One did not accept all grain settings.");
        });

        void VerifySelection()
        {
            if (!ReferenceEquals(SelectedTarget(window, values.VariantId).Document, target.Document))
                throw new InvalidOperationException("The selected catalog changed. Choose the grain settings again.");
        }
    }

    private static void CheckSlider(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 100)
            throw new ArgumentOutOfRangeException(name, "Capture One Grain uses a 0–100 scale.");
    }

}
