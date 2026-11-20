using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

internal static class PreviewRows
{
    private const int ParallelPixelThreshold = 4_000_000;
    private const int MaximumWorkers = 6;
    private static readonly SemaphoreSlim LargeConversion = new(1, 1);

    internal static void Process(int width, int height, CancellationToken token, Action<int, int> processRange,
        int parallelPixelThreshold = ParallelPixelThreshold, int maxWorkers = MaximumWorkers)
    {
        token.ThrowIfCancellationRequested();
        var workers = Math.Min(Math.Min(Environment.ProcessorCount, maxWorkers), height);
        if ((long)width * height < parallelPixelThreshold || workers <= 1)
        {
            processRange(0, height);
            return;
        }

        LargeConversion.Wait(token);
        try
        {
            var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = workers };
            Parallel.For(0, workers, options, worker =>
            {
                var start = (int)((long)height * worker / workers);
                var end = (int)((long)height * (worker + 1) / workers);
                processRange(start, end);
            });
        }
        catch (AggregateException error) when (error.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(error.InnerExceptions[0]).Throw();
        }
        finally { LargeConversion.Release(); }
    }
}
