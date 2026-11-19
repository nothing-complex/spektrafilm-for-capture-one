using System;
using System.Runtime.InteropServices;
using System.Threading;

internal enum RenderAllocation { NativeDevelopment, WorkerTransport, ImageEncoding }

internal sealed class RenderMemoryBudget(Func<long?> availableBytes)
{
    private readonly object sync = new();
    private long reserved;
    private const long MiB = 1024L * 1024L;
    internal static RenderMemoryBudget SystemMemory { get; } = new(ReadAvailableMemory);

    internal IDisposable Reserve(RenderAllocation stage, long pixels, string resolutionAction)
    {
        var estimate = EstimateBytes(stage, pixels);
        lock (sync)
        {
            if (availableBytes() is long available)
            {
                // Keep one eighth of current headroom for Capture One and the OS.
                var budget = Math.Max(available, 0);
                budget -= budget / 8;
                if (estimate > budget - reserved)
                    throw new InsufficientMemoryException(
                        $"Spektrafilm needs about {Math.Ceiling(estimate / (double)MiB):F0} MiB of system memory for this frame. {resolutionAction}");
            }
            reserved += estimate;
        }
        return new Reservation(this, estimate);
    }

    internal static long EstimateBytes(RenderAllocation stage, long pixels)
    {
        if (pixels is <= 0 or > 100_000_000)
            throw new ArgumentOutOfRangeException(nameof(pixels));
        // The source is already present by worker transport. Worker samples
        // reached about 68 B/px of peak working set at 24 MP. Reserve overhead
        // for buffered I/O, the managed reply and less cache-friendly workloads.
        return stage switch
        {
            RenderAllocation.NativeDevelopment => 64 * MiB + pixels * 32,
            RenderAllocation.WorkerTransport => 128 * MiB + pixels * 96,
            RenderAllocation.ImageEncoding => 64 * MiB + pixels * 24,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
    }

    private void Release(long bytes)
    {
        lock (sync)
            reserved -= bytes;
    }

    private sealed class Reservation(RenderMemoryBudget budget, long bytes) : IDisposable
    {
        private RenderMemoryBudget? owner = budget;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(bytes);
    }

    private static long? ReadAvailableMemory()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref state))
            return null;
        return checked((long)Math.Min(state.AvailablePhysical, state.AvailableCommit));
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhysical, AvailablePhysical, TotalCommit, AvailableCommit, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
