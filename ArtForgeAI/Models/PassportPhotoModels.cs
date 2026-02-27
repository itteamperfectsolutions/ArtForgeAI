namespace ArtForgeAI.Models;

/// <summary>
/// Static configuration constants for passport photo processing.
/// Based on ICAO 9303 / ISO/IEC 19794-5 standards.
/// </summary>
public static class PassportPhotoConfig
{
    // Photo dimensions: 35mm × 45mm @ 300 DPI
    public const int PhotoWidthPx = 413;
    public const int PhotoHeightPx = 531;
    public const double PhotoWidthMm = 35.0;
    public const double PhotoHeightMm = 45.0;
    public const int Dpi = 300;

    // Pixels per mm at 300 DPI
    public const double PxPerMm = 300.0 / 25.4; // ~11.811

    /// <summary>ICAO face positioning guidelines within the 35×45mm frame.</summary>
    public static class FaceGrid
    {
        // Head top margin: 3–5mm from top edge
        public const double HeadTopMinMm = 3.0;
        public const double HeadTopMaxMm = 5.0;

        // Chin position: 28–33mm from top edge
        public const double ChinMinMm = 28.0;
        public const double ChinMaxMm = 33.0;

        // Eye line: 18–22mm from top edge
        public const double EyeLineMinMm = 18.0;
        public const double EyeLineMaxMm = 22.0;

        // Face width: 24–30mm
        public const double FaceWidthMinMm = 24.0;
        public const double FaceWidthMaxMm = 30.0;

        // Convert to pixel ratios (fraction of photo height/width)
        public static double HeadTopMinRatio => HeadTopMinMm / PhotoHeightMm;
        public static double HeadTopMaxRatio => HeadTopMaxMm / PhotoHeightMm;
        public static double ChinMinRatio => ChinMinMm / PhotoHeightMm;
        public static double ChinMaxRatio => ChinMaxMm / PhotoHeightMm;
        public static double EyeLineMinRatio => EyeLineMinMm / PhotoHeightMm;
        public static double EyeLineMaxRatio => EyeLineMaxMm / PhotoHeightMm;
        public static double FaceWidthMinRatio => FaceWidthMinMm / PhotoWidthMm;
        public static double FaceWidthMaxRatio => FaceWidthMaxMm / PhotoWidthMm;
    }

    /// <summary>Standard paper sizes for multi-up print sheets.</summary>
    public static readonly PaperSize[] PaperSizes =
    [
        new("4×6\"",   102, 152, 1205, 1795),
        new("A4",      210, 297, 2480, 3508),
        new("A3",      297, 420, 3508, 4961),
        new("12×18\"", 305, 457, 3602, 5398),
        new("13×19\"", 330, 483, 3898, 5704),
    ];

    /// <summary>Create a custom paper size from mm dimensions.</summary>
    public static PaperSize CreateCustom(int widthMm, int heightMm)
    {
        var widthPx = (int)Math.Round(widthMm * PxPerMm);
        var heightPx = (int)Math.Round(heightMm * PxPerMm);
        return new PaperSize("Custom", widthMm, heightMm, widthPx, heightPx);
    }

    /// <summary>
    /// Calculate how many passport photos fit on a given paper size.
    /// </summary>
    public static GridLayout CalculateGrid(PaperSize paper, double spacingMm, double marginMm, bool landscape = false)
    {
        var spacingPx = (int)Math.Round(spacingMm * PxPerMm);
        var marginPx = (int)Math.Round(marginMm * PxPerMm);

        var paperW = landscape ? paper.HeightPx : paper.WidthPx;
        var paperH = landscape ? paper.WidthPx : paper.HeightPx;

        var usableW = paperW - 2 * marginPx;
        var usableH = paperH - 2 * marginPx;

        var cols = Math.Max(1, (usableW + spacingPx) / (PhotoWidthPx + spacingPx));
        var rows = Math.Max(1, (usableH + spacingPx) / (PhotoHeightPx + spacingPx));

        // Center the grid on the paper
        var gridW = cols * PhotoWidthPx + (cols - 1) * spacingPx;
        var gridH = rows * PhotoHeightPx + (rows - 1) * spacingPx;
        var offsetX = marginPx + (usableW - gridW) / 2;
        var offsetY = marginPx + (usableH - gridH) / 2;

        return new GridLayout(cols, rows, spacingPx, offsetX, offsetY, paperW, paperH);
    }
}

public record PaperSize(string Name, int WidthMm, int HeightMm, int WidthPx, int HeightPx);

public record GridLayout(int Cols, int Rows, int SpacingPx, int OffsetX, int OffsetY, int PaperWidthPx, int PaperHeightPx)
{
    public int Total => Cols * Rows;
}

public class PassportPhotoRequest
{
    public string SourceImagePath { get; set; } = string.Empty;
    public string PaperSizeName { get; set; } = "4×6\"";
    public double SpacingMm { get; set; } = 2.0;
    public double MarginMm { get; set; } = 3.0;
    public bool CutMarksEnabled { get; set; } = true;
    public bool CropMarksEnabled { get; set; }
    public bool AutoCorrectFace { get; set; }
    public bool ApplyFormalSuit { get; set; }
    public string BackgroundColor { get; set; } = "#FFFFFF";
    public string SuitColor { get; set; } = "auto";
    public string TieColor { get; set; } = "auto";
}

public class PassportPhotoResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ProcessedPhotoPath { get; set; }
    public byte[]? ProcessedPhotoBytes { get; set; }
    public string? PrintSheetPath { get; set; }
    public int PhotosFitted { get; set; }
    public string? PaperSizeName { get; set; }
}

public class FacePoseAnalysis
{
    public double RollDegrees { get; set; }
    public double YawDegrees { get; set; }
    public double PitchDegrees { get; set; }
    public double FaceCenterX { get; set; }
    public double FaceCenterY { get; set; }
    public PoseSeverity Severity { get; set; }
}

public enum PoseSeverity
{
    None,           // <3° roll, <5° yaw
    MildTilt,       // <15° roll, <10° yaw — local rotation fix
    Moderate,       // <45° yaw — AI correction needed
    Uncorrectable   // >45° yaw — too extreme
}

public class FaceDetectionResult
{
    public double FaceTop { get; set; }
    public double FaceBottom { get; set; }
    public double FaceLeft { get; set; }
    public double FaceRight { get; set; }
    public double EyeLineY { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
}

public class SubjectAnalysis
{
    public string Gender { get; set; } = "neutral";
    public string SkinTone { get; set; } = "medium";
    public string BuildType { get; set; } = "average";
    public bool AlreadyFormal { get; set; }
    public string CurrentClothing { get; set; } = string.Empty;
}

public class CropRectFractions
{
    public double X { get; set; }  // left edge as fraction [0..1]
    public double Y { get; set; }  // top edge as fraction [0..1]
    public double W { get; set; }  // width as fraction [0..1]
    public double H { get; set; }  // height as fraction [0..1]
}
