using System;
using System.Threading;
using System.Threading.Tasks;

internal static class RenderCoordination
{
    // A provisional frame is never a prerequisite for the ready final source.
    internal static async Task<T> PreferFinalAsync<T>(Task<T> final, Func<CancellationToken, Task> preview, CancellationToken stop)
    {
        if (final.IsCompleted) return await final.WaitAsync(stop);
        var provisional = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task work;
        try { work = preview(provisional.Token); }
        catch
        {
            // A failed provisional renderer must not discard an already accepted
            // final development task or leave its eventual error unobserved.
            work = Task.CompletedTask;
        }
        try { return await final.WaitAsync(stop); }
        finally
        {
            provisional.Cancel();
            _ = ObserveAndDisposeAsync(work, provisional);
        }
    }

    private static async Task ObserveAndDisposeAsync(Task work, CancellationTokenSource lifetime)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { /* Provisional errors are reported by the preview route. */ }
        finally { lifetime.Dispose(); }
    }
}

internal sealed class PreviewRendererRetry<TKey> where TKey : struct, IEquatable<TKey>
{
    private TKey? tried;
    private long readyAt;

    internal bool Schedule(TKey key, long now)
    {
        if (tried is { } previous && previous.Equals(key)) return false;
        tried = key;
        readyAt = now + 250;
        return true;
    }

    internal bool IsWaiting(TKey key, long now) => now < readyAt && tried is { } previous && previous.Equals(key);
    internal void Reset() => tried = null;
}

internal sealed class QueuedRenderRequest<TSource, TResult>
{
    private Func<TSource, TResult>? callback;
    private readonly Action release;
    private TaskCompletionSource<TResult>? completion;

    internal QueuedRenderRequest(Func<TSource, TResult> render, Action release, out Task<TResult> result)
    {
        callback = render;
        this.release = release;
        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        result = completion.Task;
    }

    internal void Abandon()
    {
        // An abandoned queued callback releases its slot here. An executing
        // callback retains the slot until native processing actually returns.
        if (Interlocked.Exchange(ref callback, null) is not null)
            release();
        Interlocked.Exchange(ref completion, null)?.TrySetCanceled();
    }

    internal void Run(TSource input)
    {
        var run = Interlocked.Exchange(ref callback, null);
        if (run is null)
            return;
        try
        {
            var output = run(input);
            // Capture One can retain its callback delegate after listener
            // removal. Only the awaiting caller should own the delivered frame.
            Interlocked.Exchange(ref completion, null)?.TrySetResult(output);
        }
        catch (OperationCanceledException error)
        {
            Interlocked.Exchange(ref completion, null)?.TrySetCanceled(error.CancellationToken);
        }
        catch (Exception error) { Interlocked.Exchange(ref completion, null)?.TrySetException(error); }
        finally { release(); }
    }
}
