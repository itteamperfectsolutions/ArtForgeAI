namespace ArtForgeAI.Services;

public interface IImageEnhancerService
{
    bool IsAvailable { get; }

    /// <summary>
    /// Enhances an image using local Real-ESRGAN 4x upscaling.
    /// Returns the web-relative path to the enhanced image.
    /// </summary>
    Task<string> EnhanceImageAsync(string sourceImagePath, IProgress<int>? progress = null);
}
