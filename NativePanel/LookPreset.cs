using System;
using System.IO;
using System.Text.Json.Serialization;

[Flags]
internal enum LookSections
{
    None = 0, Film = 1, Optics = 2, Grain = 4, Enabled = 8,
    All = Film | Optics | Grain | Enabled,
}

internal sealed record LookPreset([property: JsonRequired] LookSettings Look, SpatialPreview.Settings? Optics,
    int SchemaVersion = 0, bool? Enabled = null, GrainControls? Grain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PresetEngineIdentity? Engine = null)
{
    internal static LookPreset From(VariantLook look) =>
        new(look.Look, look.Optics, SchemaVersion: 2, Enabled: look.Enabled, Grain: look.Grain);

    internal (LookSettings Film, SpatialPreview.Settings? Optics, bool? Enabled, GrainControls? Grain)
        SelectSections(VariantLook current, LookSections sections)
    {
        var optics = sections.HasFlag(LookSections.Optics)
            ? Optics ?? (SchemaVersion >= 2 ? current.Optics : null)
            : current.Optics;
        return (sections.HasFlag(LookSections.Film) ? Look : current.Look, optics,
            sections.HasFlag(LookSections.Enabled) ? Enabled : null,
            sections.HasFlag(LookSections.Grain) ? Grain : null);
    }

    internal void Validate()
    {
        if (SchemaVersion is < 0 or > 3)
            throw new InvalidDataException("This preset version requires a different Spektrafilm build.");
        if (SchemaVersion < 2 && (Enabled is not null || Grain is not null))
            throw new InvalidDataException("This preset uses settings outside its declared version.");
        if (SchemaVersion == 3 ? Engine is not { IsValid: true } : Engine is not null)
            throw new InvalidDataException("This preset has invalid renderer provenance for its declared version.");
        LookStateValidation.ValidateLook(Look);
        if (Optics is not null)
            LookStateValidation.ValidateOptics(Optics);
        LookStateValidation.ValidateGrain(Grain);
    }
}
