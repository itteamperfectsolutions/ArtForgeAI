namespace ArtForgeAI.Services;

public class OpenAiOptions
{
    public const string SectionName = "OpenAI";
    public string ApiKey { get; set; } = string.Empty;
    public string PromptModel { get; set; } = "gpt-4o";
    public string ImageModel { get; set; } = "dall-e-3";
    public string ImageEditModel { get; set; } = "gpt-image-1";
}
