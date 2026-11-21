using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

internal static class LookGenerationContracts
{
    internal static int HoldStore(string root, string catalog, Guid variant, string heldMirror, string marker, VariantLook replacement)
    {
        var store = new VariantLookStore(root, catalog, useGenerationWriter: true);
        var primary = Path.Combine(root, store.CatalogIdentity, variant.ToString("N") + ".json");
        var holdPrimary = heldMirror == "primary";
        using var locked = heldMirror == "absent" ? null : new FileStream(holdPrimary ? primary : Path.ChangeExtension(primary, ".previous.look"),
            FileMode.Open, FileAccess.Read, holdPrimary ? FileShare.Read : FileShare.None);
        store.Queue(variant, replacement);
        store.FlushVariant(variant);
        if (store.HasPending || store.RecoveryNotice is null) return 2;
        File.WriteAllText(marker, store.RecoveryNotice);
        Thread.Sleep(Timeout.Infinite);
        return 1;
    }

    internal static void CheckStore(string scratch, Action<string, bool> check, Func<string[], Process> startChild, Func<double, VariantLook> sample)
    {
        CheckStoreBoundary(scratch, "previous", check, startChild, sample);
        CheckStoreBoundary(scratch, "primary", check, startChild, sample);
        if (OperatingSystem.IsWindows()) CheckFirstSave(scratch, check, startChild, sample);
    }

    [SupportedOSPlatform("windows")]
    private static void CheckFirstSave(string scratch, Action<string, bool> check, Func<string[], Process> startChild, Func<double, VariantLook> sample)
    {
        var folder = Path.Combine(scratch, "generation-first-save");
        var root = Path.Combine(folder, "Variant looks");
        var catalog = Path.Combine(folder, "Fixture.cocatalogdb");
        var variant = Guid.NewGuid();
        var store = new VariantLookStore(root, catalog);
        var journalDirectory = Path.Combine(folder, "Look generations", store.CatalogIdentity);
        var journal = new VariantLookGenerationJournal(journalDirectory, store.CatalogIdentity);
        var marker = Path.Combine(folder, "first-stage.ready");
        Directory.CreateDirectory(folder);

        StopChild(startChild(["--hold-look-generation", journalDirectory, store.CatalogIdentity, variant.ToString("N"), "before", marker]),
            () => check("an uncommitted first generation does not create a saved look",
                store.ReadOutcome(variant) is { State: LookLoadState.Missing } && journal.Read(variant) is null));
        check("a stop before first publication discards the owned stage",
            store.Read(variant) is null && !Directory.EnumerateFiles(journalDirectory, ".spektra-*.tmp").Any());

        var mirrorFolder = Directory.CreateDirectory(Path.Combine(root, store.CatalogIdentity));
        var primary = Path.Combine(mirrorFolder.FullName, variant.ToString("N") + ".json");
        var restricted = mirrorFolder.GetAccessControl(AccessControlSections.Access);
        using var identity = WindowsIdentity.GetCurrent();
        var denyMirrorWrites = new FileSystemAccessRule(identity.User!, FileSystemRights.CreateFiles, AccessControlType.Deny);
        restricted.AddAccessRule(denyMirrorWrites);
        marker = Path.Combine(folder, "first-journal.saved");
        mirrorFolder.SetAccessControl(restricted);
        try
        {
            StopChild(startChild(["--hold-generation-store", root, catalog, variant.ToString("N"), "absent", marker]),
                () => check("the first acknowledged edit needs only the durable journal", !File.Exists(primary)
                    && journal.Read(variant) is { Generation: 1, Previous: null }));
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(denyMirrorWrites);
            mirrorFolder.SetAccessControl(restricted);
        }

        var reopened = new VariantLookStore(root, catalog);
        check("restart recovers the first acknowledged edit with no prior look or mirror",
            reopened.ReadOutcome(variant) is { State: LookLoadState.Recovered, Look.Look.Exposure: 2.2, ReadOnly: false }
            && !File.Exists(primary));
        reopened.Queue(variant, sample(2.4)); reopened.FlushVariant(variant);
        var saved = journal.Read(variant);
        check("the next save publishes both copies and retains the first acknowledged edit", saved is { Generation: 2 }
            && saved.Previous!.SequenceEqual(PanelFiles.EncodeCompactPreset(sample(2.2)))
            && File.ReadAllBytes(primary).SequenceEqual(PanelFiles.EncodeCompactPreset(sample(2.4))));

        void StopChild(Process child, Action verify)
        {
            using (child)
            {
                try
                {
                    check("first-save worker reaches its interruption boundary", SpinWait.SpinUntil(
                        () => child.HasExited || File.Exists(marker), TimeSpan.FromSeconds(10)) && !child.HasExited && File.Exists(marker));
                    verify();
                }
                finally
                {
                    if (!child.HasExited) child.Kill();
                    check("first-save worker stopped", child.WaitForExit(10_000));
                }
            }
        }
    }

    private static void CheckStoreBoundary(string scratch, string heldMirror, Action<string, bool> check,
        Func<string[], Process> startChild, Func<double, VariantLook> sample)
    {
        var folder = Path.Combine(scratch, "generation-store-" + heldMirror);
        var root = Path.Combine(folder, "Variant looks");
        var catalog = Path.Combine(folder, "Fixture.cocatalogdb");
        var variant = Guid.NewGuid();
        var legacy = new VariantLookStore(root, catalog);
        legacy.Queue(variant, sample(.6)); legacy.FlushVariant(variant);
        legacy.Queue(variant, sample(.9)); legacy.FlushVariant(variant);
        var primary = Path.Combine(root, legacy.CatalogIdentity, variant.ToString("N") + ".json");
        var old = File.ReadAllBytes(primary);
        var journal = new VariantLookGenerationJournal(Path.Combine(folder, "Look generations", legacy.CatalogIdentity), legacy.CatalogIdentity);
        var marker = Path.Combine(folder, "journal.saved");

        using (var child = startChild(["--hold-generation-store", root, catalog, variant.ToString("N"), heldMirror, marker]))
        {
            try
            {
                check("actual store acknowledges the journal with a locked compatibility file",
                    SpinWait.SpinUntil(() => child.HasExited || File.Exists(marker), TimeSpan.FromSeconds(10)) && !child.HasExited);
                check("the acknowledged generation contains the exact chosen look", journal.Read(variant) is { } saved
                    && saved.Current.SequenceEqual(PanelFiles.EncodeCompactPreset(sample(2.2))) && saved.Previous!.SequenceEqual(old));
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                check("the store process stops before compatibility recovery", child.WaitForExit(10_000));
            }
        }

        var reopened = new VariantLookStore(root, catalog);
        check("the process stop occurs at the intended mirror boundary",
            File.ReadAllBytes(Path.ChangeExtension(primary, ".previous.look")).AsSpan(52)
                .SequenceEqual(heldMirror == "primary" ? old : PanelFiles.EncodeCompactPreset(sample(.6))));
        check("the restart reads the journal without overwriting a lagging mirror",
            reopened.ReadOutcome(variant) is { State: LookLoadState.Recovered, Look.Look.Exposure: 2.2, ReadOnly: false }
            && File.ReadAllBytes(primary).SequenceEqual(old));
        var acknowledged = journal.Read(variant)!;
        var oldHash = Convert.ToHexString(SHA256.HashData(old));
        var acknowledgedHash = Convert.ToHexString(SHA256.HashData(acknowledged.Current));
        using (var batch = reopened.BatchHistory.Start(2)) batch.Prepare(variant, oldHash, acknowledgedHash);
        check("batch recovery uses the acknowledged look instead of the prior compatibility bytes",
            reopened.BatchHistory.ReadLatest() is { Applied: [var applied], Uncertain: null } && applied == variant);

        var checkpoint = Path.Combine(folder, "Look recovery", reopened.CatalogIdentity, variant.ToString("N") + ".pending.json");
        Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
        PanelFiles.WriteCompactPreset(checkpoint, new { Version = 1, CatalogKey = reopened.CatalogIdentity,
            Variant = variant, BaseHash = acknowledgedHash, Look = sample(2.4) });
        check("an unsaved checkpoint compares against the acknowledged journal generation", reopened.Read(variant)?.Look.Exposure == 2.4
            && reopened.HasPending);
        reopened.FlushVariant(variant);
        var next = journal.Read(variant)!;
        check("another edit repairs the lagging copy and keeps the prior acknowledged generation", next.Generation == 2
            && File.ReadAllBytes(primary).SequenceEqual(PanelFiles.EncodeCompactPreset(sample(2.4)))
            && next.Previous!.SequenceEqual(acknowledged.Current) && !File.Exists(checkpoint) && !reopened.HasPending);

        File.Move(primary, Path.Combine(folder, "verified-compatibility-copy.json"));
        check("the actual store can read an acknowledged look without a compatibility file",
            reopened.ReadOutcome(variant) is { State: LookLoadState.Recovered, Look.Look.Exposure: 2.4 });
        File.WriteAllBytes(primary, PanelFiles.EncodeCompactPreset(sample(3.0)));
        var unrelated = File.ReadAllBytes(primary);
        check("an unrelated compatibility edit blocks automatic recovery and preserves both versions",
            reopened.ReadOutcome(variant) is { ReadOnly: true } && reopened.IsBlocked(variant)
            && File.ReadAllBytes(primary).SequenceEqual(unrelated) && journal.Read(variant)?.Receipt == next.Receipt);

        var raw = File.ReadAllBytes(journal.PathFor(variant));
        raw[^1] ^= 1;
        File.WriteAllBytes(journal.PathFor(variant), raw);
        var chosen = reopened.ReadOutcome(variant).Look!;
        RequireFailure<InvalidDataException>(() => reopened.RestorePrevious(variant, chosen), "manual recovery refuses a journal changed since review", check);
        var damaged = reopened.ReadOutcome(variant);
        check("damaged journal offers its independently verified predecessor", damaged is
            { State: LookLoadState.Recovered, ReadOnly: true, Look.Look.Exposure: 2.2 });
        reopened.RestorePrevious(variant, damaged.Look!);
        check("explicit recovery publishes the chosen look and archives the damaged journal before retirement",
            reopened.Read(variant)?.Look.Exposure == 2.2 && journal.Read(variant) is null
            && Directory.EnumerateFiles(Path.Combine(folder, "Look recovery"), "*-journal-before-reset-*.json", SearchOption.AllDirectories)
                .Any(path => File.ReadAllBytes(path).SequenceEqual(raw)));
    }

    internal static int HoldForBackup(string directory, string catalog, Guid variant, string current, string previous, string marker)
    {
        var journal = new VariantLookGenerationJournal(directory, catalog);
        using var writer = journal.OpenWriter(variant);
        var saved = writer.Commit(null, File.ReadAllBytes(current), legacyPrevious: File.ReadAllBytes(previous));
        File.WriteAllText(marker, saved.Receipt);
        Thread.Sleep(Timeout.Infinite);
        return 1;
    }

    internal static int Hold(string directory, string catalog, Guid variant, string stage, string marker)
    {
        var journal = new VariantLookGenerationJournal(directory, catalog);
        using var writer = journal.OpenWriter(variant);
        var receipt = journal.Read(variant)?.Receipt;
        writer.Commit(receipt, "new committed look"u8.ToArray(), verifySource: stage == "before" ? Pause : null);
        Pause();
        return 1;

        void Pause()
        {
            File.WriteAllText(marker, stage);
            Thread.Sleep(Timeout.Infinite);
        }
    }

    internal static void CheckRecovery(string scratch, Action<string, bool> check, Func<string[], Process> startChild)
    {
        foreach (var stage in new[] { "before", "after" })
        {
            var directory = Path.Combine(scratch, "generations-" + stage);
            var variant = Guid.NewGuid();
            var catalog = Convert.ToHexString(SHA256.HashData("donor catalog"u8));
            var journal = new VariantLookGenerationJournal(directory, catalog);
            var legacy = "existing legacy look"u8.ToArray();
            var first = "first journal look"u8.ToArray();
            VariantLookGenerationJournal.Snapshot original;
            using (var writer = journal.OpenWriter(variant))
                original = writer.Commit(null, first, legacyPrevious: legacy);
            check("first pair retains the legacy look", original.Generation == 1 && original.Previous!.SequenceEqual(legacy));

            var marker = Path.Combine(directory, "commit.ready");
            using var child = startChild(["--hold-look-generation", directory, catalog, variant.ToString("N"), stage, marker]);
            try
            {
                check("generation writer reached " + stage, SpinWait.SpinUntil(() => child.HasExited || File.Exists(marker),
                    TimeSpan.FromSeconds(10)) && !child.HasExited && File.Exists(marker));
                RequireFailure<IOException>(() => { using var blocked = journal.OpenWriter(variant); },
                    "a separate generation writer holds exclusive ownership", check);
                if (stage == "before")
                    check("a flushed stage leaves the live pair readable", journal.Read(variant)?.Receipt == original.Receipt);
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                check("generation writer stopped", child.WaitForExit(10_000));
            }

            var reopened = journal.Read(variant)!;
            check("generation pair survives a stop " + stage, stage == "before"
                ? reopened.Generation == 1 && reopened.Current.SequenceEqual(first) && reopened.Previous!.SequenceEqual(legacy)
                : reopened.Generation == 2 && reopened.Current.AsSpan().SequenceEqual("new committed look"u8)
                    && reopened.Previous!.SequenceEqual(first));

            using var resume = journal.OpenWriter(variant);
            RequireFailure<InvalidDataException>(() => resume.Commit(null, "stale write"u8.ToArray()),
                "an earlier receipt cannot replace the current pair", check);
            RequireFailure<IOException>(() => resume.Commit(reopened.Receipt, "rejected source"u8.ToArray(),
                verifySource: () => throw new IOException("The fixture's source changed.")), "failed final guard blocks publication", check);
            check("stale and rejected commits preserve the acknowledged pair", journal.Read(variant)?.Receipt == reopened.Receipt);
            var callerCopy = resume.Read()!;
            callerCopy.Current[0] ^= 1;
            var next = resume.Commit(reopened.Receipt, "resumed write"u8.ToArray());
            check("a new writer can resume after a hard stop with a defensive predecessor", next.Generation == reopened.Generation + 1
                && next.Previous!.SequenceEqual(reopened.Current));

            var otherCatalog = new VariantLookGenerationJournal(directory, Convert.ToHexString(SHA256.HashData("other catalog"u8)));
            RequireFailure<InvalidDataException>(() => otherCatalog.Read(variant), "a generation pair is bound to its catalog", check);
            var file = Path.Combine(directory, variant.ToString("N") + ".generations");
            var exact = File.ReadAllBytes(file);
            var conflicting = (byte[])exact.Clone();
            conflicting[^1] ^= 1;
            RequireFailure<InvalidDataException>(() => resume.Commit(next.Receipt, "pending comparison"u8.ToArray(),
                verifySource: () => File.WriteAllBytes(file, conflicting)), "the final receipt rejects a same-length external rewrite", check);
            check("the guarded write leaves the externally changed bytes untouched", File.ReadAllBytes(file).SequenceEqual(conflicting));
            File.WriteAllBytes(file, exact);
            var anotherVariant = Guid.NewGuid();
            File.Copy(file, Path.Combine(directory, anotherVariant.ToString("N") + ".generations"));
            RequireFailure<InvalidDataException>(() => journal.Read(anotherVariant), "a generation pair is bound to its variant", check);
            var damaged = File.ReadAllBytes(file);
            damaged[^1] ^= 1;
            File.WriteAllBytes(file, damaged);
            RequireFailure<InvalidDataException>(() => journal.Read(variant), "a damaged pair fails its checksum", check);
            check("a damaged current capsule leaves the verified predecessor readable",
                journal.ReadPrevious(variant)!.SequenceEqual(reopened.Current));
            File.WriteAllBytes(file, damaged[..^8]);
            check("a truncated current capsule leaves the verified predecessor readable",
                journal.ReadPrevious(variant)!.SequenceEqual(reopened.Current));
        }

        var maximumDirectory = Path.Combine(scratch, "generations-maximum");
        var bound = new VariantLookGenerationJournal(maximumDirectory, new string('1', 64));
        var maxVariant = Guid.NewGuid();
        var maximum = new byte[PanelFiles.MaxPresetBytes];
        using var boundedWriter = bound.OpenWriter(maxVariant);
        var accepted = boundedWriter.Commit(null, maximum);
        for (int i = 0; i < 16; i++)
        {
            maximum[0] = (byte)i;
            accepted = boundedWriter.Commit(accepted.Receipt, maximum);
        }
        check("a maximum-size pair holds only the current and prior bytes", accepted.Generation == 17
            && accepted.Current[0] == 15 && accepted.Previous![0] == 14
            && new FileInfo(Path.Combine(maximumDirectory, maxVariant.ToString("N") + ".generations")).Length
                == 2 * (PanelFiles.MaxPresetBytes + 96));
    }

    private static void RequireFailure<T>(Action action, string message, Action<string, bool> check) where T : Exception
    {
        var rejected = false;
        try { action(); }
        catch (T) { rejected = true; }
        check(message, rejected);
    }
}
