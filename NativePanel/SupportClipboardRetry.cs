using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal enum SupportCopyResult { Copied, Busy, Changed }

internal static class SupportClipboardRetry
{
    private const int Attempts = 3;

    internal static async Task<SupportCopyResult> CopyAsync(Action write, Func<uint> sequence, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var originalSequence = sequence();
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt != 0)
            {
                await Task.Delay(80 * attempt, stop);
                stop.ThrowIfCancellationRequested();
                if (sequence() != originalSequence)
                    return SupportCopyResult.Changed;
            }
            try
            {
                write();
                return SupportCopyResult.Copied;
            }
            catch (ExternalException)
            {
                // Release the host UI thread while the clipboard owner finishes.
            }
        }
        return SupportCopyResult.Busy;
    }
}
