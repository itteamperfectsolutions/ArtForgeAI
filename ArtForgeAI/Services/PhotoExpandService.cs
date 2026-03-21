using ArtForgeAI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

/// <summary>
/// Orchestrates photo size expansion: composite → Gemini outpainting → upscale.
/// </summary>
public class PhotoExpandService
{
    private readonly IGeminiImageService _gemini;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PhotoExpandService> _logger;

    public PhotoExpandService(
        IGeminiImageService gemini,
        IWebHostEnvironment env,
        ILogger<PhotoExpandService> logger)
    {
        _gemini = gemini;
        _env = env;
        _logger = logger;
    }

    public async Task<PhotoExpandResult> ExpandAsync(
        PhotoExpandRequest request,
        IProgress<string>? status = null)
    {
        try
        {
            var (tgtW, tgtH) = PhotoExpandConfig.GetPixels(request.TargetSize, request.Landscape);

            // Canvas wrap: bleed added AFTER AI expansion via edge mirroring
            int wrapPx = (int)Math.Round(request.CanvasWrapInches * PhotoExpandConfig.Dpi);
            int totalW = tgtW + 2 * wrapPx;
            int totalH = tgtH + 2 * wrapPx;

            // Source size = actual uploaded image dimensions
            int srcW, srcH;
            using (var srcImg = Image.Load<Rgba32>(request.SourceBytes))
            {
                srcW = srcImg.Width;
                srcH = srcImg.Height;
            }

            _logger.LogInformation("Expand: {SrcW}x{SrcH} → {TgtW}x{TgtH} (wrap={WrapPx}px, total={TotalW}x{TotalH}), pos=({PosX},{PosY})",
                srcW, srcH, tgtW, tgtH, wrapPx, totalW, totalH, request.PosX, request.PosY);

            // 1. Best-fit upscale source to 96% of the TARGET area (not including wrap)
            //    This ensures the source image stays within the printable area
            status?.Report("Preparing image for AI...");
            int outW = wrapPx > 0 ? totalW : tgtW;
            int outH = wrapPx > 0 ? totalH : tgtH;
            var (bestFitBytes, fitW, fitH) = BestFitScale(request.SourceBytes, srcW, srcH, tgtW, tgtH);

            _logger.LogInformation("Best-fit scaled: {SrcW}x{SrcH} → {FitW}x{FitH} for target {TgtW}x{TgtH} (output {OutW}x{OutH})",
                srcW, srcH, fitW, fitH, tgtW, tgtH, outW, outH);

            // 2. Downscale for Gemini API limits if needed
            var (geminiSourceBytes, geminiW, geminiH) = DownscaleForGemini(bestFitBytes, fitW, fitH, outW, outH);

            // 3. Single Gemini call to expand to full output size (target + wrap)
            status?.Report("Expanding with AI (this may take a moment)...");
            var prompt = BuildPrompt(fitW, fitH, outW, outH, request.PosX, request.PosY, request.PromptHint);
            var images = new List<(byte[] data, string mimeType)>
            {
                (geminiSourceBytes, "image/png")
            };

            var (_, geminiResultBytes) = await _gemini.EditImageAsync(prompt, images, outW, outH);

            // 4. Resize to exact output dimensions
            status?.Report("Resizing to print dimensions...");
            byte[] finalBytes = ResizeToTarget(geminiResultBytes, outW, outH);

            // 5. Draw wrap guide lines on the full image
            if (wrapPx > 0)
            {
                finalBytes = DrawWrapGuides(finalBytes, totalW, totalH, wrapPx);
            }

            // 5. Save result
            status?.Report("Saving result...");
            var fileName = $"{Guid.NewGuid():N}_expanded.png";
            var outputDir = Path.Combine(_env.WebRootPath, "generated");
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, fileName);
            await File.WriteAllBytesAsync(outputPath, finalBytes);

            var webPath = $"generated/{fileName}";

            return new PhotoExpandResult
            {
                Success = true,
                ResultBytes = finalBytes,
                ResultPath = webPath,
                ResultWidthPx = totalW,
                ResultHeightPx = totalH,
                TargetWidthPx = tgtW,
                TargetHeightPx = tgtH,
                WrapPx = wrapPx,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Photo expand failed");
            return new PhotoExpandResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    // 5mm minimum headroom at 300 DPI ≈ 59 pixels on each side
    private const int MinHeadroomPx = 59;

    /// <summary>
    /// Best-fit scale the source image to fill as much of the target as possible.
    /// Ensures at least 5mm (59px) headroom on every side for safe printing.
    /// </summary>
    private (byte[] bytes, int w, int h) BestFitScale(byte[] sourceBytes, int srcW, int srcH, int tgtW, int tgtH)
    {
        // Two constraints:
        // 1) 96% of best-fit — leaves a small margin for AI to blend edges
        // 2) Absolute minimum 5mm (59px) headroom on each side
        double blendScale = Math.Min((double)tgtW / srcW, (double)tgtH / srcH) * 0.96;
        double headroomScale = Math.Min(
            (double)(tgtW - 2 * MinHeadroomPx) / srcW,
            (double)(tgtH - 2 * MinHeadroomPx) / srcH);

        double scale = Math.Min(blendScale, headroomScale);
        if (scale <= 0) scale = blendScale; // fallback for extremely small targets

        int fitW = (int)Math.Round(srcW * scale);
        int fitH = (int)Math.Round(srcH * scale);

        // If already at or larger than target on both axes, no scaling needed
        if (fitW == srcW && fitH == srcH)
        {
            return (sourceBytes, srcW, srcH);
        }

        using var img = Image.Load<Rgba32>(sourceBytes);
        img.Mutate(ctx => ctx.Resize(fitW, fitH, KnownResamplers.Lanczos3));

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return (ms.ToArray(), fitW, fitH);
    }

    /// <summary>
    /// Downscale source image so it's within Gemini's processing limits.
    /// Returns the image bytes and its dimensions.
    /// </summary>
    private (byte[] bytes, int w, int h) DownscaleForGemini(byte[] sourceBytes, int srcW, int srcH, int tgtW, int tgtH)
    {
        int maxSide = PhotoExpandConfig.GeminiMaxSide;

        // Scale if source exceeds Gemini limits
        if (srcW <= maxSide && srcH <= maxSide)
        {
            return (sourceBytes, srcW, srcH);
        }

        double scale = Math.Min((double)maxSide / srcW, (double)maxSide / srcH);
        int newW = (int)Math.Round(srcW * scale);
        int newH = (int)Math.Round(srcH * scale);

        using var img = Image.Load<Rgba32>(sourceBytes);
        img.Mutate(ctx => ctx.Resize(newW, newH, KnownResamplers.Lanczos3));

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return (ms.ToArray(), newW, newH);
    }

    private static string BuildPrompt(int srcW, int srcH, int tgtW, int tgtH, double posX, double posY, string? userHint)
    {
        // Describe the expansion direction based on position
        var directions = new List<string>();
        double ratioW = (double)tgtW / srcW;
        double ratioH = (double)tgtH / srcH;

        if (posY >= 0.3) directions.Add("above");
        if (posY <= 0.7) directions.Add("below");
        if (posX >= 0.3) directions.Add("to the left");
        if (posX <= 0.7) directions.Add("to the right");

        var directionText = directions.Count > 0
            ? string.Join(", ", directions)
            : "in all directions";

        var prompt = $"Expand this photograph to a wider canvas, extending the scene {directionText}. " +
                     $"The final image should be approximately {ratioW:F1}x wider and {ratioH:F1}x taller. " +
                     "CRITICAL RULES: " +
                     "1) PROPORTIONS: The subject must stay at EXACTLY the same physical scale relative to the scene. " +
                     "The head-to-body ratio, shoulder width, and all body proportions MUST remain realistic and natural. " +
                     "Do NOT shrink, miniaturize, or make the subject look like a doll or toy figure. " +
                     "The person should occupy the same visual proportion of the frame as in the original photo. " +
                     "2) HEADROOM: Keep at least a small margin of background above the subject's head — the top of the head must NOT touch or be too close to the top edge. " +
                     "3) IDENTITY: Keep the original subject EXACTLY as they are — same face, same clothes, same pose, same colors, same expression. Change NOTHING about the person. " +
                     "4) BACKGROUND: Only extend the background, environment, and surroundings. " +
                     "Match lighting, color grading, perspective, depth-of-field, and photographic style perfectly. " +
                     "5) REALISM: The result must look like a single natural photograph taken with a wider lens — NOT a collage, composite, or digitally manipulated image. " +
                     "6) Do NOT add new people, text, watermarks, or logos. " +
                     "7) MANDATORY: Preserve 1:1 pixel-perfect facial geometry and features; do not alter, redraw, or enhance eyes, nose, mouth, teeth, or expression—apply color and lighting adjustments only to the surrounding pixels.";

        if (!string.IsNullOrWhiteSpace(userHint))
            prompt += $" Additional context for the expanded areas: {userHint.Trim()}";

        return prompt;
    }

    /// <summary>
    /// Resize Gemini output to exact target dimensions preserving aspect ratio.
    /// Scales to fill and center-crops any excess — never stretches/skews.
    /// </summary>
    private byte[] ResizeToTarget(byte[] geminiBytes, int targetW, int targetH)
    {
        using var img = Image.Load<Rgba32>(geminiBytes);

        if (img.Width == targetW && img.Height == targetH)
            return geminiBytes;

        img.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(targetW, targetH),
            Mode = ResizeMode.Crop,
            Sampler = KnownResamplers.Lanczos3,
            Position = AnchorPositionMode.Center
        }));

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Draw very subtle, color-merged guide lines on the printed result.
    /// - Fold boundary: thin dashed line (tiny brightness nudge, blends with colors)
    /// - Corner marks: small cross at each fold corner
    /// Lines are barely visible — just enough for the wrapping person to see.
    /// </summary>
    private byte[] DrawWrapGuides(byte[] imageBytes, int totalW, int totalH, int wrapPx)
    {
        using var img = Image.Load<Rgba32>(imageBytes);

        float foldShift = 0.10f;   // 10% brightness nudge — visible but blends with colors
        float cornerShift = 0.14f; // 14% for corner marks — a touch stronger

        // ── Fold boundary — solid continuous line at each edge ──
        DrawDashedHLine(img, wrapPx, 0, totalW, foldShift, 0, 0);           // top
        DrawDashedHLine(img, totalH - wrapPx, 0, totalW, foldShift, 0, 0);  // bottom
        DrawDashedVLine(img, wrapPx, 0, totalH, foldShift, 0, 0);           // left
        DrawDashedVLine(img, totalW - wrapPx, 0, totalH, foldShift, 0, 0);  // right
        // Second pixel for slightly thicker line
        DrawDashedHLine(img, wrapPx + 1, 0, totalW, foldShift, 0, 0);
        DrawDashedHLine(img, totalH - wrapPx - 1, 0, totalW, foldShift, 0, 0);
        DrawDashedVLine(img, wrapPx + 1, 0, totalH, foldShift, 0, 0);
        DrawDashedVLine(img, totalW - wrapPx - 1, 0, totalH, foldShift, 0, 0);

        // ── Corner cross marks at the 4 fold corners ──
        int markLen = Math.Min(wrapPx, 80);
        int cx1 = wrapPx, cx2 = totalW - wrapPx;
        int cy1 = wrapPx, cy2 = totalH - wrapPx;

        DrawDashedHLine(img, cy1, cx1 - markLen, cx1 + markLen, cornerShift, 0, 0);
        DrawDashedVLine(img, cx1, cy1 - markLen, cy1 + markLen, cornerShift, 0, 0);
        DrawDashedHLine(img, cy1, cx2 - markLen, cx2 + markLen, cornerShift, 0, 0);
        DrawDashedVLine(img, cx2, cy1 - markLen, cy1 + markLen, cornerShift, 0, 0);
        DrawDashedHLine(img, cy2, cx1 - markLen, cx1 + markLen, cornerShift, 0, 0);
        DrawDashedVLine(img, cx1, cy2 - markLen, cy2 + markLen, cornerShift, 0, 0);
        DrawDashedHLine(img, cy2, cx2 - markLen, cx2 + markLen, cornerShift, 0, 0);
        DrawDashedVLine(img, cx2, cy2 - markLen, cy2 + markLen, cornerShift, 0, 0);

        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>Draw a dashed horizontal line by nudging pixel brightness.</summary>
    private static void DrawDashedHLine(Image<Rgba32> img, int y, int x1, int x2, float shift, int dash, int gap)
    {
        if (y < 0 || y >= img.Height) return;
        x1 = Math.Max(0, x1);
        x2 = Math.Min(img.Width, x2);
        int total = dash + gap;
        for (int x = x1; x < x2; x++)
        {
            if (total > 0 && gap > 0 && (x - x1) % total >= dash) continue;
            img[x, y] = NudgeBrightness(img[x, y], shift);
        }
    }

    /// <summary>Draw a dashed vertical line by nudging pixel brightness.</summary>
    private static void DrawDashedVLine(Image<Rgba32> img, int x, int y1, int y2, float shift, int dash, int gap)
    {
        if (x < 0 || x >= img.Width) return;
        y1 = Math.Max(0, y1);
        y2 = Math.Min(img.Height, y2);
        int total = dash + gap;
        for (int y = y1; y < y2; y++)
        {
            if (total > 0 && gap > 0 && (y - y1) % total >= dash) continue;
            img[x, y] = NudgeBrightness(img[x, y], shift);
        }
    }

    /// <summary>
    /// Nudge a pixel's brightness by a tiny amount — lightens dark, darkens light.
    /// Keeps the same hue/color, just a minimal visible shift.
    /// </summary>
    private static Rgba32 NudgeBrightness(Rgba32 px, float amount)
    {
        float luma = (0.299f * px.R + 0.587f * px.G + 0.114f * px.B) / 255f;
        float dir = luma < 0.5f ? 1f : -1f;
        float shift = dir * amount * 255f;
        return new Rgba32(
            ClampByte(px.R + shift),
            ClampByte(px.G + shift),
            ClampByte(px.B + shift),
            255);
    }

    private static byte ClampByte(float v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>Export result as JPEG at specified quality.</summary>
    public static byte[] ConvertToJpeg(byte[] pngBytes, int quality = 95)
    {
        using var img = Image.Load<Rgba32>(pngBytes);
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    /// <summary>
    /// Detect the primary face in the image using skin-tone analysis.
    /// Returns (faceCenterX, eyeY) as fractions 0.0–1.0 of the image dimensions.
    /// eyeY is the estimated eye-line position (upper portion of face bounding box).
    /// If multiple faces exist, picks the largest/most prominent one.
    /// </summary>
    public static (double faceCenterX, double eyeY) DetectFaceCenter(byte[] imageBytes)
    {
        using var img = Image.Load<Rgba32>(imageBytes);

        int step = Math.Max(1, Math.Min(img.Width, img.Height) / 200);

        var skinPixels = new List<(int x, int y)>();

        for (int y = 0; y < img.Height; y += step)
        {
            for (int x = 0; x < img.Width; x += step)
            {
                if (IsSkinTone(img[x, y]))
                    skinPixels.Add((x, y));
            }
        }

        if (skinPixels.Count < 20)
            return (0.5, 0.30); // Fallback: upper-center, eyes at 30%

        // Grid-based clustering to find the primary face
        int gridCols = 8, gridRows = 10;
        int cellW = img.Width / gridCols, cellH = img.Height / gridRows;
        var grid = new int[gridCols, gridRows];

        foreach (var (px, py) in skinPixels)
        {
            int gx = Math.Min(px / Math.Max(1, cellW), gridCols - 1);
            int gy = Math.Min(py / Math.Max(1, cellH), gridRows - 1);
            grid[gx, gy]++;
        }

        // Find the densest cluster (primary face)
        int bestGx = gridCols / 2, bestGy = gridRows / 3;
        double bestScore = 0;

        for (int gy = 0; gy < gridRows; gy++)
        {
            for (int gx = 0; gx < gridCols; gx++)
            {
                if (grid[gx, gy] < 3) continue;

                int clusterCount = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = gx + dx, ny = gy + dy;
                        if (nx >= 0 && nx < gridCols && ny >= 0 && ny < gridRows)
                            clusterCount += grid[nx, ny];
                    }

                double upperBonus = gy < gridRows * 0.6 ? 1.5 : 1.0;
                double centerBonus = 1.0 + 0.3 * (1.0 - Math.Abs(gx - gridCols / 2.0) / (gridCols / 2.0));
                double score = clusterCount * upperBonus * centerBonus;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestGx = gx;
                    bestGy = gy;
                }
            }
        }

        // Find bounding box + center of the winning cluster's skin pixels
        int minX = img.Width, maxX = 0, minY = img.Height, maxY = 0;
        double sumX = 0;
        int count = 0;

        foreach (var (px, py) in skinPixels)
        {
            int gx = Math.Min(px / Math.Max(1, cellW), gridCols - 1);
            int gy = Math.Min(py / Math.Max(1, cellH), gridRows - 1);

            if (Math.Abs(gx - bestGx) <= 1 && Math.Abs(gy - bestGy) <= 1)
            {
                sumX += px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                count++;
            }
        }

        if (count < 5)
            return (0.5, 0.30);

        double faceCenterX = Math.Clamp(sumX / count / img.Width, 0.05, 0.95);

        // Eye level ≈ 35% down from the top of the face bounding box
        // (forehead → eyes → nose → mouth → chin)
        double faceTop = (double)minY / img.Height;
        double faceBottom = (double)maxY / img.Height;
        double faceHeight = faceBottom - faceTop;
        double eyeY = Math.Clamp(faceTop + faceHeight * 0.35, 0.05, 0.95);

        return (faceCenterX, eyeY);
    }

    /// <summary>
    /// Calculate posX/posY to place the eye-line at the portrait rule-of-thirds position
    /// (≈33% from top) and center the face horizontally.
    /// </summary>
    public static (double posX, double posY) CalculateFaceCenterPosition(
        double faceCX, double faceEyeY,
        int srcW, int srcH, int tgtW, int tgtH)
    {
        double fitScale = Math.Min((double)tgtW / srcW, (double)tgtH / srcH) * 0.96;
        double fitW = srcW * fitScale;
        double fitH = srcH * fitScale;

        double moveX = tgtW - fitW;
        double moveY = tgtH - fitH;

        if (moveX < 1 && moveY < 1) return (0.5, 0.5);

        // Horizontal: center the face
        double posX = moveX > 1 ? (tgtW / 2.0 - faceCX * fitW) / moveX : 0.5;

        // Vertical: place eye-line at 33% from top of target (portrait eye-line rule)
        double targetEyeY = tgtH * 0.33;
        double eyeInFit = faceEyeY * fitH;
        double posY = moveY > 1 ? (targetEyeY - eyeInFit) / moveY : 0.5;

        return (Math.Clamp(posX, 0, 1), Math.Clamp(posY, 0, 1));
    }

    private static bool IsSkinTone(Rgba32 px)
    {
        float r = px.R / 255f, g = px.G / 255f, b = px.B / 255f;
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        if (max < 0.15f || delta < 0.03f) return false;

        // Hue in degrees (0–360)
        float hue;
        if (max == r) hue = 60f * (((g - b) / delta) % 6f);
        else if (max == g) hue = 60f * (((b - r) / delta) + 2f);
        else hue = 60f * (((r - g) / delta) + 4f);
        if (hue < 0) hue += 360f;

        float sat = delta / max;

        // Skin tones: warm hues (red → orange → yellow), moderate saturation
        return (hue <= 55f || hue >= 345f) && sat >= 0.08f && sat <= 0.72f && max >= 0.15f;
    }
}
