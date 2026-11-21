using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args is ["--measure-durable-edits", var measurementDirectory])
    return DurableEditProbe.Run(measurementDirectory);
if (args is ["--measure-look-journal", var journalMeasurementDirectory])
    return DurableEditProbe.Run(journalMeasurementDirectory, DurableEditProbe.Storage.Append);
if (args is ["--measure-look-generation-pair", var pairMeasurementDirectory])
    return DurableEditProbe.Run(pairMeasurementDirectory, DurableEditProbe.Storage.GenerationPair);
if (args is ["--hold-look-journal", var journalPath, var journalVariant, var journalStage, var journalMarker])
    return DurableLookJournalProbe.Hold(journalPath, Guid.Parse(journalVariant), journalStage, journalMarker);
if (args is ["--hold-look-generation", var generationDirectory, var generationCatalog, var generationVariant, var generationStage, var generationMarker])
    return LookGenerationContracts.Hold(generationDirectory, generationCatalog, Guid.Parse(generationVariant), generationStage, generationMarker);
if (args is ["--hold-journal-for-backup", var archiveDirectory, var archiveCatalog, var archiveVariant, var archiveCurrent, var archivePrevious, var archiveMarker])
    return LookGenerationContracts.HoldForBackup(archiveDirectory, archiveCatalog, Guid.Parse(archiveVariant), archiveCurrent, archivePrevious, archiveMarker);
if (args is ["--measure-diagnostic-log", var diagnosticsDirectory, var seconds])
    return await PanelLogContracts.SoakAsync(diagnosticsDirectory, int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture));
if (args is ["--verify-compiled-controls", var parameterManifest])
{
    OpticalControlContracts.VerifyCompiled(parameterManifest);
    return 0;
}
if (args is ["--host-signatures"])
{
    var passed = 0;
    await NativeHostShapeContracts.RunAsync((name, valid) =>
    {
        if (!valid) throw new InvalidOperationException("FAILED: " + name);
        Console.WriteLine("PASS " + name);
        passed++;
    });
    Console.WriteLine($"PASS: {passed} host signature assertions.");
    return 0;
}

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
    if (args[0] == "--verify-previous")
    {
        var previous = childStore.ReadOutcome(childVariant);
        return previous.State == LookLoadState.Recovered && previous.ReadOnly
            && previous.Look?.Look.Exposure == double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) ? 0 : 16;
    }
    if (args[0] == "--verify")
        return childStore.Read(childVariant)?.Look.Exposure == double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) ? 0 : 13;
    if (args[0] == "--commit-boundary")
    {
        var file = Directory.GetFiles(args[1], childVariant.ToString("N") + ".json", SearchOption.AllDirectories).Single();
        void WaitForKill()
        {
            File.WriteAllText(args[5], args[4]);
            Thread.Sleep(Timeout.Infinite);
        }
        PanelFiles.WriteAtomically(file, stream => JsonSerializer.Serialize(stream, Sample(2.4)),
            args[4] == "before-replace" ? WaitForKill : null, discardOnProcessExit: true);
        WaitForKill();
    }
    if (args[0] == "--batch-commit-boundary")
    {
        var at = args[4];
        var prefix = Guid.Parse(args[6]);
        using var batch = childStore.BatchHistory.Start(2);
        childStore.CommitBatchChange(prefix, null, Sample(1), (before, after) => batch.Prepare(prefix, before, after));
        batch.Committed(prefix);
        childStore.CommitBatchChange(childVariant, Sample(.6), Sample(1.5), (before, after) =>
        {
            batch.Prepare(childVariant, before, after);
            if (at == "before-replace") WaitForKill();
        });
        WaitForKill();

        void WaitForKill()
        {
            File.WriteAllText(args[5], at);
            Thread.Sleep(Timeout.Infinite);
        }
    }
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

DurableLookJournalProbe.CheckRecovery(scratch, Check, StartChild);
LookGenerationContracts.CheckRecovery(scratch, Check, StartChild);
string StateFile(string root, Guid id) => Directory.GetFiles(root, id.ToString("N") + ".json", SearchOption.AllDirectories).Single();
var root = Path.Combine(scratch, "Variant looks");
var catalog = Path.Combine(scratch, "test.cocatalogdb");
Check("startup selection parses a fully qualified catalog without requiring disk access",
    ApprovedCatalog.ParseSelection(catalog) == catalog && !File.Exists(catalog));
Check("startup selection refuses relative catalogs and other document types",
    ApprovedCatalog.ParseSelection("relative.cocatalogdb") is null
    && ApprovedCatalog.ParseSelection(Path.ChangeExtension(catalog, ".cosessiondb")) is null);
if (OperatingSystem.IsWindows())
{
    const string unavailableShare = @"\\example.invalid\fixture-share\nested\..\test.cocatalogdb";
    Check("an unavailable UNC catalog selection resolves lexically",
        ApprovedCatalog.ParseSelection(unavailableShare) == @"\\example.invalid\fixture-share\test.cocatalogdb");
}
var id = Guid.NewGuid();
var original = Sample(.7);
var store = new VariantLookStore(root, catalog);
Check("missing means no saved look", store.Read(id) is null);
Check("missing look is reported as a default", !store.HasCommitted(id));
store.Queue(id, original);
store.Flush();
Check("committed look is reported as saved", store.HasCommitted(id));
var stateFile = StateFile(root, id);
Check("roundtrip", new VariantLookStore(root, catalog).Read(id)?.Look.Exposure == .7);
Check("case-only catalog path", new VariantLookStore(root, catalog.ToUpperInvariant()).Read(id) is not null);
var identityRoot = Path.Combine(scratch, "variant-identity");
var identityStore = new VariantLookStore(Path.Combine(identityRoot, "Variant looks"), catalog);
var identityDocument = Guid.NewGuid();
var identityImage = Guid.NewGuid();
var identityFile = Path.Combine(identityRoot, "Look recovery", identityStore.CatalogIdentity, $"{id:N}.identity.json");
identityStore.RecordVariantIdentity(id, identityDocument, identityImage);
Check("saved variant identity is independently durable", PanelFiles.ReadPreset<SavedVariantIdentity>(identityFile)
    == new SavedVariantIdentity(1, identityDocument, id, identityImage));
File.SetLastWriteTimeUtc(identityFile, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
var identityStamp = File.GetLastWriteTimeUtc(identityFile);
identityStore.RecordVariantIdentity(id, identityDocument, identityImage);
identityStore.Queue(id, original);
identityStore.Flush();
Check("repeat observations and look writes reuse the verified identity without changing look bytes",
    File.GetLastWriteTimeUtc(identityFile) == identityStamp
    && File.ReadAllBytes(StateFile(Path.Combine(identityRoot, "Variant looks"), id)).AsSpan().SequenceEqual(File.ReadAllBytes(stateFile)));
Throws<InvalidDataException>("variant identity cannot be reassigned to another RAW",
    () => identityStore.RecordVariantIdentity(id, identityDocument, Guid.NewGuid()));
Check("the conflicting native image leaves the original identity intact",
    PanelFiles.ReadPreset<SavedVariantIdentity>(identityFile).Image == identityImage);
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

var boundaryId = Guid.NewGuid();
var boundaryRoot = Path.Combine(scratch, new string('s', 140), "Variant looks");
var boundaryStore = new VariantLookStore(boundaryRoot, catalog);
boundaryStore.Queue(boundaryId, Sample(.6));
boundaryStore.Flush();
var boundaryFile = StateFile(boundaryRoot, boundaryId);
var boundaryBytes = File.ReadAllBytes(boundaryFile);
Check("interruption fixture uses a long Windows path", boundaryFile.Length >= 260);
KillAtBoundary("before-replace", boundaryRoot, catalog, boundaryId, scratch);
Check("process death after staged flush preserves committed bytes", File.ReadAllBytes(boundaryFile).SequenceEqual(boundaryBytes));
if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(boundaryFile)!).DriveFormat == "NTFS")
    Check("process death on NTFS releases its own staging file", !Directory.EnumerateFiles(Path.GetDirectoryName(boundaryFile)!, ".spektra-*.tmp").Any());
RunChild("--verify", boundaryRoot, catalog, boundaryId.ToString(), "0.6");
KillAtBoundary("after-replace", boundaryRoot, catalog, boundaryId, scratch);
RunChild("--verify", boundaryRoot, catalog, boundaryId.ToString(), "2.4");
Check("process death after replace preserves exact published bytes", File.ReadAllBytes(boundaryFile).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(Sample(2.4))));

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

using (var locked = new FileStream(readLockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
{
    store.Queue(readLockedId, Sample(2.3));
    Throws<IOException>("an identical look whose primary cannot be checked still gets a fallback", store.Flush);
    Check("temporary lock does not lose the identical edit", store.HasPending && !store.HasUnprotectedPending);
}
store.Flush();
Check("the same look is restored without changing content", File.ReadAllBytes(readLockedPath).SequenceEqual(readLockedBytes));

using (var locked = new FileStream(stateFile, FileMode.Open, FileAccess.Read, FileShare.None))
{
    store.Queue(id, Sample(1.3));
    Throws<IOException>("failed primary save reports recovery", store.Flush);
    Check("pending retained", store.HasPending);
    Check("failed primary has durable recovery", !store.HasUnprotectedPending);
    var replacementPanelStore = new VariantLookStore(root, catalog);
    Check("pending survives store replacement", replacementPanelStore.Read(id)?.Look.Exposure == 1.3);
    var checkpoints = Directory.GetFiles(Path.Combine(scratch, "Look recovery"), "*.pending.json", SearchOption.AllDirectories);
    Check("recovery checkpoint durable", checkpoints.Length == 1);
    store.Queue(id, Sample(1.3));
    Check("queueing an equal pending value retains its durability", store.HasPending && !store.HasUnprotectedPending);
    var checkpoint = checkpoints[0];
    var checkpointBytes = File.ReadAllBytes(checkpoint);
    File.SetLastWriteTimeUtc(checkpoint, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    var other = Guid.NewGuid();
    store.Queue(other, Sample(1.6));
    store.FlushVariant(other);
    Check("selected variant commits while another primary is locked", store.HasCommitted(other) && store.Read(other)?.Look.Exposure == 1.6);
    Check("selected variant save leaves the unrelated durable recovery untouched",
        store.HasPending && !store.HasUnprotectedPending
        && File.GetLastWriteTimeUtc(checkpoint).Year == 2019 && File.ReadAllBytes(checkpoint).SequenceEqual(checkpointBytes));
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

TestPreviousGeneration(store, root, catalog, scratch);

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

const string verifiedHostHash = "4E17B2AB68CB60CCE18BB92CF0C829A93A62024B15354136FD30A3F9EED2E342";
var supportedHost = HostCompatibility.Match("16.4.6.2414", "x64", verifiedHostHash);
Check("verified native build and capability list are embedded", supportedHost is not null
    && supportedHost.Capabilities.Contains("native-export") && supportedHost.Capabilities.Contains("viewer-overlay"));
Check("unverified Capture One version cannot use the verified adapter",
    HostCompatibility.Match("16.7.0.0", "x64", verifiedHostHash) is null);
Check("an altered assembly and a different process architecture are separately refused",
    HostCompatibility.Match("16.4.6.2414", "x64", new string('a', 64)) is null
    && HostCompatibility.Match("16.4.6.2414", "arm64", verifiedHostHash) is null);
foreach (var profiles in new[] { Array.Empty<CaptureOneHost>(), new[] { supportedHost!, supportedHost! },
    new[] { supportedHost! with { Capabilities = new[] { "duplicate", "duplicate" } } } })
    Throws<InvalidDataException>("bad compatibility list is rejected", () =>
    {
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { SchemaVersion = 1, Hosts = profiles }));
        HostCompatibility.Parse(stream);
    });

var presetSource = Sample(1.3) with { Enabled = false, Grain = new GrainControls(4, 18, 62) };

var panelChoicesPath = Path.Combine(scratch, "Panel choices.json");
var panelChoices = new PanelChoices(panelChoicesPath, _ => { });
panelChoices.ToggleFavorite(false, "kodak_portra_400");
panelChoices.ToggleFavorite(true, "kodak_portra_endura");
var presetChoices = Enumerable.Range(0, 8).Select(i => Path.Combine(scratch, $"Choice {i}.spektra.json")).ToArray();
foreach (var path in presetChoices)
{
    File.WriteAllText(path, "{}");
    panelChoices.RememberPreset(path);
}
panelChoices.RememberPreset(presetChoices[4]);
var choicesAfterRestart = new PanelChoices(panelChoicesPath, _ => { });
Check("stock favorites reopen as independent typed lists",
    choicesAfterRestart.IsFavorite(false, "kodak_portra_400") && choicesAfterRestart.IsFavorite(true, "kodak_portra_endura")
    && !choicesAfterRestart.IsFavorite(true, "kodak_portra_400"));
Check("recent preset history is bounded, most-recent first, and deduplicated",
    choicesAfterRestart.RecentPresets.SequenceEqual(new[] { presetChoices[4], presetChoices[7], presetChoices[6], presetChoices[5], presetChoices[3], presetChoices[2] }));
File.Delete(presetChoices[4]);
Check("removed preset files are excluded", choicesAfterRestart.RecentPresets.First() == presetChoices[7]);
choicesAfterRestart.ToggleFavorite(false, "kodak_portra_400");
Check("removing a favourite does not remove another stock class",
    !choicesAfterRestart.IsFavorite(false, "kodak_portra_400") && choicesAfterRestart.IsFavorite(true, "kodak_portra_endura"));
var futureChoices = "{\"Version\":42,\"Films\":[],\"Papers\":[],\"RecentPresets\":[]}";
File.WriteAllText(panelChoicesPath, futureChoices);
var unavailableChoices = new PanelChoices(panelChoicesPath, _ => { });
Throws<InvalidOperationException>("future panel choices cannot be silently replaced", () => unavailableChoices.ToggleFavorite(false, "kodak_portra_400"));
Check("future favourites and preferences stay byte for byte", File.ReadAllText(panelChoicesPath) == futureChoices);

var rendererFixture = Path.Combine(scratch, "renderer");
Directory.CreateDirectory(rendererFixture);
var rendererManifestPath = Path.Combine(rendererFixture, "parameter-capabilities.json");
var modeOption = new { key = "mode", kind = "choice", @default = "print", choices = new[] { "print", "scan", "process_negative" } };
var fixtureOptions = new object[]
{
    modeOption,
    new { key = "film-gamma", kind = "number", minimum = 0.100000001, maximum = 2d, @default = 1d },
    new { key = "grain-seed", kind = "integer", minimum = 0, maximum = int.MaxValue, @default = 1 },
    new { key = "uv-enabled", kind = "boolean", @default = false },
};
void WriteRendererFixture(bool academyPrinterDensity, object[] parameters, int protocol = 2) => PanelFiles.WriteCompactPreset(rendererManifestPath, new
{
    schemaVersion = 1, rendererProtocol = protocol, parameterContract = new string('a', 64), parameters,
    scientific = new { academyPrinterDensity, films = new[] { new { id = "kodak_portra_400", hanatos2025 = true, hanatos2026 = true } } },
});
WriteRendererFixture(false, fixtureOptions);
var capabilities = RendererParameters.Read(scratch);
capabilities.VerifyArguments(new[] { "--mode", "print", "--film-gamma", "0.1", "--grain-seed", "2147483647" });
Check("continuous and integer controls bind their compiled parameter types",
    !capabilities.VerifyNumericControl("film-gamma", .1, 2, 1.3, .05)
    && capabilities.VerifyNumericControl("grain-seed", 0, 32767, 1, 1));
capabilities.VerifyBooleanControl("uv-enabled");
Throws<InvalidDataException>("numeric control refuses a narrower worker maximum",
    () => capabilities.VerifyNumericControl("film-gamma", .1, 2.01, 1, .05));
Throws<InvalidDataException>("numeric control refuses a default outside the panel range",
    () => capabilities.VerifyNumericControl("film-gamma", .5, 2, .1, .05));
Throws<InvalidDataException>("numeric control refuses a nonfinite UI bound",
    () => capabilities.VerifyNumericControl("film-gamma", double.NaN, 2, 1, .05));
Throws<InvalidDataException>("integer control refuses a fractional step",
    () => capabilities.VerifyNumericControl("grain-seed", 0, 32767, 1, .5));
Throws<InvalidDataException>("integer control rejects a fractional initial value even when it formats as an integer",
    () => capabilities.VerifyNumericControl("grain-seed", 0, 32767, 1.0000001, 1));
Throws<InvalidDataException>("integer control rejects a fractional endpoint even when it formats as an integer",
    () => capabilities.VerifyNumericControl("grain-seed", .0000001, 32767, 1, 1));
Throws<InvalidDataException>("numeric control refuses an absent renderer key",
    () => capabilities.VerifyNumericControl("deleted-number", 0, 1, 0, .1));
Throws<InvalidDataException>("boolean control refuses a compiled choice using the same name",
    () => capabilities.VerifyBooleanControl("mode"));
foreach (var malformed in new object[]
{
    new { key = "overflow", kind = "number", minimum = 0d, maximum = 1e100, @default = 1d },
    new { key = "fractional-bounds", kind = "integer", minimum = 0.25, maximum = 4.75, @default = 1 },
    new { key = "overflow", kind = "integer", minimum = 0, maximum = (long)int.MaxValue + 1, @default = 1 },
    new { key = "number-with-choices", kind = "number", minimum = 0d, maximum = 2d, @default = 1d, choices = new[] { "1" } },
    new { key = "bounded-boolean", kind = "boolean", minimum = 0, maximum = 1, @default = true },
    new { key = "bounded-choice", kind = "choice", minimum = 0, maximum = 1, @default = "one", choices = new[] { "one" } },
})
{
    WriteRendererFixture(false, new[] { malformed });
    Throws<InvalidDataException>("compiled capability rejects contradictory or unrepresentable numeric limits",
        () => RendererParameters.Read(scratch));
}
Check("renderer manifest respects float32 boundaries and calibrated method advertisements",
    capabilities.SupportsHanatos2026("kodak_portra_400") && !capabilities.SupportsHanatos2026("missing"));
Check("an uncalibrated APD mode is hidden even if the parser knows its name",
    !capabilities.SupportsChoice("mode", "process_negative") && capabilities.SupportsChoice("mode", "scan"));
Throws<InvalidDataException>("unsupported calibrated mode is refused in assembled arguments",
    () => capabilities.VerifyArguments(new[] { "--mode", "process_negative" }));
Throws<InvalidDataException>("manifest rejects a nonfinite setting", () => capabilities.VerifyArguments(new[] { "--film-gamma", "NaN" }));
Throws<InvalidDataException>("manifest rejects an out-of-range int", () => capabilities.VerifyArguments(new[] { "--grain-seed", "2147483648" }));
Throws<InvalidDataException>("manifest rejects unknown keys", () => capabilities.VerifyArguments(new[] { "--unknown", "0" }));
WriteRendererFixture(false, new object[] { modeOption, modeOption });
Throws<InvalidDataException>("manifest refuses duplicate renderer parameters", () => RendererParameters.Read(scratch));
WriteRendererFixture(false, fixtureOptions, protocol: 1);
Throws<InvalidDataException>("compiled parameter description must match the panel protocol", () => RendererParameters.Read(scratch));
WriteRendererFixture(true, fixtureOptions);
Check("compiled APD data enables the declared mode", RendererParameters.Read(scratch).SupportsChoice("mode", "process_negative"));

var filmDefaults = FilmControls.Development.Values.ToDictionary(item => item.StateKey, item => item.Initial);
var developmentKeys = FilmControls.Development.Values.Select(item => item.WorkerKey).OfType<string>().ToArray();
Check("the film control schema keeps 21 saved keys and 20 distinct wire mappings",
    filmDefaults.Count == 21 && developmentKeys.Length == 20 && developmentKeys.Distinct().Count() == 20
    && filmDefaults["print_exhaustion"] == 0);
LookStateValidation.ValidateDevelopment(filmDefaults);
foreach (var definition in FilmControls.Development.Values)
{
    foreach (var bound in new[] { definition.Minimum, definition.Maximum })
    {
        var settings = new Dictionary<string, double> { [definition.StateKey] = bound };
        var decoded = PanelFiles.DecodePreset<Dictionary<string, double>>(PanelFiles.EncodeCompactPreset(settings));
        LookStateValidation.ValidateDevelopment(decoded);
        Check($"{definition.StateKey} boundary {bound} roundtrips", decoded[definition.StateKey] == bound);
    }
    Throws<InvalidDataException>($"{definition.StateKey} rejects a value just outside its control range",
        () => LookStateValidation.ValidateDevelopment(new Dictionary<string, double>
        {
            [definition.StateKey] = Math.BitIncrement(definition.Maximum),
        }));
}
var manifestJson = File.ReadAllBytes(rendererManifestPath);
var malformedRandom = new Random(1167);
for (var iteration = 0; iteration < 64; iteration++)
{
    var length = malformedRandom.Next(manifestJson.Length - 1);
    File.WriteAllBytes(rendererManifestPath, manifestJson.AsSpan(0, length).ToArray());
    Throws<JsonException>($"compiled parameter JSON rejects truncated prefix {iteration}", () => RendererParameters.Read(scratch));
}
File.WriteAllBytes(rendererManifestPath, new byte[PanelFiles.MaxPresetBytes + 1]);
Throws<InvalidDataException>("compiled parameter description refuses an oversized payload", () => RendererParameters.Read(scratch));
File.WriteAllBytes(rendererManifestPath, manifestJson);
OpticalControlContracts.Run(Check);

var presetPath = Path.Combine(scratch, "all-controls.spektra.json");
PanelFiles.WritePreset(presetPath, LookPreset.From(presetSource));
var presetCopy = PanelFiles.ReadPreset<LookPreset>(presetPath);
presetCopy.Validate();
Check("version 2 presets keep their prior JSON shape",
    !File.ReadAllText(presetPath).Contains("\"Engine\"", StringComparison.Ordinal));
var presetEngine = new PresetEngineIdentity(new string('a', 40), new string('b', 64), new string('c', 64), new string('d', 64));
var presetWithIdentity = presetCopy with { SchemaVersion = 3, Engine = presetEngine };
var provenancePath = Path.Combine(scratch, "engine-provenance.spektra.json");
PanelFiles.WritePreset(provenancePath, presetWithIdentity);
var provenanceCopy = PanelFiles.ReadPreset<LookPreset>(provenancePath);
provenanceCopy.Validate();
Check("version 3 presets carry verified renderer and source digests with the same settings",
    provenanceCopy.Engine == presetEngine
    && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(provenanceCopy.Look), JsonSerializer.SerializeToNode(presetCopy.Look)));
Throws<InvalidDataException>("invalid version 3 build identity is refused",
    () => (provenanceCopy with { Engine = presetEngine with { AssetSha256 = "invalid" } }).Validate());
Throws<InvalidDataException>("version 3 provenance is mandatory", () => (provenanceCopy with { Engine = null }).Validate());
Throws<InvalidDataException>("version 2 cannot make a version 3 provenance claim",
    () => (provenanceCopy with { SchemaVersion = 2 }).Validate());
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

var presetDestination = Sample(-.9) with
{
    Optics = new(new() { ["halation-amount"] = 0.6 }, new() { ["input-color"] = "rec2020" }, new()),
};
for (var mask = 0; mask <= (int)LookSections.All; mask++)
{
    var sections = (LookSections)mask;
    var selected = presetCopy.SelectSections(presetDestination, sections);
    Check($"preset section mask {sections} only replaces selected settings",
        selected.Film == (sections.HasFlag(LookSections.Film) ? presetCopy.Look : presetDestination.Look)
        && selected.Optics == (sections.HasFlag(LookSections.Optics) ? presetCopy.Optics : presetDestination.Optics)
        && selected.Enabled == (sections.HasFlag(LookSections.Enabled) ? false : null)
        && selected.Grain == (sections.HasFlag(LookSections.Grain) ? presetCopy.Grain : null));
}
Check("old preset resets missing optics only when optics is selected",
    oldPreset.SelectSections(presetDestination, LookSections.All).Optics is null
    && oldPreset.SelectSections(presetDestination, LookSections.Film).Optics == presetDestination.Optics);
var optionalPreset = presetCopy with { Enabled = null, Optics = null, Grain = null };
var optionalSelected = optionalPreset.SelectSections(presetDestination, LookSections.All);
Check("new preset with absent sections preserves destination optics and both toggles",
    optionalSelected.Optics == presetDestination.Optics && optionalSelected.Enabled is null && optionalSelected.Grain is null);

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

var batchStore = new VariantLookStore(root, Path.Combine(scratch, "batch-looks.cocatalogdb"));
var batchDefaults = Sample(0);
var batchPreset = LookPreset.From(Sample(1.37) with { Enabled = false, Grain = new(5, 91, 78) });
var batchIds = Enumerable.Range(0, VariantLookBatch.MaxVariants).Select(_ => Guid.NewGuid()).ToArray();
var batchDisabled = Sample(-.63) with { Enabled = false, Grain = new(1, 16, 55) };
batchStore.Queue(batchIds[0], batchDisabled);
batchStore.Flush();
var batchChanges = batchIds.Select(id => VariantLookBatch.Prepare(batchStore, id, batchDefaults, batchPreset,
    LookSections.Film)!).ToArray();
Check("batch prepares 1,000 unique targets with their own unselected sections",
    batchChanges.Length == 1000 && batchChanges[0].After.Enabled == false && batchChanges[0].After.Grain == batchDisabled.Grain
    && batchChanges.Skip(1).All(change => change.SavedBefore is null && change.After.Enabled && change.After.Grain == batchDefaults.Grain));
using (var receipt = batchStore.BatchHistory.Start(batchIds.Length))
{
    foreach (var change in batchChanges)
    {
        batchStore.CommitBatchChange(change.Variant, change.SavedBefore, change.After,
            (before, after) => receipt.Prepare(change.Variant, before, after));
        receipt.Committed(change.Variant);
    }
    receipt.Finish(BatchResult.Completed);
}
Check("bounded journal reopens all 1,000 durable per-variant records", batchStore.BatchHistory.ReadLatest() is
    { Result: BatchResult.Completed, Selected: 1000, Uncertain: null } complete && complete.Applied.SequenceEqual(batchIds));
using (var stopped = batchStore.BatchHistory.Start(2))
    stopped.Finish(BatchResult.Stopped);
Check("latest receipt retains an explicit stop and keeps the earlier operation on disk",
    batchStore.BatchHistory.ReadLatest() is { Result: BatchResult.Stopped, Selected: 2, Applied.Length: 0 }
    && Directory.GetFiles(Path.Combine(scratch, "Look batches", batchStore.CatalogIdentity), "*.jsonl").Length == 2);
Check("batch persists all 1,000 published looks", batchIds.All(id => batchStore.ReadForBatch(id)?.Look.Exposure == 1.37));
Check("already applied sections plan no change",
    VariantLookBatch.Prepare(batchStore, batchIds[0], batchDefaults, batchPreset, LookSections.Film) is null);
VariantLookBatch.Replay(batchStore, batchChanges, undo: true);
Check("group Undo restores each target's prior effective look",
    VariantLookStore.Equivalent(batchStore.ReadForBatch(batchIds[0]), batchDisabled)
    && batchIds.Skip(1).All(id => VariantLookStore.Equivalent(batchStore.ReadForBatch(id), batchDefaults)));
VariantLookBatch.Replay(batchStore, batchChanges, undo: false);
Check("group Redo restores the same durable looks", batchIds.All(id => batchStore.ReadForBatch(id)?.Look.Exposure == 1.37));
if (OperatingSystem.IsWindows())
{
    var revertSubset = batchChanges.Take(3).ToArray();
    var blocked = StateFile(root, revertSubset[1].Variant);
    var compensated = StateFile(root, revertSubset[2].Variant);
    File.SetLastWriteTimeUtc(compensated, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    using var readLease = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.Read);
    // Preflight can read this file. Windows denies replacing it after the
    // first group member has already been undone.
    bool interrupted = false;
    try { VariantLookBatch.Replay(batchStore, revertSubset, undo: true); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { interrupted = true; }
    Check("a real Windows sharing violation interrupts group Undo", interrupted);
    Check("group Undo compensates the already modified look after a partial disk failure",
        revertSubset.All(change => VariantLookStore.Equivalent(batchStore.ReadForBatch(change.Variant), change.After))
        && File.GetLastWriteTimeUtc(compensated).Year != 2020 && !batchStore.HasPending);
}
batchStore.Queue(batchIds[1], Sample(4));
batchStore.Flush();
Throws<InvalidOperationException>("batch Undo refuses a newer saved edit before touching the group",
    () => VariantLookBatch.Replay(batchStore, batchChanges, undo: true));
Check("conflicted Undo keeps both newer and unaffected variants",
    batchStore.ReadForBatch(batchIds[1])?.Look.Exposure == 4 && batchStore.ReadForBatch(batchIds[^1])?.Look.Exposure == 1.37);
batchStore.Queue(batchIds[2], Sample(2.8));
Throws<InvalidOperationException>("batch preflight refuses ordinary pending edits",
    () => VariantLookBatch.Prepare(batchStore, batchIds[2], batchDefaults, batchPreset, LookSections.Film));
batchStore.Flush();
var batchBlockedId = Guid.NewGuid();
var batchBlocked = VariantLookBatch.Prepare(batchStore, batchBlockedId, batchDefaults, batchPreset, LookSections.Film)!;
var batchesFolder = Path.GetDirectoryName(StateFile(root, batchIds[0]))!;
var blockedDestination = Directory.CreateDirectory(Path.Combine(batchesFolder, batchBlockedId.ToString("N") + ".json")).FullName;
try
{
    Throws<IOException>("failed optional batch commit leaves no queued change",
        () => batchStore.CommitBatchChange(batchBlockedId, batchBlocked.SavedBefore, batchBlocked.After));
    Check("failed batch commit schedules no delayed write", !batchStore.HasPending);
}
finally { Directory.Delete(blockedDestination); }

foreach (var boundary in new[] { "before-replace", "after-replace" })
{
    var crashCatalog = Path.Combine(scratch, $"batch-{boundary}.cocatalogdb");
    var crashStore = new VariantLookStore(root, crashCatalog);
    var pending = Guid.NewGuid();
    var prefix = Guid.NewGuid();
    var marker = Path.Combine(scratch, $"batch-{boundary}.marker");
    crashStore.Queue(pending, Sample(.6));
    crashStore.Flush();
    using var child = StartChild("--batch-commit-boundary", root, crashCatalog, pending.ToString(), boundary, marker, prefix.ToString());
    try
    {
        Check("child reached batch " + boundary, SpinWait.SpinUntil(() => File.Exists(marker) || child.HasExited, TimeSpan.FromSeconds(15))
            && File.Exists(marker) && !child.HasExited);
        Throws<IOException>("live batch cannot be classified as interrupted", () => crashStore.BatchHistory.ReadLatest());
        Throws<IOException>("a second batch cannot take ownership of the same catalog", () => crashStore.BatchHistory.Start(2));
    }
    finally
    {
        if (!child.HasExited) child.Kill();
        Check("terminated batch process exited", child.WaitForExit(15000));
    }
    var current = boundary == "before-replace" ? .6 : 1.5;
    var applied = boundary == "before-replace" ? new[] { prefix } : new[] { prefix, pending };
    var match = crashStore.BatchHistory.ReadLatest();
    Check("journal reconciles the exact saved prefix after " + boundary,
        match is { Result: BatchResult.Interrupted, Selected: 2, Uncertain: null } && match.Applied.SequenceEqual(applied));
    Check("reconciliation publishes no new looks", crashStore.Read(pending)?.Look.Exposure == current);
    var historyPath = Directory.GetFiles(Path.Combine(scratch, "Look batches", crashStore.CatalogIdentity), "*.jsonl").Single();
    File.AppendAllText(historyPath, "{\"Type\":2,\"broken");
    Check("a torn final record retains the verified prefix", crashStore.BatchHistory.ReadLatest()?.Applied.SequenceEqual(applied) == true);
    crashStore.Queue(pending, Sample(2.1));
    crashStore.Flush();
    Check("a changed unacknowledged look is uncertain and kept byte for byte",
        crashStore.BatchHistory.ReadLatest() is { Result: BatchResult.Interrupted } uncertain
            && uncertain.Uncertain == pending && uncertain.Applied.SequenceEqual(new[] { prefix })
            && crashStore.Read(pending)?.Look.Exposure == 2.1);
}

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
static void UnexpectedCompensation(Action _) => throw new InvalidOperationException("Undo compensation was not expected.");
NativeAdjustmentTransaction.Execute(nativeSteps, () => closedScopes++, UnexpectedCompensation);
Check("native transaction applies all fields", nativeValues.SequenceEqual(new[] { 11, 12, 13 }));
Check("native transaction closes successful undo group once", closedScopes == 1);
nativeValues = new[] { 1, 2, 3 };
var restorationOrder = new List<int>();
var failingSteps = Enumerable.Range(0, 3).Select(index => new NativeAdjustmentTransaction.Step(
    "control " + index,
    () => { nativeValues[index] += 10; if (index == 1) throw new IOException("setter mutated then failed"); },
    () => { nativeValues[index] = index + 1; restorationOrder.Add(index); })).ToArray();
Throws<IOException>("native setter failure preserved", () => NativeAdjustmentTransaction.Execute(
    failingSteps, () => closedScopes++, UnexpectedCompensation));
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
    NativeAdjustmentTransaction.Execute(rollbackFailure, () => { closedScopes++; throw new IOException("undo close failed"); },
        UnexpectedCompensation);
    throw new Exception("Expected aggregate native error");
}
catch (AggregateException error)
{
    Check("native error retains setter rollback and scope failures", error.InnerExceptions.Count == 3);
    Check("native rollback continues after one restoration fails", nativeValues[0] == 1);
    Check("native scope closed once even after rollback failure", closedScopes == 3);
}
nativeValues = new[] { 1, 2, 3 };
var compensationRuns = 0;
Throws<IOException>("native scope error is surfaced after successful setters", () => NativeAdjustmentTransaction.Execute(nativeSteps,
    () => { closedScopes++; throw new IOException("undo close failed after success"); }, restore => { compensationRuns++; restore(); }));
Check("native close error restores executed changes in its compensation scope", nativeValues.SequenceEqual(new[] { 1, 2, 3 }) && compensationRuns == 1);
Check("native close error closes only once", closedScopes == 4);
for (var failingIndex = 0; failingIndex < nativeValues.Length; failingIndex++)
{
    var inject = failingIndex;
    var applied = new List<int>();
    var restored = new List<int>();
    var setterFaults = Enumerable.Range(0, nativeValues.Length).Select(index => new NativeAdjustmentTransaction.Step(
        "setter " + index, () =>
        {
            applied.Add(index);
            nativeValues[index] += 10;
            if (index == inject) throw new IOException("injected partial setter");
        }, () =>
        {
            restored.Add(index);
            nativeValues[index] = index + 1;
        })).ToArray();
    Throws<IOException>($"native setter {inject} fault preserved", () =>
        NativeAdjustmentTransaction.Execute(setterFaults, () => { }, UnexpectedCompensation));
    Check($"native setter {inject} fault restores all touched values in reverse order", nativeValues.SequenceEqual(new[] { 1, 2, 3 })
        && applied.SequenceEqual(Enumerable.Range(0, inject + 1)) && restored.SequenceEqual(applied.AsEnumerable().Reverse()));
}
var selectionChecks = 0;
var untouched = nativeValues.ToArray();
Throws<OperationCanceledException>("selection change during native setters cancels transaction", () =>
    NativeAdjustmentTransaction.Execute(nativeSteps, () => { }, UnexpectedCompensation,
        () => { if (++selectionChecks == 2) throw new OperationCanceledException("selection changed"); }));
Check("selection change restores captured variant and skips later setters", selectionChecks == 2 && nativeValues.SequenceEqual(untouched));
Throws<InvalidOperationException>("post-set native value mismatch cancels transaction", () =>
    NativeAdjustmentTransaction.Execute(nativeSteps, () => { }, UnexpectedCompensation,
        verifyApplied: () => throw new InvalidOperationException("native value rejected")));
Check("post-set verification restores all native values before closing", nativeValues.SequenceEqual(untouched));
try
{
    NativeAdjustmentTransaction.Execute(nativeSteps, () => throw new IOException("native undo close failed"),
        restore => { restore(); throw new IOException("compensation close failed"); });
    throw new Exception("Expected aggregate native undo failure");
}
catch (AggregateException error)
{
    Check("compensating scope error preserves both undo failures after restoring values",
        error.InnerExceptions.Count == 2 && nativeValues.SequenceEqual(untouched));
}
await NativeHostShapeContracts.RunAsync(Check);
await PanelLogContracts.RunAsync(scratch, Check);
uint clipboardSequence = 1;
int clipboardWrites = 0;
var copiedReport = await SupportClipboardRetry.CopyAsync(() =>
{
    if (++clipboardWrites == 1) throw new System.Runtime.InteropServices.ExternalException("Clipboard unavailable.");
}, () => clipboardSequence, CancellationToken.None);
Check("support clipboard recovers a transient busy owner", copiedReport == SupportCopyResult.Copied && clipboardWrites == 2);
clipboardWrites = 0;
var replacedReport = await SupportClipboardRetry.CopyAsync(() =>
{
    clipboardWrites++;
    clipboardSequence++;
    throw new System.Runtime.InteropServices.ExternalException("Clipboard unavailable.");
}, () => clipboardSequence, CancellationToken.None);
Check("support clipboard preserves a newer copy by another application", replacedReport == SupportCopyResult.Changed && clipboardWrites == 1);
clipboardWrites = 0;
var busyReport = await SupportClipboardRetry.CopyAsync(() =>
{
    clipboardWrites++;
    throw new System.Runtime.InteropServices.ExternalException("Clipboard unavailable.");
}, () => clipboardSequence, CancellationToken.None);
Check("support clipboard gives up after three attempts", busyReport == SupportCopyResult.Busy && clipboardWrites == 3);
using (var closeClipboard = new CancellationTokenSource())
{
    clipboardWrites = 0;
    try
    {
        await SupportClipboardRetry.CopyAsync(() =>
        {
            clipboardWrites++;
            closeClipboard.Cancel();
            throw new System.Runtime.InteropServices.ExternalException("Clipboard unavailable.");
        }, () => clipboardSequence, closeClipboard.Token);
        throw new InvalidOperationException("A closed support tool retried its clipboard operation.");
    }
    catch (OperationCanceledException)
    {
        Check("closing the support tool cancels clipboard retries", clipboardWrites == 1);
    }
}
Console.WriteLine($"PASS: {assertions} state/recovery assertions, including separate-process recovery. Fixtures: {scratch}");
return 0;

static VariantLook Sample(double exposure) => new(new("kodak_portra_400", "kodak_portra_endura", exposure, .2, 1,
    "print", "hanatos2025", false, new() { ["film_gamma"] = 1 }),
    new(new() { ["halation-amount"] = 0 }, new() { ["input-color"] = "srgb" }, new()), true, Grain: new(2,25,50));

static Process StartChild(params string[] arguments)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    info.Environment.Remove("DOTNET_STARTUP_HOOKS");
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(typeof(Program).Assembly.Location);
    foreach (var arg in arguments) info.ArgumentList.Add(arg);
    return Process.Start(info)!;
}

void TestPreviousGeneration(VariantLookStore looks, string lookRoot, string catalogPath, string testRoot)
{
    var variant = Guid.NewGuid();
    Check("typed missing distinguishes default", looks.ReadOutcome(variant).State == LookLoadState.Missing);
    looks.Queue(variant, Sample(1.2)); looks.Flush();
    var primary = StateFile(lookRoot, variant);
    var priorBytes = File.ReadAllBytes(primary);
    var previous = Path.ChangeExtension(primary, ".previous.look");
    Check("first verified save has no older version", !File.Exists(previous));
    looks.Queue(variant, Sample(2.1)); looks.Flush();
    Check("previous generation preserves exact accepted JSON", File.ReadAllBytes(previous).AsSpan(52).SequenceEqual(priorBytes));
    Check("typed valid returns current value", looks.ReadOutcome(variant) is { State: LookLoadState.Valid, Look.Look.Exposure: 2.1 });
    var borrowed = looks.Read(variant)!;
    borrowed.Look.Development["example"] = -5;
    borrowed.Optics.Numbers["exposure"] = 73;
    Check("changing a returned look leaves its cached committed state untouched", VariantLookStore.Equivalent(looks.Read(variant), Sample(2.1)));
    using (var locked = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.None))
        Check("typed transient lock does not fall back to old value", looks.ReadOutcome(variant) is { State: LookLoadState.TransientIo, Look: null });

    var rejected = new byte[] { 0x7b, 0x22, 0xf0, 0x9f, 0x92 };
    File.WriteAllBytes(primary, rejected);
    RunChild("--verify-previous", lookRoot, catalogPath, variant.ToString(), "1.2");
    var recovery = looks.ReadOutcome(variant);
    Check("typed recovery exposes previous look read-only", recovery is { State: LookLoadState.Recovered, ReadOnly: true, Look.Look.Exposure: 1.2 });
    Throws<InvalidDataException>("read-only recovery rejects ordinary writes", () => looks.Queue(variant, Sample(6)));
    Check("normal edits preserve damaged original exactly", File.ReadAllBytes(primary).SequenceEqual(rejected));
    var previousBytes = File.ReadAllBytes(previous);
    var tampered = (byte[])previousBytes.Clone();
    tampered[30] ^= 1;
    File.WriteAllBytes(previous, tampered);
    Check("previous with wrong digest is unusable", looks.ReadOutcome(variant) is { State: LookLoadState.Corrupt, Look: null });
    Throws<InvalidDataException>("manual restore revalidates the exact previous choice", () => looks.RestorePrevious(variant, recovery.Look!));
    tampered[30] ^= 1;
    tampered[6] ^= 1;
    File.WriteAllBytes(previous, tampered);
    Check("previous with wrong variant is unusable", looks.ReadOutcome(variant) is { State: LookLoadState.Corrupt, Look: null });
    File.WriteAllBytes(previous, previousBytes);
    looks.RestorePrevious(variant, recovery.Look!);
    Check("explicit previous recovery restores exact look", File.ReadAllBytes(primary).SequenceEqual(priorBytes));
    Check("recovery does not replace backup with damaged bytes", File.ReadAllBytes(previous).SequenceEqual(previousBytes));
    var rejectedHash = Convert.ToHexString(SHA256.HashData(rejected));
    Check("recovery archive is timestamped, hashed and byte-identical", Directory.EnumerateFiles(
        Path.Combine(testRoot, "Look recovery"), $"{variant:N}-before-reset-*-{rejectedHash}.json", SearchOption.AllDirectories)
        .Any(path => File.ReadAllBytes(path).SequenceEqual(rejected)));
    RunChild("--verify", lookRoot, catalogPath, variant.ToString(), "1.2");

    File.Delete(primary);
    var missing = looks.ReadOutcome(variant);
    Check("missing primary with known data is recoverable", missing is { State: LookLoadState.Recovered, ReadOnly: true, Look.Look.Exposure: 1.2 });
    File.WriteAllBytes(primary, JsonSerializer.SerializeToUtf8Bytes(Sample(3.3)));
    Throws<InvalidDataException>("external change after recovery opened is preserved", () => looks.RestorePrevious(variant, missing.Look!));
    Check("reselect reads newer external version", looks.ReadOutcome(variant) is { State: LookLoadState.Valid, Look.Look.Exposure: 3.3 });
    var future = JsonNode.Parse(File.ReadAllText(primary))!;
    future["Version"] = 91;
    future["NewSchemaMember"] = "unrecognized";
    File.WriteAllText(primary, future.ToJsonString());
    Check("future schema is typed unsupported even with future members and a valid previous look",
        looks.ReadOutcome(variant) is { State: LookLoadState.Unsupported, Look: null, ReadOnly: true });
    Throws<InvalidDataException>("future schema rejects normal writes", () => looks.Queue(variant, Sample(7)));
    Check("future schema bytes remain immutable after normal write attempts", File.ReadAllText(primary) == future.ToJsonString());
    looks.ResetBlocked(variant, Sample(.1));

    var lockedPreviousVariant = Guid.NewGuid();
    looks.Queue(lockedPreviousVariant, Sample(1)); looks.Flush();
    var lockedPrevious = Path.ChangeExtension(StateFile(lookRoot, lockedPreviousVariant), ".previous.look");
    using (var lockFile = new FileStream(lockedPrevious, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        looks.Queue(lockedPreviousVariant, Sample(2));
        Throws<IOException>("previous publication fault protects pending newest look", looks.Flush);
        Check("primary remains verifiable during a failed previous publication", PanelFiles.ReadPreset<VariantLook>(StateFile(lookRoot, lockedPreviousVariant)).Look.Exposure == 1);
    }
    looks.Flush();
    Check("retry updates both verified generations", looks.Read(lockedPreviousVariant)?.Look.Exposure == 2
        && PanelFiles.DecodePreset<VariantLook>(File.ReadAllBytes(lockedPrevious).AsSpan(52)).Look.Exposure == 1);

    var cached = StateFile(lookRoot, lockedPreviousVariant);
    var previousWriteTime = File.GetLastWriteTimeUtc(cached);
    var replacement = PanelFiles.EncodeCompactPreset(Sample(3));
    Check("external fixture retains the cached file's size", new FileInfo(cached).Length == replacement.Length);
    File.WriteAllBytes(cached, replacement);
    File.SetLastWriteTimeUtc(cached, previousWriteTime);
    Check("same-size change with the original timestamp reloads the actual new bytes", looks.Read(lockedPreviousVariant)?.Look.Exposure == 3);
    looks.Queue(lockedPreviousVariant, Sample(4));
    looks.Flush();
    Check("the ordinary save backs up the reloaded external generation verbatim",
        File.ReadAllBytes(lockedPrevious).AsSpan(52).SequenceEqual(replacement));

    var sample = Sample(.4);
    var typed = sample with { Look = sample.Look with { Development = new() { ["first"] = 1, ["second"] = 2 } } };
    var reordered = typed with { Look = typed.Look with { Development = new(StringComparer.Ordinal) { ["second"] = 2, ["first"] = 1 } } };
    var otherCase = typed with { Look = typed.Look with { Development = new(StringComparer.OrdinalIgnoreCase) { ["First"] = 1, ["second"] = 2 } } };
    Check("look equality compares map values independent of insertion order", VariantLookStore.Equivalent(typed, reordered));
    Check("look equality treats key case literally even with a case-insensitive dictionary",
        !VariantLookStore.Equivalent(typed, otherCase));
    Check("look equality retains enabled, stock, grain and optics differences",
        !VariantLookStore.Equivalent(typed, typed with { Enabled = !typed.Enabled })
        && !VariantLookStore.Equivalent(typed, typed with { Look = typed.Look with { Film = typed.Look.Film + "-other" } })
        && !VariantLookStore.Equivalent(typed, typed with { Grain = new GrainControls(4, 25, 50) })
        && !VariantLookStore.Equivalent(typed, typed with { Optics = typed.Optics with { Switches = new() { ["test"] = true } } }));
}

static void RunChild(params string[] arguments)
{
    using var child = StartChild(arguments);
    if (!child.WaitForExit(30000)) { child.Kill(); throw new Exception("Child recovery probe timed out"); }
    if (child.ExitCode != 0) throw new Exception($"Child recovery probe failed: {child.ExitCode}");
}

static void KillAtBoundary(string boundary, string root, string catalog, Guid variant, string directory)
{
    var ready = Path.Combine(directory, boundary + ".ready");
    using var child = StartChild("--commit-boundary", root, catalog, variant.ToString(), boundary, ready);
    try
    {
        var timeout = Stopwatch.StartNew();
        while (!File.Exists(ready) && !child.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(30))
            Thread.Sleep(15);
        if (child.HasExited || !File.Exists(ready))
            throw new IOException($"Child did not reach the {boundary} save boundary.");
    }
    finally
    {
        if (!child.HasExited) child.Kill();
        if (!child.WaitForExit(10000)) throw new IOException("The fixture process did not terminate.");
    }
}

internal static class SpatialPreview
{
    internal sealed record Settings(Dictionary<string,double> Numbers, Dictionary<string,string> Selections, Dictionary<string,bool> Switches);
}
