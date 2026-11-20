using System;
using System.Threading;

// Accepted output jobs are independent of later UI selection / enable state.
internal sealed class ExportOperation(Guid variantId) : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private long encoderBytesWritten;
    internal Guid VariantId { get; } = variantId;
    internal CancellationToken Token => stop.Token;
    internal long EncoderBytesWritten => Interlocked.Read(ref encoderBytesWritten);
    internal void ReportEncoderWrite(int bytes) => Interlocked.Add(ref encoderBytesWritten, bytes);
    internal void Cancel() => stop.Cancel();
    public void Dispose() => stop.Dispose();
}
