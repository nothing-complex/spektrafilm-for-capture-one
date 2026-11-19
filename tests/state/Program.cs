using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length != 0)
{
    var childStore = new VariantLookStore(args[1], args[2]);
    var childVariant = Guid.Parse(args[3]);
    if (args[0] == "--failed-save")
    {
        childStore.Read(childVariant);
        var file = Directory.GetFiles(args[1], childVariant.ToString("N") + ".json", SearchOption.AllDirectories).Single();
        using var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        childStore.Queue(childVariant, Sample(2.2));
        try { childStore.Flush(); return 12; }
        catch (IOException) { return 0; }
    }
    if (args[0] == "--verify-grain")
        return childStore.GrainReceipts.Read(childVariant) is { } receipt
            && receipt.Original == new GrainValues(1, 7, 30)
            && receipt.Matches(new GrainValues(2, 35, 60))
            && receipt.Matches(new GrainValues(2, 48, 72)) ? 0 : 15;
    if (args[0] == "--verify")
        return childStore.Read(childVariant)?.Look.Exposure == double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) ? 0 : 13;
    return 14;
}

var scratch = Path.Combine(AppContext.BaseDirectory, "state-contracts-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
int assertions = 0;
void Check(string name, bool value)
{
    if (!value) throw new Exception("FAILED: " + name);
    assertions++;
}
void Throws<T>(string name, Action action) where T : Exception
{
    try { action(); }
    catch (T) { assertions++; return; }
    throw new Exception("FAILED (no expected exception): " + name);
}
string StateFile(string root, Guid id) => Directory.GetFiles(root, id.ToString("N") + ".json", SearchOption.AllDirectories).Single();
var root = Path.Combine(scratch, "Variant looks");
var catalog = Path.Combine(scratch, "test.cocatalogdb");
var id = Guid.NewGuid();
var original = Sample(.7);
var store = new VariantLookStore(root, catalog);
Check("missing means no saved look", store.Read(id) is null);
store.Queue(id, original);
store.Flush();
var stateFile = StateFile(root, id);
Check("roundtrip", new VariantLookStore(root, catalog).Read(id)?.Look.Exposure == .7);
Check("case-only catalog path", new VariantLookStore(root, catalog.ToUpperInvariant()).Read(id) is not null);
Check("committed queues empty", !store.HasPending);
File.SetLastWriteTimeUtc(stateFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
store.Queue(id, original);
store.Flush();
Check("identical committed value skips disk replacement", File.GetLastWriteTimeUtc(stateFile).Year == 2020);

var bytes = File.ReadAllBytes(stateFile);
Throws<IOException>("write failure before replace", () => PanelFiles.WriteAtomically(stateFile, stream =>
{
    stream.Write(Encoding.UTF8.GetBytes("partial"));
    throw new IOException("Injected write failure");
}));
Check("write failure preserves committed bytes", File.ReadAllBytes(stateFile).SequenceEqual(bytes));
Throws<IOException>("final guard failure", () => PanelFiles.WriteAtomically(stateFile, stream => stream.WriteByte(1), () => throw new IOException("Rejected commit")));
Check("final guard failure preserves file", File.ReadAllBytes(stateFile).SequenceEqual(bytes));
Check("atomic write temporary files cleaned", Directory.GetFiles(Path.GetDirectoryName(stateFile)!, ".spektra-*.tmp").Length == 0);

var readLockedId = Guid.NewGuid();
store.Queue(readLockedId, Sample(2.3));
store.Flush();
var readLockedPath = StateFile(root, readLockedId);
var readLockedBytes = File.ReadAllBytes(readLockedPath);
using (var locked = new FileStream(readLockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
{
    Throws<LookReadUnavailableException>("sharing violation is a retryable read", () => store.Read(readLockedId));
    Check("sharing violation does not mark the look corrupt", !store.IsBlocked(readLockedId));
}
Check("retry reads the original look after the sharing violation", store.Read(readLockedId)?.Look.Exposure == 2.3);
Check("retry preserves the original file", File.ReadAllBytes(readLockedPath).SequenceEqual(readLockedBytes));

using (var locked = new FileStream(stateFile, FileMode.Open, FileAccess.Read, FileShare.None))
{
    store.Queue(id, Sample(1.3));
    Throws<IOException>("failed primary save reports recovery", store.Flush);
    Check("pending retained", store.HasPending);
    Check("failed primary has durable recovery", !store.HasUnprotectedPending);
    var replacementPanelStore = new VariantLookStore(root, catalog);
    Check("pending survives store replacement", replacementPanelStore.Read(id)?.Look.Exposure == 1.3);
    Check("recovery checkpoint durable", Directory.GetFiles(Path.Combine(scratch, "Look recovery"), "*.pending.json", SearchOption.AllDirectories).Length == 1);
}
new VariantLookStore(root, catalog).Flush();
Check("retry saves pending", new VariantLookStore(root, catalog).Read(id)?.Look.Exposure == 1.3);
Check("committed checkpoint removed", Directory.GetFiles(Path.Combine(scratch, "Look recovery"), "*.pending.json", SearchOption.AllDirectories).Length == 0);

var mutable = Sample(.4);
store.Queue(id, mutable);
mutable.Look.Development["film_gamma"] = 1.8;
mutable.Optics.Numbers["halation-amount"] = 3;
store.Flush();
var snapshot = new VariantLookStore(root, catalog).Read(id)!;
Check("queued development immutable", snapshot.Look.Development["film_gamma"] == 1);
Check("queued optics immutable", snapshot.Optics.Numbers["halation-amount"] == 0);

// Real process exit after a failed primary write, without relying on static state.
RunChild("--failed-save", root, catalog, id.ToString());
var reopened = new VariantLookStore(root, catalog);
Check("process restart recovers pending checkpoint", reopened.Read(id)?.Look.Exposure == 2.2);
Check("recovery is explained", reopened.RecoveryNotice is not null);
reopened.Flush();
RunChild("--verify", root, catalog, id.ToString(), "2.2");
Check("fresh process sees final durable value", true);

foreach (var kind in new[] { "corrupt", "future", "unknown", "missing", "duplicate", "missing-enabled", "missing-version", "missing-exposure", "missing-grain-type", "invalid-numeric" })
{
    var badId = Guid.NewGuid();
    store.Queue(badId, Sample(.8)); store.Flush();
    var path = StateFile(root, badId);
    var node = JsonNode.Parse(File.ReadAllText(path))!;
    string bad;
    switch (kind)
    {
        case "corrupt": bad = "{truncated"; break;
        case "future": node["Version"] = 2; bad = node.ToJsonString(); break;
        case "unknown": node["FutureParameter"] = 7; bad = node.ToJsonString(); break;
        case "missing": node["Look"] = null; bad = node.ToJsonString(); break;
        case "duplicate": bad = node.ToJsonString().Replace("\"Enabled\":true", "\"Enabled\":true,\"Enabled\":false"); break;
        case "missing-enabled": node.AsObject().Remove("Enabled"); bad = node.ToJsonString(); break;
        case "missing-version": node.AsObject().Remove("Version"); bad = node.ToJsonString(); break;
        case "missing-exposure": node["Look"]!.AsObject().Remove("Exposure"); bad = node.ToJsonString(); break;
        case "missing-grain-type": node["Grain"]!.AsObject().Remove("Type"); bad = node.ToJsonString(); break;
        default: node["Look"]!["Exposure"] = 100; bad = node.ToJsonString(); break;
    }
    File.WriteAllText(path, bad);
    var broken = new VariantLookStore(root, catalog);
    Throws<InvalidDataException>(kind + " blocked read", () => broken.Read(badId));
    Check(kind + " blocked flag", broken.IsBlocked(badId));
    Throws<InvalidDataException>(kind + " blocked write", () => broken.Queue(badId, original));
    Check(kind + " original unchanged", File.ReadAllText(path) == bad);
    Check(kind + " preserved copy", Directory.GetFiles(Path.Combine(scratch, "Look recovery"), badId.ToString("N") + "-unreadable-*.json", SearchOption.AllDirectories)
        .Any(p => File.ReadAllText(p) == bad));
    Check(kind + " block survives panel disposal", new VariantLookStore(root, catalog).IsBlocked(badId));
    broken.ResetBlocked(badId, Sample(.6));
    Check(kind + " explicit reset commits new state", broken.Read(badId)?.Look.Exposure == .6);
    Check(kind + " reset preserves original", Directory.GetFiles(Path.Combine(scratch, "Look recovery"), badId.ToString("N") + "-before-reset-*.json", SearchOption.AllDirectories)
        .Any(p => File.ReadAllText(p) == bad));
}

var legacyId = Guid.NewGuid();
store.Queue(legacyId, Sample(.3)); store.Flush();
var legacyPath = StateFile(root, legacyId);
var legacy = JsonNode.Parse(File.ReadAllText(legacyPath))!;
legacy.AsObject().Remove("Grain");
legacy["Look"]!.AsObject().Remove("FilmPushPullMode");
File.WriteAllText(legacyPath, legacy.ToJsonString());
var legacySaved = new VariantLookStore(root, catalog).Read(legacyId)!;
Check("legacy optional push pull field defaults safely", legacySaved.Look.FilmPushPullMode == "standard");
Check("legacy absent native grain remains supported", legacySaved.Grain is null);

var externalId = Guid.NewGuid();
store.Queue(externalId, Sample(.1)); store.Flush();
store.Queue(externalId, Sample(.2));
var externalFile = StateFile(root, externalId);
PanelFiles.WritePreset(externalFile, Sample(.3));
Throws<IOException>("external edit blocks pending overwrite", store.Flush);
Check("external edit preserved", PanelFiles.ReadPreset<VariantLook>(externalFile).Look.Exposure == .3);
Check("pending conflict blocks", store.IsBlocked(externalId));
store.ResetBlocked(externalId, Sample(.4));

var unchangedId = Guid.NewGuid();
store.Queue(unchangedId, Sample(.1)); store.Flush();
var unchangedPath = StateFile(root, unchangedId);
PanelFiles.WritePreset(unchangedPath, Sample(.3));
store.Queue(unchangedId, Sample(.1));
Throws<IOException>("identical queued value detects external edit", store.Flush);
Check("no-op optimization preserves external edit", PanelFiles.ReadPreset<VariantLook>(unchangedPath).Look.Exposure == .3);
store.ResetBlocked(unchangedId, Sample(.4));

// Neither destination can be written: the process keeps the edit and reports
// the crash-durability failure explicitly instead of claiming it was saved.
var unavailableRoot = Path.Combine(scratch, "unavailable", "Variant looks");
var unavailable = new VariantLookStore(unavailableRoot, catalog);
var unavailableId = Guid.NewGuid();
unavailable.Queue(unavailableId, Sample(.5)); unavailable.Flush();
File.WriteAllText(Path.Combine(scratch, "unavailable", "Look recovery"), "not a directory");
using (var locked = new FileStream(StateFile(unavailableRoot, unavailableId), FileMode.Open, FileAccess.Read, FileShare.None))
{
    unavailable.Queue(unavailableId, Sample(.9));
    try { unavailable.Flush(); throw new Exception("Expected durability error"); }
    catch (IOException error) { Check("both storage failures explicitly say not saved", error.Message.Contains("NOT safely saved", StringComparison.Ordinal)); }
    Check("both storage failures expose unprotected state", unavailable.HasUnprotectedPending);
    Check("unprotected pending survives panel disposal", new VariantLookStore(unavailableRoot, catalog).Read(unavailableId)?.Look.Exposure == .9);
}
unavailable.Flush();
Check("unprotected state clears after successful retry", !unavailable.HasPending && !unavailable.HasUnprotectedPending);

// A single locked destination must not prevent another image's queued write.
var otherId = Guid.NewGuid();
store.Queue(otherId, Sample(1.1));
using (var locked = new FileStream(stateFile, FileMode.Open, FileAccess.Read, FileShare.None))
{
    store.Queue(id, Sample(1.2));
    Throws<IOException>("aggregate blocked save", store.Flush);
    Check("other image saved despite one failure", PanelFiles.ReadPreset<VariantLook>(StateFile(root, otherId)).Look.Exposure == 1.1);
}
store.Flush();
Throws<InvalidDataException>("nonfinite numeric rejected", () => store.Queue(id, Sample(.5) with { Look = Sample(.5).Look with { Strength = double.NaN } }));
Throws<InvalidDataException>("invalid grain rejected", () => store.Queue(id, Sample(.5) with { Grain = new(99, 0, 0) }));
Throws<InvalidDataException>("missing optical dictionary rejected", () => store.Queue(id, Sample(.5) with { Optics = new(null!, new(), new()) }));
var tooLarge = Path.Combine(scratch, "too-large.json");
File.WriteAllText(tooLarge, new string(' ', 65537));
Throws<InvalidDataException>("size guard", () => PanelFiles.ReadPreset<object>(tooLarge));
Throws<InvalidDataException>("write size guard", () => PanelFiles.WritePreset(stateFile, new string('x', 65536)));
Check("oversized write preserves previous committed value", PanelFiles.ReadPreset<VariantLook>(stateFile).Look.Exposure == 1.2);

var presetSource = Sample(1.3) with { Enabled = false, Grain = new GrainControls(4, 18, 62) };
var presetPath = Path.Combine(scratch, "all-controls.spektra.json");
PanelFiles.WritePreset(presetPath, LookPreset.From(presetSource));
var presetCopy = PanelFiles.ReadPreset<LookPreset>(presetPath);
presetCopy.Validate();
Check("current preset roundtrips effect, optics, enable and staged grain",
    presetCopy.SchemaVersion == 2 && presetCopy.Enabled == false && presetCopy.Grain == presetSource.Grain
    && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(presetSource.Look), JsonSerializer.SerializeToNode(presetCopy.Look))
    && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(presetSource.Optics), JsonSerializer.SerializeToNode(presetCopy.Optics)));
var oldPreset = JsonSerializer.Deserialize<LookPreset>(JsonSerializer.Serialize(new { Look = presetSource.Look }))!;
oldPreset.Validate();
Check("unversioned preset accepts missing optional sections", oldPreset.SchemaVersion == 0 && oldPreset.Optics is null
    && oldPreset.Enabled is null && oldPreset.Grain is null);
var priorPreset = new LookPreset(presetSource.Look, presetSource.Optics, 1);
priorPreset.Validate();
Check("version one accepts both original sections", priorPreset.Look.Exposure == 1.3 && priorPreset.Optics is not null);
Throws<InvalidDataException>("old preset rejects settings reserved for newer versions",
    () => (priorPreset with { Enabled = false }).Validate());
Throws<InvalidDataException>("preset validates staged native grain before apply",
    () => (presetCopy with { Grain = new GrainControls(13, 90, 60) }).Validate());
Throws<InvalidDataException>("preset cannot apply unknown future format", () => (presetCopy with { SchemaVersion = 3 }).Validate());

var undoVariant = Guid.NewGuid();
var undoFirst = Sample(0.2);
var undoSecond = Sample(1.7);
store.Queue(undoVariant, undoSecond);
store.Flush();
store.ReplaceIfCurrent(undoVariant, undoSecond, undoFirst);
Check("undo writes the captured variant durably", new VariantLookStore(root, catalog).Read(undoVariant)?.Look.Exposure == 0.2);
store.ReplaceIfCurrent(undoVariant, undoFirst, undoSecond);
Check("redo writes the same captured variant durably", new VariantLookStore(root, catalog).Read(undoVariant)?.Look.Exposure == 1.7);
var undoFile = StateFile(root, undoVariant);
PanelFiles.WriteCompactPreset(undoFile, Sample(2.4));
Throws<InvalidOperationException>("stale history cannot overwrite a newer saved look", () => store.ReplaceIfCurrent(undoVariant, undoSecond, undoFirst));
Check("history conflict preserves the newer persisted look", new VariantLookStore(root, catalog).Read(undoVariant)?.Look.Exposure == 2.4);

var grainId = Guid.NewGuid();
var grainOriginal = new GrainValues(1, 7, 30);
var grainFirst = new GrainValues(2, 35, 60);
var grainSecond = new GrainValues(2, 48, 72);
Check("legacy variant has no grain receipt", store.GrainReceipts.Read(grainId) is null);
var initialReceipt = store.GrainReceipts.Prepare(grainId, grainOriginal, grainFirst);
Check("receipt matches the exact applied native values", initialReceipt.Matches(grainFirst) && !initialReceipt.Matches(grainOriginal));
Check("receipt rejects unrelated edits", !initialReceipt.Matches(grainFirst with { Impact = 36 }));
var nextReceipt = store.GrainReceipts.Prepare(grainId, grainFirst, grainSecond);
Check("journal retains original baseline when applying another grain setting", nextReceipt.Original == grainOriginal);
Check("prepared apply can recover the previous or newly applied values", nextReceipt.Matches(grainFirst) && nextReceipt.Matches(grainSecond));
Check("separate catalog cannot use grain receipt",
    new VariantLookStore(root, Path.Combine(scratch, "other.cocatalogdb")).GrainReceipts.Read(grainId) is null);
Check("separate variant cannot use grain receipt", store.GrainReceipts.Read(Guid.NewGuid()) is null);
RunChild("--verify-grain", root, catalog, grainId.ToString());
Check("fresh process read the baseline and both crash-window values", true);
var grainFile = Directory.GetFiles(Path.Combine(scratch, "Look recovery"), grainId.ToString("N") + ".grain.json", SearchOption.AllDirectories).Single();
var grainBytes = File.ReadAllBytes(grainFile);
using (var locked = new FileStream(grainFile, FileMode.Open, FileAccess.Read, FileShare.None))
    Throws<IOException>("receipt write cannot proceed when existing data is locked", () => store.GrainReceipts.Prepare(grainId, grainSecond, grainFirst));
Check("failed grain receipt replacement preserves durable values", grainBytes.AsSpan().SequenceEqual(File.ReadAllBytes(grainFile)));
File.WriteAllText(grainFile, "{\"Version\":9}");
Throws<JsonException>("future invalid receipt blocks reapply and remains recoverable", () => store.GrainReceipts.Prepare(grainId, grainSecond, grainFirst));
Check("invalid grain data remains untouched", File.ReadAllText(grainFile) == "{\"Version\":9}");
File.WriteAllBytes(grainFile, grainBytes);
var replacementReceipt = store.GrainReceipts.Prepare(grainId, grainSecond with { Impact = 53 }, grainFirst);
Check("explicit apply after external changes takes the current native values as baseline", replacementReceipt.Original.Impact == 53);

var nativeValues = new[] { 1, 2, 3 };
var closedScopes = 0;
var nativeSteps = Enumerable.Range(0, 3).Select(index => new NativeAdjustmentTransaction.Step(
    "control " + index, () => nativeValues[index] += 10, () => nativeValues[index] = index + 1)).ToArray();
NativeAdjustmentTransaction.Execute(nativeSteps, () => closedScopes++);
Check("native transaction applies all fields", nativeValues.SequenceEqual(new[] { 11, 12, 13 }));
Check("native transaction closes successful undo group once", closedScopes == 1);
nativeValues = new[] { 1, 2, 3 };
var restorationOrder = new List<int>();
var failingSteps = Enumerable.Range(0, 3).Select(index => new NativeAdjustmentTransaction.Step(
    "control " + index,
    () => { nativeValues[index] += 10; if (index == 1) throw new IOException("setter mutated then failed"); },
    () => { nativeValues[index] = index + 1; restorationOrder.Add(index); })).ToArray();
Throws<IOException>("native setter failure preserved", () => NativeAdjustmentTransaction.Execute(failingSteps, () => closedScopes++));
Check("native partial mutation fully rolled back", nativeValues.SequenceEqual(new[] { 1, 2, 3 }));
Check("native rollback includes failing setter in reverse order", restorationOrder.SequenceEqual(new[] { 1, 0 }));
Check("native failure closes undo group once", closedScopes == 2);
var rollbackFailure = new NativeAdjustmentTransaction.Step[]
{
    new("first", () => nativeValues[0] = 11, () => nativeValues[0] = 1),
    new("second", () => { nativeValues[1] = 12; throw new IOException("setter failed"); }, () => throw new IOException("rollback failed")),
};
try
{
    NativeAdjustmentTransaction.Execute(rollbackFailure, () => { closedScopes++; throw new IOException("undo close failed"); });
    throw new Exception("Expected aggregate native error");
}
catch (AggregateException error)
{
    Check("native error retains setter rollback and scope failures", error.InnerExceptions.Count == 3);
    Check("native rollback continues after one restoration fails", nativeValues[0] == 1);
    Check("native scope closed once even after rollback failure", closedScopes == 3);
}
Throws<IOException>("native scope error is surfaced after successful setters", () => NativeAdjustmentTransaction.Execute(nativeSteps,
    () => { closedScopes++; throw new IOException("undo close failed after success"); }));
Check("native close error cannot skip executed changes", nativeValues[0] == 11);
Check("native close error closes only once", closedScopes == 4);
Console.WriteLine($"PASS: {assertions} state/recovery assertions, including separate-process recovery. Fixtures: {scratch}");
return 0;

static VariantLook Sample(double exposure) => new(new("kodak_portra_400", "kodak_portra_endura", exposure, .2, 1,
    "print", "hanatos2025", false, new() { ["film_gamma"] = 1 }),
    new(new() { ["halation-amount"] = 0 }, new() { ["input-color"] = "srgb" }, new()), true, Grain: new(2,25,50));

static void RunChild(params string[] arguments)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    info.Environment.Remove("DOTNET_STARTUP_HOOKS");
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
    foreach (var arg in arguments) info.ArgumentList.Add(arg);
    using var child = Process.Start(info)!;
    if (!child.WaitForExit(30000)) { child.Kill(); throw new Exception("Child recovery probe timed out"); }
    if (child.ExitCode != 0) throw new Exception($"Child recovery probe failed: {child.ExitCode}");
}

internal static class SpatialPreview
{
    internal sealed record Settings(Dictionary<string,double> Numbers, Dictionary<string,string> Selections, Dictionary<string,bool> Switches);
}
