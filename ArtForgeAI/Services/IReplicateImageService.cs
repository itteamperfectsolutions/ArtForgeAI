using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface IReplicateImageService
{
    Task<byte[]> GenerateImageAsync(string prompt, ImageSize size);
    Task<byte[]> EditImageAsync(string prompt, byte[] imageBytes, string mimeType, ImageSize size);
}
