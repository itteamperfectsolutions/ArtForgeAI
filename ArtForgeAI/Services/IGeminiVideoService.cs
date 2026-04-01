namespace ArtForgeAI.Services;

public interface IGeminiVideoService
{
    /// <summary>Generate a video from a text prompt only.</summary>
    Task<byte[]> GenerateVideoAsync(
        string prompt, string aspectRatio, int durationSeconds,
        Action<int>? onProgress = null, CancellationToken ct = default);

    /// <summary>Generate a video from an image + text prompt.</summary>
    Task<byte[]> GenerateVideoFromImageAsync(
        string prompt, byte[] imageData, string imageMimeType,
        string aspectRatio, int durationSeconds,
        Action<int>? onProgress = null, CancellationToken ct = default);

    /// <summary>Generate a video from a video + text prompt.</summary>
    Task<byte[]> GenerateVideoFromVideoAsync(
        string prompt, byte[] videoData, string videoMimeType,
        string aspectRatio, int durationSeconds,
        Action<int>? onProgress = null, CancellationToken ct = default);
}
