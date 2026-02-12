namespace ArtForgeAI.Services;

public class GeminiOptions
{
    public const string SectionName = "Gemini";
    public string ApiKey { get; set; } = string.Empty;
    public string ImageModel { get; set; } = "gemini-2.5-flash-image";
}
