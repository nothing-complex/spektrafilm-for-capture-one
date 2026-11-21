using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

[JsonConverter(typeof(JsonStringEnumConverter<ExportStage>))]
internal enum ExportStage { Accepted, WaitingForViewer, DevelopingSource, RenderingFilm, Encoding, Completed, Canceled, Failed, Skipped }

internal sealed record ExportRetryDescriptor(bool FollowRecipe, string NativeModel, string Original, string Delivery)
{
    internal bool Valid => IsDigest(NativeModel) && IsDigest(Original) && IsDigest(Delivery);
    internal static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

internal sealed record ExportPublication(long Length, string Sha256, string? DeliveryNote = null)
{
    internal bool Valid => Length > 0 && ExportRetryDescriptor.IsDigest(Sha256) && DeliveryNote is not { Length: > 2048 };

    internal static ExportPublication Read(Stream stream, CancellationToken stop)
    {
        var length = stream.Length;
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                stop.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, buffer.Length);
                stream.ReadExactly(buffer.AsSpan(0, count));
                hash.AppendData(buffer.AsSpan(0, count));
                remaining -= count;
            }
            stop.ThrowIfCancellationRequested();
            return new ExportPublication(length, Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}

internal sealed record ExportBatchEntry(Guid JobId, Guid ImageId, Guid VariantId, string OriginalPath, string Destination,
    ExportRetryDescriptor? Retry,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SkipExisting = false);

internal sealed record ExportBatchPlan(int Version, Guid Id, DateTime AcceptedUtc, string Folder, ExportBatchEntry[] Entries,
    bool Closed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OutputPathGuard.DirectoryIdentity? RootIdentity = null)
{
    internal static ExportBatchPlan Create(string folder, ExportBatchEntry[] entries, OutputPathGuard.DirectoryIdentity? rootIdentity = null) =>
        new(rootIdentity is null ? 1 : 2, Guid.NewGuid(), DateTime.UtcNow, folder, entries, RootIdentity: rootIdentity);
}

internal sealed record ExportReceipt([property: JsonRequired] int Version, [property: JsonRequired] Guid JobId,
    [property: JsonRequired] Guid VariantId, [property: JsonRequired] string Destination, [property: JsonRequired] DateTime AcceptedUtc,
    [property: JsonRequired] DateTime UpdatedUtc, [property: JsonRequired] ExportStage Stage,
    int Width = 0, int Height = 0, string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeliveryNote = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExportRetryDescriptor? Retry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RetryOf = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? BatchId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExportPublication? Publication = null)
{
    internal bool Finished => Stage is ExportStage.Completed or ExportStage.Canceled or ExportStage.Failed or ExportStage.Skipped;
}

internal sealed class ExportJobJournal(string directory, Action<string> log)
{
    private const int MaxBatchEntries = 1000;
    private const int MaxBatchBytes = 4 * 1024 * 1024;
    private string BatchPath => Path.Combine(directory, "batch", "pending.json");
    // Windows cannot replace an open destination even when the reader shares
    // deletion. The Viewer, diagnostics and a newly attached tool can read
    // through different journal instances while a worker advances a receipt.
    private static readonly object FileGate = new();

    internal ExportReceipt? ReadLatest()
    {
        lock (FileGate)
            return ReadLatestUnderLock();
    }

    internal ExportBatchPlan? ReadPendingBatch()
    {
        lock (FileGate)
        {
            try
            {
                using var stream = File.OpenRead(BatchPath);
                if (stream.Length > MaxBatchBytes)
                    throw new InvalidDataException("The saved export batch is too large.");
                var data = new byte[checked((int)stream.Length)];
                stream.ReadExactly(data);
                var plan = PanelFiles.DecodePreset<ExportBatchPlan>(data);
                ValidateBatch(plan);
                if (plan.Closed) return null;
                try
                {
                    return plan.Entries.All(item => ReadBatchItem(plan, item) is { Finished: true }) ? null : plan;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    log("One queued export receipt needs review: " + error.GetBaseException().Message);
                    return plan;
                }
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            {
                log("Could not read the saved export batch: " + error.GetBaseException().Message);
                return null;
            }
        }
    }

    internal void SaveBatch(ExportBatchPlan plan, CancellationToken stop = default)
    {
        stop.ThrowIfCancellationRequested();
        ValidateBatch(plan);
        var data = JsonSerializer.SerializeToUtf8Bytes(plan);
        if (data.Length > MaxBatchBytes)
            throw new InvalidDataException("This batch is too large to save. Export a smaller selection.");
        lock (FileGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BatchPath)!);
            PanelFiles.WriteAtomically(BatchPath, stream => stream.Write(data),
                beforeCommit: stop.ThrowIfCancellationRequested, discardOnProcessExit: true);
        }
    }

    internal ExportReceipt? ReadBatchItem(ExportBatchPlan plan, ExportBatchEntry item)
    {
        var expected = BatchReceipt(plan, item);
        lock (FileGate)
        {
            var path = Path.Combine(directory, Name(expected));
            if (!File.Exists(path)) return null;
            var saved = PanelFiles.ReadPreset<ExportReceipt>(path);
            if (!ValidReceipt(saved, path) || saved.BatchId != plan.Id || saved.VariantId != item.VariantId
                || !StringComparer.OrdinalIgnoreCase.Equals(saved.Destination, item.Destination))
                throw new InvalidDataException("An export receipt does not match its queued image.");
            return saved;
        }
    }

    internal ExportReceipt BeginBatchItem(ExportBatchPlan plan, ExportBatchEntry item)
    {
        lock (FileGate)
        {
            if (ReadBatchItem(plan, item) is { Finished: true })
                throw new InvalidOperationException("This queued export already finished.");
            var receipt = BatchReceipt(plan, item);
            WriteReceipt(receipt);
            return receipt;
        }
    }

    private static ExportReceipt BatchReceipt(ExportBatchPlan plan, ExportBatchEntry item)
    {
        var index = Array.FindIndex(plan.Entries, candidate => candidate.JobId == item.JobId);
        if (index < 0) throw new ArgumentException("The image is outside the approved batch.");
        var accepted = plan.AcceptedUtc.AddTicks(index);
        return new ExportReceipt(1, item.JobId, item.VariantId, item.Destination, accepted, accepted, ExportStage.Accepted,
            Retry: item.Retry, BatchId: plan.Id);
    }

    private static void ValidateBatch(ExportBatchPlan plan)
    {
        if (plan.Version is not (1 or 2) || plan.Id == Guid.Empty || !Path.IsPathFullyQualified(plan.Folder)
            || plan.Version == 1 && plan.RootIdentity is not null
            || plan.Version == 2 && (plan.RootIdentity is not { } root || !Path.IsPathFullyQualified(root.FinalName))
            || plan.AcceptedUtc.Ticks > DateTime.MaxValue.Ticks - MaxBatchEntries
            || plan.Entries is not { Length: > 0 and <= MaxBatchEntries })
            throw new InvalidDataException("The saved export batch is invalid.");
        var ids = new System.Collections.Generic.HashSet<Guid>();
        var variants = new System.Collections.Generic.HashSet<Guid>();
        var paths = new System.Collections.Generic.Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Entries)
        {
            if (item is null || item.JobId == Guid.Empty || !ids.Add(item.JobId) || item.VariantId == Guid.Empty || !variants.Add(item.VariantId)
                || item.ImageId == Guid.Empty || !Path.IsPathFullyQualified(item.OriginalPath) || !Path.IsPathFullyQualified(item.Destination)
                || item.Retry is { Valid: false } || item.SkipExisting && item.Retry is not null)
                throw new InvalidDataException("A saved export batch item is invalid.");
            var destination = Path.GetFullPath(item.Destination);
            var parent = Path.GetDirectoryName(destination);
            if (parent is null || (plan.RootIdentity is null ? !StringComparer.OrdinalIgnoreCase.Equals(parent, plan.Folder)
                : !OutputPathGuard.AtOrBelow(parent, Path.GetFullPath(plan.Folder))))
                throw new InvalidDataException("A saved export batch item is outside the approved folder.");
            if (!paths.TryAdd(destination, item.SkipExisting) && (!item.SkipExisting || !paths[destination]))
                throw new InvalidDataException("Two batch items would write to the same destination.");
        }
    }

    private static bool ValidReceipt(ExportReceipt receipt, string path) =>
        receipt.Version == 1 && receipt.JobId != Guid.Empty && receipt.VariantId != Guid.Empty
        && receipt.Destination is { Length: > 0 } && Path.IsPathFullyQualified(receipt.Destination) && Enum.IsDefined(receipt.Stage)
        && receipt.AcceptedUtc <= receipt.UpdatedUtc && Path.GetFileName(path) == Name(receipt)
        && (receipt.Retry is null || receipt.Retry.Valid)
        && (receipt.Publication is null || receipt is { Stage: ExportStage.Encoding, Width: > 0, Height: > 0, Publication.Valid: true })
        && (receipt.RetryOf is null || receipt.RetryOf != Guid.Empty && receipt.RetryOf != receipt.JobId)
        && receipt.BatchId != Guid.Empty;

    private ExportReceipt? ReadLatestUnderLock()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(path => path, StringComparer.Ordinal).Take(16))
            {
                try
                {
                    var receipt = PanelFiles.ReadPreset<ExportReceipt>(path);
                    if (ValidReceipt(receipt, path))
                        return receipt;
                    log("Ignored a malformed Spektrafilm export record.");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    log("Could not read a Spektrafilm export record: " + error.GetBaseException().Message);
                }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log("Could not read Spektrafilm export history: " + error.GetBaseException().Message);
        }
        return null;
    }

    internal ExportReceipt Accept(Guid variantId, string destination) => AcceptRetry(variantId, destination, null);

    internal ExportReceipt AcceptRetry(Guid variantId, string destination, Guid? previous)
    {
        if (variantId == Guid.Empty || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("A saved export needs a variant and an absolute destination path.");
        var now = DateTime.UtcNow;
        var receipt = new ExportReceipt(1, Guid.NewGuid(), variantId, destination, now, now, ExportStage.Accepted, RetryOf: previous);
        Save(receipt);
        return receipt;
    }

    internal ExportReceipt RecordRetry(ExportReceipt receipt, ExportRetryDescriptor descriptor)
    {
        if (receipt.Stage != ExportStage.Accepted || receipt.Retry is not null || !descriptor.Valid)
            throw new InvalidOperationException("Resume information belongs to a newly accepted export.");
        var next = receipt with { Retry = descriptor };
        Save(next);
        return next;
    }

    internal ExportReceipt RecordPublication(ExportReceipt receipt, ExportPublication publication)
    {
        if (receipt is not { Stage: ExportStage.Encoding, Width: > 0, Height: > 0, Publication: null } || !publication.Valid)
            throw new InvalidOperationException("A verified publication belongs to an export that is encoding.");
        var next = receipt with { Publication = publication };
        Save(next);
        return next;
    }

    internal ExportReceipt? CompletePublished(ExportReceipt receipt, CancellationToken stop)
    {
        if (receipt is not { Stage: ExportStage.Encoding, Publication: { Valid: true } publication })
            return null;
        stop.ThrowIfCancellationRequested();
        FileStream file;
        try
        {
            file = new FileStream(receipt.Destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        using (file)
        {
            if (file.Length != publication.Length ||
                !StringComparer.OrdinalIgnoreCase.Equals(ExportPublication.Read(file, stop).Sha256, publication.Sha256))
                return null;
            lock (FileGate)
            {
                stop.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, Name(receipt));
                var current = PanelFiles.ReadPreset<ExportReceipt>(path);
                if (!ValidReceipt(current, path) || current != receipt)
                    throw new InvalidOperationException("The saved export changed while its output was being checked.");
                var next = AdvanceReceipt(receipt, ExportStage.Completed, deliveryNote: publication.DeliveryNote);
                WriteReceipt(next);
                return next;
            }
        }
    }

    internal ExportReceipt Report(ExportReceipt current, ExportStage stage, int width = 0, int height = 0,
        string? error = null, string? deliveryNote = null)
    {
        var next = AdvanceReceipt(current, stage, width, height, error, deliveryNote);
        Save(next);
        return next;
    }

    private static ExportReceipt AdvanceReceipt(ExportReceipt current, ExportStage stage, int width = 0, int height = 0,
        string? error = null, string? deliveryNote = null)
    {
        if (current.Finished || stage <= current.Stage || !Enum.IsDefined(stage))
            throw new InvalidOperationException("The Spektrafilm export stage cannot move backwards or change after completion.");
        var now = DateTime.UtcNow;
        return current with { Stage = stage, UpdatedUtc = now < current.UpdatedUtc ? current.UpdatedUtc : now, Width = width == 0 ? current.Width : width,
            Height = height == 0 ? current.Height : height, Error = error is { Length: > 1000 } ? error[..1000] : error,
            DeliveryNote = stage == ExportStage.Completed ? deliveryNote : null,
            Retry = stage is ExportStage.Completed or ExportStage.Canceled or ExportStage.Failed or ExportStage.Skipped ? null : current.Retry,
            Publication = null };
    }

    private void Save(ExportReceipt receipt)
    {
        try
        {
            lock (FileGate)
                WriteReceipt(receipt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log("Could not save Spektrafilm export progress: " + error.GetBaseException().Message);
        }
    }

    private void WriteReceipt(ExportReceipt receipt)
    {
        Directory.CreateDirectory(directory);
        PanelFiles.WriteCompactPreset(Path.Combine(directory, Name(receipt)), receipt);
    }

    private static string Name(ExportReceipt receipt) =>
        receipt.AcceptedUtc.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + receipt.JobId.ToString("N") + ".json";
}
