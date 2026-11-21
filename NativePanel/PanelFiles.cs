using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

internal static class PanelFiles
{
    internal const int MaxPresetBytes = 65536;
    private const int FileDispositionInfo = 4;
    private const uint CreateNew = 1;
    private const uint NormalFile = 0x80;
    private const uint ReadWriteDelete = 0xC0010000;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    internal static T ReadPreset<T>(string path) => ReadPreset<T>(path, out _);

    internal static T ReadPreset<T>(string path, out string digest, Action<JsonElement>? inspect = null)
    {
        using var file = File.OpenRead(path);
        var bytes = ReadPresetBytes(file);
        digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        return DecodePreset<T>(bytes, inspect);
    }

    internal static byte[] ReadPresetBytes(FileStream file)
    {
        var remaining = file.Length - file.Position;
        if (remaining > MaxPresetBytes)
            throw new InvalidDataException("The preset is larger than 64 KB.");
        var bytes = new byte[checked((int)remaining)];
        file.ReadExactly(bytes);
        return bytes;
    }

    internal static T DecodePreset<T>(ReadOnlySpan<byte> json, Action<JsonElement>? inspect = null)
    {
        if (json.StartsWith("\uFEFF"u8))
            json = json[3..];
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        inspect?.Invoke(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, ReadOptions) ?? throw new InvalidDataException("The preset contains no look settings.");
    }

    internal static void WritePreset<T>(string path, T preset) => WriteJson(path, preset, WriteOptions);
    internal static void WritePreset<T>(Stream stream, T preset) => stream.Write(EncodePreset(preset, WriteOptions));

    internal static void WriteCompactPreset<T>(string path, T preset) => WriteJson(path, preset, null);
    internal static byte[] EncodeCompactPreset<T>(T preset) => EncodePreset(preset, null);

    private static void WriteJson<T>(string path, T preset, JsonSerializerOptions? options)
    {
        var bytes = EncodePreset(preset, options);
        WriteAtomically(path, stream => stream.Write(bytes));
    }

    private static byte[] EncodePreset<T>(T preset, JsonSerializerOptions? options)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preset, options);
        if (bytes.Length > MaxPresetBytes)
            throw new InvalidDataException("The preset is larger than 64 KB and cannot be safely saved.");
        return bytes;
    }

    internal static void WriteAtomically(string path, Action<Stream> write, Action? beforeCommit = null,
        bool overwrite = true, bool discardOnProcessExit = false)
    {
        var destination = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, $".spektra-{Guid.NewGuid():N}.tmp");
        var (staging, deletePending) = OpenStaging(temp, discardOnProcessExit);
        try
        {
            using (staging)
            {
                write(staging);
                staging.Flush(flushToDisk: true);
                // Keep the temporary file owned by the open handle until all
                // writes and the final destination validation have succeeded.
                if (deletePending)
                {
                    beforeCommit?.Invoke();
                    byte keep = 0;
                    if (!SetFileInformationByHandle(staging.SafeFileHandle, FileDispositionInfo, ref keep, 1))
                        throw new IOException("Could not release the completed Spektrafilm staging file.",
                            new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
            if (!deletePending) beforeCommit?.Invoke();
            File.Move(temp, destination, overwrite);
        }
        catch (Exception error)
        {
            try { File.Delete(temp); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                // A denied cleanup must not replace the original write or cancellation error.
                error.Data["StagingCleanupError"] = cleanupError.Message;
            }
            throw;
        }
    }

    private static (FileStream Stream, bool DeletePending) OpenStaging(string path, bool discardOnProcessExit)
    {
        if (discardOnProcessExit && OperatingSystem.IsWindows())
        {
            var handle = CreateFileW(WindowsStagingPath(path), ReadWriteDelete, 0, IntPtr.Zero, CreateNew, NormalFile, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                byte remove = 1;
                var marked = SetFileInformationByHandle(handle, FileDispositionInfo, ref remove, 1);
                try { return (new FileStream(handle, FileAccess.ReadWrite), marked); }
                catch { handle.Dispose(); throw; }
            }
            handle.Dispose();
        }
        return (new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None), false);
    }

    private static string WindowsStagingPath(string path)
    {
        // The managed file APIs accept deep paths even when the host process has
        // not opted in to long paths for direct Win32 calls.
        if (path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte value, uint size);

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"The preset repeats the '{property.Name}' property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}
