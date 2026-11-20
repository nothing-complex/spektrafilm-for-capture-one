using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private static void TestFileEncoderCancellation(string folder)
    {
        if (Directory.Exists(folder)) throw new IOException("Choose a new encoder cancellation results directory.");
        Directory.CreateDirectory(folder);
        const int width = 6000, height = 4000;
        byte[] source = { 3, 2, 7 };
        byte[] previous = { 2, 0, 2, 6 };
        var original = Path.Combine(folder, "private-original.raw");
        File.WriteAllBytes(original, source);
        var results = new List<object>();
        foreach (var (extension, format) in new[] { (".tif", PixelFormats.Rgb48), (".png", PixelFormats.Rgba64) })
        {
            var stride = width * (format.BitsPerPixel / 8);
            var pixels = GC.AllocateUninitializedArray<byte>(height * stride);
            new Random(712).NextBytes(pixels);
            var image = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
            image.Freeze();
            var destination = Path.Combine(folder, "guarded" + extension);
            File.WriteAllBytes(destination, previous);
            var approved = OutputPathGuard.Approve(destination, original);
            using var stop = new CancellationTokenSource();
            var started = Stopwatch.GetTimestamp();
            var work = Task.Run(() => SpektraExport.SaveApproved(approved, image, stop.Token));
            long stageLength = 0;
            while (!work.IsCompleted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(15))
            {
                foreach (var path in Directory.EnumerateFiles(folder, ".spektra-*.tmp"))
                {
                    try { stageLength = Math.Max(stageLength, new FileInfo(path).Length); }
                    catch (IOException) { /* The encoder may have removed its temporary file. */ }
                }
                if (stageLength >= 1024 * 1024) break;
                Thread.Sleep(4);
            }
            var wasEncoding = !work.IsCompleted;
            var signaled = Stopwatch.GetTimestamp();
            stop.Cancel();
            Check(stageLength >= 1024 * 1024 && wasEncoding, $"{extension} was writing a real temporary image at cancellation");
            try
            {
                work.WaitAsync(TimeSpan.FromSeconds(35)).GetAwaiter().GetResult();
                throw new InvalidOperationException("A canceled native encoding completed successfully.");
            }
            catch (OperationCanceledException) { assertions++; }
            var cancelMs = Stopwatch.GetElapsedTime(signaled).TotalMilliseconds;
            Check(File.ReadAllBytes(destination).SequenceEqual(previous) && File.ReadAllBytes(original).SequenceEqual(source),
                $"{extension} cancellation preserves the prior destination and original bytes");
            Check(!Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(), $"{extension} cancellation removes the partial staging file");
            results.Add(new { extension, width, height, stageLength, signalMs = Stopwatch.GetElapsedTime(started, signaled).TotalMilliseconds, cancelMs });
            Console.WriteLine($"{extension} canceled during encoding at {stageLength} bytes in {cancelMs:F2} ms after signal.");
        }
        File.WriteAllText(Path.Combine(folder, "encoder-cancellation.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
}
