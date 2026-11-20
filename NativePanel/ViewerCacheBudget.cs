using System;
using System.Threading;

// An allowance for optional retained pixel buffers. In-flight renders and their
// outputs have independent ownership and use the render admission budget.
internal sealed class ViewerCacheBudget(long byteLimit)
{
    private readonly object sync = new();
    private long retained;

    internal const long SharedByteLimit = 512L * 1024 * 1024;
    internal static ViewerCacheBudget Shared { get; } = new(SharedByteLimit);
    internal long RetainedBytes { get { lock (sync) return retained; } }

    internal IDisposable? TryRetain(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (sync)
        {
            if (bytes > byteLimit - retained)
                return null;
            var reservation = new Reservation(this, bytes);
            retained += bytes;
            return reservation;
        }
    }

    private void Release(long bytes)
    {
        lock (sync)
            retained -= bytes;
    }

    private sealed class Reservation(ViewerCacheBudget budget, long bytes) : IDisposable
    {
        private ViewerCacheBudget? owner = budget;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(bytes);
    }
}
