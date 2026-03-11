using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Enhances images using Gemini AI by sending the whole image at once.
/// Shrinks the image to fit Gemini's limits while preserving aspect ratio,
/// gets the AI-enhanced result, then upscales back to the original dimensions.
/// </summary>
public class TiledGeminiEnhanceService
{
    private readonly IGeminiImageService _gemini;
    private readonly ILogger<TiledGeminiEnhanceService> _logger;

    // Max dimension to send to Gemini — keeps payload small and within limits
    private const int MaxSendDimension = 1024;

    public TiledGeminiEnhanceService(
        IGeminiImageService gemini,
        ILogger<TiledGeminiEnhanceService> logger)
    {
        _gemini = gemini;
        _logger = logger;
    }

    /// <summary>
    /// Enhances the whole image using Gemini in one shot.
    /// Shrinks to fit Gemini limits, enhances, then upscales back to original size.
    /// </summary>
    public async Task<byte[]> EnhanceAsync(
        byte[] sourceBytes,
        int sourceWidth,
        int sourceHeight,
        IProgress<(int current, int total, string status)>? progress = null)
    {
        progress?.Report((0, 3, "Preparing image for AI enhancement..."));

        using var source = Image.Load<Rgba32>(sourceBytes);

        // Calculate shrink dimensions preserving aspect ratio
        double scale = 1.0;
        int maxDim = Math.Max(sourceWidth, sourceHeight);
        if (maxDim > MaxSendDimension)
        {
            scale = (double)MaxSendDimension / maxDim;
        }

        int sendWidth = (int)Math.Round(sourceWidth * scale);
        int sendHeight = (int)Math.Round(sourceHeight * scale);

        // Ensure even dimensions (some codecs prefer this)
        sendWidth = Math.Max(2, sendWidth);
        sendHeight = Math.Max(2, sendHeight);

        _logger.LogInformation(
            "Whole-image Gemini enhance: {OrigW}x{OrigH} -> shrink to {SendW}x{SendH} (scale {Scale:F3}), will upscale back after",
            sourceWidth, sourceHeight, sendWidth, sendHeight, scale);

        // Shrink the image for sending
        byte[] sendBytes;
        if (scale < 1.0)
        {
            using var shrunk = source.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(sendWidth, sendHeight),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3
            }));
            using var ms = new MemoryStream();
            shrunk.SaveAsPng(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed });
            sendBytes = ms.ToArray();
        }
        else
        {
            // Image already small enough, send as-is
            using var ms = new MemoryStream();
            source.SaveAsPng(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed });
            sendBytes = ms.ToArray();
        }

        progress?.Report((1, 3, "AI is enhancing your image..."));

        // Send the whole image to Gemini
        byte[]? enhancedSmallBytes = null;
        try
        {
            var (_, resultBytes) = await _gemini.EditImageAsync(
                "Enhance this image. Improve sharpness, reduce noise, restore clarity, and improve colors. " +
                "Output the EXACT same image content with better quality. " +
                "Do NOT change, move, add, or remove anything. Keep every detail and the layout exactly in place.",
                new List<(byte[] data, string mimeType)> { (sendBytes, "image/png") },
                sendWidth, sendHeight);

            if (resultBytes is { Length: > 0 })
                enhancedSmallBytes = resultBytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini whole-image enhance failed, falling back to sharpen");
        }

        progress?.Report((2, 3, "Upscaling back to original size..."));

        // Load enhanced result or fall back to sharpened original
        Image<Rgba32> enhanced;
        if (enhancedSmallBytes != null)
        {
            enhanced = Image.Load<Rgba32>(enhancedSmallBytes);
        }
        else
        {
            // Fallback: just sharpen the original
            enhanced = source.Clone(ctx => ctx.GaussianSharpen(1.5f));
        }

        using (enhanced)
        {
            // Upscale back to original dimensions
            if (enhanced.Width != sourceWidth || enhanced.Height != sourceHeight)
            {
                enhanced.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(sourceWidth, sourceHeight),
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3
                }));
            }

            progress?.Report((3, 3, "Saving enhanced image..."));

            using var resultMs = new MemoryStream();
            enhanced.SaveAsPng(resultMs);
            return resultMs.ToArray();
        }
    }
}
