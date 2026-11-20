using System;
using System.Collections.Generic;
using System.IO;

internal static class LookStateValidation
{
    internal static void Validate(VariantLook saved)
    {
        if (saved is null || saved.Version != 1 || saved.Look is null || saved.Optics is null)
            throw new InvalidDataException("The saved Spektrafilm look has an unsupported format or version.");
        ValidateLook(saved.Look);
        ValidateOptics(saved.Optics);
        ValidateGrain(saved.Grain);
    }

    internal static void ValidateGrain(GrainControls? settings)
    {
        if (settings is { } grain &&
            (grain.Type is < 0 or > 5 || !InRange(grain.Impact, 0, 100) || !InRange(grain.Granularity, 0, 100)))
            throw new InvalidDataException("The saved native grain controls are invalid.");
    }

    internal static void ValidateLook(LookSettings look)
    {
        if (look is null || !Identifier(look.Film) || !Identifier(look.Paper) || !Identifier(look.Mode)
            || !Identifier(look.SpectralMethod) || !Identifier(look.FilmPushPullMode)
            || !FilmControls.Exposure.Contains(look.Exposure) || !FilmControls.PrintExposure.Contains(look.PrintExposure)
            || !FilmControls.Strength.Contains(look.Strength * 100))
            throw new InvalidDataException("The saved film settings contain a missing, invalid or out-of-range value.");
        ValidateNumbers(look.Development);
    }

    internal static void ValidateDevelopment(IReadOnlyDictionary<string, double> values)
    {
        foreach (var (key, value) in values)
            if (!FilmControls.Development.TryGetValue(key, out var definition) || !definition.Contains(value))
                throw new InvalidDataException($"The saved development control '{key}' is unsupported or out of range.");
    }

    internal static void ValidateOptics(SpatialPreview.Settings settings)
    {
        if (settings is null) throw new InvalidDataException("The saved optical settings are missing.");
        ValidateNumbers(settings.Numbers);
        if (settings.Selections is null || settings.Switches is null || settings.Selections.Count > 256 || settings.Switches.Count > 256)
            throw new InvalidDataException("The saved optical settings are incomplete or too large.");
        foreach (var (key, value) in settings.Selections)
            if (!Identifier(key) || !Identifier(value))
                throw new InvalidDataException("The saved optical choices contain an invalid value.");
        foreach (var key in settings.Switches.Keys)
            if (!Identifier(key)) throw new InvalidDataException("The saved optical switches contain an invalid key.");
    }

    private static void ValidateNumbers(Dictionary<string, double>? values)
    {
        if (values is null || values.Count > 256)
            throw new InvalidDataException("The saved numeric controls are incomplete or too large.");
        foreach (var (key, value) in values)
            if (!Identifier(key) || !double.IsFinite(value))
                throw new InvalidDataException("The saved numeric controls contain an invalid value.");
    }

    private static bool InRange(double value, double minimum, double maximum) => double.IsFinite(value) && value >= minimum && value <= maximum;
    private static bool Identifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.') return false;
        return true;
    }
}
