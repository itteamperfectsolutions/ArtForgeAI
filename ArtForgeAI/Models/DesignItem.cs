namespace ArtForgeAI.Models;

/// <summary>
/// Represents a single design image in a multi-image gang sheet or shape cut sheet.
/// </summary>
public class DesignItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FileName { get; set; } = "";
    public string? UploadedPath { get; set; }
    public int Quantity { get; set; } = 1;
    public bool RemoveBg { get; set; }
    public bool RemoveWhites { get; set; }
    public int WhiteTolerance { get; set; } = 30;
    public bool IsProcessing { get; set; }
    public string? OriginalDataUrl { get; set; }
    public byte[]? TransparentPngBytes { get; set; }
    public string? ProcessedDataUrl { get; set; }
    public string? FinalDesignUrl { get; set; }
    public string? OutlineDataUrl { get; set; }
    public string? OutlineSvgData { get; set; }

    // Per-item outline settings
    public int CutGapPx { get; set; } = 15;
    public int CornerSmoothPx { get; set; } = 20;
    public int OutlineWidthPx { get; set; } = 3;

    // Per-item design size (inches)
    public double WidthIn { get; set; } = 3.0;
    public double HeightIn { get; set; } = 4.0;

    // Per-item spacing (inches)
    public double SpaceXIn { get; set; } = 0.125;
    public double SpaceYIn { get; set; } = 0.125;
}
