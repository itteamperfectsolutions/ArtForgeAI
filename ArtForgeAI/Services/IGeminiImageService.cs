namespace ArtForgeAI.Services;

public interface IGeminiImageService
{
    Task<(string? text, byte[] imageBytes)> GenerateImageAsync(string prompt, int width, int height);
    Task<(string? text, byte[] imageBytes)> EditImageAsync(string prompt, List<(byte[] data, string mimeType)> images, int width, int height);
}
