using System.Text.Json;
using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class CollageTemplateService : ICollageTemplateService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IGeminiImageService _gemini;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CollageTemplateService> _logger;

    public CollageTemplateService(
        IDbContextFactory<AppDbContext> dbFactory,
        IGeminiImageService gemini,
        IWebHostEnvironment env,
        ILogger<CollageTemplateService> logger)
    {
        _dbFactory = dbFactory;
        _gemini = gemini;
        _env = env;
        _logger = logger;
    }

    // ── CRUD ──

    public async Task<List<CollageTemplate>> GetAllAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CollageTemplates
            .OrderBy(t => t.Category)
            .ThenBy(t => t.SortOrder)
            .ToListAsync();
    }

    public async Task<List<CollageTemplate>> GetActiveAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CollageTemplates
            .Where(t => t.IsActive)
            .OrderBy(t => t.Category)
            .ThenBy(t => t.SortOrder)
            .ToListAsync();
    }

    public async Task<List<CollageTemplate>> GetByCategoryAsync(string category)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CollageTemplates
            .Where(t => t.IsActive && t.Category == category)
            .OrderBy(t => t.SortOrder)
            .ToListAsync();
    }

    public async Task<List<string>> GetCategoriesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CollageTemplates
            .Where(t => t.IsActive)
            .Select(t => t.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    public async Task<CollageTemplate?> GetByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CollageTemplates.FindAsync(id);
    }

    public async Task CreateAsync(CollageTemplate template)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var maxSort = await db.CollageTemplates.MaxAsync(t => (int?)t.SortOrder) ?? 0;
        template.SortOrder = maxSort + 1;
        template.CreatedAtUtc = DateTime.UtcNow;
        db.CollageTemplates.Add(template);
        await db.SaveChangesAsync();
    }

    public async Task UpdateAsync(CollageTemplate template)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.CollageTemplates.Update(template);
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var template = await db.CollageTemplates.FindAsync(id);
        if (template is not null)
        {
            db.CollageTemplates.Remove(template);
            await db.SaveChangesAsync();
        }
    }

    // ── Generation ──

    public async Task<byte[]> GenerateSlotImageAsync(CollageTemplate template, byte[] masterPhoto,
        string mimeType, int slotIndex, int width, int height)
    {
        var slotDescs = ParseSlotDescriptions(template.SlotDescriptionsJson);
        var slotDesc = slotIndex < slotDescs.Count ? slotDescs[slotIndex] : $"Photo variation {slotIndex + 1}";

        string prompt;
        if (slotIndex == template.MasterSlotIndex)
        {
            // Master slot — keep exactly as-is, only change background
            prompt = $@"Process this photo for a {template.Category} collage poster.

SLOT: {slotDesc} (this is the MASTER/hero photo)
COLOR THEME: {template.ColorTheme}
MOOD: {template.Mood}

Instructions:
- FACE LOCK: Keep the person's face EXACTLY as in the reference — same face, eyes, nose, lips, jawline, skin tone, hair. Do NOT alter ANY facial feature.
- Keep the person's EXACT pose, clothes, and expression from the source photo
- Remove the original background and replace with a themed background using the color palette ({template.ColorTheme})
- Add subtle themed decorations in the BACKGROUND ONLY: {template.DecorativeElements}
- Apply color grading to match the {template.Mood} mood
- Output a high-quality, sharp, photorealistic photo
- MANDATORY: Preserve 1:1 pixel-perfect facial geometry and features; do not alter, redraw, or enhance eyes, nose, mouth, teeth, or expression—apply color and lighting adjustments only to the surrounding pixels";
        }
        else
        {
            // Non-master slots — generate the SAME person in a DIFFERENT natural pose
            prompt = $@"Using the person from this reference photo, create a NEW photo of them in a DIFFERENT pose for a {template.Category} collage.

POSE/FRAMING: {slotDesc}
COLOR THEME: {template.ColorTheme}
MOOD: {template.Mood}

CRITICAL RULES:
1. FACE LOCK (HIGHEST PRIORITY): The person's face MUST be a PHOTOREALISTIC, EXACT copy from the reference. Same face shape, eyes, nose, lips, jawline, skin tone, hair color, hairstyle. Do NOT alter ANY facial feature.
2. CHANGE THE POSE: Generate the person in a DISTINCTLY DIFFERENT body pose/angle as described: {slotDesc}. The pose MUST be clearly different from the original photo. Examples of changes: different head tilt, looking in a different direction, different arm position, different body angle, different expression (smiling vs laughing vs serene).
3. KEEP THE SAME PERSON: Same clothes/outfit as the source photo, same body type, same skin tone. Only the pose, angle, expression, and framing should change.
4. BACKGROUND: Remove original background. Use a fresh themed background with the color palette ({template.ColorTheme}) and subtle decorations ({template.DecorativeElements}).
5. FRAMING: {slotDesc} — make this framing distinctly different from other slots.
6. Do NOT add any props, accessories, or objects that weren't in the original photo.
7. Output a high-quality, sharp, photorealistic photo that looks like a different shot from the same photo session.
8. MANDATORY: Preserve 1:1 pixel-perfect facial geometry and features; do not alter, redraw, or enhance eyes, nose, mouth, teeth, or expression—apply color and lighting adjustments only to the surrounding pixels.";
        }

        var images = new List<(byte[] data, string mimeType)> { (masterPhoto, mimeType) };
        var (_, resultBytes) = await _gemini.EditImageAsync(prompt, images, width, height);

        _logger.LogInformation("Generated slot {SlotIndex} ({SlotDesc}) for template {TemplateName}",
            slotIndex, slotDesc, template.Name);

        return resultBytes;
    }

    public async Task<byte[]> ComposeCollageAsync(CollageTemplate template, List<byte[]> slotImages,
        int width, int height, CollagePersonalisation? personalisation = null)
    {
        var slotDescs = ParseSlotDescriptions(template.SlotDescriptionsJson);
        var images = new List<(byte[] data, string mimeType)>();

        // If a thumbnail exists, send it as the FIRST reference image (the design template)
        var hasThumbnail = false;
        if (!string.IsNullOrEmpty(template.ThumbnailPath))
        {
            var thumbFullPath = Path.Combine(_env.WebRootPath,
                template.ThumbnailPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(thumbFullPath))
            {
                var thumbBytes = await File.ReadAllBytesAsync(thumbFullPath);
                var thumbMime = template.ThumbnailPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png" : "image/jpeg";
                images.Add((thumbBytes, thumbMime));
                hasThumbnail = true;
                _logger.LogInformation("Sending template thumbnail as design reference for {Name}", template.Name);
            }
        }

        // Then add all slot images
        var slotMapping = new System.Text.StringBuilder();
        var imgOffset = hasThumbnail ? 2 : 1; // offset for reference image numbering
        for (int i = 0; i < slotImages.Count; i++)
        {
            images.Add((slotImages[i], "image/png"));
            var desc = i < slotDescs.Count ? slotDescs[i] : $"Photo {i + 1}";
            slotMapping.AppendLine($"- Reference Image {i + imgOffset}: {desc}");
        }

        // Build personalisation text instructions
        var textParts = new List<string>();

        // Name — displayed in stylish font
        var personName = personalisation?.Name?.Trim();
        if (!string.IsNullOrEmpty(personName))
        {
            textParts.Add($"Display the name '{personName}' in a LARGE, STYLISH calligraphy/script font " +
                "with an appropriate color that complements the design. Place it prominently at the bottom center.");
        }

        // Occasion — as subtitle text
        var occasion = personalisation?.Occasion?.Trim();
        if (!string.IsNullOrEmpty(occasion))
        {
            textParts.Add($"Below the name (or at the bottom if no name), display '{occasion}' " +
                "in clean uppercase letters, smaller than the name, in a color that matches the theme.");
        }

        // Static text overlay from template
        if (!string.IsNullOrEmpty(template.TextOverlay) && string.IsNullOrEmpty(occasion))
        {
            textParts.Add($"Add the title text '{template.TextOverlay}' in a stylish font that matches the {template.Mood} theme.");
        }

        // Message — auto-generate if occasion given but message blank
        var message = personalisation?.Message?.Trim();
        if (string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(occasion))
        {
            textParts.Add($"Generate a short warm heartfelt message (1-2 lines) for '{occasion}' " +
                "and display it in small elegant text at the bottom of the collage.");
        }
        else if (!string.IsNullOrEmpty(message))
        {
            textParts.Add($"Display this message in small elegant text at the bottom: '{message}'");
        }

        // Date — as a small calendar month grid
        if (personalisation?.Date.HasValue == true)
        {
            var d = personalisation.Date.Value;
            textParts.Add($"Include a small elegant CALENDAR MONTH GRID for {d:MMMM yyyy} somewhere in the design " +
                $"(corner or bottom area). Highlight day {d.Day} with a circle or color accent. " +
                "Keep it subtle and small so it doesn't dominate the composition.");
        }

        var titleInstruction = textParts.Count > 0
            ? "TEXT/OVERLAY INSTRUCTIONS: " + string.Join(" ", textParts)
            : "No text overlay needed.";

        string prompt;
        if (hasThumbnail)
        {
            // Thumbnail-guided composition — match the template design EXACTLY
            prompt = "Reference Image 1 is the TEMPLATE DESIGN — you MUST replicate this EXACT layout, colors, decorations, " +
                "text placement, frame positions, background style, and overall composition. " +
                "Replace the placeholder/silhouette photos in the template with the REAL person photos provided. " +
                $"PHOTO ASSIGNMENTS: {slotMapping}" +
                "CRITICAL RULES: " +
                "- The output must look IDENTICAL to the template design (Reference Image 1) in terms of layout, colors, " +
                "decorative elements, text style, frame shapes/positions, and background. " +
                "- ONLY replace the photo placeholders with the real person photos — keep EVERYTHING else from the template. " +
                "- FACE LOCK: Every person's face must remain PHOTOREALISTIC and IDENTICAL to their reference image. " +
                "Same eyes, nose, lips, jawline, skin tone. Do NOT alter any facial feature. " +
                "- Fit each person photo naturally into the corresponding frame/position from the template. " +
                $"- {titleInstruction} " +
                "- The final output should be a professional, print-ready poster that matches the template exactly. " +
                "- Output in 2:3 portrait aspect ratio.";
        }
        else
        {
            // No thumbnail — fallback to text-based layout description
            var layoutDesc = !string.IsNullOrEmpty(template.LayoutDescription)
                ? template.LayoutDescription
                : "Arrange the photos in an attractive collage layout with the master photo prominently centered.";

            prompt = $"Create a professional {template.Category} collage poster. " +
                $"COLOR THEME: {template.ColorTheme}. MOOD: {template.Mood}. " +
                $"DECORATIVE ELEMENTS: {template.DecorativeElements}. " +
                $"LAYOUT INSTRUCTIONS (FOLLOW EXACTLY): {layoutDesc} " +
                $"PHOTO ASSIGNMENTS: {slotMapping}" +
                "CRITICAL RULES: " +
                "- Compose ALL reference images into a single cohesive poster following the LAYOUT INSTRUCTIONS exactly. " +
                "- FACE LOCK: Every person's face must remain PHOTOREALISTIC and IDENTICAL to their reference image. " +
                "- Follow the layout description precisely. " +
                $"- {titleInstruction} " +
                $"- Background should use the color theme: {template.ColorTheme}. " +
                "- Output a complete, high-quality poster in 2:3 portrait aspect ratio.";
        }

        var (_, resultBytes) = await _gemini.EditImageAsync(prompt, images, width, height);

        _logger.LogInformation("Composed final collage for template {TemplateName} with {SlotCount} images",
            template.Name, slotImages.Count);

        return resultBytes;
    }

    private static List<string> ParseSlotDescriptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
