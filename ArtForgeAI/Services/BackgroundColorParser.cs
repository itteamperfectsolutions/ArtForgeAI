using System.Text.RegularExpressions;
using SixLabors.ImageSharp.PixelFormats;

namespace ArtForgeAI.Services;

/// <summary>
/// Extracts target background color from natural language prompts.
/// </summary>
public static partial class BackgroundColorParser
{
    // Regex priority order: transparent → hex → "make it [color]" → "[color] background"

    [GeneratedRegex(@"\btransparent\b", RegexOptions.IgnoreCase)]
    private static partial Regex TransparentPattern();

    [GeneratedRegex(@"#([0-9a-fA-F]{6})\b")]
    private static partial Regex HexColorPattern();

    [GeneratedRegex(@"(?:make|change|set|replace)\s+(?:it|the\s+(?:background|bg))?\s*(?:to\s+)?(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex MakeItColorPattern();

    [GeneratedRegex(@"(\w+)\s+(?:background|bg|backdrop)", RegexOptions.IgnoreCase)]
    private static partial Regex ColorBackgroundPattern();

    private static readonly Dictionary<string, Rgba32> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = new Rgba32(255, 255, 255, 255),
        ["black"] = new Rgba32(0, 0, 0, 255),
        ["red"] = new Rgba32(255, 0, 0, 255),
        ["green"] = new Rgba32(0, 128, 0, 255),
        ["blue"] = new Rgba32(0, 0, 255, 255),
        ["yellow"] = new Rgba32(255, 255, 0, 255),
        ["gray"] = new Rgba32(128, 128, 128, 255),
        ["grey"] = new Rgba32(128, 128, 128, 255),
        ["pink"] = new Rgba32(255, 192, 203, 255),
        ["orange"] = new Rgba32(255, 165, 0, 255),
        ["purple"] = new Rgba32(128, 0, 128, 255),
        ["cyan"] = new Rgba32(0, 255, 255, 255),
        ["magenta"] = new Rgba32(255, 0, 255, 255),
        ["navy"] = new Rgba32(0, 0, 128, 255),
        ["teal"] = new Rgba32(0, 128, 128, 255),
        ["brown"] = new Rgba32(139, 69, 19, 255),
        ["beige"] = new Rgba32(245, 245, 220, 255),
        ["ivory"] = new Rgba32(255, 255, 240, 255),
        ["cream"] = new Rgba32(255, 253, 208, 255),
    };

    /// <summary>
    /// Extracts background color from a prompt. Returns "transparent" for transparent requests.
    /// </summary>
    public static string ExtractBackgroundColor(string prompt)
    {
        if (TransparentPattern().IsMatch(prompt))
            return "transparent";

        var hexMatch = HexColorPattern().Match(prompt);
        if (hexMatch.Success)
            return "#" + hexMatch.Groups[1].Value;

        var makeItMatch = MakeItColorPattern().Match(prompt);
        if (makeItMatch.Success)
        {
            var color = makeItMatch.Groups[1].Value.ToLowerInvariant();
            if (NamedColors.ContainsKey(color))
                return color;
        }

        var colorBgMatch = ColorBackgroundPattern().Match(prompt);
        if (colorBgMatch.Success)
        {
            var color = colorBgMatch.Groups[1].Value.ToLowerInvariant();
            if (NamedColors.ContainsKey(color))
                return color;
        }

        return "white";
    }

    /// <summary>
    /// Parses a color name or hex string into an Rgba32 value.
    /// Returns null for "transparent" (caller should handle alpha channel).
    /// </summary>
    public static Rgba32? ParseBackgroundColor(string colorName)
    {
        if (string.Equals(colorName, "transparent", StringComparison.OrdinalIgnoreCase))
            return null;

        if (colorName.StartsWith('#') && colorName.Length == 7)
        {
            if (byte.TryParse(colorName.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(colorName.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(colorName.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return new Rgba32(r, g, b, 255);
            }
        }

        if (NamedColors.TryGetValue(colorName, out var named))
            return named;

        return new Rgba32(255, 255, 255, 255); // default white
    }
}
