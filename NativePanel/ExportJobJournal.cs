using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter<ExportStage>))]
internal enum ExportStage { Accepted, WaitingForViewer, DevelopingSource, RenderingFilm, Encoding, Completed, Canceled, Failed, Skipped }

internal sealed record ExportRetryDescriptor(bool FollowRecipe, string NativeModel, string Original, string Delivery)
{
    internal bool Valid => IsDigest(NativeModel) && IsDigest(Original) && IsDigest(Delivery);
    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

internal sealed record ExportBatchEntry(Guid JobId, Guid ImageId, Guid VariantId, string OriginalPath, string Destination,
    ExportRetryDescriptor? Retry);

internal sealed record ExportBatchPlan(int Version, Guid Id, DateTime AcceptedUtc, string Folder, ExportBatchEntry[] Entries,
    bool Closed = false)
{
    internal static ExportBatchPlan Create(string folder, ExportBatchEntry[] entries) =>
        new(1, Guid.NewGuid(), DateTime.UtcNow, folder, entries);
}

internal sealed record ExportReceipt([property: JsonRequired] int Version, [property: JsonRequired] Guid JobId,
    [property: JsonRequired] Guid VariantId, [property: JsonRequired] string Destination, [property: JsonRequired] DateTime AcceptedUtc,
    [property: JsonRequired] DateTime UpdatedUtc, [property: JsonRequired] ExportStage Stage,
    int Width = 0, int Height = 0, string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeliveryNote = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExportRetryDescriptor? Retry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RetryOf = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? BatchId = null)
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

    internal void SaveBatch(ExportBatchPlan plan)
    {
        ValidateBatch(plan);
        var data = JsonSerializer.SerializeToUtf8Bytes(plan);
        if (data.Length > MaxBatchBytes)
            throw new InvalidDataException("This batch is too large to save. Export a smaller selection.");
        lock (FileGate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BatchPath)!);
            PanelFiles.WriteAtomically(BatchPath, stream => stream.Write(data));
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
        if (plan.Version != 1 || plan.Id == Guid.Empty || !Path.IsPathFullyQualified(plan.Folder)
            || plan.AcceptedUtc.Ticks > DateTime.MaxValue.Ticks - MaxBatchEntries
            || plan.Entries is not { Length: > 0 and <= MaxBatchEntries })
            throw new InvalidDataException("The saved export batch is invalid.");
        var ids = new System.Collections.Generic.HashSet<Guid>();
        var variants = new System.Collections.Generic.HashSet<Guid>();
        var paths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Entries)
        {
            if (item is null || item.JobId == Guid.Empty || !ids.Add(item.JobId) || item.VariantId == Guid.Empty || !variants.Add(item.VariantId)
                || item.ImageId == Guid.Empty || !Path.IsPathFullyQualified(item.OriginalPath) || !Path.IsPathFullyQualified(item.Destination)
                || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(Path.GetFullPath(item.Destination)), plan.Folder)
                || !paths.Add(Path.GetFullPath(item.Destination)) || item.Retry is { Valid: false })
                throw new InvalidDataException("A saved export batch item is invalid.");
        }
    }

    private static bool ValidReceipt(ExportReceipt receipt, string path) =>
        receipt.Version == 1 && receipt.JobId != Guid.Empty && receipt.VariantId != Guid.Empty
        && receipt.Destination is { Length: > 0 } && Path.IsPathFullyQualified(receipt.Destination) && Enum.IsDefined(receipt.Stage)
        && receipt.AcceptedUtc <= receipt.UpdatedUtc && Path.GetFileName(path) == Name(receipt)
        && (receipt.Retry is null || receipt.Retry.Valid)
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

    internal ExportReceipt Report(ExportReceipt current, ExportStage stage, int width = 0, int height = 0,
        string? error = null, string? deliveryNote = null)
    {
        if (current.Finished || stage <= current.Stage || !Enum.IsDefined(stage))
            throw new InvalidOperationException("The Spektrafilm export stage cannot move backwards or change after completion.");
        var now = DateTime.UtcNow;
        var next = current with { Stage = stage, UpdatedUtc = now < current.UpdatedUtc ? current.UpdatedUtc : now, Width = width == 0 ? current.Width : width,
            Height = height == 0 ? current.Height : height, Error = error is { Length: > 1000 } ? error[..1000] : error,
            DeliveryNote = stage == ExportStage.Completed ? deliveryNote : null,
            Retry = stage is ExportStage.Completed or ExportStage.Canceled or ExportStage.Failed or ExportStage.Skipped ? null : current.Retry };
        Save(next);
        return next;
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
