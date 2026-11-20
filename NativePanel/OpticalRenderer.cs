using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed class OpticalRenderer(bool export = false, bool nativePreview = false) : IDisposable, IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Hello = Encoding.ASCII.GetBytes(FormattableString.Invariant($"HELLO {RendererProtocol.Version}\n"));
    private Process? process;
    private WeakReference<byte[]>? source;
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private Task shutdown = Task.CompletedTask;
    private bool disposed;
    private bool negotiated;
    private int negotiatedMaxEdge, negotiatedMaxPixels;
    private bool negotiatedPackedDisplay;
    private int sourceWidth, sourceHeight;
    private ulong requestSequence;
    internal string? ParameterContract { get; private set; }
    internal string? AssetContract { get; private set; }

    internal async Task<byte[]> RenderAsync(int width, int height, byte[] rgba, string[] arguments, CancellationToken stop,
        bool displayPixels = false)
    {
        ValidateRequest(width, height, rgba, arguments, displayPixels);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, lifetime.Token);
        await requests.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await shutdown.WaitAsync(linked.Token).ConfigureAwait(false);
            using var memory = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.WorkerTransport, (long)width * height,
                export ? "Free memory or choose a smaller output." : "Free memory or choose a smaller Viewer resolution.");
            return await RenderCoreAsync(width, height, rgba, arguments, linked.Token, displayPixels).ConfigureAwait(false);
        }
        finally { requests.Release(); }
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
        CancellationToken stop, bool displayPixels)
    {
        stop.ThrowIfCancellationRequested();
        var renderer = GetProcess();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(export || nativePreview ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(20));
        var token = deadline.Token;
        var input = renderer.StandardInput.BaseStream;
        var output = renderer.StandardOutput.BaseStream;
        var newSource = sourceWidth != width || sourceHeight != height
            || source is null || !source.TryGetTarget(out var previous) || !ReferenceEquals(previous, rgba);
        var verb = newSource ? (displayPixels ? "FRAME8" : "FRAME") : (displayPixels ? "REUSE8" : "REUSE");
        var request = checked(++requestSequence);
        var command = FormattableString.Invariant($"REQUEST {request} {verb} {width} {height}");
        var header = Encoding.ASCII.GetBytes(command + " " + string.Join(' ', arguments) + "\n");
        try
        {
            if (!negotiated)
                await NegotiateAsync(renderer, token).ConfigureAwait(false);
            if (width > negotiatedMaxEdge || height > negotiatedMaxEdge || (long)width * height > negotiatedMaxPixels)
                throw new InvalidOperationException("This renderer build cannot process the requested dimensions. Use a matching build or choose a smaller output.");
            if (displayPixels && !negotiatedPackedDisplay)
                throw new InvalidOperationException("This renderer build does not support packed Viewer pixels.");
            await input.WriteAsync(header, token).ConfigureAwait(false);
            if (newSource)
                await input.WriteAsync(rgba, token).ConfigureAwait(false);
            await input.FlushAsync(token).ConfigureAwait(false);
            var (code, size) = await ReadReplyAsync(output, request, token).ConfigureAwait(false);
            if (code is 1 or 2 && size is >= 1 and <= 4096)
            {
                var error = new byte[size];
                await output.ReadExactlyAsync(error, token).ConfigureAwait(false);
                var message = StrictUtf8.GetString(error);
                if (code == 2)
                    throw new InvalidDataException("The optical renderer lost request synchronization: " + message);
                source = null;
                throw new InvalidOperationException(message);
            }
            if (code != 0 || size != (displayPixels ? rgba.Length / sizeof(float) : rgba.Length))
                throw new InvalidDataException("The optical renderer returned an invalid frame.");

            var pixels = GC.AllocateUninitializedArray<byte>(size);
            await output.ReadExactlyAsync(pixels, token).ConfigureAwait(false);
            if (newSource)
            {
                source = new WeakReference<byte[]>(rgba);
                sourceWidth = width;
                sourceHeight = height;
            }
            return pixels;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            await StopAsync(abort: true).ConfigureAwait(false);
            var target = export ? "export" : nativePreview ? "native Viewer preview" : "preview";
            throw new TimeoutException($"Spektrafilm did not finish the {target} in time.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException
            or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException or DecoderFallbackException)
        {
            await StopAsync(abort: true).ConfigureAwait(false);
            // Disposing a worker can close its pipe before Windows completes
            // the pending I/O cancellation.
            stop.ThrowIfCancellationRequested();
            throw;
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
                ClearProcess();
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
            var child = Process.Start(start) ?? throw new IOException("Could not start the optical renderer.");
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

    private Task StopAsync(bool abort)
    {
        lock (sync)
        {
            if (process is not { } child)
                return shutdown;
            ClearProcess();
            // Process exit is the resource boundary. Killing a worker only
            // requests termination and does not guarantee GPU resources are free.
            shutdown = Task.Run(() => CloseWorker(child, abort));
            return shutdown;
        }
    }

    private void ClearProcess()
    {
        process = null;
        source = null;
        sourceWidth = sourceHeight = 0;
        negotiated = false;
        ParameterContract = null;
        AssetContract = null;
    }

    private static async Task ObserveDisposalAsync(Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception error) { StartupHook.Log("Optical renderer shutdown: " + error.GetBaseException().Message); }
    }

    private static void CloseWorker(Process child, bool abort)
    {
        try
        {
            if (child.HasExited)
                return;
            if (!abort)
            {
                try { child.StandardInput.BaseStream.Close(); }
                catch (IOException) { /* The worker can close its pipe on exit. */ }
                if (child.WaitForExit(300))
                    return;
            }
            try { if (!child.HasExited) child.Kill(); }
            catch (InvalidOperationException) when (child.HasExited) { }
            if (!child.WaitForExit(5000))
                throw new TimeoutException("A Spektrafilm renderer could not exit before the next render.");
        }
        finally { child.Dispose(); }
    }
}
