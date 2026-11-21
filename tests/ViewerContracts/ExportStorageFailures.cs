using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static partial class Program
{
    private const int InterruptedStageBytes = 1024 * 1024;
    private const string PublicationNote = "The recipe requested PNG. Saved 16-bit TIFF.";

    private static void TestCrashOwnedExportStage(string folder)
    {
        var directory = Directory.CreateDirectory(Path.Combine(folder, "storage-crash-" + Guid.NewGuid().ToString("N"))).FullName;
        var original = Path.Combine(directory, "original.raw");
        var destination = Path.Combine(directory, "existing.tif");
        var unrelated = Path.Combine(directory, ".spektra-unrelated.tmp");
        File.WriteAllText(original, "source");
        File.WriteAllText(destination, "previous");
        File.WriteAllText(unrelated, "unrelated");

        using var child = StartStorageFixture("--hold-guarded-export-stage", directory);
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

    private static void TestInterruptedPublication(string folder)
    {
        foreach (var point in new[] { "before", "after" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(folder, $"publication-{point}-{Guid.NewGuid():N}")).FullName;
            var original = Path.Combine(directory, "original.raw");
            var destination = Path.Combine(directory, "existing.tif");
            var unrelated = Path.Combine(directory, ".spektra-unrelated.tmp");
            File.WriteAllText(original, "source");
            File.WriteAllText(destination, "previous");
            File.WriteAllText(unrelated, "unrelated");
            var journal = new ExportJobJournal(Path.Combine(directory, "journal"), _ => { });
            using var child = StartStorageFixture("--hold-verified-export", directory, point);
            ExportReceipt? receipt = null;
            try
            {
                var ready = Path.Combine(directory, "verified.ready");
                Check(SpinWait.SpinUntil(() => child.HasExited || File.Exists(ready), TimeSpan.FromSeconds(15))
                    && !child.HasExited && File.Exists(ready), $"a separate process reached the boundary {point} atomic publication");
                receipt = journal.ReadLatest();
                Check(receipt is { Stage: ExportStage.Encoding, Width: 32, Height: 24, Publication.Valid: true }
                    && receipt.Publication.DeliveryNote == PublicationNote, "the encoding receipt already contains the verified bytes and delivery details");
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                Check(child.WaitForExit(10000), "the owned publication fixture stopped");
            }
            Check(Directory.EnumerateFiles(directory, ".spektra-*.tmp").SequenceEqual(new[] { unrelated })
                && File.ReadAllText(original) == "source" && File.ReadAllText(unrelated) == "unrelated",
                "the hard stop leaves the original and unrelated file intact and clears owned staging");
            if (point == "before")
            {
                Check(File.ReadAllText(destination) == "previous" && journal.CompletePublished(receipt!, CancellationToken.None) is null
                    && journal.ReadLatest() == receipt, "a persisted proof alone cannot complete an export before atomic publication");
                continue;
            }

            var published = File.ReadAllBytes(destination);
            Check(published.LongLength == receipt!.Publication!.Length
                && Convert.ToHexString(SHA256.HashData(published)) == receipt.Publication.Sha256,
                "the proof matches an independently read published 16-bit TIFF");
            var changed = (byte[])published.Clone();
            changed[^1] ^= 1;
            File.WriteAllBytes(destination, changed);
            Check(journal.CompletePublished(receipt, CancellationToken.None) is null && journal.ReadLatest() == receipt
                && File.ReadAllBytes(destination).SequenceEqual(changed), "an edited file with the same length is preserved and cannot complete the old job");
            File.WriteAllBytes(destination, published);
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            var canceled = false;
            try { journal.CompletePublished(receipt, stop.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && journal.ReadLatest() == receipt, "canceling verification keeps the Encoding receipt");

            var completed = journal.CompletePublished(receipt, CancellationToken.None);
            Check(completed is { Stage: ExportStage.Completed, Width: 32, Height: 24, Publication: null, Retry: null }
                && completed.JobId == receipt.JobId && completed.DeliveryNote == PublicationNote && completed == journal.ReadLatest()
                && completed.BatchId == receipt.BatchId && completed.BatchId is not null && journal.ReadPendingBatch() is null
                && File.ReadAllBytes(destination).SequenceEqual(published),
                "recovery closes the one-item batch under its original job ID and leaves the TIFF byte-exact");
        }
    }

    private static void HoldVerifiedExport(string directory, string point)
    {
        var destination = Path.Combine(directory, "existing.tif");
        var approved = OutputPathGuard.Approve(destination, Path.Combine(directory, "original.raw"));
        var journal = new ExportJobJournal(Path.Combine(directory, "journal"), _ => { });
        ExportReceipt accepted;
        if (point == "after")
        {
            var entry = new ExportBatchEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Path.Combine(directory, "original.raw"), destination, null);
            var plan = ExportBatchPlan.Create(directory, new[] { entry });
            journal.SaveBatch(plan);
            accepted = journal.BeginBatchItem(plan, entry);
        }
        else
            accepted = journal.Accept(Guid.NewGuid(), destination);
        var receipt = journal.Report(accepted, ExportStage.Encoding, width: 32, height: 24);
        var pixels = Enumerable.Range(0, 32 * 24 * 3).Select(i => (ushort)(i * 61)).ToArray();
        var image = BitmapSource.Create(32, 24, 96, 96, PixelFormats.Rgb48, null, pixels, 32 * 6);
        SpektraExport.SaveApproved(approved, image, CancellationToken.None, verified: proof =>
        {
            journal.RecordPublication(receipt, proof with { DeliveryNote = PublicationNote });
            if (point == "before") SignalWait();
        });
        SignalWait();

        void SignalWait()
        {
            File.WriteAllText(Path.Combine(directory, "verified.ready"), point);
            Thread.Sleep(TimeSpan.FromSeconds(25));
            throw new TimeoutException("The test did not stop the publication process.");
        }
    }

    private static Process StartStorageFixture(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the export storage fixture.");
    }

    private static void TestInterruptedBatchApproval(string folder)
    {
        var directory = Directory.CreateDirectory(Path.Combine(folder, "batch-approval-" + Guid.NewGuid().ToString("N"))).FullName;
        var batchFolder = Path.Combine(directory, "batch");
        var original = Path.Combine(directory, "original.raw");
        File.WriteAllText(original, "original bytes");
        var entries = new[] { "first", "second" }.Select(name =>
            new ExportBatchEntry(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), original,
                Path.Combine(directory, name + ".tif"), new(true, new string('A', 64), new string('B', 64), new string('C', 64)))).ToArray();
        var plan = ExportBatchPlan.Create(directory, entries);
        var journal = new ExportJobJournal(directory, _ => { });
        journal.SaveBatch(plan with { Id = Guid.NewGuid(), Closed = true });
        var pending = Path.Combine(batchFolder, "pending.json");
        var previous = File.ReadAllBytes(pending);
        var unrelated = Path.Combine(batchFolder, ".spektra-unrelated.tmp");
        File.WriteAllText(unrelated, "unrelated bytes");
        File.WriteAllBytes(Path.Combine(directory, "approved-fixture.json"), JsonSerializer.SerializeToUtf8Bytes(plan));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = false;
        try { journal.SaveBatch(plan, cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled && journal.ReadPendingBatch() is null && File.ReadAllBytes(pending).SequenceEqual(previous),
            "canceling batch approval preserves the preceding closed queue exactly");

        foreach (var point in new[] { "before", "after" })
        {
            var ready = Path.Combine(directory, point + ".ready");
            using var child = StartStorageFixture("--hold-batch-approval", directory, point);
            try
            {
                Check(SpinWait.SpinUntil(() => child.HasExited || File.Exists(ready), TimeSpan.FromSeconds(15))
                    && !child.HasExited && File.Exists(ready), $"the batch approval process reached {point} publication");
                if (point == "before")
                    Check(File.ReadAllBytes(pending).SequenceEqual(previous), "a flushed batch stage cannot replace the previous queue early");
            }
            finally
            {
                if (!child.HasExited) child.Kill();
                Check(child.WaitForExit(10000), "the batch approval process stopped");
            }
            var reopened = new ExportJobJournal(directory, _ => { });
            if (point == "before")
                Check(reopened.ReadPendingBatch() is null && File.ReadAllBytes(pending).SequenceEqual(previous),
                    "a stop before batch publication leaves no recoverable uncommitted queue");
            else
                Check(reopened.ReadPendingBatch() is { } saved && saved.Id == plan.Id && saved.Entries.SequenceEqual(entries)
                    && entries.All(entry => reopened.ReadBatchItem(saved, entry) is null),
                    "a stop immediately after approval recovers every exact destination and descriptor before the first job starts");
            Check(Directory.EnumerateFiles(batchFolder, ".spektra-*.tmp").SequenceEqual(new[] { unrelated })
                && File.ReadAllText(unrelated) == "unrelated bytes" && File.ReadAllText(original) == "original bytes"
                && entries.All(entry => !File.Exists(entry.Destination)),
                "batch approval touches neither original nor output and releases its own interrupted stage");
        }
    }

    private static void HoldBatchApproval(string directory, string point)
    {
        var path = Path.Combine(directory, "approved-fixture.json");
        var bytes = File.ReadAllBytes(path);
        if (point == "before")
        {
            // Hold the same crash-owned atomic writer as SaveBatch immediately before publication.
            PanelFiles.WriteAtomically(Path.Combine(directory, "batch", "pending.json"), stream => stream.Write(bytes),
                beforeCommit: SignalWait, discardOnProcessExit: true);
            return;
        }
        var journal = new ExportJobJournal(directory, _ => { });
        journal.SaveBatch(PanelFiles.DecodePreset<ExportBatchPlan>(bytes));
        SignalWait();

        void SignalWait()
        {
            File.WriteAllText(Path.Combine(directory, point + ".ready"), point);
            Thread.Sleep(TimeSpan.FromSeconds(25));
            throw new TimeoutException("The test did not stop batch approval.");
        }
    }

    private static void TestExportStorageFailures(string folder)
    {
        var directory = Directory.CreateDirectory(Path.Combine(folder, "storage-failure-" + Guid.NewGuid().ToString("N")));
        // Changing the destination folder ACL must not update the original's NTFS change time.
        var original = Path.Combine(directory.FullName, "original.raw");
        directory = directory.CreateSubdirectory("delivery");
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

        var diskFull = new IOException("The test volume exhausted its available storage.", unchecked((int)0x80070070));
        try
        {
            approved.Write(stream =>
            {
                stream.Write(new byte[64 * 1024]);
                throw diskFull;
            });
            throw new InvalidOperationException("A truncated protected export was unexpectedly committed.");
        }
        catch (IOException error)
        {
            Check(ReferenceEquals(error, diskFull) && (error.HResult & 0xffff) == 112,
                "a disk-full write failure preserves the Windows error after staging has started");
        }
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
