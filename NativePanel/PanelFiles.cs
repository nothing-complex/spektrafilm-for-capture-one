using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

internal static class PanelFiles
{
    private const int MaxPresetBytes = 65536;
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    internal static T ReadPreset<T>(string path) => ReadPreset<T>(path, out _);

    internal static T ReadPreset<T>(string path, out string digest)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaxPresetBytes)
            throw new InvalidDataException("The preset is larger than 64 KB.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith("\uFEFF"u8))
            json = json[3..];
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, ReadOptions) ?? throw new InvalidDataException("The preset contains no look settings.");
    }

    internal static void WritePreset<T>(string path, T preset) => WriteJson(path, preset, WriteOptions);
    internal static void WritePreset<T>(Stream stream, T preset) => stream.Write(EncodePreset(preset, WriteOptions));

    internal static void WriteCompactPreset<T>(string path, T preset) => WriteJson(path, preset, null);

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

    internal static void WriteAtomically(string path, Action<Stream> write, Action? beforeCommit = null, bool overwrite = true)
    {
        var destination = Path.GetFullPath(path);
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, $".spektra-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            beforeCommit?.Invoke();
            File.Move(temp, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

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
