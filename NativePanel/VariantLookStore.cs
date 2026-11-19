using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal sealed record GrainControls([property: JsonRequired] int Type, [property: JsonRequired] double Impact,
    [property: JsonRequired] double Granularity);
internal sealed record VariantLook([property: JsonRequired] LookSettings Look, [property: JsonRequired] SpatialPreview.Settings Optics,
    [property: JsonRequired] bool Enabled, [property: JsonRequired] int Version = 1, GrainControls? Grain = null);

internal sealed class LookReadUnavailableException(Exception inner)
    : IOException("The saved look is temporarily inaccessible. Spektrafilm will retry loading it. " + inner.Message, inner);

internal sealed class VariantLookStore
{
    // Failed writes must survive UI teardown. Pending edits also get an independent
    // on-disk recovery checkpoint when the normal destination is unavailable.
    private static readonly ConcurrentDictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string directory;
    private readonly string recoveryDirectory;
    private readonly string catalogKey;
    private readonly Session session;
    private readonly Dictionary<Guid, string> blocked;
    internal GrainReceiptStore GrainReceipts { get; }
    internal string CatalogIdentity => catalogKey;
    internal string? RecoveryNotice { get; private set; }

    internal VariantLookStore(string root, string catalogDatabase)
    {
        catalogKey = CatalogKey(catalogDatabase);
        directory = Path.Combine(Path.GetFullPath(root), catalogKey);
        recoveryDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(root))!, "Look recovery", catalogKey);
        GrainReceipts = new GrainReceiptStore(recoveryDirectory);
        session = Sessions.GetOrAdd(directory, _ => new Session());
        blocked = session.Blocked;
    }

    internal bool IsBlocked(Guid variant) { lock (session) return blocked.ContainsKey(variant); }
    internal bool HasPending { get { lock (session) return session.Pending.Count != 0; } }
    internal bool HasUnprotectedPending { get { lock (session) return session.Pending.Values.Any(edit => !edit.RecoveryDurable); } }

    internal VariantLook? Read(Guid variant)
    {
        lock (session)
        {
            RecoveryNotice = null;
            if (blocked.TryGetValue(variant, out var issue))
                throw new InvalidDataException(issue);
            if (session.Pending.TryGetValue(variant, out var edit))
                return Clone(edit.Look);
            try
            {
                var primary = ReadPrimary(variant, out var hash);
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
                session.KnownHash[variant] = hash;
                session.KnownLookHash[variant] = primary is null ? null : LookHash(primary);
                return primary;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
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
                throw new InvalidDataException(issue);
            if (!session.KnownHash.ContainsKey(variant) && !session.Pending.ContainsKey(variant))
                Read(variant); // Never overwrite an existing look without validating it.
            session.Pending.TryGetValue(variant, out var prior);
            var baseline = prior is null ? session.KnownHash.GetValueOrDefault(variant) : prior.BaseHash;
            var edit = PrepareEdit(look, baseline);
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

    internal void Flush() => FlushPending(null);

    private void FlushPending(Guid? onlyVariant)
    {
        lock (session)
        {
            var failures = new List<string>();
            foreach (var (variant, pending) in session.Pending.ToArray())
            {
                if (onlyVariant is { } only && only != variant) continue;
                try
                {
                    if (blocked.TryGetValue(variant, out var issue))
                        throw new InvalidDataException(issue);
                    Directory.CreateDirectory(directory);
                    var path = GetPath(variant);
                    PanelFiles.WriteAtomically(path, stream => stream.Write(pending.Json), () =>
                    {
                        if (FileHash(path) != pending.BaseHash)
                            throw Block(variant, new InvalidDataException("The saved look changed outside this panel. Your pending edit and the external file are both retained."));
                    }, overwrite: pending.BaseHash is not null);
                    session.KnownHash[variant] = pending.Hash;
                    session.KnownLookHash[variant] = pending.Hash;
                    session.Pending.Remove(variant);
                    TryDeleteRecovery(variant);
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

    // Only invoked after an explicit user recovery action. Archive both alternatives
    // before replacing either with defaults; a backup failure aborts the reset.
    internal void ResetBlocked(Guid variant, VariantLook replacement)
    {
        LookStateValidation.Validate(replacement);
        lock (session)
        {
            if (!blocked.ContainsKey(variant))
                throw new InvalidOperationException("This image has no blocked look to recover.");
            ArchiveFile(GetPath(variant), variant, "before-reset");
            ArchiveFile(GetRecoveryPath(variant), variant, "pending-before-reset");
            var baseline = FileHash(GetPath(variant));
            var edit = PrepareEdit(replacement, baseline);
            session.KnownHash[variant] = baseline;
            session.KnownLookHash.Remove(variant);
            session.Pending[variant] = edit;
            blocked.Remove(variant);
            FlushPending(variant);
        }
    }

    internal void ProtectUnreadable(Guid variant, Exception error)
    {
        lock (session) { if (!blocked.ContainsKey(variant)) Block(variant, error); }
    }

    private VariantLook? ReadPrimary(Guid variant, out string? hash)
    {
        hash = null;
        try
        {
            var saved = PanelFiles.ReadPreset<VariantLook>(GetPath(variant), out var digest);
            LookStateValidation.Validate(saved);
            hash = digest;
            return saved;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private InvalidDataException Block(Guid variant, Exception error)
    {
        var detail = "The original saved look has not been changed.";
        try
        {
            var archived = ArchiveFile(GetPath(variant), variant, "unreadable");
            ArchiveFile(GetRecoveryPath(variant), variant, "unreadable-recovery");
            if (archived is not null) detail += $" A recovery copy is at {archived}.";
        }
        catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
        {
            detail += $" An additional recovery copy could not be written: {backupError.Message}";
        }
        var message = $"{error.Message} {detail} Editing is paused for this image. Use Recover saved look only if you want to reset it.";
        blocked[variant] = message;
        return new InvalidDataException(message, error);
    }

    private string? ArchiveFile(string source, Guid variant, string reason)
    {
        if (!File.Exists(source)) return null;
        var archives = Path.Combine(recoveryDirectory, "Preserved originals");
        Directory.CreateDirectory(archives);
        var destination = Path.Combine(archives, $"{variant:N}-{reason}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
        using var original = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        PanelFiles.WriteAtomically(destination, stream => original.CopyTo(stream));
        return destination;
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
    internal static bool Equivalent(VariantLook? first, VariantLook second) => first is not null &&
        JsonNode.DeepEquals(JsonSerializer.SerializeToNode(first), JsonSerializer.SerializeToNode(second));
    private string GetPath(Guid variant) => Path.Combine(directory, variant.ToString("N") + ".json");
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
        internal readonly Dictionary<Guid, string> Blocked = new();
    }
    private sealed record PendingEdit(VariantLook Look, string? BaseHash, byte[] Json, string Hash, bool RecoveryDurable = false);
    private sealed record RecoveryCheckpoint([property: JsonRequired] int Version, [property: JsonRequired] string CatalogKey,
        [property: JsonRequired] Guid Variant, [property: JsonRequired] string? BaseHash, [property: JsonRequired] VariantLook Look);
}
