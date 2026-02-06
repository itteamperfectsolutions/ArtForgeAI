using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IImageGenerationService
{
    Task<GenerationResult> GenerateImageAsync(GenerationRequest request);
}
