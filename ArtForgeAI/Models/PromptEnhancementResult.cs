namespace ArtForgeAI.Models;

public class PromptEnhancementResult
{
    public bool Success { get; set; }
    public string EnhancedPrompt { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
