using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static void TestRgb16()
    {
        foreach (var channels in new[] { 3, 4 })
        foreach (var width in new[] { 1, 2, 3, 4, 5, 15, 256, 257 })
        {
            var height = width >= 256 ? 257 : 1;
            var stride = width * channels * sizeof(ushort) + 5;
            var input = new byte[stride * height];
            var expected = new float[width * height * 4];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                expected[index + 3] = 1;
                for (var c = 0; c < channels; c++)
                {
                    var value = unchecked((ushort)((y * width + x) * 167 + c * 141));
                    BitConverter.TryWriteBytes(input.AsSpan(y * stride + (x * channels + c) * sizeof(ushort)), value);
                    expected[index + c] = value / (float)ushort.MaxValue;
                }
            }
            var allocation = Marshal.AllocHGlobal(input.Length);
            try
            {
                Marshal.Copy(input, 0, allocation, input.Length);
                var output = HighDepthSource.Convert16(width, height, stride, allocation, channels == 4, default);
                Check(output.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(expected.AsSpan())),
                    $"Exact scalar {channels}-channel RGB16 conversion, {width}×{height} with padded rows");
                using var stop = new CancellationTokenSource();
                stop.Cancel();
                try
                {
                    HighDepthSource.Convert16(width, height, stride, allocation, channels == 4, stop.Token);
                    Check(false, "Canceled RGB16 conversion returned an image");
                }
                catch (OperationCanceledException)
                {
                    Check(true, "Canceled RGB16 conversion drains before the native caller owns disposal");
                }
            }
            finally { Marshal.FreeHGlobal(allocation); }
        }
    }

    private static void BenchmarkRgb16(string path)
    {
        var cases = new List<object>();
        foreach (var width in new[] { 1200, 6000 })
        foreach (var channels in new[] { 3, 4 })
        {
            var height = width * 2 / 3;
            var stride = width * channels * sizeof(ushort) + 8;
            var block = Marshal.AllocHGlobal(stride * height);
            try
            {
                var row = new byte[stride];
                new Random(42).NextBytes(row);
                for (var y = 0; y < height; y++)
                    Marshal.Copy(row, 0, block + y * stride, row.Length);

                var measurements = new List<double>();
                var allocated = new List<long>();
                var coldMs = 0d;
                var coldManagedBytes = 0L;
                for (var iteration = 0; iteration < 12; iteration++)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    var before = GC.GetTotalAllocatedBytes(precise: true);
                    var start = Stopwatch.GetTimestamp();
                    var value = HighDepthSource.Convert16(width, height, stride, block, channels == 4, default);
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
                    GC.KeepAlive(value);
                    if (iteration == 0)
                    {
                        coldMs = elapsed;
                        coldManagedBytes = bytes;
                    }
                    else
                    {
                        measurements.Add(elapsed);
                        allocated.Add(bytes);
                    }
                }
                measurements.Sort();
                allocated.Sort();
                cases.Add(new
                {
                    width, height, channels, measurements,
                    coldMs, coldManagedBytes,
                    medianMs = measurements[measurements.Count / 2],
                    p95Ms = measurements[(int)Math.Ceiling(measurements.Count * .95) - 1],
                    medianManagedBytes = allocated[allocated.Count / 2],
                });
            }
            finally { Marshal.FreeHGlobal(block); }
        }
        using var output = new FileStream(path, FileMode.CreateNew);
        JsonSerializer.Serialize(output, cases, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void TestFloatSource()
    {
        const int width = 3, height = 2, stride = width * 3 * sizeof(float) + 5;
        var rgb = new[] { -.25f, 2f, .45f, -0f, .25f, 1f, 1.7f, 0f, float.MaxValue,
            .18f, .0001f, float.Epsilon, -2f, .7f, 0f, .35f, .42f, .34f };
        var expected = new[] { -.25f, 2f, .45f, 1, -0f, .25f, 1f, 1, 1.7f, 0f, float.MaxValue, 1,
            .18f, .0001f, float.Epsilon, 1, -2f, .7f, 0f, 1, .35f, .42f, .34f, 1 };
        var allocation = Marshal.AllocHGlobal(stride * height);
        try
        {
            for (var row = 0; row < height; row++)
                Marshal.Copy(rgb, row * width * 3, allocation + row * stride, width * 3);
            var output = HighDepthSource.ConvertFloat(width, height, stride, allocation, default);
            Check(output.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(expected.AsSpan())),
                "Padded RGB32F rows preserve exact finite extended-range codes and add opaque alpha");
            foreach (var nonfinite in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            {
                Marshal.WriteInt32(allocation + stride, BitConverter.SingleToInt32Bits(nonfinite));
                try
                {
                    HighDepthSource.ConvertFloat(width, height, stride, allocation, default);
                    Check(false, "Nonfinite RGB32F conversion returned pixels");
                }
                catch (InvalidDataException)
                {
                    Check(true, "Nonfinite RGB32F input is rejected before publication");
                }
            }
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    private static void TestConversionBudget()
    {
        if (Environment.ProcessorCount < 2)
            return;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var queued = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var first = Task.Run(() => PreviewRows.Process(2200, 2200, default, (_, _) =>
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Timed out holding the conversion fixture.");
        }, maxWorkers: 2));
        try
        {
            Check(started.Wait(TimeSpan.FromSeconds(5)), "First parallel conversion entered the CPU budget");
            var second = Task.Run(() =>
            {
                queued.Set();
                PreviewRows.Process(2200, 2200, cancellation.Token, (_, _) =>
                    throw new InvalidOperationException("A second parallel conversion entered before the first released its CPU budget."));
            });
            Check(queued.Wait(TimeSpan.FromSeconds(5)), "Second conversion is queued at the CPU budget");
            cancellation.Cancel();
            try
            {
                second.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                Check(false, "Canceled queued conversion unexpectedly finished");
            }
            catch (OperationCanceledException) { Check(true, "A waiting parallel conversion is cancelable"); }
        }
        finally
        {
            release.Set();
            first.GetAwaiter().GetResult();
        }
        var rows = 0;
        PreviewRows.Process(2200, 2200, default, (start, end) => Interlocked.Add(ref rows, end - start));
        Check(rows == 2200, "The next parallel conversion receives every row after release");

        using var copying = new ManualResetEventSlim();
        using var finishCopy = new ManualResetEventSlim();
        using var stopCopy = new CancellationTokenSource();
        var bitmap = Marshal.AllocHGlobal(sizeof(short));
        Marshal.WriteInt16(bitmap, 12345);
        var conversion = Task.Run(() => PreviewRows.Process(2200, 2200, stopCopy.Token, (_, _) =>
        {
            copying.Set();
            if (!finishCopy.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Timed out waiting to inspect the native bitmap.");
            if (Marshal.ReadInt16(bitmap) != 12345)
                throw new InvalidDataException("The retained native bitmap changed during conversion.");
            stopCopy.Token.ThrowIfCancellationRequested();
        }, maxWorkers: 2));
        try
        {
            Check(copying.Wait(TimeSpan.FromSeconds(5)), "Cancellation fixture observed an active native-memory reader");
            stopCopy.Cancel();
            Check(!conversion.IsCompleted, "Cancellation leaves the native bitmap owned until active readers return");
        }
        finally
        {
            finishCopy.Set();
            try { conversion.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { Check(true, "Canceled parallel conversion drains before the native bitmap is released"); }
            finally { Marshal.FreeHGlobal(bitmap); }
        }
    }
}
