# Trendy Photo Collage — Seeding Guide

## Overview

Trendy Photo Collage templates are AI-powered style presets that transform user-uploaded photos into themed photo compositions. Each template is a `StylePreset` record seeded into the database on application startup via `Program.cs`.

The system uses **Gemini AI** to generate the final image based on a detailed prompt template, while enforcing strict facial identity preservation rules defined in `CollageRules.txt`.

---

## Architecture

```
User uploads photo
       │
       ▼
QuickStyle.razor (UI) ──► StylePreset selected (Category = "Trendy Photo Collage")
       │
       ▼
PromptTemplate + FacialIdentitySuffix = EffectivePrompt
       │
       ▼
Gemini AI generates image
       │
       ▼
Auto-enhance (4x upscale) ──► Download
```

### Key Files

| File | Purpose |
|------|---------|
| `Program.cs` (lines ~1923–2313) | Seeds all Trendy Photo Collage presets into DB |
| `Models/StylePreset.cs` | Data model with `EffectivePrompt` (prompt + identity suffix) |
| `Services/StylePresetService.cs` | CRUD operations for presets |
| `Components/Pages/QuickStyle.razor` | UI — displays presets by category |
| `Components/Pages/Settings.razor` | Admin — test style, set thumbnails |
| `CollageRules.txt` | Master rules for identity preservation |
| `Data/AppDbContext.cs` | EF Core context, original preset seeds |

---

## StylePreset Model

```csharp
public class StylePreset
{
    public int Id { get; set; }                // Auto-generated
    public string Name { get; set; }           // Unique display name (max 50 chars)
    public string Description { get; set; }    // User-facing description (max 200 chars)
    public string PromptTemplate { get; set; } // AI prompt instruction (unlimited)
    public string Category { get; set; }       // Must be "Trendy Photo Collage"
    public string IconEmoji { get; set; }      // Display emoji (max 10 chars)
    public string? AccentColor { get; set; }   // CSS hex color (max 10 chars)
    public string? ThumbnailPath { get; set; } // Path to preview image (max 500 chars)
    public bool IsActive { get; set; }         // true = visible to users
    public int SortOrder { get; set; }         // Display ordering within category
}
```

### EffectivePrompt

Every `StylePreset` automatically appends a `FacialIdentitySuffix` to the `PromptTemplate` at runtime via the `EffectivePrompt` computed property. This suffix enforces:

1. Exact facial feature recreation
2. Expression lock
3. Hair & distinguishing feature preservation
4. Style as visual filter (not reimagining)
5. Person count preservation
6. Multi-panel consistency

---

## Seeding Pattern

Each preset follows this exact pattern in `Program.cs`:

```csharp
// ── Seed [Template Name] ──
try
{
    await db.Database.ExecuteSqlRawAsync(@"
        IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = '[Template Name]')
            INSERT INTO StylePresets
                (Name, Description, PromptTemplate, Category, IconEmoji, AccentColor, IsActive, SortOrder)
            VALUES
                (N'[Name]', N'[Description]', N'[PromptTemplate]',
                 N'Trendy Photo Collage', N'[Emoji]', '[HexColor]', 1, [SortOrder]);

        UPDATE StylePresets SET
            Description = N'[Description]',
            PromptTemplate = N'[PromptTemplate]'
        WHERE Name = '[Template Name]';
    ");
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "[Template Name] StylePreset seeding failed (non-fatal)");
}
```

### How It Works

1. **Guard clause:** `IF NOT EXISTS` prevents duplicate inserts on repeated startups
2. **INSERT:** Creates the preset if it doesn't exist
3. **UPDATE:** Always runs — updates the prompt/description to the latest version (allows prompt refinement without manual DB edits)
4. **try-catch:** All seeding is non-fatal — app starts even if seeding fails
5. **Cleanup:** At the end of all seeding, a cleanup step removes any presets the user previously deleted (tracked in `DeletedStyleSeeds` table)

### Deletion Tracking

```sql
-- When a user deletes a preset via Settings UI:
INSERT INTO DeletedStyleSeeds (Name) VALUES ('Template Name');
DELETE FROM StylePresets WHERE Name = 'Template Name';

-- On every app startup, after all seeding:
DELETE FROM StylePresets WHERE Name IN (SELECT Name FROM DeletedStyleSeeds);
```

This ensures deleted presets don't reappear after restart.

---

## CollageRules.txt — Mandatory Prompt Rules

Every prompt template MUST incorporate these rules from `CollageRules.txt`:

| Rule | Requirement |
|------|-------------|
| **Identity Lock** | Face must remain EXACTLY the same — no changes to facial structure, eyes, nose, lips, jawline, expression |
| **Skin Tone** | Maintain original skin tone, undertone, texture — no lightening, darkening, smoothing |
| **Gender Neutrality** | Do not add/remove gender-specific features, makeup, beard |
| **Anatomy Protection** | Preserve natural proportions — no stretching, distortion, extra fingers |
| **Elements** | If image is majorly white, add unique floral/decorative elements |
| **Allowed Changes** | Background, lighting, color correction, framing, clothing only |
| **Style Rule** | Face must remain photorealistic — no artistic rendering on face |
| **Realism** | Natural lighting/shadows — output should look like a real photograph |
| **No Names** | No names to be considered in output |

### Negative Constraints (must be in every prompt)

- No face swap
- No identity change
- No facial reshaping
- No skin tone change
- No beautification
- No cartoon/artistic face rendering
- No artificial skin smoothing

---

## How to Add a New Template

> **IMPORTANT:** For the current recommended approach to adding ANY new style preset (not just Trendy Photo Collage), see **[`docs/StylePreset-SeedingGuide.md`](StylePreset-SeedingGuide.md)**. The method below is outdated and should NOT be used for new styles.

### Current Method: `incrementalStyles` Array

Add a new entry to the `incrementalStyles` array in `Program.cs` (after the `} // end if (!seedAlreadyDone)` block). This runs on every startup, force-clears `DeletedStyleSeeds`, and upserts the style. No version bumping needed.

See **[`docs/StylePreset-SeedingGuide.md`](StylePreset-SeedingGuide.md)** for full step-by-step instructions.

### Test

1. Run the application
2. Go to **Settings** page
3. Find the new template under its category
4. Click **Test Prompt** -- upload a photo and verify the result
5. Click **Use as Thumbnail** to save the preview image

---

## SQL Escaping Rules

When writing prompt text in SQL string literals:

| Character | Escape As |
|-----------|-----------|
| Single quote `'` | `''` (doubled) |
| Newline | Not allowed — keep on single line |
| Unicode | Use `N'...'` prefix for NVARCHAR |

---

## Current Trendy Photo Collage Templates

| Sort | Name | Emoji | Accent | Description |
|------|------|-------|--------|-------------|
| 200 | Silhouette Double Exposure | 🖤 | #37474F | Silhouette with misty forest double exposure |
| 201 | Birthday Ghost Portrait | 🎂 | #9C27B0 | Full-body + ghost duplicate, purple watercolor florals, "Happy Birthday" |
| 202 | Romantic Love Overlay | 💕 | #D4AF37 | Close-up overlay + full-body, golden "Love" script |
| 203 | Romantic Palace & Doves | 🕊️ | #D4A017 | Grand palace, chandelier, white doves |
| 204 | B&W Multi-Panel Wedding | 🖤 | #424242 | 5-6 panel grayscale grid, multiple poses |
| 205 | Vertical Strip Anniversary | 💐 | #E8A0BF | 3 vertical strips, pastel florals, "Happy Anniversary" |
| 206 | Golden Monochrome Romance | ✨ | #DAA520 | Full golden/sepia monochrome, dreamy smoke overlay |
| 207 | Wedding Anniversary Festive | 🎉 | #E65100 | Colorful festive, ghost overlay, bokeh, "Happy Anniversary" |
| 208 | Floral Frame Couple Portrait | 🦋 | #C62828 | Watercolor roses & butterflies framing |
| 209 | Landmark Wedding Portrait | 🏛️ | #1565C0 | Heritage landmark backdrop, cinematic |
| 210 | Neon Outline Night | 💫 | #00E5FF | Dark street, neon silhouette outline |
| 211 | Beach Close-up Ghost Blend | 🌊 | #80DEEA | Large close-up + faded full-body at beach |
| 212 | Bridal Gold Magazine | 👑 | #B8860B | Golden sepia ghost, magazine album layout |
| 213 | Ink Wash Romance | 🖋️ | #37474F | Ink wash illustration, flower petals |
| 214 | Rain Umbrella Night | 🌧️ | #5C6BC0 | Transparent umbrella, heavy rain, bokeh |
| 215 | Soft Pastel Watercolor Portrait | 🌸 | #F8BBD0 | Pink/lavender watercolor, soft florals |
| 216 | Wheat Field Romance | 🌾 | #8D6E63 | Golden wheat field, rustic outdoor |
| 217 | Torn Paper Bridal Collage | 📜 | #B71C1C | 3 panels, torn paper edges, moody cinematic |

---

## Prompt Writing Best Practices

1. **Always detect person count first** — prevents adding phantom people to solo uploads
2. **Provide IF ONE PERSON / IF TWO OR MORE branches** — ensures the template works for any input
3. **Be specific about layout** — use fractions ("lower 20% of canvas"), positions ("right-of-center"), and sizes ("filling upper two-thirds")
4. **Describe lighting direction** — "backlit", "golden-hour from left", "rim light"
5. **Name the color palette explicitly** — "deep burgundy reds, warm golds, amber tones"
6. **Include negative constraints at the end** — "No text, no names, no dates, no watermarks"
7. **Reference CollageRules.txt ELEMENTS rule** — if the composition might be too plain/white, instruct to add unique decorative elements
8. **Keep faces photorealistic** — even in artistic styles (watercolor, ink wash), state "faces are the ONE element that stays sharp and realistic"
9. **Never include specific names or dates** — only generic themed text like "Happy Birthday", "Happy Anniversary"
10. **Test with both single-person and couple photos** — verify both branches work correctly
