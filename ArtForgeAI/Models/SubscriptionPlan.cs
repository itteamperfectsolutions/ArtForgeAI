using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class SubscriptionPlan
{
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public decimal PriceInr { get; set; }

    public decimal GstAmount { get; set; }

    public decimal TotalPriceInr { get; set; }

    public int MonthlyCoins { get; set; }

    public int DurationDays { get; set; } = 30;

    /// <summary>Comma-separated feature keys (e.g. "QuickStyle,Home,StyleTransfer,Gallery")</summary>
    [MaxLength(500)]
    public string AllowedFeatures { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }
}
