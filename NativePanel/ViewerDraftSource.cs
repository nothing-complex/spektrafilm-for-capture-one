using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal static class ViewerDraftSource
{
    internal const int MaxSide = 2560;
    private const int BytesPerPixel = 4 * sizeof(float);
    private const int RowsPerBlock = 8;

    internal static HostImageSource.Frame Downsample(HostImageSource.Frame frame, int maxLongEdge, CancellationToken token,
        PreviewInputSpace space = PreviewInputSpace.LinearRec2020)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLongEdge);
        var sourcePixels = (long)frame.Width * frame.Height;
        if (frame.Width <= 0 || frame.Height <= 0 || sourcePixels != frame.FloatBytes.Length / BytesPerPixel
            || frame.FloatBytes.Length % BytesPerPixel != 0)
            throw new ArgumentException("Invalid draft preview source frame.", nameof(frame));
        if (frame.Description is { } description && (description.Source.InputEncoding != space ||
            !description.Describes(frame.Width, frame.Height, frame.BitDepth, frame.FloatBytes.Length)))
            throw new ArgumentException("The draft does not match its developed source descriptor.", nameof(frame));

        token.ThrowIfCancellationRequested();
        // Limiting the long edge also caps square frames at 6,553,600 pixels.
        var scale = Math.Min(1, (double)Math.Min(maxLongEdge, MaxSide) / Math.Max(frame.Width, frame.Height));
        var width = Math.Max(1, (int)Math.Floor(frame.Width * scale + 1e-9));
        var height = Math.Max(1, (int)Math.Floor(frame.Height * scale + 1e-9));
        if (width == frame.Width && height == frame.Height)
            return frame;

        // Average the full source area covered by each destination pixel. Fractional
        // edges keep the filter centered and preserve the same normalized canvas.
        var columns = BuildBoxes(frame.Width, width);
        var rows = BuildBoxes(frame.Height, height);
        var normalizer = (float)((double)width * height / sourcePixels);
        var draft = new byte[checked(width * height * BytesPerPixel)];
        var options = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8),
        };
        Parallel.For(0, (height + RowsPerBlock - 1) / RowsPerBlock, options, block =>
        {
            var source = MemoryMarshal.Cast<byte, Vector4>(frame.FloatBytes.AsSpan());
            var destination = MemoryMarshal.Cast<byte, Vector4>(draft.AsSpan());
            var end = Math.Min(height, (block + 1) * RowsPerBlock);
            for (var y = block * RowsPerBlock; y < end; y++)
            {
                token.ThrowIfCancellationRequested();
                var targetRow = destination.Slice(y * width, width);
                var row = rows[y];
                for (var sourceY = row.First; sourceY <= row.Last; sourceY++)
                {
                    var rowWeight = sourceY == row.First ? row.FirstWeight : sourceY == row.Last ? row.LastWeight : 1;
                    var sourceRow = source.Slice(sourceY * frame.Width, frame.Width);
                    for (var x = 0; x < width; x++)
                    {
                        var column = columns[x];
                        var sum = Linear(sourceRow[column.First], space) * column.FirstWeight;
                        for (var sourceX = column.First + 1; sourceX < column.Last; sourceX++)
                            sum += Linear(sourceRow[sourceX], space);
                        if (column.Last != column.First)
                            sum += Linear(sourceRow[column.Last], space) * column.LastWeight;
                        targetRow[x] += sum * rowWeight;
                    }
                }
                for (var x = 0; x < width; x++)
                {
                    var value = targetRow[x] * normalizer;
                    targetRow[x] = new Vector4(ColorTransfer.Encode(value.X, space), ColorTransfer.Encode(value.Y, space),
                        ColorTransfer.Encode(value.Z, space), value.W);
                }
            }
        });
        return frame with
        {
            Width = width, Height = height, FloatBytes = draft,
            Description = frame.Description is { } original
                ? original with { Width = width, Height = height, Sampling = SourceSampling.LinearLightArea } : null,
        };
    }

    private static Vector4 Linear(Vector4 value, PreviewInputSpace space) => new(
        ColorTransfer.Decode(value.X, space), ColorTransfer.Decode(value.Y, space), ColorTransfer.Decode(value.Z, space), value.W);

    private static Box[] BuildBoxes(int sourceLength, int destinationLength)
    {
        var boxes = new Box[destinationLength];
        for (var i = 0; i < boxes.Length; i++)
        {
            // Work in units of 1 / destinationLength of a source pixel.
            var start = (long)i * sourceLength;
            var end = start + sourceLength;
            var first = (int)(start / destinationLength);
            var last = (int)((end - 1) / destinationLength);
            var firstWeight = (Math.Min((long)(first + 1) * destinationLength, end) - start) / (float)destinationLength;
            var lastWeight = (end - (long)last * destinationLength) / (float)destinationLength;
            boxes[i] = new Box(first, last, firstWeight, lastWeight);
        }
        return boxes;
    }

    private readonly record struct Box(int First, int Last, float FirstWeight, float LastWeight);
}

// Owned by one Viewer dispatcher. Each level is sampled directly from the
// developed source. Resampling a cached level would change fractional edges.
internal sealed class ViewerDraftPyramid(long byteLimit = ViewerDraftPyramid.DefaultByteLimit, ViewerCacheBudget? budget = null)
{
    internal const long DefaultByteLimit = 128L * 1024 * 1024;
    private readonly LinkedList<Level> levels = new();
    private readonly ViewerCacheBudget retention = budget ?? ViewerCacheBudget.Shared;
    private WeakReference<HostImageSource.Frame>? source;
    private PreviewInputSpace inputSpace;
    internal long RetainedBytes { get; private set; }

    internal HostImageSource.Frame? Find(HostImageSource.Frame input, int longEdge, PreviewInputSpace space)
    {
        if (!Matches(input, space))
            return null;
        var edge = Math.Min(longEdge, ViewerDraftSource.MaxSide);
        for (var node = levels.First; node is not null; node = node.Next)
        {
            if (node.Value.Edge != edge)
                continue;
            levels.Remove(node);
            levels.AddFirst(node);
            return node.Value.Frame;
        }
        return null;
    }

    internal void Remember(HostImageSource.Frame input, int longEdge, PreviewInputSpace space, HostImageSource.Frame draft)
    {
        if (!Matches(input, space))
        {
            Clear();
            source = new(input);
            inputSpace = space;
        }
        var bytes = draft.FloatBytes.LongLength;
        if (ReferenceEquals(input, draft) || bytes > byteLimit || Find(input, longEdge, space) is not null)
            return;
        while (RetainedBytes + bytes > byteLimit && levels.Last is not null)
            EvictOldest();
        var lease = retention.TryRetain(bytes);
        while (lease is null && levels.Last is not null)
        {
            EvictOldest();
            lease = retention.TryRetain(bytes);
        }
        if (lease is null)
            return;
        levels.AddFirst(new Level(Math.Min(longEdge, ViewerDraftSource.MaxSide), draft, lease));
        RetainedBytes += bytes;
    }

    internal void Clear()
    {
        source = null;
        while (levels.Last is not null)
            EvictOldest();
    }

    private bool Matches(HostImageSource.Frame input, PreviewInputSpace space) =>
        space == inputSpace && source is not null && source.TryGetTarget(out var developed) && ReferenceEquals(input, developed);

    private void EvictOldest()
    {
        var oldest = levels.Last!.Value;
        levels.RemoveLast();
        RetainedBytes -= oldest.Frame.FloatBytes.LongLength;
        oldest.Lease.Dispose();
    }

    private sealed record Level(int Edge, HostImageSource.Frame Frame, IDisposable Lease);
}
