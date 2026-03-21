using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public enum CoinTransactionType
{
    SignupBonus = 0,
    DailyLogin = 1,
    ReferralBonus = 2,
    RefereeBonus = 3,
    SubscriptionGrant = 4,
    Purchase = 5,
    GenerationSpend = 6,
    AdminAdjustment = 7,
    Refund = 8
}

public class CoinTransaction
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public CoinTransactionType Type { get; set; }

    public int Amount { get; set; }

    public int BalanceAfter { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }

    [MaxLength(100)]
    public string? ReferenceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// HMAC-SHA256 integrity hash — detects direct database tampering.
    /// Computed from (UserId, Amount, BalanceAfter, Type, CreatedAt) using a server-side secret.
    /// </summary>
    [MaxLength(64)]
    public string? IntegrityHash { get; set; }
}
