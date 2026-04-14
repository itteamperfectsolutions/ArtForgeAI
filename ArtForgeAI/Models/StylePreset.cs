using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class StylePreset
{
    public const string FacialIdentitySuffix =
        " CRITICAL FACIAL IDENTITY RULES: " +
        "1) FACE PRESERVATION: Recreate the EXACT facial features from the reference photo — same face shape, eye shape, eye color, nose structure, lip shape, jawline, skin tone, eyebrow shape, and all distinguishing facial marks (moles, dimples, scars). The face must be unmistakably the same person. " +
        "2) EXPRESSION LOCK: Keep the EXACT SAME facial expression as in the reference photo — same smile/neutral/serious look, same eye openness, same mouth position. Do NOT change, exaggerate, or stylize the expression in any way. " +
        "3) HAIR & FEATURES: Preserve exact hair color, hairstyle, length, and texture from the reference. Keep facial hair (beard, mustache) exactly as shown. " +
        "4) STYLE APPLICATION: Apply the requested artistic style (painting technique, color palette, textures, lighting, background) to the ENTIRE image including the face, but the underlying facial geometry, proportions, and expression must remain identical to the source photo. The style is a visual filter over the real identity, not a reimagining of the face. " +
        "5) ABSOLUTE PERSON COUNT RULE (MANDATORY — ZERO TOLERANCE): First, COUNT the exact number of people visible in the uploaded source/reference photo. The output image MUST contain EXACTLY that same number of people — no more, no fewer. " +
        "If the source has 1 person: output MUST show ONLY 1 person. Do NOT add a partner, spouse, companion, second person, or anyone else under ANY circumstance. " +
        "If the source has 2 people: output MUST show EXACTLY 2 people — no third person added. " +
        "If the source has 3 or more people: output MUST show EXACTLY that many people. " +
        "NEVER invent, fabricate, hallucinate, or add people who are not in the source photo. This rule overrides any style description that implies multiple people (e.g., 'couple', 'romantic pose') — if the source has 1 person, treat the style as a SOLO version. " +
        "6) MULTI-PANEL: If multiple panels/scenes, each panel must show a completely different scene, pose, outfit, angle, and setting, but the face identity and expression in each panel must match the reference photo exactly. The person count rule applies to EVERY panel. " +
        "7) MANDATORY: Preserve 1:1 pixel-perfect facial geometry and features; do not alter, redraw, or enhance eyes, nose, mouth, teeth, or expression—apply color and lighting adjustments only to the surrounding pixels.";

    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    [Required]
    public string PromptTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Returns the PromptTemplate with the facial identity preservation instruction appended.
    /// Use this instead of PromptTemplate when generating images.
    /// </summary>
    public string EffectivePrompt => PromptTemplate + FacialIdentitySuffix;

    [Required]
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(10)]
    public string IconEmoji { get; set; } = string.Empty;

    [MaxLength(10)]
    public string? AccentColor { get; set; }

    [MaxLength(500)]
    public string? ThumbnailPath { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>Optional grouping for display on the Quick Style page.</summary>
    public int? StyleGroupId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
