using System.Text.Json;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Background removal using Gemini AI image editing.
/// Sends image to Gemini with a prompt to remove background,
/// then processes the result for bounding box and outline generation.
/// </summary>
public sealed class RemoveBgService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOptions _geminiOptions;
    private readonly string _outputDir;
    private readonly ILogger<RemoveBgService> _logger;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(_geminiOptions.ApiKey);

    public RemoveBgService(
        HttpClient httpClient,
        IOptions<GeminiOptions> geminiOptions,
        IWebHostEnvironment env,
        ILogger<RemoveBgService> logger)
    {
        _httpClient = httpClient;
        _geminiOptions = geminiOptions.Value;
        _logger = logger;
        _outputDir = Path.Combine(env.WebRootPath, "generated");
        Directory.CreateDirectory(_outputDir);
    }

    public async Task<SmartSelectResult> DetectSubjectAsync(byte[] imageBytes,
        int? regionX = null, int? regionY = null, int? regionW = null, int? regionH = null)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Gemini API key is not configured");

        // Get original image dimensions
        using var originalImage = Image.Load<Rgba32>(imageBytes);
        var originalWidth = originalImage.Width;
        var originalHeight = originalImage.Height;

        // Call Gemini to remove background
        _logger.LogInformation("Calling Gemini for background removal ({W}x{H}, {Size} bytes)...",
            originalWidth, originalHeight, imageBytes.Length);

        var transparentBytes = await CallGeminiRemoveBgAsync(imageBytes);

        // Load the result
        using var transparentImage = Image.Load<Rgba32>(transparentBytes);

        // Resize to match original if Gemini returned different dimensions
        if (transparentImage.Width != originalWidth || transparentImage.Height != originalHeight)
        {
            _logger.LogInformation("Resizing Gemini output from {Sw}x{Sh} to {Ow}x{Oh}",
                transparentImage.Width, transparentImage.Height, originalWidth, originalHeight);
            transparentImage.Mutate(ctx => ctx.Resize(originalWidth, originalHeight));
        }

        // Ensure we have a proper alpha channel — Gemini may return with solid background
        EnsureTransparency(transparentImage);

        // Re-encode with proper alpha
        {
            using var ms = new MemoryStream();
            var enc = new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };
            transparentImage.SaveAsPng(ms, enc);
            transparentBytes = ms.ToArray();
        }

        // If region hint provided, zero out alpha outside region
        if (regionX.HasValue && regionY.HasValue && regionW.HasValue && regionH.HasValue)
        {
            int rx = Math.Max(0, regionX.Value);
            int ry = Math.Max(0, regionY.Value);
            int rx2 = Math.Min(originalWidth, rx + regionW.Value);
            int ry2 = Math.Min(originalHeight, ry + regionH.Value);

            transparentImage.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < accessor.Width; x++)
                    {
                        if (y < ry || y >= ry2 || x < rx || x >= rx2)
                            row[x] = new Rgba32(row[x].R, row[x].G, row[x].B, 0);
                    }
                }
            });

            using var ms = new MemoryStream();
            var encoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };
            transparentImage.SaveAsPng(ms, encoder);
            transparentBytes = ms.ToArray();
        }

        // Compute bounding box from alpha channel
        int minX = originalWidth, minY = originalHeight, maxX = 0, maxY = 0;
        transparentImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    if (row[x].A >= 128)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        });

        if (maxX < minX) { minX = 0; minY = 0; maxX = originalWidth - 1; maxY = originalHeight - 1; }

        int bboxW = maxX - minX + 1;
        int bboxH = maxY - minY + 1;

        // Generate outline from alpha edges
        using var outlineImage = new Image<Rgba32>(originalWidth, originalHeight, new Rgba32(0, 0, 0, 0));
        var edgeColor = new Rgba32(0, 200, 255, 200);

        var alpha = new byte[originalHeight, originalWidth];
        transparentImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                    alpha[y, x] = row[x].A;
            }
        });

        outlineImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < originalHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < originalWidth; x++)
                {
                    if (alpha[y, x] < 128) continue;

                    bool isEdge = false;
                    if (x > 0 && alpha[y, x - 1] < 128) isEdge = true;
                    else if (x < originalWidth - 1 && alpha[y, x + 1] < 128) isEdge = true;
                    else if (y > 0 && alpha[y - 1, x] < 128) isEdge = true;
                    else if (y < originalHeight - 1 && alpha[y + 1, x] < 128) isEdge = true;

                    if (isEdge)
                    {
                        row[x] = edgeColor;
                        if (x + 1 < originalWidth) row[x + 1] = edgeColor;
                    }
                }
            }
        });

        // Vertical thickening pass
        outlineImage.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < originalHeight - 1; y++)
            {
                var row = accessor.GetRowSpan(y);
                var nextRow = accessor.GetRowSpan(y + 1);
                for (int x = 0; x < originalWidth; x++)
                {
                    if (row[x].A > 0 && nextRow[x].A == 0)
                        nextRow[x] = edgeColor;
                }
            }
        });

        // Save outline and transparent to files
        var rgbaEncoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };

        var outlineFileName = $"{Guid.NewGuid():N}_outline.png";
        var outlinePath = Path.Combine(_outputDir, outlineFileName);
        outlineImage.SaveAsPng(outlinePath, rgbaEncoder);

        var transparentFileName = $"{Guid.NewGuid():N}_transparent.png";
        var transparentPath = Path.Combine(_outputDir, transparentFileName);
        await File.WriteAllBytesAsync(transparentPath, transparentBytes);

        return new SmartSelectResult(
            minX, minY, bboxW, bboxH,
            originalWidth, originalHeight,
            $"/generated/{outlineFileName}", $"/generated/{transparentFileName}", transparentBytes);
    }

    /// <summary>
    /// Calls Gemini image editing to remove the background.
    /// Returns PNG bytes of the subject with background removed.
    /// </summary>
    private async Task<byte[]> CallGeminiRemoveBgAsync(byte[] imageBytes)
    {
        var model = _geminiOptions.ImageModel;
        var fallback = _geminiOptions.FallbackImageModel;

        try
        {
            return await SendGeminiRequestAsync(imageBytes, model);
        }
        catch (Exception ex) when (!string.IsNullOrEmpty(fallback) && fallback != model)
        {
            _logger.LogWarning(ex, "Gemini model {Model} failed for bg removal, trying fallback {Fallback}", model, fallback);
            return await SendGeminiRequestAsync(imageBytes, fallback);
        }
    }

    private async Task<byte[]> SendGeminiRequestAsync(byte[] imageBytes, string model)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_geminiOptions.ApiKey}";

        var base64Image = Convert.ToBase64String(imageBytes);

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new
                            {
                                mime_type = "image/png",
                                data = base64Image
                            }
                        },
                        new
                        {
                            text = "Remove the background from this image completely. " +
                                   "Return ONLY the main human subject (person/people) with a fully transparent background. " +
                                   "Remove ALL background objects including furniture, chairs, tables, walls, decorations, plants, and any other items that are not part of the person's body or clothing. " +
                                   "Keep ONLY the person — their body, face, hair, clothes, and accessories they are wearing. " +
                                   "Do NOT keep any objects the person is sitting on, leaning against, or standing near. " +
                                   "If there is no human subject, extract only the single most prominent foreground object. " +
                                   "Keep the subject exactly as it is with no modifications to colors, details, or proportions."
                        }
                    }
                }
            },
            generationConfig = new
            {
                responseModalities = new[] { "IMAGE" },
                imageConfig = new
                {
                    outputMimeType = "image/png"
                }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending background removal request to Gemini model {Model}", model);

        var response = await _httpClient.PostAsync(url, content);
        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini API error ({Model}): {Status} - {Body}", model, response.StatusCode, responseJson);
            throw new HttpRequestException($"Gemini API error ({model}): {response.StatusCode}");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var candidates = doc.RootElement.GetProperty("candidates");
        if (candidates.GetArrayLength() == 0)
            throw new InvalidOperationException($"Gemini ({model}) returned no candidates for background removal.");

        var firstCandidate = candidates[0];

        if (!firstCandidate.TryGetProperty("content", out var candidateContent)
            || !candidateContent.TryGetProperty("parts", out var parts))
        {
            var reason = firstCandidate.TryGetProperty("finishReason", out var fr)
                ? fr.GetString() : "unknown";
            throw new InvalidOperationException(
                $"Gemini ({model}) blocked the background removal request (reason: {reason}). Try a different image.");
        }

        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("inlineData", out var inlineData)
                || part.TryGetProperty("inline_data", out inlineData))
            {
                var base64 = inlineData.GetProperty("data").GetString();
                if (base64 is not null)
                {
                    _logger.LogInformation("Gemini ({Model}) returned background-removed image", model);
                    return Convert.FromBase64String(base64);
                }
            }
        }

        throw new InvalidOperationException($"Gemini ({model}) did not return an image for background removal.");
    }

    /// <summary>
    /// Ensures the image has proper transparency. Gemini may return the subject on a solid
    /// background instead of transparent. This detects the dominant corner color and removes it.
    /// </summary>
    private static void EnsureTransparency(Image<Rgba32> image)
    {
        // Check if image already has meaningful alpha
        bool hasAlpha = false;
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height && !hasAlpha; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width && !hasAlpha; x++)
                {
                    if (row[x].A < 240) hasAlpha = true;
                }
            }
        });

        if (hasAlpha) return; // Already has transparency

        // Sample corners to find the background color
        var w = image.Width;
        var h = image.Height;
        var corners = new List<Rgba32>();

        image.ProcessPixelRows(accessor =>
        {
            var topRow = accessor.GetRowSpan(0);
            var botRow = accessor.GetRowSpan(h - 1);
            corners.Add(topRow[0]);
            corners.Add(topRow[w - 1]);
            corners.Add(botRow[0]);
            corners.Add(botRow[w - 1]);
            // Sample a few more edge pixels for robustness
            corners.Add(topRow[w / 2]);
            corners.Add(botRow[w / 2]);
            var midRow = accessor.GetRowSpan(h / 2);
            corners.Add(midRow[0]);
            corners.Add(midRow[w - 1]);
        });

        // Find most common corner color
        var bgColor = corners
            .GroupBy(c => (c.R / 8, c.G / 8, c.B / 8)) // quantize to reduce noise
            .OrderByDescending(g => g.Count())
            .First()
            .First();

        // Remove background color with tolerance
        const int tolerance = 40;
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < accessor.Width; x++)
                {
                    var px = row[x];
                    int dr = Math.Abs(px.R - bgColor.R);
                    int dg = Math.Abs(px.G - bgColor.G);
                    int db = Math.Abs(px.B - bgColor.B);
                    int dist = dr + dg + db;

                    if (dist <= tolerance)
                    {
                        row[x] = new Rgba32(px.R, px.G, px.B, 0);
                    }
                    else if (dist <= tolerance * 2)
                    {
                        // Feather edges for smooth transition
                        byte a = (byte)Math.Min(255, (dist - tolerance) * 255 / tolerance);
                        row[x] = new Rgba32(px.R, px.G, px.B, a);
                    }
                }
            }
        });
    }

    public async Task<byte[]> CropWithMarginAsync(byte[] imageBytes, int x, int y, int width, int height)
    {
        return await Task.Run(() =>
        {
            using var image = Image.Load<Rgba32>(imageBytes);

            int cx = Math.Max(0, x);
            int cy = Math.Max(0, y);
            int cw = Math.Min(width, image.Width - cx);
            int ch = Math.Min(height, image.Height - cy);

            if (cw <= 0 || ch <= 0)
                throw new ArgumentException("Crop region is outside image bounds");

            image.Mutate(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(cx, cy, cw, ch)));

            var encoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha, BitDepth = PngBitDepth.Bit8 };
            using var ms = new MemoryStream();
            image.SaveAsPng(ms, encoder);
            return ms.ToArray();
        });
    }

    public async Task<byte[]> GenerateCutLineAsync(byte[] transparentPngBytes)
    {
        return await Task.Run(() => CutLineGenerator.Generate(transparentPngBytes));
    }

    public async Task<byte[]> GenerateCutLineAsync(byte[] transparentPngBytes, int markInset, int markSize)
    {
        return await Task.Run(() => CutLineGenerator.Generate(transparentPngBytes, markInset, markSize));
    }

    /// <summary>
    /// Generates a print file: original image cropped to subject+margin with registration marks.
    /// </summary>
    public async Task<byte[]> GeneratePrintFileAsync(byte[] originalImageBytes, int x, int y, int w, int h, int markInset, int markSize)
    {
        return await Task.Run(() =>
        {
            using var image = Image.Load<Rgba32>(originalImageBytes);

            int cx = Math.Max(0, x);
            int cy = Math.Max(0, y);
            int cw = Math.Min(w, image.Width - cx);
            int ch = Math.Min(h, image.Height - cy);

            if (cw <= 0 || ch <= 0)
                throw new ArgumentException("Crop region is outside image bounds");

            image.Mutate(ctx => ctx.Crop(new SixLabors.ImageSharp.Rectangle(cx, cy, cw, ch)));

            if (markSize > 0 && markInset > 0)
                CutLineGenerator.DrawRegistrationMarks(image, markInset, markSize, new Rgba32(0, 0, 0, 255));

            var encoder = new PngEncoder { ColorType = PngColorType.Rgb, BitDepth = PngBitDepth.Bit8 };
            using var ms = new MemoryStream();
            image.SaveAsPng(ms, encoder);
            return ms.ToArray();
        });
    }
}
