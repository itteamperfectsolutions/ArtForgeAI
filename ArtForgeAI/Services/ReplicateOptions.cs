namespace ArtForgeAI.Services;

public class ReplicateOptions
{
    public const string SectionName = "Replicate";
    public string ApiToken { get; set; } = string.Empty;
    public string ImageModel { get; set; } = "black-forest-labs/flux-1.1-pro";
    public string ImageEditModel { get; set; } = "black-forest-labs/flux-kontext-max";
}
