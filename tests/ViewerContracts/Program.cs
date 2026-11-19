using System;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class HostImageSource
{
    internal sealed record Frame(int Width, int Height, byte[] Codes, byte[] FloatBytes, int BitDepth);
}

internal static class StartupHook
{
    internal static void Log(string message) => throw new InvalidOperationException(message);
}

internal static class Program
{
    private static int assertions;
    private static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new InvalidOperationException(message);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--large-encoding", var results])
        {
            Directory.CreateDirectory(results);
            TestLargeEncoding(results);
            Console.WriteLine($"PASS: {assertions} large 16-bit encoder checks.");
            return;
        }
        var folder = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "Spektrafilm-viewer-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        TestGeometry();
        TestDpiAndCropGeometry();
        TestViewportQuality();
        TestViewerHistogram();
        TestDraft();
        TestExport(folder);
        TestExportMetadata(folder);
        TestExportResolution();
        TestColorSampling();
        TestFractionalColorSampling();
        TestColorPrecision();
        TestLookStrengthDelivery();
        TestOutputProtection(folder);
        TestSectionPreferences(folder);
        TestScheduling().GetAwaiter().GetResult();
        TestNativeCallbackOwnership().GetAwaiter().GetResult();
        TestMemoryAdmission();
        TestExportFrameLimits();
        Console.WriteLine($"PASS: {assertions} assertions. Pure current-source probes only; no Capture One process used.");
    }

    private static void TestViewerHistogram()
    {
        var pixels = new byte[]
        {
            0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 99, 70, 99, 0,
            0, 0, 0, 255, 255, 255, 255, 255, 177, 169, 156, 133, 255, 255, 255, 0,
        };
        var image = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        image.Freeze();
        var bins = ViewerHistogram.Read(image, CancellationToken.None);
        Check(bins.PixelCount == 6 && bins.HighlightCount == 4, "histogram counts visible pixels with a channel at the display maximum");
        Check(bins.Red[255] == 2 && bins.Green[255] == 2 && bins.Blue[255] == 2, "red, green and blue histograms follow the display code channel order");
        Check(bins.Red[156] == 1 && bins.Green[169] == 1 && bins.Blue[177] == 1 && bins.Green[70] == 0,
            "partially transparent colour is counted and fully transparent colour is excluded");
        Check(bins.Red.Sum() == 6 && bins.Green.Sum() == 6 && bins.Blue.Sum() == 6, "histogram totals match the exact visible sample count");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            ViewerHistogram.Read(image, stop.Token);
            throw new InvalidOperationException("A stale histogram ignored cancellation.");
        }
        catch (OperationCanceledException) { Check(true, "stale histogram can be cancelled before scanning the shared display frame"); }
    }

    private static void TestViewportQuality()
    {
        var quality = new ViewportQuality();
        Check(quality.LongEdge == 1600 && quality.SizeId == "1600", "automatic quality starts at a bounded initial size");
        Check(!quality.Update(null, 1, null, 0), "missing geometry holds the initial size");
        Check(!quality.Update(3000, 1, null, 100) && !quality.Update(3000, 1, null, 349), "an upgrade requires sustained demand");
        Check(quality.Update(3000, 1, null, 350) && quality.LongEdge == 4096, "stable zoom raises the cap without native-size rendering");
        for (var i = 0; i < 100; i++)
            Check(!quality.Update(2870 + i % 2 * 20, 1, null, 500 + i * 10), "zoom jitter near the threshold keeps the current cap");
        Check(!quality.Update(2000, 1, null, 2000) && !quality.Update(2000, 1, null, 2799), "downgrade waits for the viewport to settle");
        Check(quality.Update(2000, 1, null, 2800) && quality.LongEdge == 2560, "a stable smaller viewport reduces source work");
        Check(quality.Update(4000, 1, 320 * 1024L * 1024, 2900) && quality.LongEdge == 1200, "low system memory immediately reduces automatic demand");
        Check(!quality.Update(4000, 1, 330 * 1024L * 1024, 3200) && !quality.Update(4000, 1, 330 * 1024L * 1024, 4000),
            "a small budget rebound does not trigger a new large allocation");
        Check(!quality.Update(4000, 1, 3 * 1024L * 1024 * 1024, 4300)
            && quality.Update(4000, 1, 3 * 1024L * 1024 * 1024, 4600) && quality.LongEdge == 4096,
            "sustained demand recovers after memory pressure clears");
        Check(!quality.Update(double.NaN, .75, null, 8000) && quality.LongEdge == 4096, "temporarily hidden or invalid geometry does not drop a usable frame");
        quality.Reset();
        Check(quality.LongEdge == 1600 && !quality.Update(3200, .75, null, 10000), "navigation resets demand without changing the preference");

        var points = new[] { new PointF(5, 9), new PointF(1005, 9), new PointF(1005, 759), new PointF(5, 759) };
        var panned = points.Select(point => new PointF(point.X - 400, point.Y + 20)).ToArray();
        var clip = new RectangleF(0, 0, 1500, 900);
        Check(ViewerGeometry.TryCreate(clip, points, 200, 150, out var centered)
            && ViewerGeometry.TryCreate(clip, panned, 200, 150, out var translated)
            && centered.ProjectedLongEdge == 1000 && centered.ProjectedLongEdge == translated.ProjectedLongEdge,
            "panning and clip translation alone preserve the estimated full image resolution");
        var zoomed = points.Select(point => new PointF(point.X * 1.5f, point.Y * 1.5f)).ToArray();
        Check(ViewerGeometry.TryCreate(clip, zoomed, 200, 150, out var enlarged) && enlarged.ProjectedLongEdge == 1500,
            "quality uses the native physical geometry after a zoom or display scaling change");
        Console.WriteLine("Viewer Auto quality: bounded size, physical geometry, hysteresis and memory pressure passed.");
    }

    private static void TestLookStrengthDelivery()
    {
        const double tolerance = 0.000003;
        var neutral = new[] { .25f, .25f, .25f };
        var bright = new[] { 1.5f, 1.5f, 1.5f };
        var mid = new SrgbDelivery(PreviewInputSpace.LinearRec2020, "linear_rec709", .5).GetSrgb(neutral, bright, 0);
        Check(Math.Abs(mid.Red - Encode(.625)) < tolerance && Math.Abs(mid.Green - Encode(.625)) < tolerance
            && Math.Abs(mid.Blue - Encode(.625)) < tolerance, "the film's linear 1.5 channel clips before mixing with linear .25");

        var chart = new[] { 1.25f, .65f, -.35f };
        foreach (var encoding in new[] { "srgb", "linear_rec709", "rec709_gamma24" })
        {
            var output = new[] { -.2f, .5f, 1.5f };
            var atHalf = new SrgbDelivery(PreviewInputSpace.Srgb, encoding, .5).GetSrgb(chart, output, 0);
            var midFilm = encoding switch { "linear_rec709" => .5, "rec709_gamma24" => Math.Pow(.5, 2.4), _ => Decode(.5) };
            Check(Math.Abs(atHalf.Red - Encode(.5)) < tolerance && Math.Abs(atHalf.Blue - Encode(.5)) < tolerance
                && Math.Abs(atHalf.Green - Encode((Decode(chart[1]) + midFilm) / 2)) < tolerance,
                "negative and saturated channels follow the same clipping policy in " + encoding);
            foreach (var space in Enum.GetValues<PreviewInputSpace>())
            {
                var atZero = new SrgbDelivery(space, encoding, 0).GetSrgb(chart, Array.Empty<float>(), 0);
                var barelyOn = new SrgbDelivery(space, encoding, 0.0000001).GetSrgb(chart, output, 0);
                var atOne = new SrgbDelivery(space, encoding, 1).GetSrgb(Array.Empty<float>(), output, 0);
                var almostFull = new SrgbDelivery(space, encoding, 0.9999999).GetSrgb(chart, output, 0);
                Check(Close(atZero, barelyOn) && Close(atOne, almostFull), "continuous strength endpoints for " + space + " and " + encoding);
                Check(atOne.Red == 0 && Math.Abs(atOne.Green - Encode(midFilm)) < tolerance && Math.Abs(atOne.Blue - 1) < tolerance,
                    "the full processed frame has the correct transfer for " + encoding);
            }
        }

        var invalid = new SrgbDelivery(PreviewInputSpace.Srgb, "srgb", .5);
        foreach (var nonfinite in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var damaged = new[] { .5f, nonfinite, .5f };
            Check(Rejects(() => invalid.GetSrgb(damaged, bright, 0)), "a non-finite source component is rejected");
            Check(Rejects(() => invalid.GetSrgb(neutral, damaged, 0)), "a non-finite processed component is rejected");
        }
        Console.WriteLine("Look strength: clip-before-blend policy, all source/output spaces, HDR highlights and endpoint continuity passed.");

        static double Encode(double linear) => linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;
        static double Decode(double encoded) => encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
        static bool Close(SrgbDelivery.LinearBgr a, SrgbDelivery.LinearBgr b) => Math.Abs(a.Red - b.Red) < tolerance
            && Math.Abs(a.Green - b.Green) < tolerance && Math.Abs(a.Blue - b.Blue) < tolerance;
        static bool Rejects(Action action)
        {
            try { action(); return false; }
            catch (InvalidDataException) { return true; }
        }
    }

    private static void TestSectionPreferences(string folder)
    {
        var path = Path.Combine(folder, "panel-preferences.json");
        PanelFiles.WritePreset(path, new { Version = 1, ViewerSize = "1600", DragSize = 720, DraftSynthesis = true });
        var messages = new List<string>();
        var store = new PanelPreferenceStore(path, messages.Add);
        var panel = new System.Windows.Controls.StackPanel();
        var parent = store.CreateSection("parent", "Parent", panel, 5);
        var child = store.CreateSection("child", "Child", new System.Windows.Controls.StackPanel(), 5);
        panel.Children.Add(child);
        Check(!parent.IsExpanded && !child.IsExpanded, "Legacy preferences changed default layout");
        parent.IsExpanded = true;
        child.IsExpanded = true;
        parent.IsExpanded = false;
        Check(store.Current.Sections?["parent"] == false && store.Current.Sections?["child"] == true,
            "Routed expansion/collapse changed a different section's saved state");
        store.Update(preferences => preferences with { FloatingScrollOffset = 314.25 });
        var reopened = new PanelPreferenceStore(path, messages.Add);
        Check(reopened.CreateSection("parent", "Localized parent", new System.Windows.Controls.StackPanel(), 5).IsExpanded == false,
            "Parent section did not restore independently of its title");
        Check(reopened.CreateSection("child", "Localized child", new System.Windows.Controls.StackPanel(), 5).IsExpanded,
            "Child section did not restore independently of its title");
        Check(reopened.Current.FloatingScrollOffset == 314.25 && reopened.Current.ViewerSize == "1600"
            && reopened.Current.DragSize == 720 && reopened.Current.DraftSynthesis, "Layout persistence changed quality preferences");
        Check(messages.Count == 0, "Reading a compatible preference file reported an error");
        const string future = "{\"Version\":99,\"Unknown\":true}";
        File.WriteAllText(path, future);
        var fallback = new PanelPreferenceStore(path, messages.Add);
        fallback.CreateSection("child", "Child", new System.Windows.Controls.StackPanel(), 5).IsExpanded = true;
        Check(File.ReadAllText(path) == future && messages.Count == 1, "Future preferences were overwritten or unreported");
    }

    private static void TestColorSampling()
    {
        var values = new float[] {0,0,0,1, 1,1,1,1};
        var bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var frame = new HostImageSource.Frame(2,1,Array.Empty<byte>(),bytes,16);
        foreach (var space in Enum.GetValues<PreviewInputSpace>())
        {
            var result = ViewerDraftSource.Downsample(frame,1,CancellationToken.None,space);
            var output = MemoryMarshal.Cast<byte,float>(result.FloatBytes);
            Check(Math.Abs(ColorTransfer.Decode(output[0],space)-0.5) < 2e-6, $"{space} average is not linear light");
            Check(output[3] == 1, "Resampling changed alpha");
            for(var i=-25;i<=200;i++)
            {
                var code=i/100f;
                Check(Math.Abs(ColorTransfer.Encode(ColorTransfer.Decode(code,space),space)-code)<1e-4, $"Transfer roundtrip {space} {code}");
            }
        }
        Check(Math.Abs(ColorTransfer.Encode(0.5f,PreviewInputSpace.Srgb)-0.73535698f)<2e-6,"sRGB light average reference");
        Console.WriteLine("Colour: all input encodings average in linear light and preserve extended-range roundtrips.");
    }

    private static void TestOutputProtection(string folder)
    {
        var source=Path.Combine(folder,"source.tif");
        File.WriteAllText(source,"ORIGINAL");
        var refused=false;
        try { OutputPathGuard.Validate(source.ToUpperInvariant(),source); } catch(IOException) { refused=true; }
        Check(refused,"Original path was accepted");
        Check(OutputPathGuard.Validate(Path.Combine(folder,"different.tif"),source).EndsWith("different.tif"),"Distinct output rejected");
        ExpectRefused(() => OutputPathGuard.Validate(Path.Combine(folder, ".", "source.tif"), source), "Source path with dot segment accepted");
        var link=Path.Combine(folder,"linked-source.tif");
        try
        {
            File.CreateSymbolicLink(link,source);
            refused=false;
            try { OutputPathGuard.Validate(link,source); } catch(IOException) { refused=true; }
            Check(refused,"Original symlink was accepted");
        }
        catch(UnauthorizedAccessException) { Console.WriteLine("SKIP: symbolic-link creation unavailable for alias test."); }
        catch(IOException error) when ((error.HResult & 0xffff) == 1314)
        { Console.WriteLine("SKIP: symbolic-link privilege unavailable for alias test."); }

        var hardlink = Path.Combine(folder, "hardlink-source.tif");
        if (!CreateHardLink(hardlink, source, IntPtr.Zero))
            throw new IOException($"Hardlink fixture failed: {Marshal.GetLastWin32Error()}.");
        ExpectRefused(() => OutputPathGuard.Validate(hardlink, source), "Original file identity through a hardlink was accepted");

        var junction = Path.Combine(folder, "junction");
        var junctionTarget = Path.Combine(folder, "junction-target");
        Directory.CreateDirectory(junctionTarget);
        var junctionSource = Path.Combine(junctionTarget, "source.tif");
        File.WriteAllText(junctionSource, "JUNCTION ORIGINAL");
        var mklinkInfo = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"%SPEKTRA_TEST_JUNCTION%\" \"%SPEKTRA_TEST_TARGET%\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        mklinkInfo.Environment["SPEKTRA_TEST_JUNCTION"] = junction;
        mklinkInfo.Environment["SPEKTRA_TEST_TARGET"] = junctionTarget;
        using (var mklink = Process.Start(mklinkInfo)!)
        {
            if (!mklink.WaitForExit(10000))
                throw new TimeoutException("Disposable junction creation timed out.");
            var response = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
            if (mklink.ExitCode == 0)
            {
                ExpectRefused(() => OutputPathGuard.Validate(Path.Combine(junction, "source.tif"), junctionSource), "Output junction alias accepted");
                ExpectRefused(() => OutputPathGuard.Validate(junctionSource, Path.Combine(junction, "source.tif")), "Source junction alias accepted");
                OutputPathGuard.Approve(Path.Combine(junction, "separate.tif"), junctionSource).Write(stream => stream.WriteByte(17));
                Check(File.ReadAllBytes(Path.Combine(junctionTarget, "separate.tif")).SequenceEqual(new byte[] { 17 }), "Separate junction output was not committed");
                Check(File.ReadAllText(junctionSource) == "JUNCTION ORIGINAL", "Junction test altered the original");
            }
            else if (response.Contains(new Win32Exception(5).Message, StringComparison.CurrentCultureIgnoreCase)
                || response.Contains(new Win32Exception(1314).Message, StringComparison.CurrentCultureIgnoreCase))
                Console.WriteLine("SKIP: Windows denied creation of the disposable junction fixture.");
            else
                throw new IOException("Could not create the disposable junction fixture: " + response.Trim());
        }

        var late = Path.Combine(folder, "late.tif");
        var approved = OutputPathGuard.Approve(late, source);
        ExpectRefused(() => approved.Write(stream =>
        {
            stream.WriteByte(20);
            File.WriteAllText(late, "CONCURRENT");
        }), "Concurrent creation was silently replaced");
        Check(File.ReadAllText(late) == "CONCURRENT", "Concurrent destination bytes changed");

        var replacement = OutputPathGuard.Approve(late, source);
        ExpectRefused(() => replacement.Write(stream =>
        {
            stream.WriteByte(21);
            File.Move(late, Path.Combine(folder, "previous.tif"));
            File.WriteAllText(late, "DIFFERENT");
        }), "Replacement with a different identity was silently overwritten");
        Check(File.ReadAllText(late) == "DIFFERENT", "Replacement bytes changed");
        OutputPathGuard.Approve(late, source).Write(stream => stream.WriteByte(22));
        Check(File.ReadAllBytes(late).SequenceEqual(new byte[] { 22 }), "Unchanged approved destination could not be replaced");
        using var stop = new CancellationTokenSource();
        try
        {
            OutputPathGuard.Approve(late, source).Write(stream =>
            {
                stream.WriteByte(99);
                stop.Cancel();
            }, stop.Token);
            throw new InvalidOperationException("Canceled output committed after encoding.");
        }
        catch (OperationCanceledException) { assertions++; }
        Check(File.ReadAllBytes(late).SequenceEqual(new byte[] { 22 }), "Cancellation after encoding changed an existing destination");
        try
        {
            OutputPathGuard.Approve(late, source).Write(stream =>
            {
                stream.WriteByte(90);
                throw new InsufficientMemoryException("Simulated allocation failure during encoding.");
            });
            throw new InvalidOperationException("Allocation failure published an incomplete file.");
        }
        catch (InsufficientMemoryException) { assertions++; }
        Check(File.ReadAllBytes(late).SequenceEqual(new byte[] { 22 }), "Allocation failure changed an existing output");
        Check(!Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(), "Output failures left partial files behind");

        var lateAlias = Path.Combine(folder, "late-alias.tif");
        var aliasApproval = OutputPathGuard.Approve(lateAlias, source);
        ExpectRefused(() => aliasApproval.Write(stream =>
        {
            stream.WriteByte(23);
            if (!CreateHardLink(lateAlias, source, IntPtr.Zero))
                throw new InvalidOperationException("Could not create concurrent hardlink fixture.");
        }), "Concurrent original alias was accepted");
        Check(File.ReadAllText(lateAlias) == "ORIGINAL", "Concurrent original alias was altered");
        Check(File.ReadAllText(source)=="ORIGINAL","Path test altered original");

        void ExpectRefused(Action action, string failure)
        {
            try { action(); }
            catch (IOException) { assertions++; return; }
            throw new InvalidOperationException(failure);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existingPath, IntPtr reserved);

    private static void TestFractionalColorSampling()
    {
        const int width = 13, height = 7;
        var samples = new float[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            samples[index] = (x - 2) / 9f;
            samples[index + 1] = ((x + y) % 3) / 2f;
            samples[index + 2] = (x == 12 ? 1.4f : 0.003f) + y * 0.008f;
            samples[index + 3] = ((x + y * 2) % 7) / 6f;
        }
        var frame = new HostImageSource.Frame(width, height, Array.Empty<byte>(),
            MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), 16);

        foreach (var space in Enum.GetValues<PreviewInputSpace>())
        foreach (var (edge, columns, rows) in new[] { (12, 12, 6), (8, 8, 4), (5, 5, 2), (2, 2, 1), (1, 1, 1) })
        {
            var draft = ViewerDraftSource.Downsample(frame, edge, CancellationToken.None, space);
            Check(draft.Width == columns && draft.Height == rows && draft.BitDepth == 16, "Fractional draft dimensions or precision changed");
            var output = MemoryMarshal.Cast<byte, float>(draft.FloatBytes);
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            for (var channel = 0; channel < 4; channel++)
            {
                double weighted = 0;
                for (var sy = 0; sy < height; sy++)
                for (var sx = 0; sx < width; sx++)
                {
                    // Independent overlap in destination coordinates. Every footprint has area one.
                    double weight = Overlap(x, sx, width, columns) * Overlap(y, sy, height, rows);
                    double sample = samples[(sy * width + sx) * 4 + channel];
                    weighted += weight * (channel == 3 ? sample : DecodeReference(sample, space));
                }
                var actual = output[(y * columns + x) * 4 + channel];
                var linear = channel == 3 ? actual : DecodeReference(actual, space);
                Check(Math.Abs(linear - weighted) < 4e-6, $"{space} fractional pixel ({x}, {y}, {channel}) at {edge} disagrees with independent overlap integral");
            }
        }
        Console.WriteLine("Fractional area filtering agrees with an independent nonconstant colour and alpha integral.");

        static double Overlap(int dest, int src, int sourceSize, int destSize) =>
            Math.Max(0, Math.Min(dest + 1, (src + 1) * (double)destSize / sourceSize)
                - Math.Max(dest, src * (double)destSize / sourceSize));

        static double DecodeReference(double encoded, PreviewInputSpace space)
        {
            var value = Math.Abs(encoded);
            var linear = space switch
            {
                PreviewInputSpace.Srgb => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4),
                PreviewInputSpace.AdobeRgb => Math.Pow(value, 563.0 / 256),
                PreviewInputSpace.ProPhotoRgb => value <= 16.0 / 512 ? value / 16 : Math.Pow(value, 1.8),
                PreviewInputSpace.LinearRec2020 => value,
                _ => throw new ArgumentOutOfRangeException(nameof(space)),
            };
            return Math.CopySign(linear, encoded);
        }
    }

    private static void TestColorPrecision()
    {
        foreach (var space in Enum.GetValues<PreviewInputSpace>())
        {
            foreach (var value in new[] {float.NaN,float.PositiveInfinity,float.NegativeInfinity})
            {
                var refused=false;
                try {ColorTransfer.Decode(value,space);}catch(ArgumentOutOfRangeException){refused=true;}
                Check(refused,"Decode accepted nonfinite input");
                refused=false;
                try {ColorTransfer.Encode(value,space);}catch(ArgumentOutOfRangeException){refused=true;}
                Check(refused,"Encode accepted nonfinite input");
            }
        }
        foreach(var space in new[]{(PreviewInputSpace)(-1),(PreviewInputSpace)4,(PreviewInputSpace)int.MaxValue})
        {
            var refused=false;
            try {ColorTransfer.Encode(.5f,space);}catch(ArgumentOutOfRangeException){refused=true;}
            Check(refused,"Invalid input space accepted");
        }
        for(var i=1;i<=500;i++)
        {
            var encoded=i/100000f;
            var linear=(float)Math.Pow(encoded,563.0/256);
            var actual=ColorTransfer.Encode(linear,PreviewInputSpace.AdobeRgb);
            Check(Math.Abs(actual-encoded)<2e-8,"Adobe near-black encode precision lost");
            Check(Math.Abs(ColorTransfer.Encode(-linear,PreviewInputSpace.AdobeRgb)+encoded)<2e-8,"Signed Adobe near-black precision lost");
        }
        foreach(var linear in new[]{1f/256-1e-7f,1f/256,1f/256+1e-7f,.25f,.8f,2f})
            Check(Math.Abs(ColorTransfer.Encode(linear,PreviewInputSpace.AdobeRgb)-Math.Pow(linear,256.0/563))<2e-7,"Adobe encode reference/threshold discontinuity");
        Console.WriteLine("Colour precision: Adobe near-black reference, transfer boundary, signed range, finite inputs and enum validation passed.");
    }

    private static void TestExportFrameLimits()
    {
        foreach (var (width, height) in new[] { (6000, 5000), (9000, 5000), (10000, 6100), (10000, 10000), (16384, 8), (8, 16384) })
            Check(RenderFrameLimits.ValidateExportCanvas(width, height, 1) == (width, height), "allowed export was silently resized");
        Check(RenderFrameLimits.ValidateExportCanvas(10000, 6100, 0.5) == (5000, 3050), "recipe scale changed");
        Check(RenderFrameLimits.ValidateExportCanvas(3999.5, 2000.1, 1) == (4000, 2001), "fractional canvas must reserve its final pixel");
        foreach (var (width, height, scale) in new[] { (10001d, 10000d, 1d), (16385d, 1d, 1d), (10d, 10d, 0d), (double.NaN, 1d, 1d) })
        {
            try
            {
                RenderFrameLimits.ValidateExportCanvas(width, height, scale);
                throw new Exception("Invalid requested canvas was admitted");
            }
            catch (InvalidOperationException) { assertions++; }
        }
        Console.WriteLine("Export preflight: exact 30/45/61/100 MP, long thin edges, recipe scaling and oversized refusal passed.");
    }

    private static void TestMemoryAdmission()
    {
        const long gib = 1024L * 1024L * 1024L;
        const string alternatives = "Choose a smaller Viewer resolution.";
        var available = 4 * gib;
        var budget = new RenderMemoryBudget(() => available);
        var stage = RenderAllocation.WorkerTransport;
        bool Refused(long pixels)
        {
            try
            {
                using var lease = budget.Reserve(stage, pixels, alternatives);
                return false;
            }
            catch (InsufficientMemoryException error) { return error.Message.Contains(alternatives, StringComparison.Ordinal); }
        }

        Check(RenderMemoryBudget.EstimateBytes(stage, 24_000_000)
            > RenderMemoryBudget.EstimateBytes(RenderAllocation.NativeDevelopment, 24_000_000), "Worker reserve must include extra transport");
        var first = budget.Reserve(stage, 24_000_000, alternatives);
        Check(Refused(24_000_000), "4 GiB fresh system headroom admitted competing high-cost requests");
        available = 6 * gib;
        using (budget.Reserve(stage, 24_000_000, alternatives))
            Check(Refused(24_000_000), "6 GiB fresh headroom admitted a third expensive reserve");
        first.Dispose();
        first.Dispose();
        available = 8 * gib;
        Check(Refused(100_000_000), "unsupported memory demand must return an explicit resolution choice");
        using var request = budget.Reserve(stage, 24_000_000, alternatives);
        using var codec = budget.Reserve(RenderAllocation.ImageEncoding, 24_000_000, alternatives);
        available = 512 * 1024L * 1024L;
        Check(Refused(960_000), "headroom drop during active work did not prevent further admission");

        available = 3 * gib;
        var competing = 0;
        Parallel.For(0, 16, _ =>
        {
            try
            {
                using var lease = budget.Reserve(stage, 24_000_000, alternatives);
                Interlocked.Increment(ref competing);
            }
            catch (InsufficientMemoryException) { }
        });
        Check(competing == 0, "cross-context admission bypassed the outstanding leases");
        Console.WriteLine("System memory reserve: 4/6/8 GiB synthetic headroom, overlap, release and pressure behavior passed.");
    }

    private static async Task TestNativeCallbackOwnership()
    {
        using var slot = new SemaphoreSlim(0, 1);
        var invoked = 0;
        var queued = new QueuedRenderRequest<int, int>(value => { invoked++; return value; }, () => slot.Release());
        queued.Abandon();
        queued.Abandon();
        queued.Run(42);
        Check(invoked == 0 && queued.Result.IsCanceled && slot.CurrentCount == 1,
            "an abandoned queued callback gives its slot back once and cannot run");

        await slot.WaitAsync();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var running = new QueuedRenderRequest<int, int>(value =>
        {
            entered.Set();
            if (!finish.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Native callback fixture did not release its gate.");
            return value;
        }, () => slot.Release());
        var thread = Task.Run(() => running.Run(88));
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "native callback obtained ownership of the slot");
        try
        {
            running.Abandon();
            running.Abandon();
            Check(running.Result.IsCanceled && slot.CurrentCount == 0,
                "caller cancellation cannot release an executing native callback's slot");
            Check(!await slot.WaitAsync(TimeSpan.FromMilliseconds(20)), "a second development could not enter during native drain");
        }
        finally { finish.Set(); }
        await thread.WaitAsync(TimeSpan.FromSeconds(10));
        Check(slot.CurrentCount == 1 && running.Result.IsCanceled,
            "native completion releases its slot without publishing a canceled frame");

        await slot.WaitAsync();
        var failure = new QueuedRenderRequest<int, int>(_ => throw new IOException("native failure"), () => slot.Release());
        failure.Run(0);
        try { await failure.Result; throw new InvalidOperationException("Native failure was swallowed."); }
        catch (IOException) { Check(slot.CurrentCount == 1, "native fault releases its slot and surfaces the real error"); }

        for (var attempt = 0; attempt < 200; attempt++)
        {
            await slot.WaitAsync();
            var raced = new QueuedRenderRequest<int, int>(value => value, () => slot.Release());
            await Task.WhenAll(Task.Run(() => raced.Run(5)), Task.Run(raced.Abandon));
            Check(slot.CurrentCount == 1 && (raced.Result.IsCanceled || raced.Result is { IsCompletedSuccessfully: true, Result: 5 }),
                "competing native delivery/cancel leaves one slot and a terminal result");
        }
        Console.WriteLine("Capture One scheduler contracts: pending cancellation, running drain, native faults and 200 delivery races passed.");
    }

    private static async Task TestScheduling()
    {
        var previewEnd=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken previewStop=default;
        var final=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var render=RenderCoordination.PreferFinalAsync(final.Task, token => {previewStop=token;return previewEnd.Task;},CancellationToken.None);
        final.SetResult(42);
        var result=await render.WaitAsync(TimeSpan.FromSeconds(2));
        Check(result==42,"Ready final source was blocked by draft");
        Check(previewStop.IsCancellationRequested,"Obsolete initial draft not cancelled");
        previewEnd.SetResult();
        var invoked=false;
        Check(await RenderCoordination.PreferFinalAsync(Task.FromResult(43),_=>{invoked=true;return Task.CompletedTask;},CancellationToken.None)==43&&!invoked,"Ready final started obsolete draft");
        var afterPreviewFailure=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered=RenderCoordination.PreferFinalAsync(afterPreviewFailure.Task,_=>throw new IOException("preview failed synchronously"),CancellationToken.None);
        afterPreviewFailure.SetResult(44);
        Check(await recovered==44,"Synchronous preview failure discarded final");
        using var export=new ExportOperation(Guid.NewGuid());
        Check(!export.Token.IsCancellationRequested,"New export already cancelled");
        var identity=export.VariantId;
        Check(identity!=Guid.Empty,"Accepted export lost target identity");
        export.Cancel();
        Check(export.Token.IsCancellationRequested,"Explicit cancellation ignored");
        var failed=Task.FromException<int>(new IOException("source failed"));
        var caught=false;
        try { await RenderCoordination.PreferFinalAsync(failed,_=>Task.CompletedTask,CancellationToken.None); } catch(IOException) {caught=true;}
        Check(caught,"Final source error was hidden");
        Console.WriteLine("Scheduling: final-ready priority, draft cancellation, export identity and explicit cancel passed.");
    }

    private static void BenchmarkDurableWrites(string folder)
    {
        var path=Path.Combine(folder,"synthetic-look.json");
        var numbers=Enumerable.Range(0,110).ToDictionary(i=>"optical-parameter-"+i,i=>(double)i/100);
        var settings=new {Look=new {Film="kodak_portra_400",Paper="kodak_portra_endura",Exposure=0.5,PrintExposure=0.0,Strength=1.0,Mode="print",SpectralMethod="hanatos",ScanInvert=false,Development=new Dictionary<string,double>{{"film_gamma",1.0},{"film_push_pull",0.0}},FilmPushPullMode="standard"},Optics=new {Numbers=numbers,Selections=new Dictionary<string,string>{{"input-color","linear_rec2020"},{"output-color","srgb"},{"preview-size","native"},{"drag-preview-size","1200"}},Switches=new Dictionary<string,bool>{{"show-source",false}}},Enabled=true,Version=1,Grain=new {Type=0,Impact=0.0,Granularity=0.0}};
        for(int i=0;i<5;i++) PanelFiles.WritePreset(path,settings);
        var ms=new double[200];
        for(int i=0;i<ms.Length;i++)
        {
            numbers["optical-parameter-0"]=i/100.0;
            var start=Stopwatch.GetTimestamp();PanelFiles.WritePreset(path,settings);ms[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(ms);
        Console.WriteLine(FormattableString.Invariant($"Durable-write microbenchmark: {ms.Length} warmed atomic JSON writes, payload {new FileInfo(path).Length} bytes, median {ms[ms.Length/2]:F3} ms, p95 {ms[(int)(ms.Length*.95)-1]:F3} ms, max {ms[^1]:F3} ms. This local storage probe is not a Capture One UI latency measurement."));
    }

    private static void TestGeometry()
    {
        PointF[][] cases = {
            new[] {new PointF(10,20),new PointF(210,20),new PointF(210,120),new PointF(10,120)},
            new[] {new PointF(210,20),new PointF(10,20),new PointF(10,120),new PointF(210,120)},
            new[] {new PointF(110,20),new PointF(110,220),new PointF(10,220),new PointF(10,20)},
            new[] {new PointF(20,20),new PointF(220,40),new PointF(250,140),new PointF(50,120)},
        };
        foreach(var corners in cases)
        {
            Check(ViewerGeometry.TryCreate(new RectangleF(0,0,500,500),corners,2000,1000,out var geometry),"Valid affine geometry rejected");
            var imageCorners = new[] {new System.Windows.Point(0,0),new System.Windows.Point(2000,0),new System.Windows.Point(2000,1000),new System.Windows.Point(0,1000)};
            for(int i=0;i<4;i++)
            {
                var p=geometry.Transform.Transform(imageCorners[i]);
                Check(Math.Abs(p.X-corners[i].X)<1e-9 && Math.Abs(p.Y-corners[i].Y)<1e-9,"Corner correspondence wrong");
            }
        }
        var invalid=(PointF[])cases[0].Clone();invalid[2].X+=5;
        Check(!ViewerGeometry.TryCreate(new RectangleF(0,0,500,500),invalid,2000,1000,out _),"Non-affine geometry accepted");
        Check(!ViewerGeometry.TryCreate(new RectangleF(300,300,500,500),cases[0],2000,1000,out _),"Offscreen geometry accepted");
        invalid[0].X=float.NaN;
        Check(!ViewerGeometry.TryCreate(new RectangleF(0,0,500,500),invalid,2000,1000,out _),"Nonfinite geometry accepted");
        Console.WriteLine("Geometry: affine, mirror, rotation, shear, offscreen and invalid-corner probes passed.");
    }

    private static void TestDpiAndCropGeometry()
    {
        var rectangle = new RectangleF(52, 33, 100, 61);
        var corners = new[] { new PointF(32, 24), new PointF(192, 24), new PointF(192, 104), new PointF(32, 104) };
        var color = new byte[] { 29, 68, 115, 255 };
        var bitmap = BitmapSource.Create(4, 2, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat(color, 8).SelectMany(pixel => pixel).ToArray(), 16);
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        {
            Check(ViewerGeometry.TryCreate(rectangle, corners, bitmap.PixelWidth, bitmap.PixelHeight, out var geometry),
                "DPI fixture geometry rejected");
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                // Capture One's painter supplies this physical-pixel-to-DIP transform.
                dc.PushTransform(new ScaleTransform(1 / scale, 1 / scale));
                dc.PushClip(new RectangleGeometry(geometry.Clip));
                dc.PushTransform(new MatrixTransform(geometry.Transform));
                dc.DrawImage(bitmap, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                dc.Pop();
                dc.Pop();
                dc.Pop();
            }
            var frame = new RenderTargetBitmap(220, 140, scale * 96, scale * 96, PixelFormats.Pbgra32);
            frame.Render(drawing);
            byte[] Pixel(int x, int y)
            {
                var rgba = new byte[4];
                frame.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), rgba, 4, 0);
                return rgba;
            }
            Check(Pixel(110, 65).SequenceEqual(color), "frame center drifted at " + scale + "x DPI");
            Check(Pixel(46, 65)[3] == 0 && Pixel(156, 65)[3] == 0, "clip moved at " + scale + "x DPI");
            Check(Pixel(110, 28)[3] == 0 && Pixel(110, 101)[3] == 0, "vertical clip moved at " + scale + "x DPI");
        }

        var approximate = (PointF[])corners.Clone();
        approximate[2].X += .49f;
        Check(ViewerGeometry.TryCreate(rectangle, approximate, 4, 2, out _), "subpixel native corner rounding was rejected");
        approximate[2].X += .03f;
        Check(!ViewerGeometry.TryCreate(rectangle, approximate, 4, 2, out _), "nonclosing corner passed the rounding threshold");
        var flat = new[] { new PointF(32, 50), new PointF(192, 50), new PointF(192, 50.5f), new PointF(32, 50.5f) };
        Check(!ViewerGeometry.TryCreate(rectangle, flat, 4, 2, out _), "degenerate half-pixel rows should not paint");

        var canvas = new SizeF(6000, 4000);
        Check(ViewerGeometry.ScaleCrop(new RectangleF(444.2f, 100.4f, 3000, 2000), canvas, 3000, 2000)
            == new Rectangle(222, 50, 1500, 1000), "crop pixel rounding drifted from native canvas");
        Check(ViewerGeometry.ScaleCrop(new RectangleF(5999.8f, 3999.8f, .2f, .2f), canvas, 3000, 2000)
            == new Rectangle(2999, 1999, 1, 1), "rounded crop at canvas edges lost the final pixel");
        Check(ViewerGeometry.ScaleCrop(new RectangleF(5998, 3998, 10, 10), canvas, 3000, 2000)
            == new Rectangle(2999, 1999, 1, 1), "crop extending outside frame exceeded its delivered canvas");
        Console.WriteLine("Geometry: WPF physical coordinates at 100/150/200% DPI, closure limits and crop rounding passed.");
    }

    private static void TestDraft()
    {
        foreach(var shape in new[] {(7,5,3),(29,13,7),(1,19,3),(17,1,4),(257,263,73),(3001,1,2800)})
        {
            var (width,height,edge)=shape;
            var bytes=new byte[width*height*16];var floats=MemoryMarshal.Cast<byte,float>(bytes);
            for(int i=0;i<width*height;i++) { floats[4*i]=.25f;floats[4*i+1]=-.5f;floats[4*i+2]=1.5f;floats[4*i+3]=.75f; }
            var input=new HostImageSource.Frame(width,height,Array.Empty<byte>(),bytes,16);
            var draft=ViewerDraftSource.Downsample(input,edge,CancellationToken.None);
            var result=MemoryMarshal.Cast<byte,float>(draft.FloatBytes);
            Check(Math.Max(draft.Width,draft.Height)<=Math.Min(edge,2560),"Draft cap exceeded");
            for(int i=0;i<result.Length;i+=4) Check(Math.Abs(result[i]-.25f)<3e-5&&Math.Abs(result[i+1]+.5f)<3e-5&&Math.Abs(result[i+2]-1.5f)<3e-5&&Math.Abs(result[i+3]-.75f)<3e-5,"Area filter changed constant or range/alpha");
            Check(ReferenceEquals(ViewerDraftSource.Downsample(draft,2560,CancellationToken.None),draft),"Same-size source copied");
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try {ViewerDraftSource.Downsample(input,edge,cancel.Token);throw new Exception("Expected cancellation");}catch(OperationCanceledException){assertions++;}
        }
        Console.WriteLine("Draft: noninteger/extreme aspect ratios, constant energy, out-of-range channels, alpha, size cap, identity and cancellation passed.");
    }

    private static void TestExport(string folder)
    {
        foreach(var suffix in new[] {".tif",".png"})
        {
            var path=Path.Combine(folder,"probe"+suffix);var format=SpektraExport.PixelFormatFor(path);
            ushort[] values=format==PixelFormats.Rgba64
                ? new ushort[]{1,257,32769,65535,32770,65534,2001,14001,257,258,259,65535,62321,21234,54321,65535}
                : new ushort[]{1,257,32769,32770,65534,2001,257,258,259,62321,21234,54321};
            var expected=new byte[values.Length*2];Buffer.BlockCopy(values,0,expected,0,expected.Length);
            var stride=expected.Length/2;var image=BitmapSource.Create(2,2,150,200,format,null,values,stride);
            SpektraExport.Save(path,image,CancellationToken.None);
            using var file=File.OpenRead(path);
            var decoded=BitmapDecoder.Create(file,BitmapCreateOptions.PreservePixelFormat|BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad).Frames[0];
            Check(decoded.Format==format,"Wrong decoded pixel format");
            var actual=new byte[expected.Length];decoded.CopyPixels(actual,stride,0);
            Check(actual.SequenceEqual(expected),"16-bit samples changed");
            Check(decoded.ColorContexts?.Count>0,"ICC not embedded");
            var existing=File.ReadAllBytes(path);
            using var cancel=new CancellationTokenSource();cancel.Cancel();
            try {SpektraExport.Save(path,image,cancel.Token);throw new Exception("Expected canceled export");}catch(OperationCanceledException){assertions++;}
            Check(existing.SequenceEqual(File.ReadAllBytes(path)),"Canceled write changed destination");
            var eight=BitmapSource.Create(2,2,96,96,PixelFormats.Bgr24,null,new byte[12],6);
            try {SpektraExport.Save(path,eight,CancellationToken.None);throw new Exception("Expected high-depth validation");}catch(InvalidDataException){assertions++;}
            Check(existing.SequenceEqual(File.ReadAllBytes(path)),"Rejected write changed destination");
            Check(Math.Abs(decoded.DpiX - 150) < 0.03 && Math.Abs(decoded.DpiY - 200) < 0.03, "Encoder changed supplied non-square resolution");
            Console.WriteLine($"Export {suffix}: {decoded.Format}, exact samples, ICC, resolution and canceled/rejected writes passed.");
        }
    }

    private static void TestLargeEncoding(string folder)
    {
        var original = Path.Combine(folder, "fixture-original.bin");
        File.WriteAllBytes(original, new byte[] { 9, 42, 101 });
        var metadata = new ExportMetadata("Fixture copyright", null, "Large high-depth synthetic image", null, null, Array.Empty<string>());
        foreach (var (width, height, suffix) in new[] { (10000, 6100, ".tif"), (9000, 5000, ".png") })
        {
            var destination = Path.Combine(folder, $"synthetic-{width}x{height}{suffix}");
            var format = SpektraExport.PixelFormatFor(destination);
            var channels = format.BitsPerPixel / 16;
            var stride = width * channels * sizeof(ushort);
            using var lease = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.ImageEncoding, (long)width * height, "");
            var row = new ushort[width * channels];
            for (var x = 0; x < width; x++)
            {
                row[x * channels] = (ushort)(1024 + x);
                row[x * channels + 1] = (ushort)(31001 + x);
                row[x * channels + 2] = (ushort)(60001 - x);
                if (channels == 4) row[x * channels + 3] = ushort.MaxValue;
            }
            var samples = new byte[checked(height * stride)];
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(row, 0, samples, y * stride, stride);
            var image = BitmapSource.Create(width, height, 300, 150, format, null, samples, stride);
            var timer = Stopwatch.StartNew();
            SpektraExport.SaveApproved(OutputPathGuard.Approve(destination, original), image, CancellationToken.None, metadata);
            using var input = File.OpenRead(destination);
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnDemand).Frames[0];
            Check(frame.Format == format && frame.PixelWidth == width && frame.PixelHeight == height,
                "Large output was resized or lost 16-bit precision");
            Check(frame.ColorContexts?.Count > 0 && Math.Abs(frame.DpiX - 300) < 0.03 && Math.Abs(frame.DpiY - 150) < 0.03,
                "Large output lost its ICC or resolution");
            foreach (var (x, y) in new[] { (0, 0), (width / 2, height / 2), (width - 1, height - 1) })
            {
                var read = new ushort[channels];
                frame.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), read, channels * sizeof(ushort), 0);
                Check(read.AsSpan().SequenceEqual(row.AsSpan(x * channels, channels)), "Large output changed a 16-bit sample");
            }
            Console.WriteLine($"Large {suffix} {width}x{height}, {format}, {input.Length} bytes in {timer.ElapsedMilliseconds} ms.");
        }
        Check(File.ReadAllBytes(original).AsSpan().SequenceEqual(new byte[] { 9, 42, 101 }),
            "Large encoders touched their source fixture");
    }

    private static void TestExportMetadata(string folder)
    {
        var original = Path.Combine(folder, "metadata-input.raw");
        File.WriteAllBytes(original, new byte[] { 1, 2, 3 });
        var metadata = new ExportMetadata("Rights \u00A9 2026", "Luka \u017D", "K\u00F8benhavn \u65E5 & portrait",
            new DateTime(2025, 3, 14, 13, 27, 6), 4, new[] { "street", "L\u00FCbecker", "street" });
        foreach (var extension in new[] { ".tif", ".png" })
        {
            var path = Path.Combine(folder, "metadata-output" + extension);
            var format = SpektraExport.PixelFormatFor(path);
            const int width = 3, height = 2;
            var stride = width * format.BitsPerPixel / 8;
            var pixels = Enumerable.Range(0, height * stride).Select(index => (byte)index).ToArray();
            var image = BitmapSource.Create(width, height, 300, 150, format, null, pixels, stride);
            SpektraExport.SaveApproved(OutputPathGuard.Approve(path, original), image, CancellationToken.None, metadata);
            using var file = File.OpenRead(path);
            var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];
            var data = (BitmapMetadata)frame.Metadata;
            Check(frame.Format == format && frame.PixelWidth == width && frame.PixelHeight == height,
                "Metadata changed pixel format or dimensions");
            var decodedPixels = new byte[pixels.Length];
            frame.CopyPixels(decodedPixels, stride, 0);
            Check(decodedPixels.SequenceEqual(pixels) && frame.ColorContexts?.Count > 0, "Metadata changed 16-bit samples or pixel ICC");
            Check(Math.Abs(frame.DpiX - 300) < 0.03 && Math.Abs(frame.DpiY - 150) < 0.03, "Metadata changed resolution");

            if (extension == ".tif")
                Check(data.GetQuery("/ifd/{ushort=274}") is (ushort)1, "TIFF orientation is not normalized");
            var bytes = File.ReadAllBytes(path);
            var start = bytes.AsSpan().IndexOf("<?xpacket begin="u8);
            Check(start >= 0, "XMP packet missing");
            var endMessage = "<?xpacket end=\"w\"?>"u8;
            var end = bytes.AsSpan(start).IndexOf(endMessage);
            Check(end > 0, "XMP packet was truncated");
            var packet = System.Text.Encoding.UTF8.GetString(bytes, start, end + endMessage.Length);
            var xml = System.Xml.Linq.XDocument.Parse(packet);
            var fields = xml.Descendants().Single(element => element.Name.LocalName == "Description");
            Check(fields.Elements().Single(element => element.Name.LocalName == "title").Value == metadata.Title,
                "Unicode title or XML special character changed");
            Check(fields.Elements().Single(element => element.Name.LocalName == "rights").Value == metadata.Copyright,
                "Unicode rights changed");
            Check(fields.Elements().Single(element => element.Name.LocalName == "creator").Value == metadata.Creator,
                "Creator changed");
            Check(fields.Elements().Single(element => element.Name.LocalName == "subject").Descendants()
                .Where(element => element.Name.LocalName == "li").Select(element => element.Value)
                .SequenceEqual(new[] { "street", "L\u00FCbecker" }), "Keyword list changed");
            var properties = fields.Attributes().ToDictionary(attribute => (attribute.Name.NamespaceName, attribute.Name.LocalName),
                attribute => attribute.Value);
            const string tiff = "http://ns.adobe.com/tiff/1.0/", exif = "http://ns.adobe.com/exif/1.0/";
            Check(properties[(tiff, "Orientation")] == "1" && properties[(tiff, "ImageWidth")] == "3"
                && properties[(tiff, "ImageLength")] == "2" && properties[(exif, "PixelXDimension")] == "3"
                && properties[(exif, "PixelYDimension")] == "2", "Normalized orientation or dimensions missing");
            Check(properties[(exif, "DateTimeOriginal")] == "2025-03-14T13:27:06"
                && properties[("http://ns.adobe.com/xap/1.0/", "Rating")] == "4"
                && !properties.Keys.Any(key => key.LocalName.StartsWith("GPS", StringComparison.Ordinal)), "Capture date, rating or GPS policy changed");
            using var verification = File.OpenRead(path);
            var packetBytes = System.Text.Encoding.UTF8.GetBytes(packet);
            bool Verify() => extension == ".png" ? PngXmp.Contains(verification, packetBytes) : EncodedXmp.TiffContains(verification, packetBytes);
            Check(Verify(), "Container metadata validation rejected a correct packet");
            packetBytes[packetBytes.Length / 2] ^= 1;
            Check(!Verify(), "Container metadata validation accepted changed packet bytes");
        }
        Check(File.ReadAllBytes(original).SequenceEqual(new byte[] { 1, 2, 3 }), "Metadata output changed source bytes");
        Console.WriteLine("Export metadata: Unicode XMP, normalized dimensions and orientation, ICC, DPI and exact 16-bit samples passed.");
    }

    private static void TestExportResolution()
    {
        foreach (var (amount, unit) in new[] { (300d, "Inch"), (300 / 2.54, "Centimeter"), (300 / 25.4, "Millimeter") })
            Check(Math.Abs(ExportResolution.DotsPerInch(amount, unit) - 300) < 1e-9, $"Incorrect DPI conversion from {unit}");
        foreach (var amount in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.MaxValue })
        {
            try
            {
                ExportResolution.DotsPerInch(amount, "Millimeter");
                throw new InvalidOperationException("Invalid recipe resolution was accepted.");
            }
            catch (ArgumentOutOfRangeException) { assertions++; }
        }
        try
        {
            ExportResolution.DotsPerInch(300, "Unknown");
            throw new InvalidOperationException("Unknown units were accepted.");
        }
        catch (ArgumentException) { assertions++; }
    }
}
