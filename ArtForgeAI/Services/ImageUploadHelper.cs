using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Shared helper for compressing oversized upload images by resizing dimensions
/// without reducing quality. Used across all pages that accept image uploads.
/// </summary>
public static class ImageUploadHelper
{
    public const long DefaultMaxUploadSize = 10 * 1024 * 1024; // 10 MB
    public const long MaxReadSize = 50 * 1024 * 1024; // 50 MB (allow reading for compression)

    /// <summary>
    /// Compresses an image by reducing its dimensions so the resulting file
    /// fits within the target size. Uses high-quality Lanczos3 resampling.
    /// Returns a MemoryStream positioned at 0, ready for saving.
    /// </summary>
    public static MemoryStream CompressToFit(byte[] rawBytes, string fileName, long targetSize = DefaultMaxUploadSize)
    {
        using var img = Image.Load<Rgba32>(rawBytes);

        double scaleFactor = Math.Sqrt((double)targetSize / rawBytes.Length) * 0.9; // 10% safety margin
        int newW = Math.Max(1, (int)(img.Width * scaleFactor));
        int newH = Math.Max(1, (int)(img.Height * scaleFactor));
        img.Mutate(ctx => ctx.Resize(newW, newH, KnownResamplers.Lanczos3));

        var outMs = new MemoryStream();
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg")
            img.SaveAsJpeg(outMs, new JpegEncoder { Quality = 95 });
        else
            img.SaveAsPng(outMs);

        outMs.Position = 0;
        return outMs;
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }
}
