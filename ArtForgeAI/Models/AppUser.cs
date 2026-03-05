using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public enum AppRole
{
    User = 0,
    SuperAdmin = 1
}

public class AppUser
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string GoogleId { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? AvatarUrl { get; set; }

    public AppRole Role { get; set; } = AppRole.User;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;

    // ── Coin & Subscription fields ──
    public int CoinBalance { get; set; }

    public int? ActiveSubscriptionId { get; set; }

    [MaxLength(20)]
    public string? ReferralCode { get; set; }

    public int? ReferredByUserId { get; set; }

    public DateTime? LastDailyLoginReward { get; set; }
}
