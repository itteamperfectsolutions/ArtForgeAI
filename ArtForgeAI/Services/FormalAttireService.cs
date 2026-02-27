using System.Text.Json;
using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public class FormalAttireService : IFormalAttireService
{
    private readonly IGeminiImageService _gemini;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<FormalAttireService> _logger;

    public FormalAttireService(
        IGeminiImageService gemini,
        IWebHostEnvironment env,
        ILogger<FormalAttireService> logger)
    {
        _gemini = gemini;
        _env = env;
        _logger = logger;
    }

    public async Task<byte[]> ApplyFormalAttireAsync(
        string sourcePath, string backgroundColor = "#FFFFFF",
        string suitColor = "auto", string tieColor = "auto")
    {
        var fullPath = Path.Combine(_env.WebRootPath, sourcePath.TrimStart('/'));
        var imageBytes = await File.ReadAllBytesAsync(fullPath);
        var mimeType = sourcePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        // Analyze the subject
        var analysis = await AnalyzeSubject(imageBytes, mimeType);

        if (analysis.AlreadyFormal)
        {
            _logger.LogInformation("Subject already wearing formal attire, skipping");
            return imageBytes;
        }

        // Auto-select suit color based on skin tone
        var resolvedSuitColor = suitColor == "auto"
            ? SelectSuitColor(analysis.SkinTone)
            : suitColor;

        var resolvedTieColor = tieColor == "auto" ? "dark red" : tieColor;

        // Build gender-appropriate prompt
        var genderLabel = analysis.Gender.ToLowerInvariant() switch
        {
            "female" or "woman" => "women's",
            "male" or "man" => "men's",
            _ => "professional"
        };

        var fitDesc = analysis.BuildType.ToLowerInvariant() switch
        {
            "slim" or "thin" => "slim-fit",
            "large" or "heavy" => "relaxed-fit",
            _ => "well-tailored"
        };

        // Try up to 2 times with progressively simpler prompts
        var prompts = new[]
        {
            $"This is for an official passport photo. Replace ALL clothing, jewelry, necklaces, and accessories below the neck " +
            $"with a {genderLabel} {fitDesc} {resolvedSuitColor} formal suit with a crisp white dress shirt and {resolvedTieColor} tie. " +
            "Remove any visible necklaces, pendants, chains, scarves, or decorative accessories — the neckline must show ONLY the shirt collar and tie. " +
            "Do NOT alter the face, hair, skin tone, or any facial features. " +
            "Preserve the exact biometric facial identity. Keep the background as-is.",

            $"For a passport photo: Put this person in a clean {resolvedSuitColor} formal business suit with white shirt and {resolvedTieColor} tie. " +
            "Remove all jewelry, necklaces, and accessories from the neck area. " +
            "Do not change the person's face or identity at all. Keep background unchanged."
        };

        var images = new List<(byte[] data, string mimeType)> { (imageBytes, mimeType) };

        foreach (var prompt in prompts)
        {
            try
            {
                _logger.LogInformation("Applying formal attire with prompt: {Prompt}", prompt[..Math.Min(80, prompt.Length)]);
                var (_, resultBytes) = await _gemini.EditImageAsync(prompt, images, 0, 0);
                return resultBytes;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Formal attire attempt failed, trying simpler prompt");
            }
        }

        _logger.LogError("All formal attire attempts failed, returning original image");
        return imageBytes;
    }

    private async Task<SubjectAnalysis> AnalyzeSubject(byte[] imageData, string mimeType)
    {
        var prompt = @"Analyze the person in this photo. Return ONLY a JSON object:
{
  ""gender"": ""male"" or ""female"" or ""neutral"",
  ""skinTone"": ""light"" or ""medium"" or ""dark"",
  ""buildType"": ""slim"" or ""average"" or ""large"",
  ""alreadyFormal"": true if wearing a suit/blazer/formal attire,
  ""currentClothing"": ""brief description of current outfit""
}
Return ONLY the JSON, no explanation.";

        try
        {
            var response = await _gemini.AnalyzeImageAsync(imageData, mimeType, prompt);
            var json = ExtractJson(response);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new SubjectAnalysis
            {
                Gender = root.GetProperty("gender").GetString() ?? "neutral",
                SkinTone = root.GetProperty("skinTone").GetString() ?? "medium",
                BuildType = root.GetProperty("buildType").GetString() ?? "average",
                AlreadyFormal = root.GetProperty("alreadyFormal").GetBoolean(),
                CurrentClothing = root.GetProperty("currentClothing").GetString() ?? ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Subject analysis failed, using defaults");
            return new SubjectAnalysis();
        }
    }

    private static string SelectSuitColor(string skinTone) => skinTone.ToLowerInvariant() switch
    {
        "light" => "charcoal gray",
        "dark" => "black",
        _ => "navy blue"
    };

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
