using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArtForgeAI.Services;

public class TemplateCollageService : ITemplateCollageService
{
    private readonly IGeminiImageService _gemini;
    private readonly ILogger<TemplateCollageService> _logger;

    public TemplateCollageService(IGeminiImageService gemini, ILogger<TemplateCollageService> logger)
    {
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<TemplateAnalysis> AnalyzeTemplateAsync(byte[] templateBytes, string mimeType)
    {
        var prompt = @"Analyze this collage/poster template image in detail. Return a JSON object with these exact fields:
{
  ""colorTheme"": ""describe the dominant colors and color palette (e.g. 'soft pink, magenta, white with gold accents')"",
  ""mood"": ""describe the overall mood/vibe (e.g. 'festive, celebratory, playful')"",
  ""backgroundDescription"": ""describe the background style and color"",
  ""decorativeElements"": ""list all decorative elements like balloons, confetti, stars, borders, frames, etc."",
  ""textElements"": ""describe any text visible in the template and its styling"",
  ""photoSlotCount"": number_of_distinct_photo_placeholder_areas
}
Count the EXACT number of distinct photo areas/frames where a person's photo would go. Each separate photo frame counts as 1 slot.
Return ONLY the JSON, no other text.";

        var analysisJson = await _gemini.AnalyzeImageAsync(templateBytes, mimeType, prompt);
        _logger.LogInformation("Template analysis raw response: {Response}", analysisJson);

        var analysis = new TemplateAnalysis { FullAnalysis = analysisJson };

        try
        {
            var jsonStr = ExtractJson(analysisJson);
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            if (root.TryGetProperty("colorTheme", out var ct))
                analysis.ColorTheme = ct.GetString() ?? "";
            if (root.TryGetProperty("mood", out var mood))
                analysis.Mood = mood.GetString() ?? "";
            if (root.TryGetProperty("backgroundDescription", out var bg))
                analysis.BackgroundDescription = bg.GetString() ?? "";
            if (root.TryGetProperty("decorativeElements", out var de))
                analysis.DecorativeElements = de.GetString() ?? "";
            if (root.TryGetProperty("textElements", out var te))
                analysis.TextElements = te.GetString() ?? "";
            if (root.TryGetProperty("photoSlotCount", out var psc))
                analysis.PhotoSlotCount = psc.TryGetInt32(out var count) ? count : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse template analysis JSON, using raw text");
            analysis.ColorTheme = analysisJson;
        }

        return analysis;
    }

    public async Task<byte[]> ProcessPhotoWithThemeAsync(byte[] photoBytes, string mimeType, TemplateAnalysis theme, int width, int height)
    {
        var prompt = $@"Process this photo to match the following color theme and style for a collage:

COLOR THEME: {theme.ColorTheme}
MOOD: {theme.Mood}
BACKGROUND STYLE: {theme.BackgroundDescription}

Instructions:
- FACE LOCK: Keep the person/subject EXACTLY as they are — do NOT change their face, pose, expression, body, or any physical feature
- Apply the color grading and toning that matches the theme colors ({theme.ColorTheme})
- Add a subtle color overlay or tint that harmonizes with the template palette
- Adjust the lighting and contrast to match the template mood ({theme.Mood})
- Remove the original background and replace it with a clean background that uses the template's color palette
- The subject should look like they naturally belong in the template
- Keep the image high quality and sharp
- Output the processed photo of the person with the themed background
- The face must remain PHOTOREALISTIC and identical to the input";

        var images = new List<(byte[] data, string mimeType)> { (photoBytes, mimeType) };
        var (_, resultBytes) = await _gemini.EditImageAsync(prompt, images, width, height);
        return resultBytes;
    }

    public async Task<byte[]> GenerateCollageAsync(byte[] templateBytes, string templateMime,
        List<byte[]> processedPhotos, string photoMime, int width, int height,
        List<string>? slotDescriptions = null)
    {
        var images = new List<(byte[] data, string mimeType)>
        {
            (templateBytes, templateMime)
        };

        foreach (var photo in processedPhotos)
        {
            images.Add((photo, photoMime));
        }

        // Build slot mapping description
        var slotMapping = new System.Text.StringBuilder();
        for (int i = 0; i < processedPhotos.Count; i++)
        {
            var slotLabel = (slotDescriptions is not null && i < slotDescriptions.Count)
                ? slotDescriptions[i]
                : $"Slot {i + 1}";
            slotMapping.AppendLine($"- Reference Image {i + 2} → {slotLabel}");
        }

        var prompt = $@"Create a final collage poster using the template from Reference Image 1 as the EXACT design reference.

SLOT ASSIGNMENTS (follow precisely):
{slotMapping}

Instructions:
- Use Reference Image 1 as the EXACT template — same layout, background, decorations, text styling, frames, borders
- Place each person's photo into their ASSIGNED slot as listed above
- FACE LOCK: Every person's face must be a PHOTOREALISTIC, EXACT copy from their reference image — same eyes, nose, lips, jawline, skin tone
- Maintain the exact same layout, spacing, and frame positions as the template
- Keep ALL decorative elements (balloons, confetti, text, borders, etc.) from the template
- Each person's photo should fit naturally into their assigned slot/frame
- Maintain color harmony throughout the entire composition
- Keep text elements readable and in the same positions as the template
- The final output should look like a professional, cohesive collage poster
- Output a complete, high-quality collage poster matching the template exactly";

        var (_, resultBytes) = await _gemini.EditImageAsync(prompt, images, width, height);
        return resultBytes;
    }

    private static string ExtractJson(string text)
    {
        var match = Regex.Match(text, @"\{[\s\S]*\}", RegexOptions.Singleline);
        return match.Success ? match.Value : text;
    }
}
