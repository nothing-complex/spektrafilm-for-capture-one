using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

internal sealed class PanelLog
{
    internal const int Capacity = 256;
    internal const int MessageLimit = 4096;
    internal const long FileLimit = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly string path;
    private readonly Channel<Entry> entries;
    private readonly Task pump;
    private int dropped;

    internal PanelLog(string path)
    {
        this.path = Path.GetFullPath(path);
        entries = Channel.CreateBounded<Entry>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        }, _ => Interlocked.Increment(ref dropped));
        pump = Task.Run(DrainAsync);
    }

    internal int Dropped => Volatile.Read(ref dropped);

    internal void Write(string message)
    {
        if (message.Length > MessageLimit)
            message = message[..MessageLimit] + "…";
        entries.Writer.TryWrite(new(DateTime.UtcNow, message));
    }

    internal Task Complete()
    {
        entries.Writer.TryComplete();
        return pump;
    }

    private async Task DrainAsync()
    {
        await foreach (var entry in entries.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var line = JsonSerializer.Serialize(entry) + "\n";
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length + Utf8.GetByteCount(line) > FileLimit)
                    File.Move(path, path + ".previous", overwrite: true);
                await File.AppendAllTextAsync(path, line, Utf8).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Diagnostic storage must not hold up an edit or an export.
                Interlocked.Increment(ref dropped);
            }
        }
    }

    private sealed record Entry(DateTime Utc, string Message);
}
