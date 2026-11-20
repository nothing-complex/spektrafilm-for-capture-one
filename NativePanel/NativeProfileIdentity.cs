using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using P1.C1.Core;
using P1.ModelCoreWrapper;

internal static class NativeProfileIdentity
{
    // Called under Capture One's variant reader lock. The native model records
    // sidecar identifiers. The resolved bytes can change behind an unchanged ID.
    internal static string? TryRead(ICoreVariant variant, bool verifyLibraryProfiles)
    {
        var icc = variant.GetVariantICCIds();
        var lcc = variant.Settings.GetLCCIdsThroughLayers(argIgnoreDefaultsLayer: false);
        if (icc is null || lcc is null)
            return null;
        if (icc.Length == 0 && lcc.Count == 0)
            return string.Empty;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hasLocalProfiles = false;
        foreach (var id in icc.OrderBy(id => id.FileName, StringComparer.Ordinal).ThenBy(id => id.UndoID))
        {
            var profile = variant.GetICCSideCarGivenID(id);
            if (profile is { isLocal: false, FilePathInLibrary.Length: > 0 } library)
            {
                if (verifyLibraryProfiles)
                {
                    var path = library.FilePathInLibrary;
                    if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc)
                        return null;
                    try
                    {
                        using var bytes = File.OpenRead(path);
                        if (bytes.Length is < 128 or > 8 * 1024 * 1024)
                            return null;
                        AppendDigest(hash, (byte)'C', id, SHA256.HashData(bytes));
                        hasLocalProfiles = true;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        return null;
                    }
                }
                continue;
            }
            if (profile is not { Empty: false, isLocal: true, rawData: { Length: > 0 } data })
                return null;
            Append(hash, (byte)'I', id, data);
            hasLocalProfiles = true;
        }
        foreach (var id in lcc.OrderBy(id => id.FileName, StringComparer.Ordinal).ThenBy(id => id.UndoID))
        {
            var profile = variant.GetLCCSideCarGivenID(id);
            if (profile is not { Empty: false, isLocal: true, rawData: { Length: > 0 } data })
                return null;
            Append(hash, (byte)'L', id, data);
            hasLocalProfiles = true;
        }
        return hasLocalProfiles ? Convert.ToHexString(hash.GetHashAndReset()) : string.Empty;
    }

    private static void Append(IncrementalHash hash, byte kind, McSideCarID id, byte[] data) =>
        AppendDigest(hash, kind, id, SHA256.HashData(data));

    private static void AppendDigest(IncrementalHash hash, byte kind, McSideCarID id, byte[] digest)
    {
        Span<byte> identifier = stackalloc byte[1 + sizeof(ulong)];
        identifier[0] = kind;
        BinaryPrimitives.WriteUInt64LittleEndian(identifier[1..], id.UndoID);
        hash.AppendData(identifier);
        hash.AppendData(SHA256.HashData(Encoding.UTF8.GetBytes(id.FileName)));
        hash.AppendData(digest);
    }
}
