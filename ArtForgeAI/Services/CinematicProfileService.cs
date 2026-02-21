using System.Text.Json;
using System.Text.Json.Serialization;
using ArtForgeAI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

public class CinematicProfileService : ICinematicProfileService
{
    private readonly IGeminiImageService _gemini;
    private readonly IImageStorageService _storage;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CinematicProfileService> _logger;

    private static readonly HashSet<string> _multiFaceStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cinematic B&W Profile"
    };

    private const int MaxFaces = 5;

    private class FaceBoundingBox
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public string Label { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extras { get; set; }

        public void Normalize()
        {
            if (Extras is null) return;
            if (W == 0 && Extras.TryGetValue("width", out var width)) W = width.GetDouble();
            if (H == 0 && Extras.TryGetValue("height", out var height)) H = height.GetDouble();
            if (X == 0 && Extras.TryGetValue("left", out var left)) X = left.GetDouble();
            if (Y == 0 && Extras.TryGetValue("top", out var top)) Y = top.GetDouble();
        }

        public bool IsValid => W > 0.01 && H > 0.01 && X >= 0 && Y >= 0 && X < 1 && Y < 1;

        public double Area => W * H;
    }

    public CinematicProfileService(
        IGeminiImageService gemini,
        IImageStorageService storage,
        IWebHostEnvironment env,
        ILogger<CinematicProfileService> logger)
    {
        _gemini = gemini;
        _storage = storage;
        _env = env;
        _logger = logger;
    }

    public bool IsMultiFaceStyle(string styleName) => _multiFaceStyles.Contains(styleName);

    public async Task<GenerationResult?> ProcessAsync(string imagePath, string prompt, int width, int height)
    {
        var fullPath = SafeResolvePath(imagePath);
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Source image not found for cinematic processing");
            return null;
        }

        var originalBytes = await File.ReadAllBytesAsync(fullPath);
        var mimeType = imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        // Phase 1: Detect faces via Gemini Vision (text-only analysis — reliable)
        var faces = await DetectFacesAsync(originalBytes, mimeType);
        if (faces.Count < 2)
        {
            _logger.LogInformation("Detected {Count} face(s) — falling back to normal pipeline", faces.Count);
            return null;
        }

        faces = faces.OrderByDescending(f => f.Area).Take(MaxFaces).ToList();
        _logger.LogInformation("Processing {Count} faces (capped at {Max})", faces.Count, MaxFaces);

        // Phase 2: Crop each face with generous padding
        using var sourceImage = Image.Load<Rgba32>(originalBytes);
        var crops = CropFaces(sourceImage, faces);

        if (crops.Count == 0)
        {
            _logger.LogWarning("All face crops were invalid — falling back to normal pipeline");
            return null;
        }

        // Phase 3: Apply cinematic B&W using pure ImageSharp (NO Gemini — 100% identity preserved)
        var processedFaces = new List<Image<Rgba32>>();
        foreach (var (crop, label) in crops)
        {
            _logger.LogInformation("Applying cinematic B&W to '{Label}'", label);
            var processed = ApplyCinematicBW(crop);
            processedFaces.Add(processed);
            crop.Dispose();
        }

        // Phase 4: Composite all faces vertically on black canvas
        var compositeBytes = CompositeFaces(processedFaces, width, height);

        var fileName = $"cinematic_profile_{Guid.NewGuid():N}.png";
        var savedPath = await _storage.SaveImageFromBytesAsync(BinaryData.FromBytes(compositeBytes), fileName);

        foreach (var face in processedFaces)
            face.Dispose();

        return new GenerationResult
        {
            Success = true,
            LocalImagePath = savedPath,
            EnhancedPrompt = $"Multi-face cinematic B&W ({faces.Count} faces)"
        };
    }

    /// <summary>
    /// Applies a cinematic black-and-white effect using pure ImageSharp processing.
    /// No AI generation involved — identity is preserved exactly as-is.
    /// Effect: grayscale → high contrast → vignette spotlight → deep blacks → rim lighting.
    /// </summary>
    private Image<Rgba32> ApplyCinematicBW(Image<Rgba32> crop)
    {
        var result = crop.Clone();

        // 1. Convert to grayscale
        result.Mutate(ctx => ctx.Grayscale());

        // 2. Boost contrast for dramatic high-contrast B&W
        result.Mutate(ctx => ctx.Contrast(0.7f));

        // 3. Sharpen for crisp facial details
        result.Mutate(ctx => ctx.GaussianSharpen(0.8f));

        // 4. Cinematic vignette + deep blacks + rim lighting (pixel-level)
        ApplyCinematicEffects(result);

        return result;
    }

    /// <summary>
    /// Pixel-level cinematic effects: elliptical vignette centered on face,
    /// pure-black shadow crush, and right-side rim lighting.
    /// </summary>
    private static void ApplyCinematicEffects(Image<Rgba32> image)
    {
        var w = image.Width;
        var h = image.Height;
        var centerX = w / 2.0;
        var centerY = h * 0.4;       // Face typically in upper-center of crop
        var radiusX = w * 0.50;
        var radiusY = h * 0.55;
        var rimZone = w - (int)(w * 0.07); // Right 7% of image

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    var luminance = (double)pixel.R; // Grayscale: R == G == B

                    // Elliptical vignette — smooth fade to black outside face area
                    var dx = (x - centerX) / radiusX;
                    var dy = (y - centerY) / radiusY;
                    var dist = Math.Sqrt(dx * dx + dy * dy);
                    var vignette = Math.Clamp(1.0 - (dist - 0.6) * 2.0, 0.0, 1.0);
                    vignette *= vignette; // Quadratic falloff for natural transition

                    luminance *= vignette;

                    // Crush shadows to pure black for deep cinematic blacks
                    if (luminance < 35) luminance = 0;

                    // Rim light on right edge for dramatic side illumination
                    if (x > rimZone && luminance > 20)
                    {
                        var rimFactor = (double)(x - rimZone) / (w - rimZone);
                        luminance = Math.Min(255, luminance + 70 * rimFactor);
                    }

                    var val = (byte)Math.Clamp(luminance, 0, 255);
                    pixel = new Rgba32(val, val, val, 255);
                }
            }
        });
    }

    private async Task<List<FaceBoundingBox>> DetectFacesAsync(byte[] imageBytes, string mimeType)
    {
        var detectPrompt =
            "Detect all human faces in this image. Return a JSON array. " +
            "Each element: {\"x\": left 0-1, \"y\": top 0-1, \"w\": width 0-1, \"h\": height 0-1, " +
            "\"label\": \"brief description\"}. " +
            "Return ONLY the JSON array, no other text.";

        try
        {
            var response = await _gemini.AnalyzeImageAsync(imageBytes, mimeType, detectPrompt);
            var json = StripMarkdownFences(response);
            _logger.LogInformation("Face detection: {Json}", json);

            var faces = JsonSerializer.Deserialize<List<FaceBoundingBox>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (faces is null) return [];
            foreach (var f in faces) f.Normalize();
            return faces.Where(f => f.IsValid).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Face detection failed");
            return [];
        }
    }

    private List<(Image<Rgba32> crop, string label)> CropFaces(
        Image<Rgba32> source, List<FaceBoundingBox> faces)
    {
        var crops = new List<(Image<Rgba32>, string)>();
        var imgW = source.Width;
        var imgH = source.Height;

        foreach (var face in faces)
        {
            var faceX = (int)(face.X * imgW);
            var faceY = (int)(face.Y * imgH);
            var faceW = (int)(face.W * imgW);
            var faceH = (int)(face.H * imgH);

            if (faceW < 20 || faceH < 20) continue;

            // Generous padding to include hair and shoulders
            var padW = (int)(faceW * 0.7);
            var padH = (int)(faceH * 0.7);

            var cropX = Math.Max(0, faceX - padW);
            var cropY = Math.Max(0, faceY - padH);
            var cropW = Math.Min(imgW, faceX + faceW + padW) - cropX;
            var cropH = Math.Min(imgH, faceY + faceH + padH) - cropY;

            if (cropW <= 0 || cropH <= 0) continue;

            var cropped = source.Clone(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH)));
            crops.Add((cropped, face.Label));
            _logger.LogInformation("Cropped '{Label}': {W}x{H}", face.Label, cropW, cropH);
        }

        return crops;
    }

    private byte[] CompositeFaces(List<Image<Rgba32>> faces, int canvasWidth, int canvasHeight)
    {
        var sorted = faces; // Already sorted by area (largest first from detection phase)

        // Resize each face to fit canvas width
        foreach (var face in sorted)
        {
            var scale = (double)canvasWidth / face.Width;
            var newH = Math.Max(1, (int)(face.Height * scale));
            face.Mutate(ctx => ctx.Resize(canvasWidth, newH));
        }

        var avgHeight = sorted.Count > 0 ? sorted.Average(f => f.Height) : 0;
        var overlap = sorted.Count > 1 ? (int)(avgHeight * 0.15) : 0;

        using var canvas = new Image<Rgba32>(canvasWidth, canvasHeight, new Rgba32(0, 0, 0, 255));

        var totalHeight = sorted.Sum(f => f.Height) - overlap * Math.Max(0, sorted.Count - 1);
        var startY = Math.Max(0, (canvasHeight - totalHeight) / 2);

        var currentY = startY;
        foreach (var face in sorted)
        {
            var x = (canvasWidth - face.Width) / 2;
            canvas.Mutate(ctx => ctx.DrawImage(face, new Point(x, currentY), 1f));
            currentY += face.Height - overlap;
        }

        using var output = new MemoryStream();
        canvas.SaveAsPng(output);
        return output.ToArray();
    }

    private static string StripMarkdownFences(string text)
    {
        var json = text.Trim();
        if (!json.StartsWith("```")) return json;
        var firstNewline = json.IndexOf('\n');
        if (firstNewline >= 0) json = json[(firstNewline + 1)..];
        if (json.EndsWith("```")) json = json[..^3];
        return json.Trim();
    }

    private string SafeResolvePath(string localPath)
    {
        var normalized = localPath.Replace("/", Path.DirectorySeparatorChar.ToString());
        var fullPath = Path.GetFullPath(Path.Combine(_env.WebRootPath, normalized));
        var webRoot = Path.GetFullPath(_env.WebRootPath);
        if (!fullPath.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access to the specified path is denied.");
        return fullPath;
    }
}
