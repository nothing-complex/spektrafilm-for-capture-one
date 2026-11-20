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

internal sealed record ExportReceipt([property: JsonRequired] int Version, [property: JsonRequired] Guid JobId,
    [property: JsonRequired] Guid VariantId, [property: JsonRequired] string Destination, [property: JsonRequired] DateTime AcceptedUtc,
    [property: JsonRequired] DateTime UpdatedUtc, [property: JsonRequired] ExportStage Stage,
    int Width = 0, int Height = 0, string? Error = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeliveryNote = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExportRetryDescriptor? Retry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RetryOf = null)
{
    internal bool Finished => Stage is ExportStage.Completed or ExportStage.Canceled or ExportStage.Failed or ExportStage.Skipped;
}

internal sealed class ExportJobJournal(string directory, Action<string> log)
{
    // Windows cannot replace an open destination even when the reader shares
    // deletion. The Viewer, diagnostics and a newly attached tool can read
    // through different journal instances while a worker advances a receipt.
    private static readonly object FileGate = new();

    internal ExportReceipt? ReadLatest()
    {
        lock (FileGate)
            return ReadLatestUnderLock();
    }

    private ExportReceipt? ReadLatestUnderLock()
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(path => path, StringComparer.Ordinal).Take(16))
            {
                try
                {
                    var receipt = PanelFiles.ReadPreset<ExportReceipt>(path);
                    if (receipt.Version == 1 && receipt.JobId != Guid.Empty && receipt.VariantId != Guid.Empty
                        && receipt.Destination is { Length: > 0 } && Path.IsPathFullyQualified(receipt.Destination) && Enum.IsDefined(receipt.Stage)
                        && receipt.AcceptedUtc <= receipt.UpdatedUtc && Path.GetFileName(path) == Name(receipt)
                        && (receipt.Retry is null || receipt.Retry.Valid)
                        && (receipt.RetryOf is null || receipt.RetryOf != Guid.Empty && receipt.RetryOf != receipt.JobId))
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
            {
                Directory.CreateDirectory(directory);
                PanelFiles.WriteCompactPreset(Path.Combine(directory, Name(receipt)), receipt);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log("Could not save Spektrafilm export progress: " + error.GetBaseException().Message);
        }
    }

    private static string Name(ExportReceipt receipt) =>
        receipt.AcceptedUtc.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + "-" + receipt.JobId.ToString("N") + ".json";
}
