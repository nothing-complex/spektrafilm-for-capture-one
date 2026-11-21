using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed class RendererConnectionInterruptedException(IOException inner)
    : IOException("The connection to the Spektrafilm renderer ended.", inner);

internal sealed class RendererDeadlineException(string message) : TimeoutException(message);
internal sealed class RendererDeviceLostException(string detail)
    : InvalidOperationException("The Spektrafilm renderer lost its Vulkan device. " + detail);

internal readonly record struct OpticalRenderReceipt(int ProcessId, ulong Request, bool SourceUploaded, bool PackedDisplay,
    string? ParameterContract, string? AssetContract, double? GpuWaitMs);

internal sealed class OpticalRenderer(bool export = false, bool nativePreview = false) : IDisposable, IAsyncDisposable
{
    private static readonly ConcurrentDictionary<OpticalRenderer, ResidentFrame> ViewerResidency = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Hello = Encoding.ASCII.GetBytes(FormattableString.Invariant($"HELLO {RendererProtocol.Version}\n"));
    private Process? process;
    private EventWaitHandle? requestCancellation;
    private WeakReference<byte[]>? source;
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private Task shutdown = Task.CompletedTask;
    private bool disposed;
    private bool negotiated;
    private int negotiatedMaxEdge, negotiatedMaxPixels;
    private bool negotiatedPackedDisplay;
    private bool negotiatedCancellation;
    private bool negotiatedProgress;
    private bool negotiatedGpuPlan;
    private IDisposable? gpuAdmission;
    private static readonly TimeSpan CancellationGrace = TimeSpan.FromMilliseconds(600);
    private int sourceWidth, sourceHeight;
    private ulong requestSequence;
    internal Action<RendererStage>? ReportProgress { get; init; }
    internal string? ParameterContract { get; private set; }
    internal string? AssetContract { get; private set; }
    internal OpticalRenderReceipt? LastRender { get; private set; }

    internal Task<byte[]> RenderAsync(int width, int height, byte[] rgba, string[] arguments, CancellationToken stop,
        bool displayPixels = false) =>
        RenderWithPriorityAsync(width, height, rgba, arguments, stop, displayPixels, export ? GpuWorkPriority.Output : GpuWorkPriority.Interactive);

    internal Task<byte[]> WarmPreviewAsync(int width, int height, byte[] rgba, string[] arguments, CancellationToken stop,
        bool displayPixels) =>
        RenderWithPriorityAsync(width, height, rgba, arguments, stop, displayPixels, GpuWorkPriority.Warmup);

    private async Task<byte[]> RenderWithPriorityAsync(int width, int height, byte[] rgba, string[] arguments, CancellationToken stop,
        bool displayPixels, GpuWorkPriority priority)
    {
        ValidateRequest(width, height, rgba, arguments, displayPixels);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        await requests.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await shutdown.WaitAsync(linked.Token).ConfigureAwait(false);
            return await RenderCoreAsync(width, height, rgba, arguments, linked.Token, displayPixels, priority).ConfigureAwait(false);
        }
        finally
        {
            var lease = TakeGpuLease();
            requests.Release();
            // A queued job can check this worker's residency as soon as the
            // lease is released, so publish its idle state first.
            lease?.Dispose();
        }
    }

    private void ValidateRequest(int width, int height, byte[] rgba, string[] arguments, bool displayPixels)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(arguments);
        if (displayPixels && !nativePreview)
            throw new InvalidOperationException("Packed display pixels require the native Viewer renderer.");
        var edge = export || nativePreview ? RenderFrameLimits.MaxHighDepthEdge : 2048;
        var pixels = export || nativePreview ? RenderFrameLimits.MaxHighDepthPixels : 1_048_576;
        if (width < 1 || height < 1 || width > edge || height > edge || (long)width * height > pixels
            || (long)width * height * 16 != rgba.LongLength)
            throw new ArgumentException("The optical source dimensions and float pixel buffer do not match the render limits.");
        var length = 64;
        foreach (var argument in arguments)
        {
            if (string.IsNullOrEmpty(argument))
                throw new ArgumentException("Renderer options must be nonempty ASCII tokens.");
            foreach (var character in argument)
                if (character <= ' ' || character > '~')
                    throw new ArgumentException("Renderer options must be ASCII tokens without whitespace.");
            length = checked(length + argument.Length + 1);
        }
        if (arguments.Length % 2 != 0 || length > 16384)
            throw new ArgumentException("The renderer option list is incomplete or too long.");
    }

    private async Task<byte[]> RenderCoreAsync(int width, int height, byte[] rgba, string[] arguments,
        CancellationToken stop, bool displayPixels, GpuWorkPriority priority)
    {
        stop.ThrowIfCancellationRequested();
        var renderer = GetProcess();
        var processId = renderer.Id;
        var signal = requestCancellation;
        var timeout = export || nativePreview ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(20);
        var started = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(timeout);
        var synchronizedCancellation = false;
        var frameInFlight = false;
        var token = deadline.Token;
        var input = renderer.StandardInput.BaseStream;
        var output = renderer.StandardOutput.BaseStream;
        GpuRenderPlan? admittedPlan = null;
        var newSource = sourceWidth != width || sourceHeight != height
            || source is null || !source.TryGetTarget(out var previous) || !ReferenceEquals(previous, rgba);
        var verb = newSource ? (displayPixels ? "FRAME8" : "FRAME") : (displayPixels ? "REUSE8" : "REUSE");
        try
        {
            if (!negotiated)
                await NegotiateAsync(renderer, token).ConfigureAwait(false);
            if (width > negotiatedMaxEdge || height > negotiatedMaxEdge || (long)width * height > negotiatedMaxPixels)
                throw new InvalidOperationException("This renderer build cannot process the requested dimensions. Use a matching build or choose a smaller output.");
            if (displayPixels && !negotiatedPackedDisplay)
                throw new InvalidOperationException("This renderer build does not support packed Viewer pixels.");
            var options = string.Join(' ', arguments);
            double? gpuWaitMs = null;
            if (negotiatedGpuPlan && await ReadGpuPlanAsync(renderer, width, height, options, token).ConfigureAwait(false) is { } plan)
            {
                if (priority != GpuWorkPriority.Warmup)
                    plan = await ReclaimIdleViewerMemoryAsync(renderer, width, height, options, plan, token).ConfigureAwait(false);
                var queuedAt = Stopwatch.GetTimestamp();
                bool queued;
                try { queued = await ReserveGpuAsync(renderer, plan, priority, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    synchronizedCancellation = true;
                    throw;
                }
                gpuWaitMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
                if (queued && priority != GpuWorkPriority.Warmup)
                {
                    plan = await ReadGpuPlanAsync(renderer, width, height, options, token).ConfigureAwait(false) ?? plan;
                    plan = await ReclaimIdleViewerMemoryAsync(renderer, width, height, options, plan, token).ConfigureAwait(false);
                }
                admittedPlan = plan;
            }
            using var memory = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.WorkerTransport, (long)width * height,
                export ? "Free memory or choose a smaller output." : "Free memory or choose a smaller Viewer resolution.");
            var request = checked(++requestSequence);
            var command = FormattableString.Invariant($"REQUEST {request} {verb} {width} {height}");
            var header = Encoding.ASCII.GetBytes(command + " " + options + "\n");
            if (negotiatedCancellation) signal?.Reset();
            frameInFlight = true;
            await input.WriteAsync(header, token).ConfigureAwait(false);
            if (newSource)
                await input.WriteAsync(rgba, token).ConfigureAwait(false);
            await input.FlushAsync(token).ConfigureAwait(false);
            // Upload owns the main pipe until the entire binary frame has been sent.
            // Only after that point can a canceled request safely keep the worker.
            using var replyDeadline = negotiatedCancellation && signal is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token) : null;
            if (replyDeadline is not null)
                replyDeadline.CancelAfter(TimeSpan.FromTicks(Math.Max(0, (timeout - Stopwatch.GetElapsedTime(started)).Ticks)));
            using var cancel = replyDeadline is null ? default : stop.Register(() =>
            {
                try { signal!.Set(); }
                catch (Exception error) when (error is ObjectDisposedException or IOException or UnauthorizedAccessException)
                { /* The bounded timeout still stops this worker when the signal is unavailable. */ }
                replyDeadline.CancelAfter(CancellationGrace);
            });
            var replyToken = replyDeadline?.Token ?? token;
            var (code, size) = await ReadRenderReplyAsync(output, request, replyToken).ConfigureAwait(false);
            if (code == RendererProtocol.Canceled && size == 0 && negotiatedCancellation && stop.IsCancellationRequested)
            {
                frameInFlight = false;
                source = null;
                synchronizedCancellation = true;
                stop.ThrowIfCancellationRequested();
            }
            if (code is 1 or 2 or RendererProtocol.DeviceLost && size is >= 1 and <= 4096)
            {
                var error = new byte[size];
                await output.ReadExactlyAsync(error, replyToken).ConfigureAwait(false);
                var message = StrictUtf8.GetString(error);
                if (code == 2)
                    throw new InvalidDataException("The optical renderer lost request synchronization: " + message);
                frameInFlight = false;
                source = null;
                if (stop.IsCancellationRequested)
                {
                    synchronizedCancellation = true;
                    stop.ThrowIfCancellationRequested();
                }
                if (code == RendererProtocol.DeviceLost)
                    throw new RendererDeviceLostException(message);
                throw new InvalidOperationException(message);
            }
            if (code != 0 || size != (displayPixels ? rgba.Length / sizeof(float) : rgba.Length))
                throw new InvalidDataException("The optical renderer returned an invalid frame.");

            if (stop.IsCancellationRequested)
            {
                await DiscardReplyAsync(output, size, replyToken).ConfigureAwait(false);
                frameInFlight = false;
                source = null;
                synchronizedCancellation = true;
                stop.ThrowIfCancellationRequested();
            }
            var pixels = GC.AllocateUninitializedArray<byte>(size);
            await output.ReadExactlyAsync(pixels, replyToken).ConfigureAwait(false);
            frameInFlight = false;
            if (stop.IsCancellationRequested)
            {
                source = null;
                synchronizedCancellation = true;
                stop.ThrowIfCancellationRequested();
            }
            if (newSource)
            {
                source = new WeakReference<byte[]>(rgba);
                sourceWidth = width;
                sourceHeight = height;
            }
            LastRender = new OpticalRenderReceipt(processId, request, newSource, displayPixels, ParameterContract, AssetContract, gpuWaitMs);
            if (nativePreview && admittedPlan is { } resident)
            {
                lock (sync)
                    if (ReferenceEquals(process, renderer))
                        ViewerResidency[this] = new(renderer, width, height, options, resident);
            }
            return pixels;
        }
        catch (OperationCanceledException) when (synchronizedCancellation) { throw; }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            await StopAsync(abort: true).ConfigureAwait(false);
            var target = export ? "export" : nativePreview ? "native Viewer preview" : "preview";
            throw new RendererDeadlineException($"Spektrafilm did not finish the {target} in time.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException)
        {
            await StopAsync(abort: true).ConfigureAwait(false);
            // Disposing a worker can close its pipe before Windows completes
            // the pending I/O cancellation.
            stop.ThrowIfCancellationRequested();
            if (error is IOException connection && error is not InvalidDataException)
                throw new RendererConnectionInterruptedException(connection);
            throw;
        }
        finally
        {
            if (frameInFlight)
                await StopAsync(abort: true).ConfigureAwait(false);
        }
    }

    private async Task<GpuRenderPlan?> ReadGpuPlanAsync(Process child, int width, int height, string options, CancellationToken token)
    {
        var request = checked(++requestSequence);
        var command = FormattableString.Invariant($"REQUEST {request} PLAN {width} {height} {options}\n");
        var input = child.StandardInput.BaseStream;
        var output = child.StandardOutput.BaseStream;
        await input.WriteAsync(Encoding.ASCII.GetBytes(command), token).ConfigureAwait(false);
        await input.FlushAsync(token).ConfigureAwait(false);
        var (code, length) = await ReadReplyAsync(output, request, token).ConfigureAwait(false);
        if (code is not (0 or 1) || length is < 1 or > 4096)
            throw new InvalidDataException("The renderer returned an invalid GPU plan.");
        var bytes = new byte[length];
        await output.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (code == 1) throw new InvalidOperationException(StrictUtf8.GetString(bytes));
        using var document = JsonDocument.Parse(bytes);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
            || !value.TryGetProperty("adapter", out var adapter))
            throw new InvalidDataException("The renderer returned an incompatible GPU plan.");
        if (adapter.ValueKind == JsonValueKind.Null) return null;
        if (adapter.ValueKind != JsonValueKind.String || !Guid.TryParseExact(adapter.GetString(), "N", out var id) || id == Guid.Empty
            || !value.TryGetProperty("heap", out var heap) || heap.ValueKind != JsonValueKind.Number || !heap.TryGetInt32(out var index) || index is < 0 or >= 16
            || !value.TryGetProperty("sharedPlanningBytes", out var planning) || planning.ValueKind != JsonValueKind.Number || !planning.TryGetInt64(out var budget) || budget < 0
            || !value.TryGetProperty("estimatedFullFrameBytes", out var estimate) || estimate.ValueKind != JsonValueKind.Number
            || !estimate.TryGetInt64(out var bytesNeeded) || bytesNeeded is <= 0 or > 128L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The renderer returned an invalid GPU adapter or memory estimate.");
        var reclaimable = value.TryGetProperty("reclaimableBytes", out var retained) && retained.ValueKind == JsonValueKind.Number
            && retained.TryGetInt64(out var retainedBytes) && retainedBytes is > 0 and <= 128L * 1024 * 1024 * 1024 ? retainedBytes : 0;
        return new(new(id, index), budget, bytesNeeded, reclaimable);
    }

    private async Task<GpuRenderPlan> ReclaimIdleViewerMemoryAsync(Process child, int width, int height, string options,
        GpuRenderPlan plan, CancellationToken token)
    {
        // Driver headroom alone cannot attribute another worker's residency.
        // Use prior plans to avoid probing any idle pipe when there is room.
        var candidates = ViewerResidency
            .Where(entry => !ReferenceEquals(entry.Key, this) && entry.Value.Plan.Heap == plan.Heap
                && entry.Key.requests.CurrentCount > 0).ToArray();
        if (candidates.Length == 0 || Fits(candidates.Sum(entry => Math.Max(
            entry.Value.Plan.EstimatedBytes, entry.Value.Plan.ReclaimableBytes))))
            return plan;

        var measured = new List<(OpticalRenderer Worker, ResidentFrame Frame, long Bytes)>();
        foreach (var entry in candidates)
        {
            token.ThrowIfCancellationRequested();
            var bytes = await entry.Key.MeasureIdleResidencyAsync(entry.Value).WaitAsync(token).ConfigureAwait(false);
            if (bytes > 0)
                measured.Add((entry.Key, entry.Value, bytes));
        }
        var residentBytes = measured.Sum(entry => entry.Bytes);
        foreach (var entry in measured.OrderByDescending(entry => entry.Bytes))
        {
            if (Fits(residentBytes))
                break;
            token.ThrowIfCancellationRequested();
            if (!await entry.Worker.RetireIdleAsync(entry.Frame).WaitAsync(token).ConfigureAwait(false))
                continue;
            residentBytes -= entry.Bytes;
            StartupHook.Log($"Released an idle Spektrafilm Viewer worker with {entry.Bytes} reclaimable GPU bytes.");
            plan = await ReadGpuPlanAsync(child, width, height, options, token).ConfigureAwait(false) ?? plan;
        }
        return plan;

        bool Fits(long retained) => plan.EstimatedBytes <= plan.PlanningBytes
            && retained <= plan.PlanningBytes - plan.EstimatedBytes;
    }

    private async Task<long> MeasureIdleResidencyAsync(ResidentFrame frame)
    {
        if (!requests.Wait(0))
            return 0;
        try
        {
            if (!StillResident(frame))
                return 0;
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            probe.CancelAfter(TimeSpan.FromSeconds(1));
            try
            {
                var plan = await ReadGpuPlanAsync(frame.Child, frame.Width, frame.Height, frame.Options, probe.Token).ConfigureAwait(false);
                return plan is { } current && current.Heap == frame.Plan.Heap ? current.ReclaimableBytes : 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
                or InvalidOperationException or JsonException)
            {
                // A partial plan reply cannot be left in the pipe for the next frame.
                await StopIdleWorkerAsync(abort: true).ConfigureAwait(false);
                return 0;
            }
        }
        finally { requests.Release(); }
    }

    private async Task<bool> RetireIdleAsync(ResidentFrame frame)
    {
        if (!requests.Wait(0))
            return false;
        try
        {
            if (!StillResident(frame))
                return false;
            return await StopIdleWorkerAsync(abort: false).ConfigureAwait(false);
        }
        finally { requests.Release(); }
    }

    private bool StillResident(ResidentFrame frame) =>
        ViewerResidency.TryGetValue(this, out var current) && ReferenceEquals(current, frame);

    private async Task<bool> StopIdleWorkerAsync(bool abort)
    {
        try
        {
            await StopAsync(abort, keepRenderReceipt: true).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception)
        {
            StartupHook.Log("Could not release an idle Viewer renderer: " + error.GetBaseException().Message);
            return false;
        }
    }

    private sealed record ResidentFrame(Process Child, int Width, int Height, string Options, GpuRenderPlan Plan);

    private async Task<bool> ReserveGpuAsync(Process child, GpuRenderPlan plan, GpuWorkPriority priority, CancellationToken token)
    {
        var reservation = await GpuRenderAdmission.Shared.AcquireAsync(plan, priority, token).ConfigureAwait(false);
        lock (sync)
        {
            if (ReferenceEquals(process, child) && !disposed && !token.IsCancellationRequested)
            {
                gpuAdmission = reservation.Lease;
                return reservation.Queued;
            }
        }
        reservation.Dispose();
        token.ThrowIfCancellationRequested();
        throw new ObjectDisposedException(nameof(OpticalRenderer));
    }

    private IDisposable? TakeGpuLease()
    {
        lock (sync)
        {
            var lease = gpuAdmission;
            gpuAdmission = null;
            return lease;
        }
    }

    private static async Task DiscardReplyAsync(Stream output, int length, CancellationToken stop)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (length > 0)
            {
                var block = Math.Min(length, buffer.Length);
                await output.ReadExactlyAsync(buffer.AsMemory(0, block), stop).ConfigureAwait(false);
                length -= block;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<(int Code, int Size)> ReadRenderReplyAsync(Stream output, ulong request, CancellationToken stop)
    {
        RendererStage previous = 0;
        byte[]? payload = null;
        while (true)
        {
            var reply = await ReadReplyAsync(output, request, stop).ConfigureAwait(false);
            if (reply.Code != RendererProtocol.Progress) return reply;
            if (!negotiatedProgress || reply.Size != 1)
                throw new InvalidDataException("The renderer returned unexpected progress.");
            payload ??= new byte[1];
            await output.ReadExactlyAsync(payload, stop).ConfigureAwait(false);
            var stage = (RendererStage)payload[0];
            if (stage <= previous || stage > RendererStage.Sending)
                throw new InvalidDataException("Renderer progress moved backwards or used an unsupported stage.");
            previous = stage;
            try { ReportProgress?.Invoke(stage); }
            catch (Exception error) { StartupHook.Log("Renderer progress observer: " + error.GetBaseException().Message); }
        }
    }

    private static async Task<(int Code, int Size)> ReadReplyAsync(Stream output, ulong request, CancellationToken token)
    {
        var header = new byte[RendererProtocol.ReplyHeaderBytes];
        await output.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)) != request)
            throw new InvalidDataException("The optical renderer response did not match the active request.");
        return (BinaryPrimitives.ReadInt32LittleEndian(header), BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)));
    }

    private async Task NegotiateAsync(Process child, CancellationToken token)
    {
        await child.StandardInput.BaseStream.WriteAsync(Hello, token).ConfigureAwait(false);
        await child.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
        var header = new byte[8];
        await child.StandardOutput.BaseStream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (status != 0 || length is < 2 or > 16384)
            throw new InvalidDataException("The renderer is incompatible with this panel. Reinstall the complete matching release.");
        var bytes = new byte[length];
        await child.StandardOutput.BaseStream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("protocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var version) || version != RendererProtocol.Version
            || !value.TryGetProperty("requestHeaderBytes", out var headerBytes) || headerBytes.ValueKind != JsonValueKind.Number || !headerBytes.TryGetInt32(out var envelope) || envelope != RendererProtocol.ReplyHeaderBytes
            || !value.TryGetProperty("floatLayout", out var floatLayout) || floatLayout.ValueKind != JsonValueKind.String || floatLayout.GetString() != "rgba32f"
            || !value.TryGetProperty("packedLayout", out var packedLayout) || packedLayout.ValueKind != JsonValueKind.String || packedLayout.GetString() != "bgra8"
            || !value.TryGetProperty("floatChannels", out var channels) || channels.ValueKind != JsonValueKind.Number || !channels.TryGetInt32(out var count) || count != 4
            || !value.TryGetProperty("floatBits", out var bits) || bits.ValueKind != JsonValueKind.Number || !bits.TryGetInt32(out var precision) || precision != 32
            || !value.TryGetProperty("littleEndian", out var byteOrder) || byteOrder.ValueKind != JsonValueKind.True
            || !value.TryGetProperty("maxSide", out var side) || side.ValueKind != JsonValueKind.Number || !side.TryGetInt32(out var maxSide) || maxSide <= 0
            || !value.TryGetProperty("maxPixels", out var pixels) || pixels.ValueKind != JsonValueKind.Number || !pixels.TryGetInt32(out var maxPixels) || maxPixels <= 0
            || !value.TryGetProperty("packedDisplay", out var packed) || packed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !value.TryGetProperty("parameterContract", out var contract) || contract.ValueKind != JsonValueKind.String
            || contract.GetString() is not { Length: 64 } hash || !IsSha256(hash))
            throw new InvalidDataException("The renderer returned incompatible capabilities. Reinstall the complete matching release.");
        ParameterContract = hash;
        AssetContract = ValidateBuildManifest(child, hash);
        if (AssetContract is not null) StartupHook.Log("Verified renderer asset SHA-256: " + AssetContract);
        negotiatedMaxEdge = maxSide;
        negotiatedMaxPixels = maxPixels;
        negotiatedPackedDisplay = packed.ValueKind == JsonValueKind.True;
        negotiatedCancellation = value.TryGetProperty("requestCancellation", out var cancellation)
            && cancellation.ValueKind == JsonValueKind.True && requestCancellation is not null;
        negotiatedProgress = negotiatedCancellation && ReportProgress is not null
            && value.TryGetProperty("frameProgress", out var progress) && progress.ValueKind == JsonValueKind.True;
        negotiatedGpuPlan = value.TryGetProperty("gpuPlanSchema", out var gpuPlan)
            && gpuPlan.ValueKind == JsonValueKind.Number && gpuPlan.TryGetInt32(out var planVersion) && planVersion == 1;
        negotiated = true;
    }

    private static bool IsSha256(string value)
    {
        foreach (var character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    private static string? ValidateBuildManifest(Process child, string contract)
    {
        var root = Directory.GetParent(child.StartInfo.WorkingDirectory)?.FullName;
        if (root is null) return null;
        var path = Path.Combine(root, "build-manifest.json");
        if (!File.Exists(path))
        {
            // Standalone source tests may run without staging a release.
            if (File.Exists(Path.Combine(root, "runtime-manifest.required")))
                throw new InvalidDataException("The packaged Spektrafilm build manifest is missing.");
            return null;
        }
        using var file = File.OpenRead(path);
        if (file.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("The renderer build manifest is invalid.");
        using var manifest = JsonDocument.Parse(file);
        var value = manifest.RootElement;
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("rendererProtocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var version) || version != RendererProtocol.Version
            || !value.TryGetProperty("parameterContract", out var parameter) || parameter.ValueKind != JsonValueKind.String
            || !string.Equals(parameter.GetString(), contract, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The renderer and release manifest do not match. Reinstall the complete release.");
        return RuntimeAssets.Verify(root, value);
    }

    internal async Task<JsonElement?> ReadDiagnosticsAsync(CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        await requests.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is not { HasExited: false } child || !negotiated) return null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var token = deadline.Token;
            try
            {
                var request = checked(++requestSequence);
                var command = FormattableString.Invariant($"REQUEST {request} METRICS\n");
                await child.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes(command), token).ConfigureAwait(false);
                await child.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
                var (code, length) = await ReadReplyAsync(child.StandardOutput.BaseStream, request, token).ConfigureAwait(false);
                if (code != 0 || length is < 2 or > 65536)
                    throw new InvalidDataException("The renderer returned invalid diagnostics.");
                var bytes = new byte[length];
                await child.StandardOutput.BaseStream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("schema", out var schema)
                    || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
                    throw new InvalidDataException("The renderer returned incompatible diagnostics.");
                return document.RootElement.Clone();
            }
            catch
            {
                await StopAsync(abort: true).ConfigureAwait(false);
                throw;
            }
        }
        finally { requests.Release(); }
    }

    private Process GetProcess()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is { HasExited: false })
                return process;
            if (process is { } exited)
            {
                ClearProcess()?.Dispose();
                exited.Dispose();
            }

            var root = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")
                ?? throw new InvalidOperationException("Start Capture One using the Spektrafilm launcher.");
            var exe = Path.Combine(root, "renderer", "SpektraStillRenderer.exe");
            if (!File.Exists(exe))
                throw new IOException("The Spektrafilm Vulkan renderer is missing. See the project README.");
            var start = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            WorkerEnvironment.Sanitize(start);
            // The OFX loader also searches its conventional bundle Resources
            // directory. The still bridge owns the files beside this executable.
            start.Environment["SPEKTRAFILM_RESOURCE_DIR"] = start.WorkingDirectory;
            start.ArgumentList.Add(export ? "--export-stream" : nativePreview ? "--native-preview-stream" : "--stream");
            var cancellation = CreateCancellationEvent(start);
            if (cancellation is not null && ReportProgress is not null) start.Environment["SPEKTRAFILM_REQUEST_PROGRESS"] = "1";
            Process child;
            try { child = Process.Start(start) ?? throw new IOException("Could not start the optical renderer."); }
            catch { cancellation?.Dispose(); throw; }
            requestCancellation = cancellation;
            child.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                    StartupHook.Log("Optical renderer: " + args.Data);
            };
            child.BeginErrorReadLine();
            process = child;
            return child;
        }
    }

    private static EventWaitHandle? CreateCancellationEvent(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var name = @"Local\Spektrafilm.Request." + Guid.NewGuid().ToString("N");
            var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name, out var created);
            if (created)
            {
                start.Environment["SPEKTRAFILM_REQUEST_CANCEL_EVENT"] = name;
                return signal;
            }
            signal.Dispose();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            StartupHook.Log("Cooperative renderer cancellation is unavailable. A canceled render will restart the worker.");
        }
        return null;
    }

    public void Dispose() => _ = ObserveDisposalAsync(DisposeCore());

    public ValueTask DisposeAsync() => new(DisposeCore());

    private Task DisposeCore()
    {
        lock (sync)
        {
            if (!disposed)
            {
                disposed = true;
                lifetime.Cancel();
            }
            return StopAsync(abort: requests.CurrentCount == 0);
        }
    }

    private Task StopAsync(bool abort, bool keepRenderReceipt = false)
    {
        lock (sync)
        {
            if (process is not { } child)
                return shutdown;
            var lease = gpuAdmission;
            gpuAdmission = null;
            var cancellation = ClearProcess(keepRenderReceipt);
            // Process exit is the resource boundary. Killing a worker only
            // requests termination and does not guarantee GPU resources are free.
            shutdown = Task.Run(() =>
            {
                using (cancellation) CloseWorker(child, abort, lease);
            });
            return shutdown;
        }
    }

    private EventWaitHandle? ClearProcess(bool keepRenderReceipt = false)
    {
        ViewerResidency.TryRemove(this, out _);
        var cancellation = requestCancellation;
        requestCancellation = null;
        process = null;
        source = null;
        sourceWidth = sourceHeight = 0;
        negotiated = false;
        negotiatedCancellation = false;
        negotiatedProgress = false;
        negotiatedGpuPlan = false;
        ParameterContract = null;
        AssetContract = null;
        if (!keepRenderReceipt) LastRender = null;
        return cancellation;
    }

    private static async Task ObserveDisposalAsync(Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception error) { StartupHook.Log("Optical renderer shutdown: " + error.GetBaseException().Message); }
    }

    private static void CloseWorker(Process child, bool abort, IDisposable? lease)
    {
        var exited = false;
        try
        {
            if (child.HasExited)
            {
                exited = true;
                return;
            }
            if (!abort)
            {
                try { child.StandardInput.BaseStream.Close(); }
                catch (IOException) { /* The worker can close its pipe on exit. */ }
                exited = child.WaitForExit(300);
                if (exited)
                    return;
            }
            try { if (!child.HasExited) child.Kill(); }
            catch (InvalidOperationException) when (child.HasExited) { }
            exited = child.WaitForExit(5000);
            if (!exited)
                throw new TimeoutException("A Spektrafilm renderer could not exit before the next render.");
        }
        finally
        {
            if (!exited && lease is not null)
                _ = ReleaseAfterExitAsync(child, lease);
            else
            {
                lease?.Dispose();
                child.Dispose();
            }
        }
    }

    private static async Task ReleaseAfterExitAsync(Process child, IDisposable lease)
    {
        using (child)
        {
            try
            {
                await child.WaitForExitAsync().ConfigureAwait(false);
                lease.Dispose();
            }
            catch (Exception error) { StartupHook.Log("Could not confirm renderer exit: " + error.GetBaseException().Message); }
        }
    }
}
