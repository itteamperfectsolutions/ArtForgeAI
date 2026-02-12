namespace ArtForgeAI.Services;

public record BackgroundRemovalResult(string ColoredImagePath, string TransparentImagePath, byte[] TransparentPngBytes);

public interface IBackgroundRemovalService
{
    bool IsAvailable { get; }
    Task<BackgroundRemovalResult> RemoveBackgroundAsync(string sourceImagePath, string backgroundColor = "white");
    Task<string> RecolorBackgroundAsync(string transparentImagePath, string backgroundColor);
    Task<string> RecolorBackgroundFromBytesAsync(byte[] transparentPngBytes, string backgroundColor);
    Task<string> CompositeOverImageAsync(string transparentImagePath, string backgroundImagePath);
    Task<string> CompositeOverImageFromBytesAsync(byte[] transparentPngBytes, string backgroundImagePath);
}
