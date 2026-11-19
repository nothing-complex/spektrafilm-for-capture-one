using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class RendererParameters
{
    private readonly Manifest manifest;
    private readonly Dictionary<string, Parameter> parameters;

    private RendererParameters(Manifest manifest)
    {
        this.manifest = manifest;
        if (manifest.SchemaVersion != 1 || manifest.Contract is not { Length: 64 } || !manifest.Contract.All(Uri.IsHexDigit)
            || manifest.Parameters is not { Length: > 0 and <= 256 } || manifest.Scientific?.Films is not { Length: > 0 and <= 128 })
            throw new InvalidDataException("Unsupported Spektrafilm renderer parameter description.");
        parameters = new(StringComparer.Ordinal);
        foreach (var parameter in manifest.Parameters)
        {
            if (parameter is null || parameter.Key is not { Length: > 0 and <= 128 }
                || !parameter.HasValidDefault() || !parameters.TryAdd(parameter.Key, parameter))
                throw new InvalidDataException("The renderer parameter description contains a malformed or repeated option.");
        }
        if (manifest.Scientific.Films.Any(film => film?.Id is not { Length: > 0 and <= 128 })
            || manifest.Scientific.Films.Select(film => film.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Scientific.Films.Length)
            throw new InvalidDataException("The renderer parameter description contains malformed film calibration entries.");
    }

    internal static RendererParameters Read(string root) =>
        new(PanelFiles.ReadPreset<Manifest>(Path.Combine(root, "renderer", "parameter-capabilities.json")));

    internal bool SupportsChoice(string key, string value) => parameters.TryGetValue(key, out var parameter)
        && parameter.Choices?.Contains(value, StringComparer.Ordinal) == true && IsAvailable(key, value);

    internal bool SupportsHanatos2026(string stock) => manifest.Scientific.Films.Any(film => film.Id == stock && film.Hanatos2026);

    internal void VerifyOptions(string key, IEnumerable<string> values)
    {
        foreach (var value in values)
            if (!SupportsChoice(key, value))
                throw new InvalidDataException($"The renderer does not advertise the selected {key} option '{value}'. Reinstall the matching runtime.");
    }

    internal void VerifyArguments(string[] arguments)
    {
        if (arguments.Length % 2 != 0) throw new InvalidDataException("The renderer option list is incomplete.");
        for (var index = 0; index < arguments.Length; index += 2)
        {
            var key = arguments[index];
            var value = arguments[index + 1];
            if (!key.StartsWith("--", StringComparison.Ordinal) || !parameters.TryGetValue(key[2..], out var option)
                || !option.Accepts(value) || !IsAvailable(key[2..], value))
                throw new InvalidDataException($"The renderer option '{key} {value}' is outside the build's parameter description.");
        }
    }

    private bool IsAvailable(string key, string value) => key != "mode" || value != "process_negative"
        || manifest.Scientific.AcademyPrinterDensity;

    private sealed record Manifest([property: JsonPropertyName("schemaVersion"), JsonRequired] int SchemaVersion,
        [property: JsonPropertyName("parameterContract"), JsonRequired] string Contract,
        [property: JsonPropertyName("parameters"), JsonRequired] Parameter[] Parameters,
        [property: JsonPropertyName("scientific"), JsonRequired] Scientific Scientific);

    private sealed record Scientific([property: JsonPropertyName("academyPrinterDensity"), JsonRequired] bool AcademyPrinterDensity,
        [property: JsonPropertyName("films"), JsonRequired] Film[] Films);

    private sealed record Film([property: JsonPropertyName("id"), JsonRequired] string Id,
        [property: JsonPropertyName("hanatos2025"), JsonRequired] bool Hanatos2025,
        [property: JsonPropertyName("hanatos2026"), JsonRequired] bool Hanatos2026);

    private sealed record Parameter([property: JsonPropertyName("key"), JsonRequired] string Key,
        [property: JsonPropertyName("kind"), JsonRequired] string Kind,
        [property: JsonPropertyName("default"), JsonRequired] JsonElement Default,
        [property: JsonPropertyName("minimum")] double? Minimum,
        [property: JsonPropertyName("maximum")] double? Maximum,
        [property: JsonPropertyName("choices")] string[]? Choices)
    {
        internal bool HasValidDefault() => Kind switch
        {
            "number" or "integer" => Minimum is double min && Maximum is double max && double.IsFinite(min)
                && double.IsFinite(max) && min <= max && Default.ValueKind == JsonValueKind.Number && Accepts(Default.GetRawText()),
            "boolean" => Default.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "choice" => Choices is { Length: > 0 and <= 128 } && Choices.All(value => value is { Length: > 0 and <= 128 })
                && Choices.Distinct(StringComparer.Ordinal).Count() == Choices.Length
                && Default.ValueKind == JsonValueKind.String && Accepts(Default.GetString()!),
            _ => false,
        };

        internal bool Accepts(string value) => Kind switch
        {
            "number" => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && float.IsFinite(number) && number >= (float)Minimum!.Value && number <= (float)Maximum!.Value,
            "integer" => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
                && integer >= Minimum!.Value && integer <= Maximum!.Value,
            "boolean" => value is "true" or "false" or "1" or "0",
            "choice" => Choices?.Contains(value, StringComparer.Ordinal) == true,
            _ => false,
        };
    }
}
