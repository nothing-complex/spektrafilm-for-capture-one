using System;
using System.IO;
using System.Text.Json.Serialization;

internal readonly record struct GrainValues([property: JsonRequired] int Type, [property: JsonRequired] double Impact,
    [property: JsonRequired] double Granularity)
{
    internal bool IsValid => Type is >= 0 and <= 5 && double.IsFinite(Impact) && Impact is >= 0 and <= 100
        && double.IsFinite(Granularity) && Granularity is >= 0 and <= 100;
}

internal sealed record GrainReceipt([property: JsonRequired] int Version, [property: JsonRequired] Guid Variant,
    [property: JsonRequired] GrainValues Original, [property: JsonRequired] GrainValues Applied, GrainValues? PreviousApplied)
{
    internal bool Matches(GrainValues current) => current != Original && (current == Applied || current == PreviousApplied);
}

internal sealed class GrainReceiptStore(string directory)
{
    internal GrainReceipt? Read(Guid variant)
    {
        GrainReceipt receipt;
        try { receipt = PanelFiles.ReadPreset<GrainReceipt>(PathFor(variant)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        Validate(variant, receipt);
        return receipt;
    }

    internal GrainReceipt Prepare(Guid variant, GrainValues current, GrainValues next)
    {
        var previous = Read(variant);
        var owned = previous?.Matches(current) == true;
        var receipt = new GrainReceipt(1, variant, owned ? previous!.Original : current, next, owned ? current : null);
        Save(receipt);
        return receipt;
    }

    internal void Save(GrainReceipt receipt)
    {
        Validate(receipt.Variant, receipt);
        Directory.CreateDirectory(directory);
        PanelFiles.WriteCompactPreset(PathFor(receipt.Variant), receipt);
    }

    private string PathFor(Guid variant) => Path.Combine(directory, variant.ToString("N") + ".grain.json");

    private static void Validate(Guid variant, GrainReceipt receipt)
    {
        if (variant == Guid.Empty || receipt.Variant != variant || receipt.Version != 1
            || !receipt.Original.IsValid || !receipt.Applied.IsValid || receipt.PreviousApplied is { IsValid: false })
            throw new InvalidDataException("The native grain restore record has an invalid identity, version or value.");
    }
}
