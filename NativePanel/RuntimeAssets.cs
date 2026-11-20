using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed record PresetEngineIdentity(string SourceCommit, string SourceDigest, string ParameterSha256, string AssetSha256)
{
    internal bool IsValid => IsHash(SourceCommit, 40) && IsHash(SourceDigest, 64) && IsHash(ParameterSha256, 64) && IsHash(AssetSha256, 64);
    private static bool IsHash(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);
}

internal static class RuntimeAssets
{
    private static readonly string[] Required =
    {
        "profiles/preview/Linear Rec.2020.icc", "profiles/preview/ProPhoto RGB.icc",
        "renderer/SpektraHanatos2025Spectra.f32", "renderer/SpektraOutputGamutCompression.f32",
        "renderer/SpektraStillRenderer.exe", "renderer/parameter-capabilities.json",
        "renderer/shaders/SpektraCopy.comp.spv", "renderer/shaders/SpektraCurveDevelop.comp.spv",
        "renderer/shaders/SpektraDiffusion.comp.spv", "renderer/shaders/SpektraDir.comp.spv",
        "renderer/shaders/SpektraFilmExposure.comp.spv", "renderer/shaders/SpektraFormatConvert.comp.spv",
        "renderer/shaders/SpektraGrain.comp.spv", "renderer/shaders/SpektraHalation.comp.spv",
        "renderer/shaders/SpektraPrintScan.comp.spv", "renderer/shaders/SpektraScannerPost.comp.spv",
        "stocks.json",
    };
    internal static IReadOnlyList<string> RequiredFiles { get; } = Array.AsReadOnly(Required);

    internal static PresetEngineIdentity? ReadPresetIdentity(string? root)
    {
        if (string.IsNullOrEmpty(root)) return null;
        var path = Path.Combine(root, "build-manifest.json");
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("The renderer build manifest is too large.");
        using var document = JsonDocument.Parse(file);
        var manifest = document.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("rendererProtocol", out var protocol) || protocol.ValueKind != JsonValueKind.Number
            || !protocol.TryGetInt32(out var version) || version != RendererProtocol.Version
            || !manifest.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object
            || !manifest.TryGetProperty("parameterContract", out var parameters) || parameters.ValueKind != JsonValueKind.String
            || !source.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String
            || !source.TryGetProperty("treeDigest", out var tree) || tree.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The renderer build manifest is missing its source and parameter identity.");
        var identity = new PresetEngineIdentity(commit.GetString()!, tree.GetString()!, parameters.GetString()!, Verify(root, manifest));
        return identity.IsValid ? identity : throw new InvalidDataException("The renderer build identity is malformed.");
    }

    internal static string Verify(string root, JsonElement manifest)
    {
        if (!manifest.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
            || files.GetArrayLength() < Required.Length || files.GetArrayLength() > 64)
            throw new InvalidDataException("The Spektrafilm build manifest is missing its runtime file inventory.");
        var inventory = new Dictionary<string, (long Bytes, string Hash)>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in files.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                || path.GetString() is not { Length: > 0 and <= 200 } filename
                || !record.TryGetProperty("bytes", out var size) || size.ValueKind != JsonValueKind.Number
                || !size.TryGetInt64(out var bytes) || bytes <= 0
                || !record.TryGetProperty("sha256", out var digest) || digest.ValueKind != JsonValueKind.String
                || digest.GetString() is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)
                || !inventory.TryAdd(filename.Replace('\\', '/'), (bytes, hash)))
                throw new InvalidDataException("The Spektrafilm build manifest has an invalid runtime file inventory.");
        }

        var contents = new StringBuilder();
        foreach (var relative in Required)
        {
            if (!inventory.TryGetValue(relative, out var expected))
                throw new InvalidDataException($"The Spektrafilm build manifest omits '{relative}'.");
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            for (var component = path; !string.Equals(component, root, StringComparison.OrdinalIgnoreCase);
                component = Path.GetDirectoryName(component)!)
            {
                if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The Spektrafilm runtime asset '{relative}' uses a linked path.");
            }
            using var file = File.OpenRead(path);
            if (file.Length != expected.Bytes
                || !string.Equals(Convert.ToHexString(SHA256.HashData(file)), expected.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The Spektrafilm runtime asset '{relative}' differs from its verified build. Reinstall the complete release.");
            contents.Append(relative).Append(':').Append(expected.Hash.ToLowerInvariant()).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contents.ToString()))).ToLowerInvariant();
    }
}
