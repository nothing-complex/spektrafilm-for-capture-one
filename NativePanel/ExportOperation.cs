using System;
using System.Threading;

// Accepted output jobs are independent of later UI selection / enable state.
internal sealed class ExportOperation(Guid variantId) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private long encoderBytesWritten;
    internal Guid VariantId { get; } = variantId;
    internal bool IsAccepted { get; set; }
    internal CancellationToken Token => stop.Token;
    internal long EncoderBytesWritten => Interlocked.Read(ref encoderBytesWritten);
    internal void ReportEncoderWrite(int bytes) => Interlocked.Add(ref encoderBytesWritten, bytes);
    internal void Cancel() => stop.Cancel();
    public void Dispose() => stop.Dispose();
}

// One activity per native document. Views can unsubscribe and reattach on the
// UI thread without taking ownership of the accepted operation.
internal sealed class ExportActivity
{
    internal ExportOperation? Active { get; private set; }
    internal string? Message { get; private set; }
    internal event Action? Changed;

    internal void Begin(ExportOperation work)
    {
        if (Active is not null)
            throw new InvalidOperationException("A Spektrafilm export is already in progress.");
        Active = work;
        Changed?.Invoke();
    }

    internal void Report(ExportOperation? work, string message)
    {
        if (!ReferenceEquals(Active, work))
            return;
        Message = message;
        Changed?.Invoke();
    }

    internal void End(ExportOperation work)
    {
        if (!ReferenceEquals(Active, work))
            return;
        Active = null;
        Changed?.Invoke();
    }
}
