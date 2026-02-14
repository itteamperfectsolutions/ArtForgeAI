namespace ArtForgeAI.Services;

public interface IColorEnhancementService
{
    bool IsAvailable { get; }

    /// <summary>
    /// Enhances image colors/lighting using local SCI model.
    /// Returns the web-relative path to the enhanced image.
    /// </summary>
    Task<string> EnhanceColorsAsync(string sourceImagePath);
}
