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

internal sealed class QueuedRenderRequest<TSource, TResult>(Func<TSource, TResult> render, Action release)
{
    private Func<TSource, TResult>? callback = render;
    private readonly TaskCompletionSource<TResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task<TResult> Result => completion.Task;

    internal void Abandon()
    {
        // An abandoned queued callback releases its slot here. An executing
        // callback retains the slot until native processing actually returns.
        if (Interlocked.Exchange(ref callback, null) is not null)
            release();
        completion.TrySetCanceled();
    }

    internal void Run(TSource input)
    {
        var run = Interlocked.Exchange(ref callback, null);
        if (run is null)
            return;
        try { completion.TrySetResult(run(input)); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { release(); }
    }
}
