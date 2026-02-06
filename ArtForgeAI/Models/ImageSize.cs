namespace ArtForgeAI.Models;

public enum ImageSize
{
    Square,
    Landscape,
    Portrait
}

public static class ImageSizeExtensions
{
    public static string ToDallESize(this ImageSize size) => size switch
    {
        ImageSize.Square => "1024x1024",
        ImageSize.Landscape => "1792x1024",
        ImageSize.Portrait => "1024x1792",
        _ => "1024x1024"
    };

    public static string ToDisplayName(this ImageSize size) => size switch
    {
        ImageSize.Square => "Square (1024×1024)",
        ImageSize.Landscape => "Landscape (1792×1024)",
        ImageSize.Portrait => "Portrait (1024×1792)",
        _ => "Square (1024×1024)"
    };

    public static (int Width, int Height) ToDimensions(this ImageSize size) => size switch
    {
        ImageSize.Square => (1024, 1024),
        ImageSize.Landscape => (1792, 1024),
        ImageSize.Portrait => (1024, 1792),
        _ => (1024, 1024)
    };
}
