namespace ArtForgeAI.Models;

/// <summary>
/// Gang sheet size preset at 300 DPI.
/// </summary>
public record GangSheetSize(string Id, string Label, double WidthInches, double HeightInches)
{
    public int WidthPx => (int)Math.Round(WidthInches * GangSheetConfig.Dpi);
    public int HeightPx => (int)Math.Round(HeightInches * GangSheetConfig.Dpi);
}

/// <summary>
/// Computed layout for tiling designs onto a gang sheet.
/// </summary>
public record GangSheetLayout(
    int Cols, int Rows, int Total,
    int OffsetX, int OffsetY,
    int SheetWidthPx, int SheetHeightPx,
    int DesignWidthPx, int DesignHeightPx,
    int SpacingPx);

/// <summary>
/// Static configuration and helpers for DTF/UVDTF gang sheet generation.
/// </summary>
public static class GangSheetConfig
{
    public const int Dpi = 300;

    public static readonly GangSheetSize[] Presets =
    [
        new("11x12",  "11\" x 12\"",  11, 12),
        new("11x18",  "11\" x 18\"",  11, 18),
        new("22x39",  "22\" x 39\"",  22, 39),
    ];

    /// <summary>
    /// Calculate how many designs fit on a sheet, trying both orientations and picking the best.
    /// </summary>
    public static GangSheetLayout CalculateLayout(
        int sheetWidthPx, int sheetHeightPx,
        double designWidthInches, double designHeightInches,
        double spacingInches)
    {
        var normal = CalcOrientation(sheetWidthPx, sheetHeightPx,
            designWidthInches, designHeightInches, spacingInches);
        var rotated = CalcOrientation(sheetWidthPx, sheetHeightPx,
            designHeightInches, designWidthInches, spacingInches);

        return rotated.Total > normal.Total ? rotated : normal;
    }

    private static GangSheetLayout CalcOrientation(
        int sheetW, int sheetH,
        double designWIn, double designHIn,
        double spacingIn)
    {
        int designWPx = (int)Math.Round(designWIn * Dpi);
        int designHPx = (int)Math.Round(designHIn * Dpi);
        int spacingPx = (int)Math.Round(spacingIn * Dpi);

        if (designWPx <= 0 || designHPx <= 0) return new(0, 0, 0, 0, 0, sheetW, sheetH, designWPx, designHPx, spacingPx);

        int cols = Math.Max(1, (sheetW + spacingPx) / (designWPx + spacingPx));
        int rows = Math.Max(1, (sheetH + spacingPx) / (designHPx + spacingPx));

        int gridW = cols * designWPx + (cols - 1) * spacingPx;
        int gridH = rows * designHPx + (rows - 1) * spacingPx;
        int offsetX = (sheetW - gridW) / 2;
        int offsetY = (sheetH - gridH) / 2;

        return new(cols, rows, cols * rows, offsetX, offsetY,
            sheetW, sheetH, designWPx, designHPx, spacingPx);
    }

    /// <summary>Create a custom sheet size from inches.</summary>
    public static GangSheetSize CreateCustom(double widthInches, double heightInches)
        => new("custom", $"Custom {widthInches}\" x {heightInches}\"", widthInches, heightInches);
}
