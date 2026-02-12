using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IGeminiImageService
{
    Task<(string? text, byte[] imageBytes)> GenerateImageAsync(string prompt, ImageSize size);
    Task<(string? text, byte[] imageBytes)> EditImageAsync(string prompt, List<(byte[] data, string mimeType)> images, ImageSize size);
}
