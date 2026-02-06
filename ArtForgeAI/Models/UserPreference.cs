using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class UserPreference
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string UserId { get; set; } = "default";

    [MaxLength(20)]
    public string DefaultImageSize { get; set; } = "Square";

    public bool DarkMode { get; set; } = true;

    public bool AutoEnhancePrompt { get; set; } = true;

    [MaxLength(10)]
    public string DefaultDownloadFormat { get; set; } = "png";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
