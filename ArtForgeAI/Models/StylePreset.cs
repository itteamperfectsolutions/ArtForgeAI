using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class StylePreset
{
    public const string FacialIdentitySuffix = " Preserve biometric facial identity exactly as in the original image without modification. CRITICAL PERSON COUNT RULE: You MUST include ONLY the exact number of people visible in the source photo. If the source image has 1 person, the output MUST show only 1 person — do NOT add extra people, partners, or companions. If the source has 2 people, show exactly 2 people. Match the source image person count precisely. CRITICAL: If this image contains multiple panels, photos, or scenes — every single panel MUST depict a COMPLETELY DIFFERENT scene, pose, outfit, camera angle, and setting. No two panels should look similar or use the same composition. Each panel must tell a distinctly different moment or story.";

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
}
