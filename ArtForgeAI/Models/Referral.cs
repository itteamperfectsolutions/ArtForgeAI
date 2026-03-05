namespace ArtForgeAI.Models;

public class Referral
{
    public int Id { get; set; }

    public int ReferrerUserId { get; set; }

    public int RefereeUserId { get; set; }

    public int ReferrerBonusCoins { get; set; } = 15;

    public int RefereeBonusCoins { get; set; } = 10;

    public bool IsRewarded { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
