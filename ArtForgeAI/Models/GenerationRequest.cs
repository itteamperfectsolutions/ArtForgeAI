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

    /// <summary>
    /// When true, skip the auto-prepended "Combine subjects from all N images" text
    /// for multi-image requests. Used when extra images are layout references (e.g. calendar),
    /// not additional subjects to merge.
    /// </summary>
    public bool SkipMultiImageComposition { get; set; }
    public string UserId { get; set; } = "default";
}
