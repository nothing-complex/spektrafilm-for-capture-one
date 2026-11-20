using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

internal static class HighDepthSource
{
    private static readonly float[] NormalizedCode = BuildNormalizedCodes();

    // The caller owns the immutable Capture One bitmap until this synchronous conversion completes.
    internal static unsafe byte[] Convert16(int width, int height, int stride, nint data, bool hasAlpha, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const int outputChannels = 4;
        var inputChannels = hasAlpha ? 4 : 3;
        var inputRowLength = checked(width * inputChannels);
        var outputRowLength = checked(width * outputChannels);
        var result = GC.AllocateUninitializedArray<byte>(checked(outputRowLength * height * sizeof(float)));
        PreviewRows.Process(width, height, token, (first, last) =>
        {
            var floats = MemoryMarshal.Cast<byte, float>(result.AsSpan());
            for (var y = first; y < last; y++)
            {
                token.ThrowIfCancellationRequested();
                var codes = new ReadOnlySpan<ushort>((void*)(data + y * stride), inputRowLength);
                var output = floats.Slice(y * outputRowLength, outputRowLength);
                if (hasAlpha)
                {
                    for (var c = 0; c < output.Length; c++)
                        output[c] = NormalizedCode[codes[c]];
                }
                else
                {
                    for (int read = 0, write = 0; read < inputRowLength; read += 3, write += outputChannels)
                    {
                        output[write] = NormalizedCode[codes[read]];
                        output[write + 1] = NormalizedCode[codes[read + 1]];
                        output[write + 2] = NormalizedCode[codes[read + 2]];
                        output[write + 3] = 1;
                    }
                }
            }
        }, parallelPixelThreshold: 500_000, maxWorkers: 8);
        return result;
    }

    internal static unsafe byte[] ConvertFloat(int width, int height, int stride, nint data, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var inputRowLength = checked(width * 3);
        var outputRowLength = checked(width * 4);
        var result = GC.AllocateUninitializedArray<byte>(checked(outputRowLength * height * sizeof(float)));
        var invalid = 0;
        PreviewRows.Process(width, height, token, (first, last) =>
        {
            var floats = MemoryMarshal.Cast<byte, float>(result.AsSpan());
            for (var y = first; y < last; y++)
            {
                token.ThrowIfCancellationRequested();
                var input = new ReadOnlySpan<float>((void*)(data + y * stride), inputRowLength);
                var output = floats.Slice(y * outputRowLength, outputRowLength);
                for (int read = 0, write = 0; read < inputRowLength; read += 3, write += 4)
                {
                    var red = input[read];
                    var green = input[read + 1];
                    var blue = input[read + 2];
                    if (!float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue))
                    {
                        Interlocked.Exchange(ref invalid, 1);
                        return;
                    }
                    output[write] = red;
                    output[write + 1] = green;
                    output[write + 2] = blue;
                    output[write + 3] = 1;
                }
            }
        });
        if (invalid != 0) throw new InvalidDataException("Capture One returned a non-finite preview pixel.");
        return result;
    }

    private static float[] BuildNormalizedCodes()
    {
        var codes = new float[ushort.MaxValue + 1];
        for (var i = 0; i < codes.Length; i++)
            codes[i] = i / (float)ushort.MaxValue;
        return codes;
    }
}
