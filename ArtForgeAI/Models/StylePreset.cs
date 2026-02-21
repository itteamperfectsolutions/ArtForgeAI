using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class StylePreset
{
    public const string FacialIdentitySuffix = " Preserve biometric facial identity exactly as in the original image without modification.";

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
