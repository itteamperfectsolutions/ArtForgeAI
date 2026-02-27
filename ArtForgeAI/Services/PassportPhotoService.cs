using System.Text.Json;
using ArtForgeAI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;

namespace ArtForgeAI.Services;

public class PassportPhotoService : IPassportPhotoService
{
    private readonly IGeminiImageService _gemini;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PassportPhotoService> _logger;

    public PassportPhotoService(
        IGeminiImageService gemini,
        IWebHostEnvironment env,
        ILogger<PassportPhotoService> logger)
    {
        _gemini = gemini;
        _env = env;
        _logger = logger;
    }

    public async Task<FaceDetectionResult> DetectFaceAsync(string sourcePath)
    {
        var fullPath = System.IO.Path.Combine(_env.WebRootPath, sourcePath.TrimStart('/'));
        var imageBytes = await File.ReadAllBytesAsync(fullPath);
        var mimeType = sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        return await DetectFaceAsync(imageBytes, mimeType);
    }

    public async Task<byte[]> CropToPassportSizeAsync(
        string sourcePath, string backgroundColor, CropRectFractions cropRect)
    {
        var fullPath = System.IO.Path.Combine(_env.WebRootPath, sourcePath.TrimStart('/'));
        var imageBytes = await File.ReadAllBytesAsync(fullPath);

        using var image = Image.Load<Rgba32>(imageBytes);

        // Convert fractions to pixel rectangle
        var x = (int)Math.Round(cropRect.X * image.Width);
        var y = (int)Math.Round(cropRect.Y * image.Height);
        var w = (int)Math.Round(cropRect.W * image.Width);
        var h = (int)Math.Round(cropRect.H * image.Height);

        // Clamp to image bounds
        x = Math.Max(0, Math.Min(x, image.Width - 1));
        y = Math.Max(0, Math.Min(y, image.Height - 1));
        w = Math.Max(1, Math.Min(w, image.Width - x));
        h = Math.Max(1, Math.Min(h, image.Height - y));

        var pixelRect = new Rectangle(x, y, w, h);

        // Crop and resize to passport dimensions
        image.Mutate(ctx =>
        {
            ctx.Crop(pixelRect);
            ctx.Resize(PassportPhotoConfig.PhotoWidthPx, PassportPhotoConfig.PhotoHeightPx);
        });

        // Apply background color if not white
        if (!string.IsNullOrEmpty(backgroundColor) && backgroundColor != "#FFFFFF")
        {
            var bgColor = Rgba32.ParseHex(backgroundColor);
            var bg = new Image<Rgba32>(PassportPhotoConfig.PhotoWidthPx, PassportPhotoConfig.PhotoHeightPx, bgColor);
            bg.Mutate(ctx => ctx.DrawImage(image, new Point(0, 0), 1f));
            using var ms = new MemoryStream();
            await bg.SaveAsPngAsync(ms);
            bg.Dispose();
            return ms.ToArray();
        }

        using var output = new MemoryStream();
        await image.SaveAsPngAsync(output);
        return output.ToArray();
    }

    public async Task<byte[]> GenerateMultiUpSheetAsync(
        byte[] photoBytes, PaperSize paperSize, double spacingMm, double marginMm,
        bool cutMarks, bool cropMarks, bool landscape = false)
    {
        var grid = PassportPhotoConfig.CalculateGrid(paperSize, spacingMm, marginMm, landscape);
        using var photo = Image.Load<Rgba32>(photoBytes);
        using var sheet = new Image<Rgba32>(grid.PaperWidthPx, grid.PaperHeightPx, Color.White);

        var photoW = PassportPhotoConfig.PhotoWidthPx;
        var photoH = PassportPhotoConfig.PhotoHeightPx;

        // Tile passport photos onto the sheet
        sheet.Mutate(ctx =>
        {
            for (int row = 0; row < grid.Rows; row++)
            {
                for (int col = 0; col < grid.Cols; col++)
                {
                    var x = grid.OffsetX + col * (photoW + grid.SpacingPx);
                    var y = grid.OffsetY + row * (photoH + grid.SpacingPx);
                    ctx.DrawImage(photo, new Point(x, y), 1f);
                }
            }

            // Draw outline (solid thin border around each photo)
            if (cutMarks)
            {
                var pen = Pens.Solid(Color.Black, 1f);

                for (int row = 0; row < grid.Rows; row++)
                {
                    for (int col = 0; col < grid.Cols; col++)
                    {
                        var x = grid.OffsetX + col * (photoW + grid.SpacingPx);
                        var y = grid.OffsetY + row * (photoH + grid.SpacingPx);
                        ctx.Draw(pen, new RectangleF(x, y, photoW, photoH));
                    }
                }
            }

            // Draw crop marks (bold black L-shaped corner marks — the actual cutting guides)
            if (cropMarks)
            {
                var armLen = (int)Math.Round(Math.Min(grid.SpacingPx * 0.8, 5.0 * PassportPhotoConfig.PxPerMm));
                armLen = Math.Max(armLen, (int)Math.Round(2.0 * PassportPhotoConfig.PxPerMm));
                var pen = Pens.Solid(Color.Black, 2.5f);

                for (int row = 0; row < grid.Rows; row++)
                {
                    for (int col = 0; col < grid.Cols; col++)
                    {
                        var px = grid.OffsetX + col * (photoW + grid.SpacingPx);
                        var py = grid.OffsetY + row * (photoH + grid.SpacingPx);

                        DrawLMark(ctx, pen, px, py, armLen, leftSide: true, topSide: true);
                        DrawLMark(ctx, pen, px + photoW, py, armLen, leftSide: false, topSide: true);
                        DrawLMark(ctx, pen, px, py + photoH, armLen, leftSide: true, topSide: false);
                        DrawLMark(ctx, pen, px + photoW, py + photoH, armLen, leftSide: false, topSide: false);
                    }
                }
            }
        });

        using var ms = new MemoryStream();
        await sheet.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    private async Task<FaceDetectionResult> DetectFaceAsync(byte[] imageData, string mimeType)
    {
        var prompt = @"Analyze this photo and detect the face bounding box. Return ONLY a JSON object with these numeric values (as fractions 0.0-1.0 of image dimensions):
{
  ""faceTop"": <top of head as fraction of image height>,
  ""faceBottom"": <bottom of chin as fraction of image height>,
  ""faceLeft"": <left edge of face as fraction of image width>,
  ""faceRight"": <right edge of face as fraction of image width>,
  ""eyeLineY"": <vertical position of eye line as fraction of image height>
}
Return ONLY the JSON, no explanation.";

        try
        {
            var response = await _gemini.AnalyzeImageAsync(imageData, mimeType, prompt);

            // Extract JSON from response (may have markdown fences)
            var json = ExtractJson(response);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            using var img = Image.Load(imageData);

            return new FaceDetectionResult
            {
                FaceTop = root.GetProperty("faceTop").GetDouble(),
                FaceBottom = root.GetProperty("faceBottom").GetDouble(),
                FaceLeft = root.GetProperty("faceLeft").GetDouble(),
                FaceRight = root.GetProperty("faceRight").GetDouble(),
                EyeLineY = root.GetProperty("eyeLineY").GetDouble(),
                ImageWidth = img.Width,
                ImageHeight = img.Height
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Face detection failed, using center-crop fallback");
            using var img = Image.Load(imageData);
            // Fallback: assume face is centered in upper portion
            return new FaceDetectionResult
            {
                FaceTop = 0.1,
                FaceBottom = 0.7,
                FaceLeft = 0.25,
                FaceRight = 0.75,
                EyeLineY = 0.35,
                ImageWidth = img.Width,
                ImageHeight = img.Height
            };
        }
    }

    private static Rectangle CalculateFaceCenteredCrop(
        FaceDetectionResult face, int imgW, int imgH)
    {
        // Convert face ratios to pixel coordinates
        var faceTopPx = face.FaceTop * imgH;
        var faceBottomPx = face.FaceBottom * imgH;
        var faceCenterX = (face.FaceLeft + face.FaceRight) / 2.0 * imgW;
        var eyeLinePx = face.EyeLineY * imgH;
        var faceHeightPx = faceBottomPx - faceTopPx;

        // Passport ratio: face should occupy ~75% of photo height,
        // eyes at ~40% from top of photo
        var passportRatio = (double)PassportPhotoConfig.PhotoWidthPx / PassportPhotoConfig.PhotoHeightPx;
        var targetPhotoH = faceHeightPx / 0.55; // face is ~55% of photo height (chin-to-top-of-head)
        var targetPhotoW = targetPhotoH * passportRatio;

        // Position: eyes at 40% from top
        var targetTop = eyeLinePx - targetPhotoH * 0.40;
        var targetLeft = faceCenterX - targetPhotoW / 2.0;

        // Clamp to image bounds
        targetTop = Math.Max(0, Math.Min(targetTop, imgH - targetPhotoH));
        targetLeft = Math.Max(0, Math.Min(targetLeft, imgW - targetPhotoW));

        // Ensure we don't exceed image dimensions
        targetPhotoW = Math.Min(targetPhotoW, imgW - targetLeft);
        targetPhotoH = Math.Min(targetPhotoH, imgH - targetTop);

        // Maintain aspect ratio by adjusting the smaller dimension
        var currentRatio = targetPhotoW / targetPhotoH;
        if (currentRatio > passportRatio)
            targetPhotoW = targetPhotoH * passportRatio;
        else
            targetPhotoH = targetPhotoW / passportRatio;

        return new Rectangle(
            (int)Math.Round(targetLeft),
            (int)Math.Round(targetTop),
            (int)Math.Round(targetPhotoW),
            (int)Math.Round(targetPhotoH));
    }

    private static void DrawLMark(IImageProcessingContext ctx, Pen pen,
        int x, int y, int len, bool leftSide, bool topSide)
    {
        // Horizontal arm
        var hDir = leftSide ? -1 : 1;
        ctx.DrawLine(pen, new PointF(x, y), new PointF(x + hDir * len, y));
        // Vertical arm
        var vDir = topSide ? -1 : 1;
        ctx.DrawLine(pen, new PointF(x, y), new PointF(x, y + vDir * len));
    }

    private static void DrawCrosshair(IImageProcessingContext ctx, Pen pen,
        int cx, int cy, int armLen)
    {
        ctx.DrawLine(pen, new PointF(cx - armLen, cy), new PointF(cx + armLen, cy));
        ctx.DrawLine(pen, new PointF(cx, cy - armLen), new PointF(cx, cy + armLen));
    }

    private static string ExtractJson(string text)
    {
        // Strip markdown fences if present
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }

        // Find first { and last }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];

        return trimmed;
    }
}
