using System;
using System.Globalization;

internal sealed class ViewportQuality
{
    private static readonly int[] Edges = { 720, 900, 1200, 1600, 2048, 2560, 3072, 4096 };
    private const int Initial = 3;
    private int current = Initial;
    private int candidate = Initial;
    private long candidateSince;
    internal int LongEdge => Edges[current];
    internal string SizeId { get; private set; } = Edges[Initial].ToString(CultureInfo.InvariantCulture);

    internal void Reset() => Commit(Initial);

    internal bool Update(double? projectedLongEdge, double shortToLongRatio, long? budget, long now, bool firstFrame = false)
    {
        var ratio = double.IsFinite(shortToLongRatio) ? Math.Clamp(shortToLongRatio, .01, 1) : 1;
        var affordable = Edges.Length - 1;
        while (affordable > 0 && budget is { } available && Estimate(Edges[affordable], ratio) > available)
            affordable--;
        if (!firstFrame && current > affordable)
            return Commit(affordable);

        if (projectedLongEdge is not double projection || !double.IsFinite(projection) || projection < 1)
        {
            candidate = current;
            return current > affordable && Commit(affordable);
        }

        // Require modest display oversampling. At a lower boundary, keep the
        // current size through small zoom and DPI rounding changes.
        var target = Math.Min(projection * 1.06, Edges[^1]);
        var next = Array.FindIndex(Edges, edge => edge >= target);
        next = Math.Min(next, affordable);
        // There's no frame to protect from a resolution change at startup.
        if (firstFrame)
            return Commit(next);
        if (next < current && target > Edges[current - 1] * .9)
            next = current;
        if (next > current && budget is { } headroom && Estimate(Edges[next], ratio) > headroom * .8)
            next = current;

        if (candidate != next)
        {
            candidate = next;
            candidateSince = now;
            return false;
        }
        if (current == next || now - candidateSince < (next > current ? 250 : 800))
            return false;
        return Commit(next);
    }

    private bool Commit(int value)
    {
        var changed = current != value;
        current = candidate = value;
        SizeId = Edges[current].ToString(CultureInfo.InvariantCulture);
        return changed;
    }

    private static long Estimate(int edge, double ratio) => RenderMemoryBudget.EstimateBytes(
        RenderAllocation.WorkerTransport, (long)Math.Ceiling(edge * edge * ratio));
}
