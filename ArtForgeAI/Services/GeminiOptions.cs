namespace ArtForgeAI.Services;

public class GeminiOptions
{
    public const string SectionName = "Gemini";
    public string ApiKey { get; set; } = string.Empty;
    public string ImageModel { get; set; } = "gemini-2.5-flash-image";
    public string AnalysisModel { get; set; } = "gemini-2.0-flash";
}
