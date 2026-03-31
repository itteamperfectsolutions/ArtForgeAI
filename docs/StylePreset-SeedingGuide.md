# StylePreset Seeding Guide

## How Seeding Works

There are **two** seeding mechanisms in `Program.cs`:

| Mechanism | When it runs | Where in code | Use for |
|-----------|-------------|---------------|---------|
| **Bulk seeds** | Only once (seed-version gate) | Inside `if (!seedAlreadyDone)` | Original/bulk styles already shipped |
| **Incremental seeds** | Every startup | `incrementalStyles` array AFTER the gate | All NEW styles going forward |

### Why two mechanisms?

The bulk seeds (100+ styles) are expensive to re-run. They're gated by `CURRENT_SEED_VERSION` in the `__SeedVersion` table. Once stamped, they never run again.

The **incremental seeds** array runs every startup and handles new styles. It:

1. **Force-clears `DeletedStyleSeeds`** for the style name (so dev-added styles always appear)
2. **Upserts** -- inserts if missing, updates prompt/metadata if already present
3. Requires **zero version bumping** -- just add an entry and restart

---

## Adding a New Style (Step by Step)

### Step 1: Design the prompt

Follow `CollageRules.txt` rules:
- Face must remain 100% photorealistic and identity-preserved
- No face swap, beautification, skin smoothing, or reshaping
- Maintain exact skin tone, natural proportions
- Lighting and shadows must remain natural and consistent

### Step 2: Add entry to `incrementalStyles` array

Open `Program.cs`, find the `incrementalStyles` array (search for `// ── Add future styles here`), and add a new entry **before** the comment:

```csharp
var incrementalStyles = new[]
{
    // ... existing styles ...

    new {
        Name        = "Your Style Name",           // max 50 chars, must be unique
        Description = "Short user-facing summary",  // max 200 chars
        Prompt      = @"Your full prompt here...",  // use @"" for verbatim string
        Category    = "Artistic",                   // Artistic, Fun, Professional, Abstract, Regional, etc.
        Emoji       = "\U0001F58B\uFE0F",           // Unicode escape for emoji
        Color       = "#8B4513",                    // Hex accent color
        SortOrder   = 221                           // Next available number
    },
    // ── Add future styles here as new entries in this array ──
};
```

### Step 3: Build and run

```
dotnet build && dotnet run
```

The style will appear in **Settings > Style Presets** immediately. No database migration, no version bump, no manual SQL needed.

### Step 4: Test and set thumbnail

1. Go to **Settings** page
2. Find the new style under its category
3. Click **Test Prompt** -- upload a photo and verify
4. Click **Use as Thumbnail** to save the preview

---

## Common Emoji Unicode Escapes

| Emoji | Escape |
|-------|--------|
| 🖋️ | `"\U0001F58B\uFE0F"` |
| 🎨 | `"\U0001F3A8"` |
| 🖌️ | `"\U0001F58C\uFE0F"` |
| ✨ | `"\u2728"` |
| 🌸 | `"\U0001F338"` |
| 🎭 | `"\U0001F3AD"` |
| 💎 | `"\U0001F48E"` |
| 🔥 | `"\U0001F525"` |
| 🌀 | `"\U0001F300"` |
| 👑 | `"\U0001F451"` |

---

## What NOT to Do

| Mistake | Why it breaks | What to do instead |
|---------|--------------|-------------------|
| Add styles inside `if (!seedAlreadyDone)` | Skipped when seed version is already stamped | Use the `incrementalStyles` array |
| Bump `CURRENT_SEED_VERSION` for a single new style | Forces re-run of ALL 100+ seed queries | Use the `incrementalStyles` array |
| Use raw SQL with `N'...'` strings for new styles | Apostrophes need `''` escaping, error-prone | Use C# `@""` strings with `@p0` parameters |
| Check `DeletedStyleSeeds` before inserting | Previously deleted styles will never reappear | Incremental array force-clears `DeletedStyleSeeds` |
| Forget `CollageRules.txt` constraints in prompt | Face identity may be altered by AI | Always include identity preservation rules |

---

## How the Incremental Seed Loop Works

```csharp
foreach (var s in incrementalStyles)
{
    // 1. Remove from blocklist so it ALWAYS appears
    DELETE FROM DeletedStyleSeeds WHERE Name = @p0

    // 2. Upsert: insert or update
    IF NOT EXISTS (SELECT 1 FROM StylePresets WHERE Name = @p0)
        INSERT INTO StylePresets (...) VALUES (...)
    ELSE
        UPDATE StylePresets SET ... WHERE Name = @p0
}
```

This means:
- **New styles** are inserted on first startup
- **Prompt edits** in the code are applied on next restart (via UPDATE)
- **User deletions** of incremental styles are overridden (force-cleared from `DeletedStyleSeeds`)
- If a user truly wants to remove an incremental style permanently, remove it from the `incrementalStyles` array and delete manually from DB

---

## Database Tables Involved

| Table | Purpose |
|-------|---------|
| `StylePresets` | All style presets (auto-increment Id) |
| `StyleGroups` | Optional grouping for presets |
| `DeletedStyleSeeds` | Tracks user-deleted styles to prevent re-seeding |
| `__SeedVersion` | Gates bulk seed re-runs (single Version int) |

---

## Prompt Template Structure

For styles that apply to people/portraits, follow this structure:

```
1. CRITICAL: identity preservation statement
2. STYLE DESCRIPTION: what the artistic style looks like
3. COLORING RULES: what gets colored vs greyscale
4. COMPOSITION: background, layout, framing
5. MOOD: emotional tone of the output
6. STRICT RULES: face photorealism, no beautification, etc.
```

For styles that handle single AND group photos:

```
1. CRITICAL: identity preservation
2. Person count detection: "DETECT how many people... do NOT add or remove anyone"
3. IF ONE PERSON: [solo description]
4. IF TWO OR MORE PEOPLE: [group description]
5. Style/lighting/color details
6. Strict rules footer
```
