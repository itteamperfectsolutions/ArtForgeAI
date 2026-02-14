namespace ArtForgeAI.Models;

public class GenerationRequest
{
    public string Prompt { get; set; } = string.Empty;
    public int Width { get; set; } = 1024;
    public int Height { get; set; } = 1024;
    public string SizeName { get; set; } = "Square";
    public bool EnhancePrompt { get; set; } = true;
    public List<string> ReferenceImagePaths { get; set; } = [];
    public ImageProvider Provider { get; set; } = ImageProvider.OpenAI;

    /// <summary>Primary reference image (first uploaded), or null if none.</summary>
    public string? ReferenceImagePath => ReferenceImagePaths.Count > 0 ? ReferenceImagePaths[0] : null;

    public bool HasReferenceImages => ReferenceImagePaths.Count > 0;
    public bool ForceCloudProvider { get; set; }
}
