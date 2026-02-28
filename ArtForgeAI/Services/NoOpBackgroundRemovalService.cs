namespace ArtForgeAI.Services;

/// <summary>
/// No-op stub — background removal is handled by cloud APIs (Remove.bg).
/// This keeps Home.razor and StyleTransfer.razor compiling; their IsAvailable
/// guards hide all bg removal UI when this stub is registered.
/// </summary>
public sealed class NoOpBackgroundRemovalService : IBackgroundRemovalService
{
    public bool IsAvailable => false;

    public Task<BackgroundRemovalResult> RemoveBackgroundAsync(string sourceImagePath, string backgroundColor = "white")
        => throw new NotSupportedException("Local background removal is not available. Use Remove.bg cloud API.");

    public Task<string> RecolorBackgroundAsync(string transparentImagePath, string backgroundColor)
        => throw new NotSupportedException();

    public Task<string> RecolorBackgroundFromBytesAsync(byte[] transparentPngBytes, string backgroundColor)
        => throw new NotSupportedException();

    public Task<string> CompositeOverImageAsync(string transparentImagePath, string backgroundImagePath)
        => throw new NotSupportedException();

    public Task<string> CompositeOverImageFromBytesAsync(byte[] transparentPngBytes, string backgroundImagePath)
        => throw new NotSupportedException();

    public Task<SmartSelectResult> DetectSubjectAsync(byte[] imageBytes, int? regionX = null, int? regionY = null, int? regionW = null, int? regionH = null)
        => throw new NotSupportedException();

    public Task<byte[]> CropWithMarginAsync(byte[] imageBytes, int x, int y, int width, int height)
        => throw new NotSupportedException();

    public Task<byte[]> GenerateCutLineAsync(byte[] transparentPngBytes)
        => throw new NotSupportedException();
}
