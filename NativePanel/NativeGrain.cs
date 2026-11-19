using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;

internal static class NativeGrain
{
    internal readonly record struct GrainSettings(Guid VariantId, int Type, double Impact, double Granularity)
    {
        internal GrainValues Values => new(Type, Impact, Granularity);
    }

    internal static GrainSettings Read(object window)
    {
        var target = HostProfile.SelectedVariant(window);
        return ReadValues(target);
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
        var current = ReadValues(target);
        var next = new GrainSettings(
            target.Id,
            type,
            (float)impact,
            // Capture One's Granularity slider is disabled for Fine Grain.
            type == 0 ? current.Granularity : (float)granularity);
        if (current == next)
            return;

        // Persist the baseline and expected host values before touching any native setter.
        var receipt = looks.GrainReceipts.Prepare(target.Id, current.Values, next.Values);
        ChangeWithReceipt(target, looks.GrainReceipts, receipt, next,
            () => SetValues(target, next, current, "Spektrafilm: Capture One Grain"));
    }

    internal static bool HasApplied(object window, VariantLookStore looks, Guid expectedVariantId)
    {
        var target = SelectedTarget(window, expectedVariantId);
        return looks.GrainReceipts.Read(target.Id)?.Matches(ReadValues(target).Values) == true;
    }

    internal static void Restore(object window, VariantLookStore looks, Guid expectedVariantId)
    {
        var target = SelectedTarget(window, expectedVariantId);
        var current = ReadValues(target);
        var receipt = looks.GrainReceipts.Read(target.Id);
        if (receipt is null)
            throw new InvalidOperationException("There is no Spektrafilm grain adjustment to restore.");
        if (current.Values == receipt.Original)
            throw new InvalidOperationException("Capture One Grain is already at the saved original values.");
        if (!receipt.Matches(current.Values))
            throw new InvalidOperationException("Capture One Grain changed since the panel applied it.");

        var original = new GrainSettings(target.Id, receipt.Original.Type, receipt.Original.Impact, receipt.Original.Granularity);
        // Keep the receipt if native persistence lags behind the successful restore.
        // A restored value invalidates ownership on both this and future launches.
        ChangeWithReceipt(target, looks.GrainReceipts, receipt, original,
            () => SetValues(target, original, current, "Spektrafilm: Restore Capture One Grain"));
    }

    private static void ChangeWithReceipt((object Variant, Type Type, Guid Id) target, GrainReceiptStore store,
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

    private static (object Variant, Type Type, Guid Id) SelectedTarget(object window, Guid expectedVariantId)
    {
        var target = HostProfile.SelectedVariant(window);
        if (target.Id != expectedVariantId)
            throw new InvalidOperationException("The selected image changed. Choose the grain settings again.");
        return target;
    }

    private static GrainSettings ReadValues((object Variant, Type Type, Guid Id) target) =>
        new(
            target.Id,
            Convert.ToInt32(Property(target.Type, "FilmGrainType").GetValue(target.Variant)),
            (float)Property(target.Type, "FilmGrainAmount").GetValue(target.Variant)!,
            (float)Property(target.Type, "FilmGrainGranularity").GetValue(target.Variant)!);

    private static void SetValues(
        (object Variant, Type Type, Guid Id) target,
        GrainSettings values,
        GrainSettings current,
        string operation)
    {
        var grainType = Property(target.Type, "FilmGrainType");
        var amount = Property(target.Type, "FilmGrainAmount");
        var granularity = Property(target.Type, "FilmGrainGranularity");
        if (!grainType.CanRead || !grainType.CanWrite || !grainType.PropertyType.IsEnum
            || !amount.CanRead || !amount.CanWrite || amount.PropertyType != typeof(float)
            || !granularity.CanRead || !granularity.CanWrite || granularity.PropertyType != typeof(float))
            throw new InvalidOperationException("This Capture One version exposes incompatible grain controls.");
        var steps = new List<NativeAdjustmentTransaction.Step>();
        if (values.Type != current.Type)
            steps.Add(new("grain type",
                () => grainType.SetValue(target.Variant, Enum.ToObject(grainType.PropertyType, values.Type)),
                () => grainType.SetValue(target.Variant, Enum.ToObject(grainType.PropertyType, current.Type))));
        if (values.Granularity != current.Granularity)
            steps.Add(new("granularity",
                () => granularity.SetValue(target.Variant, (float)values.Granularity),
                () => granularity.SetValue(target.Variant, (float)current.Granularity)));
        if (values.Impact != current.Impact)
            steps.Add(new("amount",
                () => amount.SetValue(target.Variant, (float)values.Impact),
                () => amount.SetValue(target.Variant, (float)current.Impact)));
        var (scope, endScope) = BeginUndoScope(operation);
        NativeAdjustmentTransaction.Execute(steps, () => endScope.Invoke(scope, null));
    }

    private static (object Scope, MethodInfo EndScope) BeginUndoScope(string operation)
    {
        var program = Assembly.Load("CaptureOne").GetType("P1.C1.Program", throwOnError: true)!;
        var documentManager = Property(program, "DocumentManager").GetValue(null)
            ?? throw new InvalidOperationException("Capture One's document manager is unavailable.");
        var document = Property(documentManager.GetType(), "ActiveDocument").GetValue(documentManager)
            ?? throw new InvalidOperationException("No active Capture One catalog.");

        var undoModels = Assembly.Load("P1.C1.ModelEntities");
        var undo = undoModels.GetType("P1.C1.ModelEntities.UndoInterfaces.IUndo", throwOnError: true)!;
        var manager = Property(undo, "UndoManager").GetValue(document)
            ?? throw new InvalidOperationException("Capture One Undo is unavailable.");
        var grouping = undoModels.GetType("P1.C1.ModelEntities.UndoInterfaces.IGroupingUndoManager", throwOnError: true)!;
        var begin = Method(grouping, "UndoGroupingKeepAllScope", typeof(string));
        var end = Method(begin.ReturnType, "EndScope");
        var scope = begin.Invoke(manager, new object[] { operation })
            ?? throw new InvalidOperationException("Could not start the Capture One grain adjustment.");
        return (scope, end);
    }

    private static void CheckSlider(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 100)
            throw new ArgumentOutOfRangeException(name, "Capture One Grain uses a 0–100 scale.");
    }

    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name) ?? throw new MissingMemberException(type.FullName, name);

    private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, parameters) ?? throw new MissingMethodException(type.FullName, name);

}
