using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record GrainControls([property: JsonRequired] int Type, [property: JsonRequired] double Impact,
    [property: JsonRequired] double Granularity);
internal sealed record VariantLook([property: JsonRequired] LookSettings Look, [property: JsonRequired] SpatialPreview.Settings Optics,
    [property: JsonRequired] bool Enabled, [property: JsonRequired] int Version = 1, GrainControls? Grain = null);
internal sealed record SavedVariantIdentity([property: JsonRequired] int Version, [property: JsonRequired] Guid Document,
    [property: JsonRequired] Guid Variant, [property: JsonRequired] Guid Image);

internal sealed class LookReadUnavailableException(Exception inner)
    : IOException("The saved look is temporarily inaccessible. Spektrafilm will retry loading it. " + inner.Message, inner);

internal enum LookLoadState { Missing, Valid, Recovered, Unsupported, Corrupt, TransientIo }
internal sealed record LookLoadOutcome(LookLoadState State, VariantLook? Look, string? Detail = null, bool ReadOnly = false);
internal sealed class LookUnsupportedException(string message) : IOException(message);

internal sealed class VariantLookStore
{
    // Failed writes must survive UI teardown. Pending edits also get an independent
    // on-disk recovery checkpoint when the normal destination is unavailable.
    private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string directory;
    private readonly string recoveryDirectory;
    private readonly string catalogKey;
    private readonly Session session;
    private readonly Dictionary<Guid, BlockedLook> blocked;
    private readonly PrimaryCache primaryCache = new();
    internal GrainReceiptStore GrainReceipts { get; }
    internal VariantLookBatchJournal BatchHistory { get; }
    internal string CatalogIdentity => catalogKey;
    internal string? RecoveryNotice { get; private set; }

    internal VariantLookStore(string root, string catalogDatabase)
    {
        catalogKey = CatalogKey(catalogDatabase);
        directory = Path.Combine(Path.GetFullPath(root), catalogKey);
        recoveryDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, "Look recovery", catalogKey);
        GrainReceipts = new GrainReceiptStore(recoveryDirectory);
        BatchHistory = new VariantLookBatchJournal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, "Look batches", catalogKey),
            variant => FileHash(GetPath(variant)));
        session = Sessions.GetOrAdd(directory, _ => new Session());
        blocked = session.Blocked;
    }

    internal bool IsBlocked(Guid variant) { lock (session) return blocked.ContainsKey(variant); }
    internal bool HasPending { get { lock (session) return session.Pending.Count != 0; } }
    internal bool HasUnprotectedPending { get { lock (session) return session.Pending.Values.Any(edit => !edit.RecoveryDurable); } }
    internal bool HasCommitted(Guid variant) { lock (session) return session.KnownHash.GetValueOrDefault(variant) is not null; }

    internal void RecordVariantIdentity(Guid variant, Guid document, Guid image)
    {
        if (variant == Guid.Empty || document == Guid.Empty || image == Guid.Empty)
            return;
        var identity = new SavedVariantIdentity(1, document, variant, image);
        lock (session)
        {
            if (session.VerifiedIdentities.GetValueOrDefault(variant) == identity)
                return;
            var path = Path.Combine(recoveryDirectory, $"{variant:N}.identity.json");
            SavedVariantIdentity? existing = null;
            try { existing = PanelFiles.ReadPreset<SavedVariantIdentity>(path); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (existing is not null && existing != identity)
                throw new InvalidDataException("This variant already has a different saved catalog or image identity.");
            if (existing is null)
            {
                Directory.CreateDirectory(recoveryDirectory);
                var bytes = PanelFiles.EncodeCompactPreset(identity);
                PanelFiles.WriteAtomically(path, stream => stream.Write(bytes), overwrite: false, discardOnProcessExit: true);
            }
            session.VerifiedIdentities[variant] = identity;
        }
    }

    internal LookLoadOutcome ReadOutcome(Guid variant)
    {
        lock (session)
        {
            try
            {
                var look = Read(variant);
                var state = RecoveryNotice is not null ? LookLoadState.Recovered : look is null ? LookLoadState.Missing : LookLoadState.Valid;
                return new(state, look, RecoveryNotice);
            }
            catch (LookReadUnavailableException error)
            {
                return new(LookLoadState.TransientIo, null, error.Message, ReadOnly: true);
            }
            catch (InvalidDataException) when (blocked.TryGetValue(variant, out var issue))
            {
                var previous = ReadPrevious(variant, issue);
                return previous is null ? new(issue.State, null, issue.Message, ReadOnly: true)
                    : new(LookLoadState.Recovered, previous,
                        "Loaded the previous verified Spektrafilm look. Choose Recover saved look to restore it. " + issue.Message, ReadOnly: true);
            }
        }
    }

    internal VariantLook? Read(Guid variant)
    {
        lock (session)
        {
            RecoveryNotice = null;
            if (blocked.TryGetValue(variant, out var issue))
                throw new InvalidDataException(issue.Message);
            if (session.Pending.TryGetValue(variant, out var edit))
                return Clone(edit.Look);
            try
            {
                CommittedLook? stored;
                try { stored = ReadPrimary(variant); }
                catch (Exception error) when (error is JsonException or InvalidDataException)
                {
                    throw Block(variant, error, canRecoverPrevious: true);
                }
                var primary = stored?.Look;
                var hash = stored?.Hash;
                session.KnownHash[variant] = hash;
                session.KnownLookHash[variant] = stored?.LookHash;
                var recoveryPath = GetRecoveryPath(variant);
                RecoveryCheckpoint? recovery;
                try { recovery = PanelFiles.ReadPreset<RecoveryCheckpoint>(recoveryPath); }
                catch (FileNotFoundException) { recovery = null; }
                catch (DirectoryNotFoundException) { recovery = null; }
                if (recovery is not null)
                {
                    if (recovery.Version != 1 || recovery.CatalogKey != catalogKey || recovery.Variant != variant)
                        throw new InvalidDataException("The saved recovery checkpoint has an unsupported identity or version.");
                    LookStateValidation.Validate(recovery.Look);
                    if (Equivalent(primary, recovery.Look))
                    {
                        // Previous commit succeeded but cleanup failed. Its content is
                        // already durable, so this checkpoint must not revive an old edit.
                        TryDeleteRecovery(variant);
                    }
                    else if (hash == recovery.BaseHash)
                    {
                        session.Pending[variant] = PrepareEdit(recovery.Look, hash) with { RecoveryDurable = true };
                        RecoveryNotice = "Recovered this image’s last unsaved look. Spektrafilm will retry saving it.";
                        return Clone(recovery.Look);
                    }
                    else
                        throw new InvalidDataException("A recovered edit conflicts with another saved look. Both files have been kept; use explicit recovery to reset.");
                }
                if (primary is null && File.Exists(GetPreviousPath(variant)))
                    throw Block(variant, new InvalidDataException("The current saved look is missing."), canRecoverPrevious: true);
                return primary is null ? null : Clone(primary);
            }
            catch (LookUnsupportedException error) { throw Block(variant, error); }
            catch (Exception error) when ((error is JsonException or InvalidDataException) && !blocked.ContainsKey(variant))
            {
                throw Block(variant, error);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new LookReadUnavailableException(error);
            }
        }
    }

    internal void Queue(Guid variant, VariantLook look)
    {
        if (variant == Guid.Empty) throw new ArgumentException("A saved look requires a variant identity.", nameof(variant));
        LookStateValidation.Validate(look);
        lock (session)
        {
            if (blocked.TryGetValue(variant, out var issue))
                throw new InvalidDataException(issue.Message);
            if (!session.KnownHash.ContainsKey(variant) && !session.Pending.ContainsKey(variant))
                Read(variant); // Never overwrite an existing look without validating it.
            session.Pending.TryGetValue(variant, out var prior);
            var baseline = prior is null ? session.KnownHash.GetValueOrDefault(variant) : prior.BaseHash;
            var edit = PrepareEdit(look, baseline);
            if (prior?.Hash == edit.Hash)
                return; // Preserve the durability receipt for the same pending value.
            if (prior is null && session.KnownLookHash.GetValueOrDefault(variant) == edit.Hash)
            {
                try
                {
                    if (FileHash(GetPath(variant)) == baseline)
                        return; // The identical value is already durable; no new edit occurred.
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Flush can protect the edit in recovery when the primary cannot be checked.
                }
            }
            session.Pending[variant] = edit;
        }
    }

    internal void ReplaceIfCurrent(Guid variant, VariantLook expected, VariantLook replacement)
    {
        lock (session)
        {
            if (!Equivalent(Read(variant), expected))
                throw new InvalidOperationException("The saved look changed since this Spektrafilm history entry. The newer look was kept.");
            Queue(variant, replacement);
            FlushPending(variant);
        }
    }

    internal VariantLook? ReadForBatch(Guid variant)
    {
        lock (session)
        {
            var current = Read(variant);
            if (session.Pending.ContainsKey(variant))
                throw new InvalidOperationException("A selected variant has a pending save. Resolve it before applying copied settings.");
            return current;
        }
    }

    internal void RequireBatchBaseline(Guid variant, VariantLook? expected)
    {
        var current = ReadForBatch(variant);
        if (expected is null ? current is not null : !Equivalent(current, expected))
            throw new InvalidOperationException("A selected variant’s saved look changed. The newer look was kept.");
    }

    internal void CommitBatchChange(Guid variant, VariantLook? expected, VariantLook replacement, Action<string?, string>? prepare = null)
    {
        LookStateValidation.Validate(replacement);
        lock (session)
        {
            RequireBatchBaseline(variant, expected);
            // A failed optional copy must not turn into a pending edit and
            // overwrite a user's later change when the normal watchdog runs.
            var edit = PrepareEdit(replacement, session.KnownHash.GetValueOrDefault(variant));
            prepare?.Invoke(edit.BaseHash, edit.Hash);
            Commit(variant, edit);
        }
    }

    internal void Flush() => FlushPending(null);

    internal void FlushVariant(Guid variant) => FlushPending(variant);

    private void FlushPending(Guid? onlyVariant)
    {
        lock (session)
        {
            KeyValuePair<Guid, PendingEdit>[] edits;
            if (onlyVariant is { } only)
            {
                if (!session.Pending.TryGetValue(only, out var pendingEdit)) return;
                edits = [new(only, pendingEdit)];
            }
            else
                edits = session.Pending.ToArray();

            var failures = new List<string>();
            foreach (var (variant, pending) in edits)
            {
                try
                {
                    if (blocked.TryGetValue(variant, out var issue))
                        throw new InvalidDataException(issue.Message);
                    Commit(variant, pending);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    try
                    {
                        Directory.CreateDirectory(recoveryDirectory);
                        PanelFiles.WriteCompactPreset(GetRecoveryPath(variant),
                            new RecoveryCheckpoint(1, catalogKey, variant, pending.BaseHash, pending.Look));
                        session.Pending[variant] = pending with { RecoveryDurable = true };
                        failures.Add($"{error.Message} The latest edit is protected in {GetRecoveryPath(variant)}.");
                    }
                    catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    {
                        session.Pending[variant] = pending with { RecoveryDurable = false };
                        failures.Add($"{error.Message} Recovery could not be saved either: {recoveryError.Message} The latest edit is NOT safely saved. Keep Capture One open and restore disk access.");
                    }
                }
            }
            if (failures.Count != 0)
                throw new IOException(string.Join(Environment.NewLine, failures));
        }
    }

    private void Commit(Guid variant, PendingEdit edit)
    {
        Directory.CreateDirectory(directory);
        var path = GetPath(variant);
        PanelFiles.WriteAtomically(path, stream => stream.Write(edit.Json), () =>
        {
            if (edit.KeepPrevious && edit.BaseHash is not null)
                SavePrevious(variant, path, edit.BaseHash);
            EnsureExpectedPrimary(variant, path, edit.BaseHash);
        }, overwrite: edit.BaseHash is not null);
        session.KnownHash[variant] = edit.Hash;
        session.KnownLookHash[variant] = edit.Hash;
        primaryCache.Remember(variant, new CommittedLook(edit.Look, edit.Hash, edit.Hash));
        session.Pending.Remove(variant);
        TryDeleteRecovery(variant);
    }

    // Only invoked after an explicit user recovery action. Archive both alternatives
    // before replacing either with defaults; a backup failure aborts the reset.
    internal void ResetBlocked(Guid variant, VariantLook replacement)
    {
        LookStateValidation.Validate(replacement);
        lock (session)
        {
            if (!blocked.TryGetValue(variant, out var issue))
                throw new InvalidOperationException("This image has no blocked look to recover.");
            var baseline = ArchiveFile(GetPath(variant), variant, "before-reset")?.Hash;
            if (issue.ArchivedPrimary && baseline != issue.ObservedHash)
            {
                blocked.Remove(variant);
                throw new InvalidDataException("The saved look changed after it was opened for recovery. Reselect this image before trying again.");
            }
            ArchiveFile(GetRecoveryPath(variant), variant, "pending-before-reset");
            var edit = PrepareEdit(replacement, baseline) with { KeepPrevious = false };
            session.KnownHash[variant] = baseline;
            session.KnownLookHash.Remove(variant);
            session.Pending[variant] = edit;
            blocked.Remove(variant);
            FlushPending(variant);
        }
    }

    internal void RestorePrevious(Guid variant, VariantLook expected)
    {
        lock (session)
        {
            if (!blocked.TryGetValue(variant, out var issue) || ReadPrevious(variant, issue) is not { } latest
                || !Equivalent(latest, expected))
                throw new InvalidDataException("The previous saved look changed or is no longer available.");
            ResetBlocked(variant, latest);
        }
    }

    internal void ProtectUnreadable(Guid variant, Exception error)
    {
        lock (session) { if (!blocked.ContainsKey(variant)) Block(variant, error); }
    }

    private CommittedLook? ReadPrimary(Guid variant)
    {
        try
        {
            using var file = File.OpenRead(GetPath(variant));
            var bytes = PanelFiles.ReadPresetBytes(file);
            var digest = Digest(bytes);
            // Compare exact file bytes before reusing the validated snapshot.
            if (primaryCache.Find(variant, digest) is { } cached) return cached;
            var saved = PanelFiles.DecodePreset<VariantLook>(bytes, CheckVersion);
            LookStateValidation.Validate(saved);
            var stored = new CommittedLook(saved, digest, LookHash(saved));
            primaryCache.Remember(variant, stored);
            return stored;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void CheckVersion(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("Version", out var version)
            && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var value) && value != 1)
            throw new LookUnsupportedException($"Saved Spektrafilm look version {value} is unsupported.");
    }

    private InvalidDataException Block(Guid variant, Exception error, bool canRecoverPrevious = false)
    {
        var detail = "The original saved look has not been changed.";
        (string Path, string Hash)? archived = null;
        var archivedPrimary = false;
        try
        {
            archived = ArchiveFile(GetPath(variant), variant, "unreadable");
            archivedPrimary = true;
            ArchiveFile(GetRecoveryPath(variant), variant, "unreadable-recovery");
            if (archived is { } copy) detail += $" A recovery copy is at {copy.Path}.";
        }
        catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
        {
            detail += $" An additional recovery copy could not be written: {backupError.Message}";
        }
        var message = $"{error.Message} {detail} Editing is paused for this image. Use Recover saved look only if you want to reset it.";
        var state = error is LookUnsupportedException ? LookLoadState.Unsupported : LookLoadState.Corrupt;
        blocked[variant] = new(state, message, canRecoverPrevious, archived?.Hash, archivedPrimary);
        return new InvalidDataException(message, error);
    }

    private (string Path, string Hash)? ArchiveFile(string source, Guid variant, string reason)
    {
        FileStream original;
        try { original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using (original)
        {
            var hash = Convert.ToHexString(SHA256.HashData(original));
            original.Position = 0;
            var archives = Path.Combine(recoveryDirectory, "Preserved originals");
            Directory.CreateDirectory(archives);
            var destination = Path.Combine(archives, $"{variant:N}-{reason}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}-{hash}.json");
            PanelFiles.WriteAtomically(destination, stream => original.CopyTo(stream), overwrite: false);
            if (FileHash(destination) != hash)
                throw new IOException("The preserved look did not match the original bytes.");
            return (destination, hash);
        }
    }

    private void EnsureExpectedPrimary(Guid variant, string path, string? expectedHash)
    {
        if (FileHash(path) != expectedHash)
            throw ExternalChange(variant);
    }

    private InvalidDataException ExternalChange(Guid variant) => Block(variant,
        new InvalidDataException("The saved look changed outside this panel. Your pending edit and the external file are both retained."));

    private void SavePrevious(Guid variant, string primary, string expectedHash)
    {
        byte[] bytes;
        try
        {
            using var original = new FileStream(primary, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytes = PanelFiles.ReadPresetBytes(original);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
        {
            throw ExternalChange(variant);
        }
        if (Digest(bytes) != expectedHash)
            throw ExternalChange(variant);
        PanelFiles.WriteAtomically(GetPreviousPath(variant), stream =>
        {
            stream.Write("SFL1"u8);
            stream.Write(variant.ToByteArray());
            stream.Write(Convert.FromHexString(expectedHash));
            stream.Write(bytes);
        });
    }

    private VariantLook? ReadPrevious(Guid variant, BlockedLook issue)
    {
        if (!issue.CanRecoverPrevious) return null;
        try
        {
            using var file = File.OpenRead(GetPreviousPath(variant));
            Span<byte> header = stackalloc byte[52]; // version, variant UUID, SHA-256
            file.ReadExactly(header);
            if (!header[..4].SequenceEqual("SFL1"u8) || new Guid(header[4..20]) != variant)
                return null;
            var bytes = PanelFiles.ReadPresetBytes(file);
            if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(header[20..]))
                return null;
            var previous = PanelFiles.DecodePreset<VariantLook>(bytes, CheckVersion);
            LookStateValidation.Validate(previous);
            return previous;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private void TryDeleteRecovery(Guid variant)
    {
        try { File.Delete(GetRecoveryPath(variant)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string? FileHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static VariantLook Clone(VariantLook look) => look with
    {
        Look = look.Look with { Development = new(look.Look.Development, StringComparer.Ordinal) },
        Optics = look.Optics with { Numbers = new(look.Optics.Numbers, StringComparer.Ordinal),
            Selections = new(look.Optics.Selections, StringComparer.Ordinal), Switches = new(look.Optics.Switches, StringComparer.Ordinal) },
    };
    private static PendingEdit PrepareEdit(VariantLook look, string? baseline)
    {
        var snapshot = Clone(look);
        var json = PanelFiles.EncodeCompactPreset(snapshot);
        return new(snapshot, baseline, json, Digest(json));
    }

    private static string Digest(ReadOnlySpan<byte> json) => Convert.ToHexString(SHA256.HashData(json));
    private static string LookHash(VariantLook look) => Digest(JsonSerializer.SerializeToUtf8Bytes(look));
    internal static bool Equivalent(VariantLook? first, VariantLook second)
    {
        if (first is null) return false;
        if (ReferenceEquals(first, second)) return true;
        if (first.Version != second.Version || first.Enabled != second.Enabled || first.Grain != second.Grain)
            return false;
        var film = second.Look;
        var optics = second.Optics;
        return (first.Look with { Development = film.Development }) == film
            && (first.Optics with { Numbers = optics.Numbers, Selections = optics.Selections, Switches = optics.Switches }) == optics
            && SameValues(first.Look.Development, film.Development)
            && SameValues(first.Optics.Numbers, optics.Numbers)
            && SameValues(first.Optics.Selections, optics.Selections)
            && SameValues(first.Optics.Switches, optics.Switches);
    }

    private static bool SameValues<T>(Dictionary<string, T> first, Dictionary<string, T> second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first.Count != second.Count) return false;
        if (second.Comparer != StringComparer.Ordinal && second.Comparer != EqualityComparer<string>.Default)
            second = new Dictionary<string, T>(second, StringComparer.Ordinal);
        foreach (var (key, value) in first)
            if (!second.TryGetValue(key, out var other) || !EqualityComparer<T>.Default.Equals(value, other))
                return false;
        return true;
    }
    private string GetPath(Guid variant) => Path.Combine(directory, variant.ToString("N") + ".json");
    private string GetPreviousPath(Guid variant) => Path.Combine(directory, variant.ToString("N") + ".previous.look");
    private string GetRecoveryPath(Guid variant) => Path.Combine(recoveryDirectory, variant.ToString("N") + ".pending.json");
    private static string CatalogKey(string database)
    {
        var normalized = Path.GetFullPath(database).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private sealed class Session
    {
        internal readonly Dictionary<Guid, PendingEdit> Pending = new();
        internal readonly Dictionary<Guid, string?> KnownHash = new();
        internal readonly Dictionary<Guid, string?> KnownLookHash = new();
        internal readonly Dictionary<Guid, SavedVariantIdentity> VerifiedIdentities = new();
        internal readonly Dictionary<Guid, BlockedLook> Blocked = new();
    }

    private sealed class PrimaryCache
    {
        private const int Capacity = 64;
        private readonly LinkedList<(Guid Variant, CommittedLook Look)> recent = new();
        private readonly Dictionary<Guid, LinkedListNode<(Guid Variant, CommittedLook Look)>> committed = new();

        internal CommittedLook? Find(Guid variant, string digest)
        {
            if (!committed.TryGetValue(variant, out var node) || node.Value.Look.Hash != digest)
                return null;
            recent.Remove(node);
            recent.AddFirst(node);
            return node.Value.Look;
        }

        internal void Remember(Guid variant, CommittedLook look)
        {
            if (committed.TryGetValue(variant, out var node))
            {
                recent.Remove(node);
                node.Value = (variant, look);
                recent.AddFirst(node);
                return;
            }
            committed.Add(variant, recent.AddFirst((variant, look)));
            if (committed.Count <= Capacity) return;
            committed.Remove(recent.Last!.Value.Variant);
            recent.RemoveLast();
        }
    }

    private sealed record CommittedLook(VariantLook Look, string Hash, string LookHash);
    private sealed record BlockedLook(LookLoadState State, string Message, bool CanRecoverPrevious,
        string? ObservedHash, bool ArchivedPrimary);
    private sealed record PendingEdit(VariantLook Look, string? BaseHash, byte[] Json, string Hash, bool RecoveryDurable = false, bool KeepPrevious = true);
    private sealed record RecoveryCheckpoint([property: JsonRequired] int Version, [property: JsonRequired] string CatalogKey,
        [property: JsonRequired] Guid Variant, [property: JsonRequired] string? BaseHash, [property: JsonRequired] VariantLook Look);
}
