using System.Text.Json;
using ArtForgeAI.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArtForgeAI.Services;

public class FaceCorrectionService : IFaceCorrectionService
{
    private readonly IGeminiImageService _gemini;
    private readonly IImageStorageService _storage;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<FaceCorrectionService> _logger;

    public FaceCorrectionService(
        IGeminiImageService gemini,
        IImageStorageService storage,
        IWebHostEnvironment env,
        ILogger<FaceCorrectionService> logger)
    {
        _gemini = gemini;
        _storage = storage;
        _env = env;
        _logger = logger;
    }

    public async Task<FacePoseAnalysis> AnalyzeFacePoseAsync(byte[] imageData, string mimeType)
    {
        var prompt = @"Analyze the face pose in this photo. Return ONLY a JSON object:
{
  ""rollDegrees"": <head tilt left/right in degrees, positive = clockwise>,
  ""yawDegrees"": <head turn left/right in degrees, positive = turned right>,
  ""pitchDegrees"": <head tilt up/down in degrees, positive = looking up>,
  ""faceCenterX"": <horizontal center of face as fraction 0.0-1.0 of image width>,
  ""faceCenterY"": <vertical center of face as fraction 0.0-1.0 of image height>
}
Return ONLY the JSON, no explanation.";

        try
        {
            var response = await _gemini.AnalyzeImageAsync(imageData, mimeType, prompt);
            var json = ExtractJson(response);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var roll = root.GetProperty("rollDegrees").GetDouble();
            var yaw = root.GetProperty("yawDegrees").GetDouble();
            var pitch = root.GetProperty("pitchDegrees").GetDouble();

            var severity = ClassifySeverity(Math.Abs(roll), Math.Abs(yaw));

            return new FacePoseAnalysis
            {
                RollDegrees = roll,
                YawDegrees = yaw,
                PitchDegrees = pitch,
                FaceCenterX = root.GetProperty("faceCenterX").GetDouble(),
                FaceCenterY = root.GetProperty("faceCenterY").GetDouble(),
                Severity = severity
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Face pose analysis failed, assuming no correction needed");
            return new FacePoseAnalysis { Severity = PoseSeverity.None };
        }
    }

    public async Task<byte[]> CorrectFaceAsync(string sourcePath)
    {
        var fullPath = Path.Combine(_env.WebRootPath, sourcePath.TrimStart('/'));
        var imageBytes = await File.ReadAllBytesAsync(fullPath);
        var mimeType = sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        var pose = await AnalyzeFacePoseAsync(imageBytes, mimeType);

        _logger.LogInformation("Face pose: roll={Roll}°, yaw={Yaw}°, severity={Severity}",
            pose.RollDegrees, pose.YawDegrees, pose.Severity);

        // Always use AI correction for any detected tilt/turn — gives best passport results
        if (pose.Severity == PoseSeverity.None)
            return imageBytes;

        // For any tilt, use AI to straighten the face for passport compliance
        return await CorrectByAI(imageBytes, mimeType, pose);
    }

    private async Task<byte[]> CorrectByRotation(byte[] imageBytes, FacePoseAnalysis pose)
    {
        _logger.LogInformation("Applying local rotation correction: {Roll}°", -pose.RollDegrees);

        using var image = Image.Load<Rgba32>(imageBytes);
        image.Mutate(ctx =>
        {
            ctx.Rotate((float)-pose.RollDegrees);
            // Re-center crop to original dimensions
            var newW = image.Width;
            var newH = image.Height;
            if (newW > image.Width || newH > image.Height)
            {
                var cropX = Math.Max(0, (newW - image.Width) / 2);
                var cropY = Math.Max(0, (newH - image.Height) / 2);
                ctx.Crop(new Rectangle(cropX, cropY,
                    Math.Min(image.Width, newW), Math.Min(image.Height, newH)));
            }
        });

        using var ms = new MemoryStream();
        await image.SaveAsPngAsync(ms);
        return ms.ToArray();
    }

    private async Task<byte[]> CorrectByAI(byte[] imageBytes, string mimeType, FacePoseAnalysis pose)
    {
        _logger.LogInformation("Applying AI face correction for yaw={Yaw}°", pose.YawDegrees);

        try
        {
            var prompt = "This is for an official passport photo. " +
                         "Straighten the person's head so it is perfectly level and looking directly at the camera with a neutral frontal view. " +
                         "The face must be symmetrically centered, with both eyes at the same height. " +
                         "Correct any head tilt, rotation, or turn. " +
                         "Do NOT change any facial features, identity, skin tone, hair color, or eye color. " +
                         "The result must be the EXACT same person. Keep the background unchanged.";

            var images = new List<(byte[] data, string mimeType)> { (imageBytes, mimeType) };
            var (_, resultBytes) = await _gemini.EditImageAsync(prompt, images, 0, 0);
            return resultBytes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI face correction failed, falling back to rotation");
            return await CorrectByRotation(imageBytes, pose);
        }
    }

    private static PoseSeverity ClassifySeverity(double absRoll, double absYaw)
    {
        if (absRoll < 3 && absYaw < 5) return PoseSeverity.None;
        if (absRoll < 15 && absYaw < 10) return PoseSeverity.MildTilt;
        if (absYaw < 45) return PoseSeverity.Moderate;
        return PoseSeverity.Uncorrectable;
    }

    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0) trimmed = trimmed[(firstNewline + 1)..];
            if (trimmed.EndsWith("```")) trimmed = trimmed[..^3];
            trimmed = trimmed.Trim();
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start) return trimmed[start..(end + 1)];
        return trimmed;
    }
}
