using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Pixel-faithful image enhancement using ImageSharp filters.
/// No AI generation — the image content stays identical.
/// </summary>
public static class ImageEnhanceHelper
{
    /// <summary>
    /// Applies sharpening, contrast, saturation, and brightness improvements in-place.
    /// </summary>
    public static void ApplyEnhancements(Image<Rgba32> image)
    {
        image.Mutate(ctx =>
        {
            ctx.GaussianSharpen(1.5f);
            ctx.Contrast(1.08f);
            ctx.Saturate(1.1f);
            ctx.Brightness(1.03f);
        });
    }

    /// <summary>
    /// Resizes an image to exact target dimensions using high-quality Lanczos3.
    /// </summary>
    public static void ResizeExact(Image<Rgba32> image, int width, int height)
    {
        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3
        }));
    }

    /// <summary>
    /// Saves image to PNG byte array.
    /// </summary>
    public static byte[] SaveToPngBytes(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }
}
