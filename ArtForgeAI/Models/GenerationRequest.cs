namespace ArtForgeAI.Models;

public class GenerationRequest
{
    public string Prompt { get; set; } = string.Empty;
    public ImageSize Size { get; set; } = ImageSize.Square;
    public bool EnhancePrompt { get; set; } = true;
    public string? ReferenceImagePath { get; set; }
}
