namespace ArtForgeAI.Services;

public record BackgroundRemovalResult(string ColoredImagePath, string TransparentImagePath, byte[] TransparentPngBytes);

public record SmartSelectResult(
    int BboxX, int BboxY, int BboxWidth, int BboxHeight,
    int OriginalWidth, int OriginalHeight,
    string OutlineImagePath, string TransparentImagePath, byte[] TransparentPngBytes);

public interface IBackgroundRemovalService
{
    bool IsAvailable { get; }
    Task<BackgroundRemovalResult> RemoveBackgroundAsync(string sourceImagePath, string backgroundColor = "white");
    Task<string> RecolorBackgroundAsync(string transparentImagePath, string backgroundColor);
    Task<string> RecolorBackgroundFromBytesAsync(byte[] transparentPngBytes, string backgroundColor);
    Task<string> CompositeOverImageAsync(string transparentImagePath, string backgroundImagePath);
    Task<string> CompositeOverImageFromBytesAsync(byte[] transparentPngBytes, string backgroundImagePath);
    Task<SmartSelectResult> DetectSubjectAsync(byte[] imageBytes, int? regionX = null, int? regionY = null, int? regionW = null, int? regionH = null);
    Task<byte[]> CropWithMarginAsync(byte[] imageBytes, int x, int y, int width, int height);

    /// <summary>
    /// Generates a cut-line PNG (black outline on white background) from a transparent subject image.
    /// Used by cutting machines (Silhouette, Cricut, etc.) to define the cut path.
    /// </summary>
    Task<byte[]> GenerateCutLineAsync(byte[] transparentPngBytes);
}
