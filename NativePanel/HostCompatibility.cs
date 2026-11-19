using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed record CaptureOneHost(
    [property: JsonRequired] string FileVersion,
    [property: JsonRequired] string Architecture,
    [property: JsonRequired] string AssemblySha256,
    [property: JsonRequired] string[] Capabilities);

internal static class HostCompatibility
{
    private const string ResourceName = "Spektrafilm.CaptureOneHosts.json";
    private static readonly Lazy<CaptureOneHost[]> Supported = new(Load);
    private static readonly JsonSerializerOptions Strict = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static string SupportedVersions => string.Join(", ", Supported.Value.Select(host => host.FileVersion).Distinct());

    internal static CaptureOneHost? Match(string? version, string architecture, string assemblySha256) =>
        Supported.Value.SingleOrDefault(host => host.FileVersion == version
            && StringComparer.Ordinal.Equals(host.Architecture, architecture)
            && StringComparer.OrdinalIgnoreCase.Equals(host.AssemblySha256, assemblySha256));

    private static CaptureOneHost[] Load()
    {
        using var stream = typeof(HostCompatibility).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The Capture One host compatibility manifest is missing.");
        return Parse(stream);
    }

    internal static CaptureOneHost[] Parse(Stream stream)
    {
        var list = JsonSerializer.Deserialize<HostList>(stream, Strict);
        if (list is not { SchemaVersion: 1, Hosts.Length: > 0 and <= 32 })
            throw new InvalidDataException("The Capture One host compatibility manifest has an unsupported schema.");
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in list.Hosts)
        {
            if (host is null || !Version.TryParse(host.FileVersion, out var version) || version.Revision < 0
                || host.Architecture != "x64"
                || host.AssemblySha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit)
                || host.Capabilities is not { Length: > 0 and <= 20 } names
                || names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 64)
                || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || !identifiers.Add(host.FileVersion + ":" + host.Architecture + ":" + hash))
                throw new InvalidDataException("The Capture One host compatibility manifest contains a malformed or duplicate profile.");
        }
        return list.Hosts;
    }

    private sealed record HostList([property: JsonRequired] int SchemaVersion, [property: JsonRequired] CaptureOneHost[] Hosts);
}
