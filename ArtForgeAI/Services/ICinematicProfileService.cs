using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface ICinematicProfileService
{
    bool IsMultiFaceStyle(string styleName);
    Task<GenerationResult?> ProcessAsync(string imagePath, string prompt, int width, int height);
}
