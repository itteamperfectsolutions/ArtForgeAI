using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IPromptEnhancerService
{
    Task<PromptEnhancementResult> EnhancePromptAsync(string rawPrompt, string? referenceImagePath = null);
}
