using System;
using System.IO;

internal sealed partial class VariantLookStore
{
    private string? ReadCommittedHash(Guid variant) =>
        ReadCommittedBytes(variant, out _) is { } bytes ? Digest(bytes) : null;

    private byte[]? ReadCommittedBytes(Guid variant, out bool journalOnly)
    {
        var generation = generationJournal.Read(variant);
        var loose = ReadLooseLook(variant);
        journalOnly = false;
        if (generation is null) return loose;
        journalOnly = !ValidateLooseGeneration(variant, generation, loose);
        return generation.Current;
    }

    private byte[]? ReadLooseLook(Guid variant)
    {
        try
        {
            using var file = File.OpenRead(GetPath(variant));
            return PanelFiles.ReadPresetBytes(file);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private bool ValidateLooseGeneration(Guid variant, VariantLookGenerationJournal.Snapshot generation, byte[]? loose)
    {
        if (generation.Previous is null && File.Exists(GetPreviousPath(variant)))
            throw new InvalidDataException("The previous look file conflicts with the saved journal. Both versions were kept.");
        if (loose is null) return false;
        if (loose.AsSpan().SequenceEqual(generation.Current)) return true;
        if (generation.Previous is { } previous && loose.AsSpan().SequenceEqual(previous)) return false;
        throw new InvalidDataException("The saved look file conflicts with the saved journal. Both versions were kept.");
    }

    private void CommitGeneration(Guid variant, PendingEdit edit)
    {
        using var writer = generationJournal.OpenWriter(variant);
        var prior = writer.Read();
        var loose = ReadLooseLook(variant);
        var priorBytes = prior?.Current ?? loose;
        var priorHash = priorBytes is null ? null : Digest(priorBytes);
        if (priorHash != edit.BaseHash) throw ExternalChange(variant);

        var looseHash = loose is null ? null : Digest(loose);
        if (prior is not null && !ValidateLooseGeneration(variant, prior, loose))
        {
            // The compatibility copy cannot fall farther than the one predecessor
            // retained in the next pair. Repair it before advancing again.
            PublishGenerationMirrors(variant, prior, looseHash);
            looseHash = priorHash;
        }

        var committed = writer.Commit(prior?.Receipt, edit.Json, legacyPrevious: prior is null ? loose : null,
            verifySource: () => EnsureExpectedPrimary(variant, GetPath(variant), looseHash));
        try { PublishGenerationMirrors(variant, committed, looseHash); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // This edit was already flushed and published. Keeping it pending
            // would misreport its durability and could replay it over a newer edit.
            RecoveryNotice = "The latest look is safely saved in its journal. Its compatibility files could not be updated. " + error.Message;
        }
    }

    private void PublishGenerationMirrors(Guid variant, VariantLookGenerationJournal.Snapshot generation, string? looseHash)
    {
        var primary = GetPath(variant);
        void Guard()
        {
            if (FileHash(primary) != looseHash)
                throw new InvalidDataException("The saved look file changed while updating its compatibility copy. Both versions were kept.");
        }

        Guard();
        if (generation.Previous is { } previous)
            WritePreviousGeneration(variant, previous, Digest(previous), Guard, flushToDisk: false);
        PanelFiles.WriteAtomically(primary, stream => stream.Write(generation.Current), Guard,
            overwrite: looseHash is not null, discardOnProcessExit: true, flushToDisk: false);
    }

    private void ResetGeneration(Guid variant, PendingEdit edit, string journalHash)
    {
        using var writer = generationJournal.OpenWriter(variant);
        var journalPath = generationJournal.PathFor(variant);
        var primary = GetPath(variant);
        if (ArchiveFile(journalPath, variant, "journal-before-reset")?.Hash != journalHash)
            throw new InvalidDataException("The look journal changed before recovery. Both versions were kept.");
        void Guard(string? expectedPrimary)
        {
            if (FileHash(journalPath) != journalHash || FileHash(primary) != expectedPrimary)
                throw new InvalidDataException("The look or journal changed during recovery. Both versions were kept.");
        }

        // Publish a durable, verified replacement before retiring the damaged
        // journal. The original journal is already archived for recovery.
        Directory.CreateDirectory(directory);
        PanelFiles.WriteAtomically(primary, stream => stream.Write(edit.Json), () => Guard(edit.BaseHash),
            overwrite: edit.BaseHash is not null, discardOnProcessExit: true);
        Guard(edit.Hash);
        File.Delete(journalPath);
    }

    private void WritePreviousGeneration(Guid variant, byte[] bytes, string digest, Action? guard = null, bool flushToDisk = true)
    {
        PanelFiles.WriteAtomically(GetPreviousPath(variant), stream =>
        {
            stream.Write("SFL1"u8);
            stream.Write(variant.ToByteArray());
            stream.Write(Convert.FromHexString(digest));
            stream.Write(bytes);
        }, guard, discardOnProcessExit: true, flushToDisk: flushToDisk);
    }
}
