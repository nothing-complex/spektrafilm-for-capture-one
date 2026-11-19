using System;

internal static class ExportResolution
{
    internal const double DefaultDpi = 96;

    internal static double DotsPerInch(double resolution, string unit)
    {
        var dpi = unit switch
        {
            "Inch" => resolution,
            "Centimeter" => resolution * 2.54,
            "Millimeter" => resolution * 25.4,
            _ => throw new ArgumentException("The Capture One recipe uses an unsupported resolution unit.", nameof(unit)),
        };
        if (!double.IsFinite(dpi) || dpi <= 0)
            throw new ArgumentOutOfRangeException(nameof(resolution), "Choose a positive resolution in the Capture One recipe.");
        return dpi;
    }
}
