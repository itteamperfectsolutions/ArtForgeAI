using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public class Mockup3DService
{
    private readonly IImageGenerationService _imageGen;
    private readonly IImageStorageService _imageStorage;
    private readonly ILogger<Mockup3DService> _logger;

    public Mockup3DService(
        IImageGenerationService imageGen,
        IImageStorageService imageStorage,
        ILogger<Mockup3DService> logger)
    {
        _imageGen = imageGen;
        _imageStorage = imageStorage;
        _logger = logger;
    }

    public async Task<GenerationResult> GenerateAi3DMockup(
        string snapshotBase64,
        string productName,
        string productColor,
        string userId)
    {
        var snapshotBytes = Convert.FromBase64String(snapshotBase64);
        var snapshotFileName = $"3d_snapshot_{Guid.NewGuid():N}.png";
        var snapshotPath = await _imageStorage.SaveImageFromBytesAsync(
            BinaryData.FromBytes(snapshotBytes), snapshotFileName);

        try
        {
            var prompt = $"Transform this 3D product render into a photorealistic product photograph. " +
                         $"The product is a {productColor} {productName}. " +
                         $"Keep the exact logo/design placement and appearance from the image. " +
                         $"Add realistic textures, reflections, ambient occlusion, and studio lighting. " +
                         $"Clean white background, professional product photography style. " +
                         $"High quality, sharp details.";

            var request = new GenerationRequest
            {
                Prompt = prompt,
                ReferenceImagePaths = [snapshotPath],
                Provider = ImageProvider.Gemini,
                EnhancePrompt = false,
                Width = 1536,
                Height = 1024,
                UserId = userId
            };

            return await _imageGen.GenerateImageAsync(request);
        }
        finally
        {
            try { await _imageStorage.DeleteImageAsync(snapshotPath); }
            catch { /* ignore cleanup failure */ }
        }
    }
}
