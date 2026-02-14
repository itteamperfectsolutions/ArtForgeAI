using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class ImageSizeMaster
{
    public int Id { get; set; }

    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public int Width { get; set; }

    public int Height { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    [MaxLength(5)]
    public string Unit { get; set; } = "px";

    public double DisplayWidth { get; set; }

    public double DisplayHeight { get; set; }
}
