using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class CollageTemplate
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? ThumbnailPath { get; set; }

    public int SlotCount { get; set; } = 5;

    /// <summary>JSON array of slot descriptions, e.g. ["Center circle - master portrait","Top-left - blowing candles"]</summary>
    public string SlotDescriptionsJson { get; set; } = "[]";

    /// <summary>0-based index of the master/hero slot</summary>
    public int MasterSlotIndex { get; set; }

    [MaxLength(200)]
    public string ColorTheme { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Mood { get; set; } = string.Empty;

    [MaxLength(500)]
    public string DecorativeElements { get; set; } = string.Empty;

    [MaxLength(200)]
    public string TextOverlay { get; set; } = string.Empty;

    /// <summary>Detailed layout prompt for Gemini to compose the final collage</summary>
    public string? LayoutDescription { get; set; }

    [MaxLength(10)]
    public string IconEmoji { get; set; } = string.Empty;

    /// <summary>Whether to ask user for their name to overlay on the collage</summary>
    public bool AskName { get; set; }

    /// <summary>Whether to ask user for the occasion (e.g. Birthday, Wedding)</summary>
    public bool AskOccasion { get; set; }

    /// <summary>Whether to ask user for a date (shown as calendar grid)</summary>
    public bool AskDate { get; set; }

    /// <summary>Whether to ask user for a custom message</summary>
    public bool AskMessage { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
