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
        var folder = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "Spektrafilm-viewer-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        TestGeometry();
        TestDraft();
        TestExport(folder);
        TestExportResolution();
        TestColorSampling();
        TestFractionalColorSampling();
        TestColorPrecision();
        TestOutputProtection(folder);
        TestScheduling().GetAwaiter().GetResult();
        Console.WriteLine($"PASS: {assertions} assertions. Pure current-source probes only; no Capture One process used.");
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
