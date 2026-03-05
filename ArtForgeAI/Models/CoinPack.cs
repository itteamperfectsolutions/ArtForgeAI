using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public class CoinPack
{
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    public int CoinAmount { get; set; }

    public int BonusCoins { get; set; }

    public decimal PriceInr { get; set; }

    public decimal GstAmount { get; set; }

    public decimal TotalPriceInr { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }
}
