using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ArtForgeAI.Services;

/// <summary>
/// Generates a cut-line image from a transparent subject PNG.
/// Output: black outline (2-3px) on white background — ready for cutting machines.
/// </summary>
public static class CutLineGenerator
{
    /// <summary>
    /// Draws crosshair (+) registration marks at the 4 corners of an image.
    /// Used to align print and cut files on cutting machines (Graphtec, Cricut, Silhouette).
    /// </summary>
    public static void DrawRegistrationMarks(Image<Rgba32> image, int markInset, int markSize, Rgba32 color)
    {
        int w = image.Width;
        int h = image.Height;
        int half = markSize / 2;

        // 4 corner mark centers
        var centers = new (int cx, int cy)[]
        {
            (markInset, markInset),
            (w - markInset, markInset),
            (markInset, h - markInset),
            (w - markInset, h - markInset)
        };

        image.ProcessPixelRows(accessor =>
        {
            foreach (var (cx, cy) in centers)
            {
                // Horizontal arm
                for (int x = cx - half; x <= cx + half; x++)
                {
                    if (x < 0 || x >= w) continue;
                    if (cy >= 0 && cy < h) accessor.GetRowSpan(cy)[x] = color;
                    if (cy + 1 >= 0 && cy + 1 < h) accessor.GetRowSpan(cy + 1)[x] = color;
                }
                // Vertical arm
                for (int y = cy - half; y <= cy + half; y++)
                {
                    if (y < 0 || y >= h) continue;
                    if (cx >= 0 && cx < w) accessor.GetRowSpan(y)[cx] = color;
                    if (cx + 1 >= 0 && cx + 1 < w) accessor.GetRowSpan(y)[cx + 1] = color;
                }
            }
        });
    }

    public static byte[] Generate(byte[] transparentPngBytes, int markInset = 0, int markSize = 0)
    {
        using var source = Image.Load<Rgba32>(transparentPngBytes);
        var w = source.Width;
        var h = source.Height;

        // Extract alpha channel
        var alpha = new byte[h, w];
        source.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                    alpha[y, x] = row[x].A;
            }
        });

        // White background, black outline
        using var result = new Image<Rgba32>(w, h, new Rgba32(255, 255, 255, 255));
        var black = new Rgba32(0, 0, 0, 255);
        const byte threshold = 128;

        // Edge detection: find pixels where foreground meets background
        result.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    if (alpha[y, x] < threshold) continue;

                    bool isEdge = false;
                    if (x > 0 && alpha[y, x - 1] < threshold) isEdge = true;
                    else if (x < w - 1 && alpha[y, x + 1] < threshold) isEdge = true;
                    else if (y > 0 && alpha[y - 1, x] < threshold) isEdge = true;
                    else if (y < h - 1 && alpha[y + 1, x] < threshold) isEdge = true;

                    if (isEdge)
                    {
                        // 3px thick outline for clean tracing by cutting software
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                                    accessor.GetRowSpan(ny)[nx] = black;
                            }
                    }
                }
            }
        });

        // Registration marks for cutter alignment
        if (markSize > 0 && markInset > 0)
            DrawRegistrationMarks(result, markInset, markSize, black);

        var encoder = new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 };
        using var ms = new MemoryStream();
        result.SaveAsPng(ms, encoder);
        return ms.ToArray();
    }
}
