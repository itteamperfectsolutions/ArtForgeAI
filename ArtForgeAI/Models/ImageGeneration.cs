using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class ImageGeneration
{
    public int Id { get; set; }

    [Required]
    [MaxLength(2000)]
    public string OriginalPrompt { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? EnhancedPrompt { get; set; }

    [MaxLength(500)]
    public string? ReferenceImagePath { get; set; }

    [MaxLength(2000)]
    public string? GeneratedImageUrl { get; set; }

    [MaxLength(500)]
    public string? LocalImagePath { get; set; }

    [MaxLength(20)]
    public string ImageSize { get; set; } = "Square";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string UserId { get; set; } = "default";

    public bool IsSuccess { get; set; }

    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }
}
