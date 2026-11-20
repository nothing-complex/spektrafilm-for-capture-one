using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
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
