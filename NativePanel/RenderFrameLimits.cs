using System;

internal static class RenderFrameLimits
{
    internal const int MaxHighDepthPixels = 100_000_000;
    internal const int MaxHighDepthEdge = 16_384;

    internal static (int Width, int Height) ValidateExportCanvas(double width, double height, double scale)
    {
        var scaledWidth = Math.Ceiling(width * scale);
        var scaledHeight = Math.Ceiling(height * scale);
        if (!double.IsFinite(scale) || scale <= 0 || width <= 0 || height <= 0
            || !double.IsFinite(scaledWidth) || !double.IsFinite(scaledHeight)
            || scaledWidth < 1 || scaledHeight < 1
            || scaledWidth > MaxHighDepthEdge || scaledHeight > MaxHighDepthEdge
            || scaledWidth * scaledHeight > MaxHighDepthPixels)
            throw new InvalidOperationException(
                "The export canvas exceeds 16,384 pixels per side or 100 megapixels. Choose a smaller Export Recipe.");
        return ((int)scaledWidth, (int)scaledHeight);
    }
}
