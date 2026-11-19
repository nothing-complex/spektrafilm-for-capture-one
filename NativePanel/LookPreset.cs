using System.IO;
using System.Text.Json.Serialization;

internal sealed record LookPreset([property: JsonRequired] LookSettings Look, SpatialPreview.Settings? Optics,
    int SchemaVersion = 0, bool? Enabled = null, GrainControls? Grain = null)
{
    internal static LookPreset From(VariantLook look) =>
        new(look.Look, look.Optics, SchemaVersion: 2, Enabled: look.Enabled, Grain: look.Grain);

    internal void Validate()
    {
        if (SchemaVersion is < 0 or > 2)
            throw new InvalidDataException("This preset version requires a different Spektrafilm build.");
        if (SchemaVersion < 2 && (Enabled is not null || Grain is not null))
            throw new InvalidDataException("This preset uses settings outside its declared version.");
        LookStateValidation.ValidateLook(Look);
        if (Optics is not null)
            LookStateValidation.ValidateOptics(Optics);
        LookStateValidation.ValidateGrain(Grain);
    }
}
