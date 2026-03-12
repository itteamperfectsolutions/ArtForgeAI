using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class StyleGroup
{
    public int StyleGroupId { get; set; }

    [Required]
    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
