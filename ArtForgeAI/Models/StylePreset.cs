using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class StylePreset
{
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    [Required]
    public string PromptTemplate { get; set; } = string.Empty;

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
