namespace ArtForgeAI.Services;

public interface ITemplateCollageService
{
    /// <summary>
    /// Analyzes a template image to extract color theme, mood, and layout description.
    /// </summary>
    Task<TemplateAnalysis> AnalyzeTemplateAsync(byte[] templateBytes, string mimeType);

    /// <summary>
    /// Processes a single photo to match the template's color theme/mood.
    /// </summary>
    Task<byte[]> ProcessPhotoWithThemeAsync(byte[] photoBytes, string mimeType, TemplateAnalysis theme, int width, int height);

    /// <summary>
    /// Generates the final collage by composing all processed images in the template style.
    /// </summary>
    Task<byte[]> GenerateCollageAsync(byte[] templateBytes, string templateMime,
        List<byte[]> processedPhotos, string photoMime, int width, int height,
        List<string>? slotDescriptions = null);
}

public class TemplateAnalysis
{
    public string ColorTheme { get; set; } = "";
    public string Mood { get; set; } = "";
    public string BackgroundDescription { get; set; } = "";
    public string DecorativeElements { get; set; } = "";
    public string TextElements { get; set; } = "";
    public int PhotoSlotCount { get; set; }
    public string FullAnalysis { get; set; } = "";
}
