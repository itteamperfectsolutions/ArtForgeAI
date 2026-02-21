using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArtForgeAI.Models;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace ArtForgeAI.Services;

/// <summary>
/// Hybrid prompt enhancement: local-first with AI fallback.
///
/// COST REDUCTION STRATEGIES:
///   1. Local intent classification via regex — zero AI cost
///   2. Template engine for literal edits — zero AI cost
///   3. Local structured prompt builder for simple requests — zero AI cost
///   4. Prompt complexity detection — skip AI if user prompt is already detailed
///   5. In-memory cache — identical prompts reuse cached enhancements
///   6. AI called ONLY for complex creative requests that need reasoning
///
/// QUALITY STRATEGIES:
///   1. Style randomization pools — guaranteed variety
///   2. Provider-specific prompt formatting
///   3. Structured prompt architecture
///   4. Temperature tuning per request type
/// </summary>
public partial class PromptEnhancerService : IPromptEnhancerService
{
    private readonly ChatClient _chatClient;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PromptEnhancerService> _logger;
    // Random.Shared is thread-safe (.NET 6+)

    // ═══════════════════════════════════════════════════════════════════
    //  CACHE — avoid duplicate AI calls
    // ═══════════════════════════════════════════════════════════════════

    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private record CacheEntry(string Enhanced, DateTime CreatedAt);

    private static string ComputeCacheKey(string prompt, ImageProvider provider, bool isEdit, bool hasRef)
    {
        var raw = $"{prompt}|{provider}|{isEdit}|{hasRef}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16];
    }

    private static string? GetCached(string key)
    {
        if (_cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.CreatedAt < CacheTtl)
            return entry.Enhanced;
        _cache.TryRemove(key, out _);
        return null;
    }

    private static void SetCache(string key, string enhanced)
    {
        _cache[key] = new CacheEntry(enhanced, DateTime.UtcNow);
        // Evict old entries if cache grows too large
        if (_cache.Count > 500)
        {
            var cutoff = DateTime.UtcNow - CacheTtl;
            foreach (var kv in _cache)
                if (kv.Value.CreatedAt < cutoff)
                    _cache.TryRemove(kv.Key, out _);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LOCAL INTENT CLASSIFICATION — zero AI cost
    // ═══════════════════════════════════════════════════════════════════

    private enum EditIntent { LiteralEdit, SceneChange, StyleTransfer, CreativeTransform, MultiInstruction }

    // Compiled regexes for performance
    [GeneratedRegex(@"\b(remove|delete|erase|clear|cut\s*out)\b.*(background|bg|backdrop)", RegexOptions.IgnoreCase)]
    private static partial Regex BgRemovalPattern();

    [GeneratedRegex(@"\b(white|black|transparent|solid|plain|blur|blurred)\b.*(background|bg|backdrop)", RegexOptions.IgnoreCase)]
    private static partial Regex BgReplacePattern();

    [GeneratedRegex(@"\b(background|bg|backdrop)\b.*(white|black|transparent|solid|plain|blur|blurred)", RegexOptions.IgnoreCase)]
    private static partial Regex BgReplacePattern2();

    [GeneratedRegex(@"\b(brighten|darken|sharpen|blur|crop|resize|rotate|flip|mirror|contrast|saturation|hue|denoise|upscale|enhance quality|fix|correct|adjust|retouch|smooth|clean\s*up|remove\s*(text|watermark|object|logo|blemish|wrinkle))\b", RegexOptions.IgnoreCase)]
    private static partial Regex LiteralEditPattern();

    [GeneratedRegex(@"\b(anime|cartoon|sketch|pencil\s*drawing|oil\s*painting|watercolor|pop\s*art|pixel\s*art|comic|manga|3d\s*render|clay|sculpture|stained\s*glass|mosaic|cubist|impressionist|surreal|abstract|low\s*poly|voxel|charcoal|pastel|gouache|woodcut|linocut|vector\s*art|flat\s*design|line\s*art|cel[\s-]?shad|ukiyo|art\s*nouveau|baroque|renaissance)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StyleTransferPattern();

    [GeneratedRegex(@"\b(place|put|set|move|transport|teleport)\b.*(in|on|at|to|into|before|near|beside|inside|under|above)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SceneChangePattern();

    [GeneratedRegex(@"\b(walking|standing|sitting|running|flying|swimming|dancing|riding|climbing|playing)\b.*(in|on|at|through|across|along|before|near)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ActionScenePattern();

    // Detects clothing / outfit / appearance changes — always need AI
    [GeneratedRegex(@"\b(change|replace|swap|switch|modify|alter|update|transform)\b.*(cloth|outfit|dress|shirt|suit|wear|attire|costume|jacket|pants|trouser|skirt|uniform|tuxedo|gown|tie|hat|shoes|hair|hairstyle|makeup|accessories|jewelry|glasses|look|appearance|style)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AppearanceChangePattern();

    // Detects add/apply instructions — always need AI
    [GeneratedRegex(@"\b(add|apply|put|give|wear|dress\s+in|make\s+(?:it|them|him|her|the))\b.*(suit|tie|hat|crown|glasses|helmet|armor|wings|makeup|tattoo|beard|mustache|scarf|cape|hood|mask|necklace|ring|earring|bracelet|watch|cloth|outfit|dress|shirt|jacket|uniform|tuxedo|gown)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AddElementPattern();

    /// <summary>
    /// Counts distinct action/instruction signals in the prompt.
    /// A prompt with multiple actions (e.g. "remove background AND change clothing")
    /// must be routed to AI for proper handling.
    /// </summary>
    private static int CountInstructionSignals(string prompt)
    {
        int count = 0;
        var p = prompt.Trim();

        if (BgRemovalPattern().IsMatch(p) || BgReplacePattern().IsMatch(p) || BgReplacePattern2().IsMatch(p))
            count++;
        if (LiteralEditPattern().IsMatch(p))
            count++;
        if (StyleTransferPattern().IsMatch(p))
            count++;
        if (SceneChangePattern().IsMatch(p) || ActionScenePattern().IsMatch(p))
            count++;
        if (AppearanceChangePattern().IsMatch(p) || AddElementPattern().IsMatch(p))
            count++;

        return count;
    }

    private static EditIntent ClassifyIntent(string prompt)
    {
        var p = prompt.Trim();

        // ── PRIORITY 0: Multi-instruction detection ──
        // If the prompt has 2+ distinct instruction types, ALWAYS route to AI.
        // This prevents templates from handling only one part and losing the rest.
        // e.g. "remove background change clothing to suit with tie" = bg removal + appearance change
        if (CountInstructionSignals(p) >= 2)
            return EditIntent.MultiInstruction;

        // ── PRIORITY 0.5: Appearance / clothing / outfit changes always need AI ──
        // These require creative understanding, never handled by templates
        if (AppearanceChangePattern().IsMatch(p) || AddElementPattern().IsMatch(p))
            return EditIntent.CreativeTransform;

        // Priority 1: Background-only operations (very common, single intent)
        if (BgRemovalPattern().IsMatch(p) || BgReplacePattern().IsMatch(p) || BgReplacePattern2().IsMatch(p))
            return EditIntent.LiteralEdit;

        // Priority 2: Technical edits
        if (LiteralEditPattern().IsMatch(p))
            return EditIntent.LiteralEdit;

        // Priority 3: Style transfer
        if (StyleTransferPattern().IsMatch(p))
            return EditIntent.StyleTransfer;

        // Priority 4: Scene change (place subject somewhere)
        if (SceneChangePattern().IsMatch(p) || ActionScenePattern().IsMatch(p))
            return EditIntent.SceneChange;

        // Default: needs creative interpretation
        return EditIntent.CreativeTransform;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LOCAL TEMPLATE ENGINE — literal edits, zero AI cost
    // ═══════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> LiteralEditTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        // Background operations
        ["remove background"] = "Remove the entire background completely. Replace with clean solid white (#FFFFFF). Sharp precise cutout edges around the subject, no fringing, no halos, no artifacts. Maintain full subject detail, natural colors, and original quality.",
        ["remove bg"] = "Remove the entire background completely. Replace with clean solid white (#FFFFFF). Sharp precise cutout edges around the subject, no fringing, no halos, no artifacts. Maintain full subject detail, natural colors, and original quality.",
        ["white background"] = "Replace the background with pure clean solid white (#FFFFFF). Crisp edges around the subject. Professional product-photography style isolation. No shadows unless natural, no color spill, high resolution.",
        ["black background"] = "Replace the background with pure solid black (#000000). Clean separation between subject and background. Dramatic studio-style isolation. Sharp edges, no color bleeding.",
        ["transparent background"] = "Remove the background completely, making it transparent. Clean precise edges around the subject. No fringing or halos. Maintain all subject detail.",
        ["blur background"] = "Apply a natural gaussian blur to the background while keeping the subject perfectly sharp. Create a professional bokeh effect similar to f/1.4 aperture. Smooth, creamy background blur with natural light circles.",

        // Quality enhancements
        ["enhance"] = "Enhance the overall image quality. Increase sharpness and clarity. Improve color vibrancy and contrast. Reduce noise. Professional retouching quality.",
        ["upscale"] = "Upscale and enhance the image to maximum resolution. Sharpen details, reduce artifacts. Improve texture clarity. Professional quality output.",
        ["fix"] = "Fix and improve this image. Correct any color issues, enhance sharpness, improve exposure balance. Clean up any imperfections. Professional retouching.",
        ["denoise"] = "Remove noise and grain from the image while preserving fine details and textures. Clean, smooth result with natural appearance.",
        ["sharpen"] = "Sharpen the image to improve clarity and detail. Enhanced edge definition while maintaining natural appearance. No over-sharpening artifacts."
    };

    private static string? TryBuildLiteralEditLocally(string rawPrompt)
    {
        var normalized = rawPrompt.Trim().ToLowerInvariant();

        // Direct template match
        foreach (var (key, template) in LiteralEditTemplates)
        {
            if (normalized.Contains(key, StringComparison.OrdinalIgnoreCase))
                return template;
        }

        // Pattern-based: "remove background and make it [color]"
        var bgColorMatch = Regex.Match(rawPrompt,
            @"(?:remove|delete|erase|clear).*(?:background|bg).*(?:make|replace|change|set).*(?:it\s+)?(\w+)",
            RegexOptions.IgnoreCase);
        if (bgColorMatch.Success)
        {
            var color = bgColorMatch.Groups[1].Value.ToLowerInvariant();
            return $"Remove the entire background completely. Replace with clean solid {color}. Sharp precise cutout edges around the subject, no fringing, no halos, no artifacts. Maintain full subject detail, natural colors, and original quality.";
        }

        // Pattern-based: "[color] background"
        var colorBgMatch = Regex.Match(rawPrompt,
            @"(\w+)\s+(?:background|bg|backdrop)",
            RegexOptions.IgnoreCase);
        if (colorBgMatch.Success && BgReplacePattern().IsMatch(rawPrompt))
        {
            var color = colorBgMatch.Groups[1].Value.ToLowerInvariant();
            return $"Replace the background with clean solid {color}. Crisp precise edges around the subject. Professional isolation quality. No artifacts, no color spill, high resolution.";
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LOCAL PROMPT BUILDER — text-to-image without AI
    // ═══════════════════════════════════════════════════════════════════

    private static readonly string[] QualityBoosters =
    [
        "masterpiece, best quality, highly detailed, 8K resolution, professional",
        "ultra-detailed, sharp focus, stunning quality, award-winning, 8K UHD",
        "exquisite detail, professional quality, photorealistic, 4K, sharp",
        "breathtaking, highly detailed, professional photography, crystal clear, 8K",
        "stunning quality, intricate details, masterful composition, high resolution"
    ];

    private static readonly string[] CameraSpecs =
    [
        "shot on Canon EOS R5, 85mm f/1.4 lens, shallow depth of field",
        "shot on Sony A7R V, 50mm f/1.2, natural bokeh",
        "shot on Nikon Z9, 35mm f/1.4, wide angle perspective",
        "shot on Hasselblad X2D, 90mm f/2.5, medium format detail",
        "shot on Fujifilm GFX 100S, 110mm f/2, tack sharp"
    ];

    /// <summary>
    /// Builds a structured prompt locally without any AI call.
    /// Used when the user's prompt is simple enough to enhance with templates.
    /// </summary>
    private static string BuildLocalTextToImagePrompt(string rawPrompt, ImageProvider provider, StyleSeed style)
    {
        var sb = new StringBuilder();

        // Subject (user's prompt)
        sb.Append(rawPrompt.Trim().TrimEnd('.'));

        // Style injection
        sb.Append(". ");
        sb.Append(style.ArtStyle);

        // Lighting
        sb.Append(", ");
        sb.Append(style.Lighting);

        // Color palette
        sb.Append(", ");
        sb.Append(style.ColorPalette);

        // Atmosphere
        sb.Append(", ");
        sb.Append(style.Atmosphere);

        // Provider-specific additions
        if (provider == ImageProvider.Replicate)
        {
            // FLUX: concise, quality tokens
            sb.Append(", ");
            sb.Append(QualityBoosters[Random.Shared.Next(QualityBoosters.Length)]);
        }
        else
        {
            // DALL-E / Gemini: more descriptive
            sb.Append(". ");
            sb.Append(CameraSpecs[Random.Shared.Next(CameraSpecs.Length)]);
            sb.Append(". ");
            sb.Append(QualityBoosters[Random.Shared.Next(QualityBoosters.Length)]);
        }

        var result = sb.ToString();

        // FLUX: trim to 500 chars
        if (provider == ImageProvider.Replicate && result.Length > 500)
            result = result[..497] + "...";

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PROMPT COMPLEXITY DETECTION — skip AI if already detailed
    // ═══════════════════════════════════════════════════════════════════

    private static readonly string[] DetailIndicators =
    [
        "8k", "4k", "uhd", "ultra", "detailed", "photorealistic", "cinematic",
        "bokeh", "f/1", "f/2", "lens", "aperture", "depth of field", "dof",
        "masterpiece", "award", "professional", "studio", "lighting",
        "composition", "foreground", "background", "palette", "chiaroscuro",
        "rembrandt", "golden hour", "blue hour", "volumetric", "ray tracing",
        "octane render", "unreal engine", "artstation", "trending"
    ];

    /// <summary>
    /// Returns true if the prompt is already detailed enough that AI enhancement
    /// would add minimal value. This saves an API call.
    /// </summary>
    private static bool IsPromptAlreadyDetailed(string prompt)
    {
        if (prompt.Length < 120) return false;

        var lower = prompt.ToLowerInvariant();
        var detailCount = DetailIndicators.Count(d => lower.Contains(d));

        // If prompt has 3+ quality/technical keywords and is reasonably long, skip AI
        return detailCount >= 3 && prompt.Length >= 200;
    }

    /// <summary>
    /// Returns true if the prompt is very short/simple — can be handled locally.
    /// </summary>
    private static bool IsSimplePrompt(string prompt)
    {
        var words = prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // 15 words or fewer and no complex instructions
        return words.Length <= 15 && !prompt.Contains(',') && !prompt.Contains('.');
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STYLE POOLS — curated variety
    // ═══════════════════════════════════════════════════════════════════

    private static readonly string[] ArtStyles =
    [
        "photorealistic DSLR photography", "cinematic film still", "oil painting on canvas",
        "soft watercolor illustration", "detailed anime art", "vintage 35mm film photograph",
        "dramatic chiaroscuro painting", "pastel-toned digital illustration", "hyper-detailed 3D render",
        "neon-lit cyberpunk concept art", "dreamy ethereal fantasy art", "gritty urban street photography",
        "minimalist flat design", "impressionist painting", "baroque-style masterpiece",
        "retro 1980s poster art", "studio portrait photography", "comic book illustration",
        "moody noir cinematography", "surrealist dreamscape", "Japanese ukiyo-e woodblock style",
        "Art Nouveau decorative style", "Pixar-quality 3D animation", "Renaissance classical painting"
    ];

    private static readonly string[] LightingStyles =
    [
        "golden hour warm sunlight", "cool blue hour twilight", "dramatic rim lighting",
        "soft diffused overcast light", "neon-colored ambient glow", "harsh midday directional sun",
        "candlelit warm interior", "volumetric god rays through fog", "split lighting with deep shadows",
        "backlit silhouette with lens flare", "moonlit cool silver tones", "studio Rembrandt lighting",
        "bioluminescent ambient glow", "northern lights aurora overhead", "firelight flickering warmth",
        "morning mist soft scattering", "theatrical spotlight", "dappled light through leaves"
    ];

    private static readonly string[] ColorPalettes =
    [
        "warm amber and burnt sienna tones", "cool cobalt and silver palette", "rich emerald and gold",
        "muted earth tones with sage green", "vibrant neon pink and electric blue", "monochromatic sepia",
        "pastel lavender and blush pink", "deep burgundy and midnight blue", "tropical teal and coral",
        "desaturated moody grays", "sunset orange and deep purple gradient", "forest green and copper",
        "icy white and pale blue", "autumn red, orange, and golden yellow", "dusty rose and cream"
    ];

    private static readonly string[] Atmospheres =
    [
        "serene and peaceful", "epic and grandiose", "mysterious and enigmatic",
        "whimsical and playful", "dark and moody", "warm and nostalgic",
        "futuristic and sleek", "romantic and dreamy", "raw and gritty",
        "magical and otherworldly", "cozy and intimate", "dramatic and intense",
        "tranquil and meditative", "vibrant and energetic", "melancholic and poetic"
    ];

    private record StyleSeed(string ArtStyle, string Lighting, string ColorPalette, string Atmosphere);

    private static StyleSeed GenerateStyleSeed() => new(
        ArtStyles[Random.Shared.Next(ArtStyles.Length)],
        LightingStyles[Random.Shared.Next(LightingStyles.Length)],
        ColorPalettes[Random.Shared.Next(ColorPalettes.Length)],
        Atmospheres[Random.Shared.Next(Atmospheres.Length)]
    );

    // ═══════════════════════════════════════════════════════════════════
    //  AI SYSTEM PROMPTS — only used when local methods can't handle it
    // ═══════════════════════════════════════════════════════════════════

    private const string SystemPromptTextToImage = """
        You are a world-class AI image generation prompt engineer.

        ## RULES
        1. PRESERVE the user's EXACT intent — never change what they asked for
        2. ENHANCE with professional detail — add what's missing, not what contradicts
        3. USE the provided style direction — do not ignore it

        ## PROMPT STRUCTURE
        Subject → Setting → Style ({ART_STYLE}) → Lighting ({LIGHTING}) →
        Color ({COLOR_PALETTE}) → Mood ({ATMOSPHERE}) → Technical → Quality

        ## PROVIDER NOTES
        {PROVIDER_GUIDELINES}

        ## TECHNIQUES
        - Concrete nouns over vague adjectives
        - Spatial relationships, textures, materials, micro-details
        - Sensory language

        Under 900 chars (DALL-E/Gemini) or 500 chars (FLUX). Output ONLY the prompt.
        """;

    private const string SystemPromptCreativeEdit = """
        You are a world-class AI image editing prompt engineer.
        The target AI model (Gemini/DALL-E) receives the original image directly — it can SEE it.
        You may also receive the reference image(s) for context — use them to understand what the user is working with.

        ## TASK
        Write a DIRECT IMPERATIVE editing instruction for the AI image model.
        The user wants a transformation of their image. Use the style direction below.

        Art style: {ART_STYLE}
        Lighting: {LIGHTING}
        Color palette: {COLOR_PALETTE}
        Atmosphere: {ATMOSPHERE}

        ## RULES
        - Write as a DIRECT COMMAND to the image model: "Transform...", "Place...", "Edit..."
        - Reference the subject as "the subject in the image" — do NOT exhaustively describe it
        - Focus on what to DO: scene, environment, style, mood, composition
        - Keep it concise and action-oriented
        - Do NOT ask questions or provide commentary — output ONLY the editing instruction

        {PROVIDER_GUIDELINES}

        Under 700 chars (DALL-E/Gemini) or 400 chars (FLUX). Output ONLY the prompt, nothing else.
        """;

    /// <summary>
    /// System prompt for multi-image composition — combining subjects from multiple reference images.
    /// Used when the user provides 2+ reference images and wants them composed together.
    /// </summary>
    private const string SystemPromptMultiImageComposition = """
        You are an expert AI image composition prompt engineer.
        The AI model receives MULTIPLE reference images and can SEE all of them.

        ## CRITICAL TASK
        The user has provided MULTIPLE reference images. They want subjects from ALL images
        to be combined into a single cohesive scene.

        ## RULES
        1. EXPLICITLY reference each image: "the subject from image 1", "the subject from image 2", etc.
        2. DESCRIBE how the subjects should be positioned relative to each other
        3. PRESERVE the appearance and identity of ALL subjects from ALL images
        4. Include the user's specific instructions about composition, setting, and arrangement
        5. Do NOT drop, ignore, or merge any subject — EVERY reference image subject must appear
        6. Specify the scene, lighting, and mood to make the composition look natural

        ## FORMAT
        Start with: "Combine the subjects from all provided reference images into a single scene."
        Then describe the composition: who goes where, their interaction, the setting.
        End with quality/style directives.

        ## EXAMPLES
        User (2 images: a boy, a tiger): "boy sitting beside the tiger on a rock"
        Good: "Combine the subjects from all provided reference images into a single scene. Place the boy from image 2 sitting beside the tiger from image 1 on a large natural rock. The boy should be positioned to the right of the tiger, sitting comfortably with a relaxed posture. Both subjects must look exactly as they appear in their respective reference images — preserve their exact appearance, features, and proportions. Natural outdoor setting with soft warm lighting. The rock should be surrounded by lush green foliage. Photorealistic quality, sharp details."

        {PROVIDER_GUIDELINES}

        Under 900 chars (DALL-E/Gemini) or 500 chars (FLUX). Output ONLY the prompt.
        """;

    /// <summary>
    /// System prompt for multi-instruction edits — faithfully executes ALL user instructions.
    /// Used when the prompt contains 2+ distinct actions (e.g. "remove bg + change clothes").
    /// </summary>
    private const string SystemPromptMultiInstructionEdit = """
        You are an expert AI image editing prompt engineer.
        The target AI model (Gemini/DALL-E) receives the original image directly — it can SEE it.
        You may also receive the reference image(s) for context — use them to understand what the user is working with.

        ## CRITICAL RULE
        The user has given MULTIPLE instructions. You MUST address EVERY SINGLE ONE.
        Do NOT skip, merge, or simplify any instruction. Each action must be explicitly stated.

        ## YOUR TASK
        Write a DIRECT IMPERATIVE editing instruction for the AI image model.
        1. Parse the user's request into individual instructions
        2. For EACH instruction, write a clear, specific directive
        3. Combine them into a single cohesive editing prompt
        4. Preserve the subject's identity unless the user says otherwise

        ## FORMAT
        Write a direct command: "Remove X", "Change Y to Z", "Add W", "Place..."
        Do NOT describe the image. Do NOT ask questions. Output ONLY the editing instruction.

        ## EXAMPLES
        User: "remove background change clothing to suit with tie"
        Good: "Remove the entire background and replace with clean solid white. Change the person's clothing to a formal black business suit with a crisp white dress shirt and a dark silk necktie. Keep the person's face, expression, hair, and body proportions exactly the same. Professional studio portrait quality."

        User: "make background beach and change outfit to casual"
        Good: "Replace the background with a beautiful tropical beach scene with turquoise water, soft white sand, and palm trees. Change the person's outfit to casual summer clothes — a relaxed linen shirt and shorts. Preserve the person's face, features, and expression exactly. Natural golden hour beach lighting."

        {PROVIDER_GUIDELINES}

        Under 800 chars (DALL-E/Gemini) or 500 chars (FLUX). Output ONLY the prompt, nothing else.
        """;

    private const string SystemPromptIdentityPreservation = """
        You are a prompt engineer for photorealistic person recreation.

        ## STRUCTURE
        1. Identity Block (40%): Copy ALL physical/facial details verbatim
        2. Scene Block (30%): User's requested setting
        3. Technical Block (30%): "Photorealistic, Canon EOS R5, 85mm f/1.4, 8K, sharp focus on face"

        NEVER omit identity details. NEVER change appearance. Under 1500 chars. Output ONLY the prompt.
        """;

    private const string VisionSystemPrompt = """
        Forensic facial analysis. Extract MAXIMUM identity detail for exact person recreation.

        Cover: face shape, forehead, eyes (shape/color/lid), eyebrows, nose, mouth/lips,
        cheeks, chin, jawline, skin tone (paint-grade: "warm medium-brown with golden undertones"),
        marks/moles/dimples, hair (color/texture/style/length), age, build, clothing.

        300-400 words. 70% facial features. Precise language. Output ONLY the description.
        """;

    private static string GetProviderGuidelines(ImageProvider provider) => provider switch
    {
        ImageProvider.Gemini => "TARGET: Gemini. Use natural conversational language, narrative style, emotional detail.",
        ImageProvider.Replicate => "TARGET: FLUX. Concise comma-separated phrases. Front-load key elements. Under 500 chars. Include: masterpiece, best quality.",
        _ => "TARGET: DALL-E 3. Detailed natural language, spatial relationships, camera specs."
    };

    // ═══════════════════════════════════════════════════════════════════
    //  STYLE TRANSFER TEMPLATES — local, zero AI cost
    // ═══════════════════════════════════════════════════════════════════

    private static readonly Dictionary<string, string> StyleTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anime"] = "Transform into high-quality anime illustration style. Clean cel-shaded coloring with soft gradients. Large expressive eyes. Fine detailed linework. Vibrant colors. Lush painted background. Professional anime production quality, Studio Ghibli inspired.",
        ["cartoon"] = "Transform into vibrant cartoon illustration style. Bold outlines, flat vibrant colors, exaggerated proportions. Playful and dynamic. Clean vector-like rendering. Professional animation quality.",
        ["sketch"] = "Transform into a detailed pencil sketch. Fine graphite linework on white paper. Cross-hatching for shadows. Detailed texture work. Professional illustration quality. Clean precise lines with artistic shading.",
        ["pencil drawing"] = "Transform into a detailed pencil sketch. Fine graphite linework on white paper. Cross-hatching for shadows. Detailed texture work. Professional illustration quality. Clean precise lines with artistic shading.",
        ["oil painting"] = "Transform into a classical oil painting. Rich impasto brushstrokes, visible canvas texture. Deep saturated colors with luminous glazing technique. Dramatic chiaroscuro lighting. Museum-quality fine art.",
        ["watercolor"] = "Transform into a delicate watercolor painting. Soft translucent washes of color. Wet-on-wet bleeding edges. Visible paper grain. Light ethereal quality with gentle color transitions. Professional illustration.",
        ["pop art"] = "Transform into bold pop art style. Bright saturated primary colors, halftone dot patterns, thick black outlines. Andy Warhol / Roy Lichtenstein inspired. High contrast, graphic, iconic.",
        ["pixel art"] = "Transform into retro pixel art style. Clean pixel grid, limited color palette, 16-bit aesthetic. Sharp pixels, no anti-aliasing. Nostalgic video game art quality.",
        ["comic"] = "Transform into comic book illustration. Bold ink outlines, dynamic shading with halftone dots. Vibrant flat colors. Action-pose energy. Professional Marvel/DC quality linework.",
        ["manga"] = "Transform into Japanese manga style. Clean precise ink linework. Screentone shading. Expressive eyes and dynamic poses. Black and white with dramatic contrast. Professional manga quality.",
        ["3d render"] = "Transform into a hyper-detailed 3D render. Smooth subsurface scattering on skin. Ray-traced reflections and global illumination. Octane/Blender quality. Sharp geometric detail. Photorealistic material textures.",
        ["clay"] = "Transform into a charming clay/claymation style. Soft rounded shapes, visible fingerprint textures. Warm handmade quality. Stop-motion aesthetic. Miniature diorama feeling.",
        ["stained glass"] = "Transform into a stained glass window design. Bold black lead lines, jewel-toned translucent colors. Geometric segmentation. Cathedral-quality craftsmanship. Light shining through colored glass.",
        ["low poly"] = "Transform into low-poly 3D art style. Geometric faceted surfaces, minimal polygon count. Clean flat-shaded triangles. Modern minimalist aesthetic. Vibrant gradient colors across faces.",
        ["charcoal"] = "Transform into an expressive charcoal drawing. Rich dark values, dramatic contrast. Smudged soft edges with sharp accents. Textured paper grain visible. Raw, emotional artistic quality.",
        ["vector art"] = "Transform into clean vector illustration. Flat colors, smooth curves, geometric precision. Bold simplified shapes. Modern graphic design aesthetic. Print-ready quality.",
        ["line art"] = "Transform into elegant line art illustration. Single weight or varying line thickness. No fill colors, just expressive linework. Clean, minimal, sophisticated. Professional ink illustration."
    };

    private static string? TryBuildStyleTransferLocally(string rawPrompt)
    {
        var lower = rawPrompt.ToLowerInvariant().Trim();
        foreach (var (style, template) in StyleTemplates)
        {
            if (lower.Contains(style, StringComparison.OrdinalIgnoreCase))
                return template;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSTRUCTOR
    // ═══════════════════════════════════════════════════════════════════

    public PromptEnhancerService(
        IOptions<OpenAiOptions> options,
        IWebHostEnvironment env,
        ILogger<PromptEnhancerService> logger)
    {
        _chatClient = new ChatClient(options.Value.PromptModel, options.Value.ApiKey);
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Loads reference images from disk and creates ChatMessageContentPart list
    /// for including in vision-enabled AI calls.
    /// </summary>
    private async Task<List<ChatMessageContentPart>> LoadReferenceImagePartsAsync(List<string>? paths)
    {
        var parts = new List<ChatMessageContentPart>();
        if (paths is null || paths.Count == 0) return parts;

        foreach (var refPath in paths)
        {
            try
            {
                var fullPath = SafeResolvePath(refPath);
                if (!File.Exists(fullPath)) continue;

                var imageBytes = await File.ReadAllBytesAsync(fullPath);
                var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                var mediaType = ext switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                parts.Add(ChatMessageContentPart.CreateImagePart(
                    BinaryData.FromBytes(imageBytes), mediaType));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load reference image");
            }
        }
        return parts;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PUBLIC API — IMAGE EDIT
    // ═══════════════════════════════════════════════════════════════════

    public async Task<PromptEnhancementResult> EnhanceForImageEditAsync(
        string rawPrompt, ImageProvider provider, int referenceImageCount = 1,
        List<string>? referenceImagePaths = null)
    {
        try
        {
            // ── Step 0: Multi-image composition path ──
            // When the user provides 2+ reference images, use a composition-aware prompt
            // that explicitly tells the model to combine subjects from ALL images.
            if (referenceImageCount > 1)
            {
                return await EnhanceForMultiImageCompositionAsync(rawPrompt, provider, referenceImageCount, referenceImagePaths);
            }

            // ── Step 1: Check cache ──
            var cacheKey = ComputeCacheKey(rawPrompt, provider, isEdit: true, hasRef: false);
            var cached = GetCached(cacheKey);
            if (cached is not null)
            {
                _logger.LogInformation("Edit prompt cache HIT — saved 1 AI call");
                return new PromptEnhancementResult { Success = true, EnhancedPrompt = cached };
            }

            // ── Step 2: Local intent classification (zero cost) ──
            var intent = ClassifyIntent(rawPrompt);
            _logger.LogInformation("Classified edit intent: {Intent} (local, no AI)", intent);

            string enhanced;

            switch (intent)
            {
                // ── MULTI-INSTRUCTION: ALWAYS use AI for faithful execution of ALL parts ──
                case EditIntent.MultiInstruction:
                {
                    var systemPrompt = SystemPromptMultiInstructionEdit
                        .Replace("{PROVIDER_GUIDELINES}", GetProviderGuidelines(provider));

                    var imageParts = await LoadReferenceImagePartsAsync(referenceImagePaths);
                    var userParts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(rawPrompt) };
                    userParts.AddRange(imageParts);

                    var messages = new List<ChatMessage>
                    {
                        new SystemChatMessage(systemPrompt),
                        new UserChatMessage(userParts)
                    };

                    var options = new ChatCompletionOptions { Temperature = 0.6f };
                    var completion = await _chatClient.CompleteChatAsync(messages, options);
                    enhanced = completion.Value.Content[0].Text.Trim();

                    _logger.LogInformation(
                        "Multi-instruction edit enhanced via AI — {Count} distinct instructions detected (provider={Provider})",
                        CountInstructionSignals(rawPrompt), provider);
                    break;
                }

                // ── LITERAL EDIT: 100% local (only for SINGLE-intent literal edits) ──
                case EditIntent.LiteralEdit:
                {
                    var template = TryBuildLiteralEditLocally(rawPrompt);
                    if (template is not null)
                    {
                        _logger.LogInformation("Literal edit handled locally — zero AI cost");
                        enhanced = template;
                        break;
                    }
                    // Couldn't template it — fall through to AI
                    goto case EditIntent.CreativeTransform;
                }

                // ── STYLE TRANSFER: 100% local with curated templates ──
                case EditIntent.StyleTransfer:
                {
                    var styleTemplate = TryBuildStyleTransferLocally(rawPrompt);
                    if (styleTemplate is not null)
                    {
                        _logger.LogInformation("Style transfer handled locally — zero AI cost");
                        enhanced = styleTemplate;
                        break;
                    }
                    goto case EditIntent.CreativeTransform;
                }

                // ── SCENE CHANGE / CREATIVE: needs AI reasoning ──
                case EditIntent.SceneChange:
                case EditIntent.CreativeTransform:
                default:
                {
                    var styleSeed = GenerateStyleSeed();
                    var systemPrompt = SystemPromptCreativeEdit
                        .Replace("{ART_STYLE}", styleSeed.ArtStyle)
                        .Replace("{LIGHTING}", styleSeed.Lighting)
                        .Replace("{COLOR_PALETTE}", styleSeed.ColorPalette)
                        .Replace("{ATMOSPHERE}", styleSeed.Atmosphere)
                        .Replace("{PROVIDER_GUIDELINES}", GetProviderGuidelines(provider));

                    var imageParts = await LoadReferenceImagePartsAsync(referenceImagePaths);
                    var userParts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(rawPrompt) };
                    userParts.AddRange(imageParts);

                    var messages = new List<ChatMessage>
                    {
                        new SystemChatMessage(systemPrompt),
                        new UserChatMessage(userParts)
                    };

                    var options = new ChatCompletionOptions { Temperature = 0.8f };
                    var completion = await _chatClient.CompleteChatAsync(messages, options);
                    enhanced = completion.Value.Content[0].Text.Trim();

                    _logger.LogInformation(
                        "Creative edit enhanced via AI (provider={Provider}, style={Style})",
                        provider, styleSeed.ArtStyle);
                    break;
                }
            }

            SetCache(cacheKey, enhanced);
            return new PromptEnhancementResult { Success = true, EnhancedPrompt = enhanced };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enhance edit prompt");
            return new PromptEnhancementResult
            {
                Success = false,
                EnhancedPrompt = rawPrompt,
                ErrorMessage = "Enhancement failed. Using original prompt."
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MULTI-IMAGE COMPOSITION — explicit subject combination
    // ═══════════════════════════════════════════════════════════════════

    private async Task<PromptEnhancementResult> EnhanceForMultiImageCompositionAsync(
        string rawPrompt, ImageProvider provider, int imageCount, List<string>? referenceImagePaths = null)
    {
        try
        {
            var cacheKey = ComputeCacheKey($"multi_{imageCount}_{rawPrompt}", provider, isEdit: true, hasRef: true);
            var cached = GetCached(cacheKey);
            if (cached is not null)
            {
                _logger.LogInformation("Multi-image composition cache HIT — saved 1 AI call");
                return new PromptEnhancementResult { Success = true, EnhancedPrompt = cached };
            }

            var systemPrompt = SystemPromptMultiImageComposition
                .Replace("{PROVIDER_GUIDELINES}", GetProviderGuidelines(provider));

            var userMessage = $"I have provided {imageCount} reference images. My instruction: {rawPrompt}";

            var imageParts = await LoadReferenceImagePartsAsync(referenceImagePaths);
            var userParts = new List<ChatMessageContentPart> { ChatMessageContentPart.CreateTextPart(userMessage) };
            userParts.AddRange(imageParts);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userParts)
            };

            var options = new ChatCompletionOptions { Temperature = 0.6f };
            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var enhanced = completion.Value.Content[0].Text.Trim();

            _logger.LogInformation(
                "Multi-image composition prompt enhanced via AI ({Count} images, provider={Provider})",
                imageCount, provider);

            SetCache(cacheKey, enhanced);
            return new PromptEnhancementResult { Success = true, EnhancedPrompt = enhanced };
        }
        catch (Exception ex)
        {
            // Fallback: build a local composition prompt without AI
            _logger.LogWarning(ex, "AI multi-image composition failed — using local fallback");
            var fallback = BuildLocalMultiImagePrompt(rawPrompt, imageCount);
            return new PromptEnhancementResult { Success = true, EnhancedPrompt = fallback };
        }
    }

    /// <summary>
    /// Builds a multi-image composition prompt locally without any AI call.
    /// Used as a fallback when AI enhancement fails.
    /// </summary>
    private static string BuildLocalMultiImagePrompt(string rawPrompt, int imageCount)
    {
        var sb = new StringBuilder();
        sb.Append("Combine the subjects from all ");
        sb.Append(imageCount);
        sb.Append(" provided reference images into a single cohesive scene. ");
        sb.Append("Every subject from every reference image MUST appear in the output. ");
        sb.Append("Preserve the exact appearance, features, and proportions of each subject. ");
        sb.Append(rawPrompt.Trim().TrimEnd('.'));
        sb.Append(". Natural lighting, photorealistic quality, sharp details, seamless composition.");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PUBLIC API — BACKGROUND REMOVAL DETECTION
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public bool IsPureBackgroundRemoval(string prompt)
    {
        var p = prompt.Trim();

        // Gate 1: Must match a background removal pattern
        if (!BgRemovalPattern().IsMatch(p) && !BgReplacePattern().IsMatch(p) && !BgReplacePattern2().IsMatch(p))
            return false;

        // Gate 2: Must NOT be blur/bokeh (those need AI)
        if (p.Contains("blur", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("bokeh", StringComparison.OrdinalIgnoreCase))
            return false;

        // Gate 3: Must be a single-intent instruction (no compound edits)
        if (CountInstructionSignals(p) >= 2)
            return false;

        return true;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PUBLIC API — TEXT-TO-IMAGE
    // ═══════════════════════════════════════════════════════════════════

    public async Task<PromptEnhancementResult> EnhancePromptAsync(
        string rawPrompt, ImageProvider provider, string? referenceImagePath = null)
    {
        try
        {
            // ── Identity preservation path (DALL-E fallback with reference) ──
            if (!string.IsNullOrEmpty(referenceImagePath))
            {
                return await EnhanceWithIdentityPreservationAsync(rawPrompt, referenceImagePath);
            }

            // ── Step 1: Check if prompt is already detailed enough ──
            if (IsPromptAlreadyDetailed(rawPrompt))
            {
                _logger.LogInformation("Prompt already detailed ({Length} chars, multiple quality keywords) — skipping AI", rawPrompt.Length);
                return new PromptEnhancementResult { Success = true, EnhancedPrompt = rawPrompt };
            }

            // ── Step 2: Check cache ──
            var cacheKey = ComputeCacheKey(rawPrompt, provider, isEdit: false, hasRef: false);
            var cached = GetCached(cacheKey);
            if (cached is not null)
            {
                _logger.LogInformation("Text-to-image prompt cache HIT — saved 1 AI call");
                return new PromptEnhancementResult { Success = true, EnhancedPrompt = cached };
            }

            // ── Step 3: Simple prompts → build locally ──
            var styleSeed = GenerateStyleSeed();
            if (IsSimplePrompt(rawPrompt))
            {
                var local = BuildLocalTextToImagePrompt(rawPrompt, provider, styleSeed);
                _logger.LogInformation("Simple prompt built locally — zero AI cost");
                SetCache(cacheKey, local);
                return new PromptEnhancementResult { Success = true, EnhancedPrompt = local };
            }

            // ── Step 4: Complex/creative prompts → AI enhancement ──
            var systemPrompt = SystemPromptTextToImage
                .Replace("{PROVIDER_GUIDELINES}", GetProviderGuidelines(provider))
                .Replace("{ART_STYLE}", styleSeed.ArtStyle)
                .Replace("{LIGHTING}", styleSeed.Lighting)
                .Replace("{COLOR_PALETTE}", styleSeed.ColorPalette)
                .Replace("{ATMOSPHERE}", styleSeed.Atmosphere);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(rawPrompt)
            };

            var options = new ChatCompletionOptions { Temperature = 0.85f };
            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var enhanced = completion.Value.Content[0].Text.Trim();

            _logger.LogInformation("Complex prompt enhanced via AI (provider={Provider})", provider);
            SetCache(cacheKey, enhanced);
            return new PromptEnhancementResult { Success = true, EnhancedPrompt = enhanced };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enhance prompt");
            return new PromptEnhancementResult
            {
                Success = false,
                EnhancedPrompt = rawPrompt,
                ErrorMessage = "Enhancement failed. Using original prompt."
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  IDENTITY PRESERVATION PIPELINE — requires AI (vision + text)
    // ═══════════════════════════════════════════════════════════════════

    private async Task<PromptEnhancementResult> EnhanceWithIdentityPreservationAsync(
        string rawPrompt, string referenceImagePath)
    {
        var referenceDescription = await DescribeReferenceImageAsync(referenceImagePath);
        if (string.IsNullOrEmpty(referenceDescription))
        {
            return new PromptEnhancementResult { Success = false, EnhancedPrompt = rawPrompt };
        }

        var userMessage = $"""
            User's request: {rawPrompt}

            REFERENCE PERSON IDENTITY (copy EVERY detail into the prompt):
            {referenceDescription}

            Create a prompt with ALL physical/facial details first, then the scene.
            """;

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPromptIdentityPreservation),
            new UserChatMessage(userMessage)
        };

        var options = new ChatCompletionOptions { Temperature = 0.4f };
        var completion = await _chatClient.CompleteChatAsync(messages, options);
        var enhanced = completion.Value.Content[0].Text.Trim();

        _logger.LogInformation("Identity preservation prompt generated via AI (2 calls: vision + text)");
        return new PromptEnhancementResult { Success = true, EnhancedPrompt = enhanced };
    }

    private async Task<string?> DescribeReferenceImageAsync(string referenceImagePath)
    {
        try
        {
            var fullPath = SafeResolvePath(referenceImagePath);
            if (!File.Exists(fullPath)) return null;

            var imageBytes = await File.ReadAllBytesAsync(fullPath);
            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            var mediaType = extension switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };

            var messages = new List<ChatMessage>
            {
                new UserChatMessage(
                    ChatMessageContentPart.CreateTextPart(VisionSystemPrompt),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), mediaType)
                )
            };

            var options = new ChatCompletionOptions { Temperature = 0.2f };
            var completion = await _chatClient.CompleteChatAsync(messages, options);
            return completion.Value.Content[0].Text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to describe reference image");
            return null;
        }
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
