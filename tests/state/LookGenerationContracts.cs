using System.Diagnostics;
using System.Security.Cryptography;

internal static class LookGenerationContracts
{
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
            var next = resume.Commit(reopened.Receipt, "resumed write"u8.ToArray());
            check("a new writer can resume after a hard stop", next.Generation == reopened.Generation + 1
                && next.Previous!.SequenceEqual(reopened.Current));

            var otherCatalog = new VariantLookGenerationJournal(directory, Convert.ToHexString(SHA256.HashData("other catalog"u8)));
            RequireFailure<InvalidDataException>(() => otherCatalog.Read(variant), "a generation pair is bound to its catalog", check);
            var file = Path.Combine(directory, variant.ToString("N") + ".generations");
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
