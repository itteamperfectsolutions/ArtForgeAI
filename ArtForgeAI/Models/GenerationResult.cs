namespace ArtForgeAI.Models;

public class GenerationResult
{
    public bool Success { get; set; }
    public string? ImageUrl { get; set; }
    public string? LocalImagePath { get; set; }
    public string? EnhancedPrompt { get; set; }
    public string? ErrorMessage { get; set; }
    public int? GenerationId { get; set; }
    public bool WasLocalProcessing { get; set; }
    public string? TransparentImagePath { get; set; }
    public byte[]? TransparentPngBytes { get; set; }
}
