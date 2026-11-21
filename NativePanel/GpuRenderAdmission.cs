using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal enum GpuWorkPriority { Output, Interactive, Warmup }

internal readonly record struct GpuHeapIdentity(Guid Adapter, int Heap);

internal readonly record struct GpuRenderPlan(GpuHeapIdentity Heap, long PlanningBytes, long EstimatedBytes,
    long ReclaimableBytes = 0);

internal sealed class GpuRenderAdmission
{
    internal static GpuRenderAdmission Shared { get; } = new();
    private readonly object sync = new();
    private readonly Dictionary<GpuHeapIdentity, HeapQueue> queues = new();

    internal async Task<IDisposable> AcquireAsync(GpuRenderPlan plan, GpuWorkPriority priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var job = new Job(plan, priority);
        lock (sync)
        {
            if (!queues.TryGetValue(plan.Heap, out var queue))
                queues.Add(plan.Heap, queue = new());
            queue.Waiting.Add(job);
            Admit(queue);
        }
        using var cancellation = token.Register(() => Cancel(job, token));
        return await job.Ready.Task.ConfigureAwait(false);
    }

    private void Cancel(Job job, CancellationToken token)
    {
        lock (sync)
        {
            if (!queues.TryGetValue(job.Plan.Heap, out var queue) || !queue.Waiting.Remove(job))
                return;
            job.Ready.TrySetCanceled(token);
            Advance(job.Plan.Heap, queue);
        }
    }

    private void Release(Job job)
    {
        lock (sync)
        {
            var queue = queues[job.Plan.Heap];
            queue.Active.Remove(job);
            Advance(job.Plan.Heap, queue);
        }
    }

    private void Advance(GpuHeapIdentity heap, HeapQueue queue)
    {
        Admit(queue);
        if (queue.Active.Count == 0 && queue.Waiting.Count == 0)
            queues.Remove(heap);
    }

    private void Admit(HeapQueue queue)
    {
        while (queue.Waiting.MinBy(job => job.Priority) is { } next)
        {
            if (queue.Active.Count > 0)
            {
                var limit = Math.Min(next.Plan.PlanningBytes, queue.Active.Min(job => job.Plan.PlanningBytes));
                var used = queue.Active.Sum(job => job.Plan.EstimatedBytes);
                if (used > limit || next.Plan.EstimatedBytes > limit - used)
                    return;
            }
            // A solitary request still uses the worker's established tile and
            // pressure policy. The full-frame estimate may exceed that budget.
            queue.Waiting.Remove(next);
            queue.Active.Add(next);
            next.Ready.SetResult(new Lease(this, next));
        }
    }

    private sealed class HeapQueue
    {
        internal readonly List<Job> Waiting = new();
        internal readonly List<Job> Active = new();
    }

    private sealed class Job(GpuRenderPlan plan, GpuWorkPriority priority)
    {
        internal GpuRenderPlan Plan { get; } = plan;
        internal GpuWorkPriority Priority { get; } = priority;
        internal readonly TaskCompletionSource<IDisposable> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(GpuRenderAdmission admission, Job job) : IDisposable
    {
        private GpuRenderAdmission? owner = admission;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(job);
    }
}
