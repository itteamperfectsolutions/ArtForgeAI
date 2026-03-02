namespace ArtForgeAI.Models;

/// <summary>
/// Print size at 300 DPI. Width/Height are the short/long sides in inches.
/// WidthPx/HeightPx are always portrait orientation (short side × long side).
/// </summary>
public record PrintSize(string Name, double WidthInches, double HeightInches, int WidthPx, int HeightPx)
{
    public string Label => $"{WidthInches}×{HeightInches}\"";
    public long TotalPixels => (long)WidthPx * HeightPx;
}

/// <summary>
/// Static configuration and presets for photo size expansion.
/// </summary>
public static class PhotoExpandConfig
{
    public const int Dpi = 300;

    /// <summary>Standard print sizes at 300 DPI (portrait orientation: short side × long side).</summary>
    public static readonly PrintSize[] Presets =
    [
        new("4×6",    4,  6,  1200,  1800),
        new("5×7",    5,  7,  1500,  2100),
        new("8×10",   8,  10, 2400,  3000),
        new("8×12",   8,  12, 2400,  3600),
        new("11×14",  11, 14, 3300,  4200),
        new("12×18",  12, 18, 3600,  5400),
        new("16×20",  16, 20, 4800,  6000),
        new("20×30",  20, 30, 6000,  9000),
        new("24×36",  24, 36, 7200, 10800),
    ];

    /// <summary>Create a custom print size from inches.</summary>
    public static PrintSize CreateCustom(double widthInches, double heightInches)
    {
        var wPx = (int)Math.Round(widthInches * Dpi);
        var hPx = (int)Math.Round(heightInches * Dpi);
        // Always store as portrait (short × long)
        if (wPx > hPx) (wPx, hPx) = (hPx, wPx);
        return new PrintSize("Custom", Math.Min(widthInches, heightInches), Math.Max(widthInches, heightInches), wPx, hPx);
    }

    /// <summary>Find the closest standard print size to given pixel dimensions.</summary>
    public static PrintSize? FindClosest(int widthPx, int heightPx, int tolerancePx = 150)
    {
        // Normalize to portrait
        int shortSide = Math.Min(widthPx, heightPx);
        int longSide = Math.Max(widthPx, heightPx);

        PrintSize? best = null;
        double bestDist = double.MaxValue;

        foreach (var p in Presets)
        {
            double dist = Math.Sqrt(Math.Pow(p.WidthPx - shortSide, 2) + Math.Pow(p.HeightPx - longSide, 2));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = p;
            }
        }

        return bestDist <= tolerancePx ? best : null;
    }

    /// <summary>Get effective pixel dimensions accounting for landscape orientation.</summary>
    public static (int w, int h) GetPixels(PrintSize size, bool landscape)
        => landscape ? (size.HeightPx, size.WidthPx) : (size.WidthPx, size.HeightPx);

    /// <summary>Max longest-side pixels to send to Gemini (keeps API fast + within limits).</summary>
    public const int GeminiMaxSide = 1536;
}

public class PhotoExpandRequest
{
    public byte[] SourceBytes { get; set; } = [];
    public PrintSize SourceSize { get; set; } = PhotoExpandConfig.Presets[0];
    public PrintSize TargetSize { get; set; } = PhotoExpandConfig.Presets[5];
    public bool Landscape { get; set; }
    /// <summary>Position of source on target canvas: 0.0–1.0 for both axes. (0.5, 0.5) = center.</summary>
    public double PosX { get; set; } = 0.5;
    public double PosY { get; set; } = 0.5;
    /// <summary>Optional user hint describing what should fill the expanded area.</summary>
    public string? PromptHint { get; set; }
    /// <summary>Canvas wrap bleed in inches — extends all 4 sides beyond the target size.</summary>
    public double CanvasWrapInches { get; set; }
}

public class PhotoExpandResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public byte[]? ResultBytes { get; set; }
    public string? ResultPath { get; set; }
    public int ResultWidthPx { get; set; }
    public int ResultHeightPx { get; set; }
    /// <summary>Target size without wrap (for display purposes).</summary>
    public int TargetWidthPx { get; set; }
    public int TargetHeightPx { get; set; }
    /// <summary>Canvas wrap bleed in pixels per side.</summary>
    public int WrapPx { get; set; }
    public int Dpi { get; set; } = PhotoExpandConfig.Dpi;
}
