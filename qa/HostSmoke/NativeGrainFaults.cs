using System;
using System.IO;
using System.Linq;
using System.Reflection;

public static partial class StartupHook
{
    private static void TestNativeGrainFaultBoundaries(Type grain, object window, object panel, object store, Guid variant)
    {
        var selection = new FixtureSelection(panel);
        var alternate = selection.Variants.First(id => id != variant);
        var profile = grain.Assembly.GetType("HostProfile", true)!;
        var readSelection = profile.GetMethod("ReadSelection", Flags)!;
        object Selected() => Prop(readSelection.Invoke(null, new[] { window })!, "Variant")!;
        var native = Selected();
        var type = native.GetType().GetInterface("P1.C1.Core.ICoreVariant")!;
        var settings = new[] { "FilmGrainType", "FilmGrainGranularity", "FilmGrainAmount" }
            .Select(name => type.GetProperty(name)!).ToArray();
        object?[] Read(object item) => settings.Select(property => property.GetValue(item)).ToArray();
        var original = Read(native);
        var changed = new object[]
        {
            Enum.ToObject(settings[0].PropertyType, Convert.ToInt32(original[0]) == 2 ? 3 : 2),
            (float)original[1]! < 65 ? 85f : 40f,
            (float)original[2]! < 65 ? 80f : 42f,
        };
        var manager = Field(panel, "nativeUndo")!;
        var model = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.ModelEntities");
        var history = model.GetType("P1.C1.ModelEntities.UndoInterfaces.IUndoManager", true)!;
        var grouping = model.GetType("P1.C1.ModelEntities.UndoInterfaces.IGroupingUndoManager", true)!;
        var name = history.GetProperty("TryGetUndoActionName")!;
        var beforeUndo = name.GetValue(manager);
        var open = grouping.GetMethod("NoUndoScope")!;
        var close = open.ReturnType.GetMethod("EndScope")!;
        Action OpenScope()
        {
            var scope = open.Invoke(manager, new object[] { "Spektrafilm disposable grain fault fixture" })!;
            return () => close.Invoke(scope, null);
        }
        var receipts = Prop(store, "GrainReceipts")!;
        var baselineReceipt = Call(receipts, "Read", variant);
        var transaction = grain.Assembly.GetType("NativeAdjustmentTransaction", true)!;
        var execute = transaction.GetMethod("Execute", Flags)!;
        var step = transaction.GetNestedType("Step", BindingFlags.NonPublic)!;
        var constructor = step.GetConstructor(Flags, null, new[] { typeof(string), typeof(Action), typeof(Action) }, null)!;

        void Inject(int setter, bool switchVariant, bool failClose)
        {
            var steps = Array.CreateInstance(step, settings.Length);
            for (var index = 0; index < settings.Length; index++)
            {
                var position = index;
                Action apply = () =>
                {
                    settings[position].SetValue(native, changed[position]);
                    if (position == setter) throw new IOException("Synthetic failure after a real native setter");
                    if (position == 0 && switchVariant) selection.Select(alternate, alternate);
                };
                Action restore = () => settings[position].SetValue(native, original[position]);
                steps.SetValue(constructor.Invoke(new object[] { settings[position].Name, apply, restore }), index);
            }
            Action end = OpenScope();
            Action closeWithFault = () =>
            {
                end();
                if (failClose) throw new IOException("Synthetic failure after real native scope close");
            };
            Action<Action> compensate = restore =>
            {
                var endCompensation = OpenScope();
                try { restore(); }
                finally { endCompensation(); }
            };
            Action guard = () =>
            {
                if (selection.Identity(Selected()) != variant)
                    throw new OperationCanceledException("The real primary variant changed during the native transaction.");
            };
            try
            {
                execute.Invoke(null, new object?[] { steps, closeWithFault, compensate, guard, null });
                throw new InvalidOperationException("The native grain transaction lost its injected failure.");
            }
            catch (TargetInvocationException error) when (error.InnerException is IOException or OperationCanceledException) { }
        }

        for (var index = 0; index < settings.Length; index++)
        {
            Inject(index, switchVariant: false, failClose: false);
            Check(Read(native).SequenceEqual(original), $"fault after real {settings[index].Name} setter restores all native grain values");
        }
        Inject(-1, switchVariant: false, failClose: true);
        Check(Read(native).SequenceEqual(original), "compensation in an actual Capture One no-undo scope restores values after a close fault");

        var restoreSelection = OpenScope();
        try
        {
            selection.Select(alternate, alternate);
            var other = Selected();
            var untouched = Read(other);
            try
            {
                grain.GetMethod("Apply", Flags)!.Invoke(null, new[] { window, store, (object)variant, 2, 88d, 67d });
                throw new InvalidOperationException("Native grain accepted a stale variant.");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
            selection.Select(variant, variant);
            Inject(-1, switchVariant: true, failClose: false);
            Check(selection.Identity(Selected()) == alternate && Read(native).SequenceEqual(original) && Read(other).SequenceEqual(untouched),
                "changing to another real variant stops subsequent setters and rollback only touches the captured variant");
        }
        finally
        {
            selection.Select(variant, variant);
            restoreSelection();
        }
        Check(Equals(beforeUndo, name.GetValue(manager)) && Equals(baselineReceipt, Call(receipts, "Read", variant)),
            "fault fixtures and stale selection rejection preserve native Undo and the captured ownership receipt");
    }
}
