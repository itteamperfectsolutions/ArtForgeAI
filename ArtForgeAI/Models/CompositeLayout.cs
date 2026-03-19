namespace ArtForgeAI.Models;

/// <summary>Structured layout for programmatic collage compositing (no AI in final step)</summary>
public class CompositeLayout
{
    public List<string> BackgroundColors { get; set; } = new() { "#1a1a2e", "#16213e" };
    public List<SlotPlacement> Slots { get; set; } = new();
    public float NameY { get; set; } = 0.88f;
    public float OccasionY { get; set; } = 0.92f;
    public float MessageY { get; set; } = 0.96f;
    public string NameColor { get; set; } = "#FFD700";
    public string TextColor { get; set; } = "#FFFFFF";
    public int NameFontSize { get; set; } = 52;
    public int OccasionFontSize { get; set; } = 26;
    public int MessageFontSize { get; set; } = 20;
}

public class SlotPlacement
{
    /// <summary>Center X as fraction (0-1) of canvas width</summary>
    public float X { get; set; }

    /// <summary>Center Y as fraction (0-1) of canvas height</summary>
    public float Y { get; set; }

    /// <summary>Width as fraction of canvas width</summary>
    public float Width { get; set; }

    /// <summary>Height as fraction of canvas height</summary>
    public float Height { get; set; }

    /// <summary>Rotation in degrees</summary>
    public float Rotation { get; set; }

    /// <summary>Opacity 0-1</summary>
    public float Opacity { get; set; } = 1.0f;

    /// <summary>Layering order (higher = on top)</summary>
    public int ZIndex { get; set; }

    /// <summary>Shape: rect, circle, rounded</summary>
    public string Shape { get; set; } = "rect";

    /// <summary>Border width in pixels</summary>
    public float BorderWidth { get; set; }

    /// <summary>Border color as hex</summary>
    public string BorderColor { get; set; } = "#FFFFFF";

    /// <summary>Apply grayscale filter</summary>
    public bool Grayscale { get; set; }

    /// <summary>Corner radius in pixels (for rounded shape)</summary>
    public float CornerRadius { get; set; } = 20f;
}
