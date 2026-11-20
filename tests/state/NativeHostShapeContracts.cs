using System.Runtime.InteropServices;

internal static class NativeHostShapeContracts
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        var reported = new List<Exception>();
        var stock = 0;
        var useNative = false;
        var legacy = new LegacyClosure(() => { stock++; return true; });
        var legacyOriginal = legacy.argExportAction;
        using (NativeExportConfirmation.Attach(new Command(new Func<bool>(legacy.Invoke)), () => useNative, reported.Add))
        {
            check("16.4 confirmation refusal leaves the dialog open and bypasses native export", !legacy.Invoke() && stock == 0);
            useNative = true;
            check("16.4 delegates an allowed native confirmation exactly once", legacy.Invoke() && stock == 1);
        }
        check("16.4 detach restores the original native callback", ReferenceEquals(legacy.argExportAction, legacyOriginal));

        int callbackCalls = 0;
        Action callback = () => callbackCalls++;
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? received = null;
        var current = new AsyncClosure(action => { stock++; received = action; return complete.Task; });
        var currentOriginal = current.argExportAction;
        var command = new ConcreteCommand(new Func<Action, Task<bool>>(current.Invoke));
        using (NativeExportConfirmation.Attach(command, () => useNative, reported.Add))
        {
            useNative = false;
            var rejected = current.Invoke(callback);
            check("16.7 interception returns a completed false without invoking the native action",
                rejected.IsCompletedSuccessfully && !await rejected && stock == 1 && callbackCalls == 0);
            useNative = true;
            var pending = current.Invoke(callback);
            check("16.7 passes the exact host callback into the original async confirmation",
                stock == 2 && ReferenceEquals(received, callback) && !pending.IsCompleted);
            received!();
            complete.SetResult(true);
            check("16.7 propagates the native result after awaiting completion", await pending && callbackCalls == 1 && reported.Count == 0);
        }
        check("16.7 detach restores the exact original async callback", ReferenceEquals(currentOriginal, current.argExportAction));

        var failure = new IOException("delayed fixture error");
        current.argExportAction = async _ => { await Task.Yield(); throw failure; };
        using (NativeExportConfirmation.Attach(command, () => true, reported.Add))
        {
            check("16.7 async native failure reports once and keeps the dialog open",
                !await current.Invoke(callback) && reported.SequenceEqual(new[] { failure }));
            current.argExportAction = currentOriginal;
        }
        check("detach preserves a later change to the command closure", ReferenceEquals(currentOriginal, current.argExportAction));

        var beforeRecipe = NativeRecipeScale.Bind<string, string, string, string>(typeof(LegacyRecipe));
        var afterRecipe = NativeRecipeScale.Bind<object, string, string, string>(typeof(CurrentRecipe));
        check("legacy scale preserves the exact ignore-crop flag and inputs",
            beforeRecipe("geometry", "raw", "tiff", 1234, false) == "geometry/raw/tiff/1234/False"
            && beforeRecipe("geometry", "raw", "png", 1000, true) == "geometry/raw/png/1000/True");
        check("enum scale maps active and full crop without relying on enum ordinals",
            afterRecipe(new Geometry(), "raw", "tiff", 1234, false) == "geometry/raw/tiff/1234/ActiveCrop/none"
            && afterRecipe(new Geometry(), "raw", "png", 1000, true) == "geometry/raw/png/1000/FullCrop/none");

        var legacyMetadata = new NativeBitmapMetadata<int>();
        check("legacy bitmap destination remains unchanged", !legacyMetadata.RequiresAuthor && legacyMetadata.SetUnsigned(16, null) == 16);
        var currentMetadata = new NativeBitmapMetadata<Destination>();
        using var author = new EmptyAuthor();
        var output = currentMetadata.SetUnsigned(new Destination { bits = 16, c2paSign = true, c2paWithEdits = true, c2paAuthor = null }, author);
        check("current bitmap destination carries a live empty author with signing disabled and 16-bit depth",
            currentMetadata.RequiresAuthor && ReferenceEquals(output.c2paAuthor, author) && !author.IsClosed
            && !output.c2paSign && !output.c2paWithEdits && output.bits == 16);
    }

    private class CommandBase(Delegate action) { protected readonly Delegate m_actionToExecute = action; }
    private sealed class Command(Delegate action) : CommandBase(action);
    private sealed class ConcreteCommand(Delegate action) { private readonly Delegate m_actionToExecute = action; }
    private sealed class LegacyClosure(Func<bool> native)
    {
        public Func<bool> argExportAction = native;
        public bool Invoke() => argExportAction();
    }
    private sealed class AsyncClosure(Func<Action, Task<bool>> native)
    {
        public Func<Action, Task<bool>> argExportAction = native;
        public Task<bool> Invoke(Action callback) => argExportAction(callback);
    }
    private static class LegacyRecipe
    {
        public static string CreateScaleCalcTakingQuickProofIntoAccount(string geometry, string raw, string recipe, int preview, bool ignoreCrop) =>
            $"{geometry}/{raw}/{recipe}/{preview}/{ignoreCrop}";
    }
    private enum ScaleByArea { ActiveCrop = 21, FullCrop = -2 }
    private interface IVariantGeometryTransformer;
    private sealed class Geometry : IVariantGeometryTransformer
    {
        public override string ToString() => "geometry";
    }
    private sealed class ICProcessSettings;
    private static class CurrentRecipe
    {
        public static string CreateScaleCalcTakingQuickProofIntoAccount(IVariantGeometryTransformer geometry, string raw, string recipe,
            int preview, ScaleByArea area, ICProcessSettings? settings = null) =>
            $"{geometry}/{raw}/{recipe}/{preview}/{area}/{(settings is null ? "none" : "supplied")}";
    }
    private struct Destination
    {
        public int bits;
        public bool c2paSign;
        public bool c2paWithEdits;
        public SafeHandle? c2paAuthor;
    }
    private sealed class EmptyAuthor() : SafeHandle(IntPtr.Zero, false)
    {
        public override bool IsInvalid => false;
        protected override bool ReleaseHandle() => true;
    }
}
