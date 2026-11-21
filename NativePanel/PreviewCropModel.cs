using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// Capture One's crop object contains the four raw rectangle coordinates. Keep
// every other model setting, including rotation, keystone and lens correction.
internal sealed record PreviewCropModel(string PixelSettingsSha256, PreviewCropModel.Rectangle? Crop)
{
    internal readonly record struct Rectangle(double X, double Y, double Width, double Height);
    private static readonly Regex Properties = new(
        "^[ \\t]*\"(crop|syncProcessCounter|adjustmentLayer)\"[ \\t]*:[ \\t]*([^\\r\\n]*)\\r?(?:\\n|$)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static PreviewCropModel? Read(string model, long expectedSyncCounter)
    {
        if (model.Length is 0 or > 1024 * 1024)
            return null;
        Match? cropRow = null, counterRow = null, layerRow = null;
        foreach (Match property in Properties.Matches(model))
        {
            if (property.Groups[1].Value == "crop")
            {
                if (cropRow is not null) return null;
                cropRow = property;
            }
            else if (property.Groups[1].Value == "syncProcessCounter")
            {
                if (counterRow is not null) return null;
                counterRow = property;
            }
            else
            {
                if (layerRow is not null) return null;
                layerRow = property;
            }
        }
        if (counterRow is null || layerRow is null || !layerRow.Groups[2].ValueSpan.TrimEnd().SequenceEqual("{")
            || !HaveExpectedScopes(model, counterRow.Index, layerRow.Groups[2].Index, cropRow?.Index)) return null;
        var counterField = counterRow.Groups[2];
        var digits = counterField.ValueSpan.TrimEnd();
        if (digits.EndsWith(",", StringComparison.Ordinal)) digits = digits[..^1].TrimEnd();
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var counter)
            || counter != expectedSyncCounter) return null;

        try
        {
            Rectangle? crop = null;
            var counterOffset = counterField.Index;
            if (cropRow is not null)
            {
                var field = cropRow.Groups[2].Value.TrimEnd();
                if (!field.EndsWith(',')) return null;
                using var rectangleJson = JsonDocument.Parse(field[..^1]);
                if (!TryReadRectangle(rectangleJson.RootElement, out crop)) return null;
                model = model.Remove(cropRow.Index, cropRow.Length);
                if (cropRow.Index < counterOffset) counterOffset -= cropRow.Length;
            }
            model = model.Remove(counterOffset, digits.Length).Insert(counterOffset, "0");
            return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model))), crop);
        }
        catch (JsonException) { return null; }
    }

    // Capture One serializes some unrelated values in its own syntax. Check
    // the two known properties' scope and hash all other bytes as they came in.
    private static bool HaveExpectedScopes(string model, int counter, int layer, int? crop)
    {
        if (model.AsSpan().TrimStart()[0] != '{') return false;
        Span<char> nesting = stackalloc char[64];
        var depth = 0;
        var quoted = false;
        var closed = false;
        var insideAdjustmentLayer = false;
        var found = 0;
        for (var index = 0; index < model.Length; index++)
        {
            if (index == counter || index == layer)
            {
                if (depth != 1 || quoted) return false;
                found++;
            }
            if (index == crop)
            {
                if (depth != 2 || quoted || !insideAdjustmentLayer) return false;
                found++;
            }
            var character = model[index];
            if (quoted)
            {
                if (character == '\\') index++;
                else if (character == '"') quoted = false;
                continue;
            }
            if (char.IsWhiteSpace(character)) continue;
            if (closed) return false;
            if (character == '"') quoted = true;
            else if (character is '{' or '[')
            {
                if (depth == nesting.Length) return false;
                nesting[depth++] = character == '{' ? '}' : ']';
                if (index == layer) insideAdjustmentLayer = true;
            }
            else if (character is '}' or ']')
            {
                if (depth == 0 || nesting[--depth] != character) return false;
                if (depth == 1) insideAdjustmentLayer = false;
                closed = depth == 0;
            }
        }
        return closed && found == (crop is null ? 2 : 3);
    }

    private static bool TryReadRectangle(JsonElement crop, out Rectangle? rectangle)
    {
        rectangle = default;
        if (crop.ValueKind != JsonValueKind.Object) return false;
        var fields = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var field in crop.EnumerateObject())
        {
            if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out var value)
                || !double.IsFinite(value) || !fields.TryAdd(field.Name, value))
                return false;
        }
        if (fields.Count != 4 || !fields.TryGetValue("x", out var x) || !fields.TryGetValue("y", out var y)
            || !fields.TryGetValue("width", out var width) || !fields.TryGetValue("height", out var height))
            return false;
        // Capture One can retain an explicit empty rectangle when clearing a crop.
        if (x == 0 && y == 0 && width == 0 && height == 0) return true;
        if (width <= 0 || height <= 0) return false;
        rectangle = new Rectangle(x, y, width, height);
        return true;
    }
}
