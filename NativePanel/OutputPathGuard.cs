using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal static class OutputPathGuard
{
    private const int FileIdInfo = 18;
    private const uint OpenExisting = 3;
    private const uint ShareReadWriteDelete = 7;
    private const uint BackupSemantics = 0x02000000;
    private const string OriginalMessage = "Choose a different filename. Spektrafilm cannot replace the original image.";

    internal static string Validate(string destination, string original) => Approve(destination, original).Path;

    internal static ApprovedDestination Approve(string destination, string original) => new(destination, original);

    internal sealed class ApprovedDestination
    {
        private readonly string sourcePath;
        private readonly string selectedDirectory;
        private readonly string resolvedDestination;
        private readonly PathInfo selectedSource;
        private readonly PathInfo directory;
        private readonly PathInfo? existing;

        internal ApprovedDestination(string destination, string original)
        {
            Path = System.IO.Path.GetFullPath(destination);
            sourcePath = System.IO.Path.GetFullPath(original);
            if (SamePath(Path, sourcePath)) throw new IOException(OriginalMessage);

            selectedDirectory = System.IO.Path.GetDirectoryName(Path) ?? throw new IOException("The output folder is unavailable.");
            selectedSource = Describe(sourcePath)!;
            directory = Describe(selectedDirectory)!;
            if (selectedSource.IsDirectory || !directory.IsDirectory)
                throw new IOException("Choose a file in an existing output folder.");
            resolvedDestination = System.IO.Path.Combine(directory.FinalName, System.IO.Path.GetFileName(Path));
            existing = Describe(Path, allowMissing: true);
            CheckOriginal(selectedSource, existing);
        }

        internal string Path { get; }

        internal void Write(Action<Stream> write, CancellationToken stop = default)
        {
            stop.ThrowIfCancellationRequested();
            Verify();
            // Use the resolved folder for the sibling temporary file. For a new name,
            // the final rename itself refuses an intervening destination creation.
            PanelFiles.WriteAtomically(resolvedDestination, write, beforeCommit: () =>
            {
                stop.ThrowIfCancellationRequested();
                Verify();
            }, overwrite: existing is not null);
        }

        private void Verify()
        {
            var source = Describe(sourcePath)!;
            var folder = Describe(selectedDirectory)!;
            var destination = Describe(Path, allowMissing: true);
            CheckOriginal(source, destination);
            if (!SameObject(source, selectedSource) || !SameObject(folder, directory)
                || !SamePath(folder.FinalName, directory.FinalName) || !SameDestination(destination, existing))
                throw new IOException("The original image, output folder or destination changed. Choose the output again.");
        }

        private void CheckOriginal(PathInfo source, PathInfo? destination)
        {
            if (SamePath(resolvedDestination, source.FinalName)
                || destination is not null && (SamePath(destination.FinalName, source.FinalName) || SameObject(destination, source)))
                throw new IOException(OriginalMessage);
            if (destination?.IsDirectory == true)
                throw new IOException("An output file cannot replace a folder.");
        }
    }

    private static bool SameDestination(PathInfo? first, PathInfo? second) => first is null || second is null
        ? first is null && second is null
        : SameObject(first, second) && SamePath(first.FinalName, second.FinalName)
            && first.Length == second.Length && first.LastWrite == second.LastWrite;

    private static bool SameObject(PathInfo first, PathInfo second) => first.Identity is { } a && second.Identity is { } b
        ? a.Equals(b)
        : SamePath(first.FinalName, second.FinalName);

    private static bool SamePath(string first, string second) => StringComparer.OrdinalIgnoreCase.Equals(first, second);

    private static PathInfo? Describe(string path, bool allowMissing = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (Directory.Exists(path)) return new(System.IO.Path.GetFullPath(path), null, true, 0, 0);
            var file = new FileInfo(path);
            if (file.Exists) return new(file.FullName, null, false, (ulong)file.Length, (ulong)file.LastWriteTimeUtc.Ticks);
            if (allowMissing) return null;
            throw new IOException("Could not verify the original and output paths.");
        }

        using var handle = CreateFile(path, 0, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (allowMissing && (error is 2 or 3)) return null;
            throw new IOException("Could not verify the original and output paths.", new Win32Exception(error));
        }
        var name = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 0);
        if (length >= name.Capacity)
        {
            name.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 0);
        }
        if (length == 0 || length >= name.Capacity)
            throw new IOException("Could not resolve the original and output paths.", new Win32Exception(Marshal.GetLastWin32Error()));
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Could not verify the output file information.", new Win32Exception(Marshal.GetLastWin32Error()));
        FileIdentity identity;
        if (!GetFileInformationByHandleEx(handle, FileIdInfo, out identity, (uint)Marshal.SizeOf<FileIdentity>()))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is not (1 or 50 or 87))
                throw new IOException("Could not verify the output file identity.", new Win32Exception(error));
            identity = new(info.VolumeSerial, Join(info.IndexHigh, info.IndexLow), 0);
            if (identity.Low == 0)
                throw new IOException("This folder does not expose a usable file identity. Choose another output folder.");
        }
        return new(name.ToString(), identity, (info.Attributes & FileAttributes.Directory) != 0,
            Join(info.SizeHigh, info.SizeLow), Join(info.LastWriteHigh, info.LastWriteLow));
    }

    private static ulong Join(uint high, uint low) => ((ulong)high << 32) | low;

    private sealed record PathInfo(string FinalName, FileIdentity? Identity, bool IsDirectory, ulong Length, ulong LastWrite);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoByHandle
    {
        internal FileAttributes Attributes;
        internal uint CreationLow, CreationHigh, LastAccessLow, LastAccessHigh, LastWriteLow, LastWriteHigh;
        internal uint VolumeSerial, SizeHigh, SizeLow, LinkCount, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoByHandle info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdentity info, uint size);
}
