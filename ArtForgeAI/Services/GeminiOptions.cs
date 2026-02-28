namespace ArtForgeAI.Services;

public class GeminiOptions
{
    public const string SectionName = "Gemini";
    public string ApiKey { get; set; } = string.Empty;
    public string ImageModel { get; set; } = "gemini-3.1-flash-image-preview";
    public string FallbackImageModel { get; set; } = "gemini-2.5-flash-image";
    public string AnalysisModel { get; set; } = "gemini-3.1-flash-image-preview";
    public string FallbackAnalysisModel { get; set; } = "gemini-2.5-flash-image";
}
