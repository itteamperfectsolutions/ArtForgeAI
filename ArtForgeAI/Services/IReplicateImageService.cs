namespace ArtForgeAI.Services;

public interface IReplicateImageService
{
    Task<byte[]> GenerateImageAsync(string prompt, int width, int height);
    Task<byte[]> EditImageAsync(string prompt, byte[] imageBytes, string mimeType, int width, int height);
}
