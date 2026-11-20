using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private const int InterruptedStageBytes = 1024 * 1024;

    private static void TestCrashOwnedExportStage(string folder)
    {
        var directory = Directory.CreateDirectory(Path.Combine(folder, "storage-crash-" + Guid.NewGuid().ToString("N"))).FullName;
        var original = Path.Combine(directory, "original.raw");
        var destination = Path.Combine(directory, "existing.tif");
        var unrelated = Path.Combine(directory, ".spektra-unrelated.tmp");
        File.WriteAllText(original, "source");
        File.WriteAllText(destination, "previous");
        File.WriteAllText(unrelated, "unrelated");

        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--hold-guarded-export-stage");
        start.ArgumentList.Add(directory);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the export staging fixture.");
        try
        {
            var ready = Path.Combine(directory, "ready");
            Check(SpinWait.SpinUntil(() => child.HasExited || File.Exists(ready), TimeSpan.FromSeconds(15))
                && !child.HasExited && File.Exists(ready), "a separate process reached the protected export writer");
            var files = Directory.EnumerateFiles(directory, ".spektra-*.tmp").ToArray();
            Check(files.Length == 2 && files.Contains(unrelated)
                && File.ReadAllText(ready) == InterruptedStageBytes.ToString(CultureInfo.InvariantCulture),
                "the child is holding a populated protected output stage");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            Check(child.WaitForExit(10000), "the exact export staging process stopped");
        }
        Check(Directory.EnumerateFiles(directory, ".spektra-*.tmp").SequenceEqual(new[] { unrelated }),
            "process termination removes the owned stage and preserves the unrelated temporary file");
        Check(File.ReadAllText(original) == "source" && File.ReadAllText(destination) == "previous"
            && File.ReadAllText(unrelated) == "unrelated", "process termination preserves the original, previous output and unrelated bytes");
    }

    private static void HoldGuardedExportStage(string directory)
    {
        var approved = OutputPathGuard.Approve(Path.Combine(directory, "existing.tif"), Path.Combine(directory, "original.raw"));
        approved.Write(stream =>
        {
            stream.Write(new byte[InterruptedStageBytes]);
            ((FileStream)stream).Flush(flushToDisk: true);
            var marker = Path.Combine(directory, "ready.pending");
            File.WriteAllText(marker, stream.Length.ToString(CultureInfo.InvariantCulture));
            File.Move(marker, Path.Combine(directory, "ready"));
            Thread.Sleep(TimeSpan.FromSeconds(25));
            throw new TimeoutException("The test did not stop the staging process.");
        }, CancellationToken.None);
    }

    private static void TestExportStorageFailures(string folder)
    {
        var directory = Directory.CreateDirectory(Path.Combine(folder, "storage-failure-" + Guid.NewGuid().ToString("N")));
        var original = Path.Combine(directory.FullName, "original.raw");
        var destination = Path.Combine(directory.FullName, "existing.tif");
        byte[] source = { 1, 7, 3 };
        byte[] previous = { 4, 2, 8 };
        File.WriteAllBytes(original, source);
        File.WriteAllBytes(destination, previous);
        var approved = OutputPathGuard.Approve(destination, original);
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Rgb48, null, new byte[24], 12);

        using var account = WindowsIdentity.GetCurrent();
        var restricted = directory.GetAccessControl(AccessControlSections.Access);
        var denyCreation = new FileSystemAccessRule(account.User!, FileSystemRights.CreateFiles, AccessControlType.Deny);
        restricted.AddAccessRule(denyCreation);
        directory.SetAccessControl(restricted);
        try
        {
            ExpectDenied(() => SpektraExport.SaveApproved(approved, image, CancellationToken.None),
                "a Windows directory ACL denies creation of export staging");
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(denyCreation);
            directory.SetAccessControl(restricted);
        }
        VerifyOutputs();

        File.SetAttributes(destination, FileAttributes.ReadOnly);
        try
        {
            ExpectDenied(() => SpektraExport.SaveApproved(approved, image, CancellationToken.None),
                "Windows refuses the final rename over a read-only destination");
        }
        finally { File.SetAttributes(destination, FileAttributes.Normal); }
        VerifyOutputs();

        var cancellation = new OperationCanceledException("The original accepted job was canceled.");
        string? staging = null;
        try
        {
            PanelFiles.WriteAtomically(destination, stream => stream.WriteByte(6), beforeCommit: () =>
            {
                staging = Directory.EnumerateFiles(directory.FullName, ".spektra-*.tmp").Single();
                File.SetAttributes(staging, FileAttributes.ReadOnly);
                throw cancellation;
            });
            throw new InvalidOperationException("The protected output was unexpectedly committed.");
        }
        catch (OperationCanceledException error)
        {
            Check(ReferenceEquals(error, cancellation) && error.Data["StagingCleanupError"] is string,
                "denied temporary-file removal preserves the primary cancellation and records the cleanup error");
        }
        finally
        {
            if (staging is not null)
            {
                File.SetAttributes(staging, FileAttributes.Normal);
                File.Delete(staging);
            }
        }
        VerifyOutputs();

        void VerifyOutputs()
        {
            Check(File.ReadAllBytes(original).SequenceEqual(source) && File.ReadAllBytes(destination).SequenceEqual(previous),
                "the original and previously exported bytes survive the storage failure");
            Check(!Directory.EnumerateFiles(directory.FullName, ".spektra-*.tmp").Any(),
                "the disposable output folder has no staging residue");
        }

        static void ExpectDenied(Action write, string test)
        {
            try { write(); }
            catch (Exception error) when ((error is UnauthorizedAccessException or IOException) && (error.HResult & 0xffff) == 5)
            {
                Check(true, test);
                return;
            }
            throw new InvalidOperationException(test);
        }
    }
}
