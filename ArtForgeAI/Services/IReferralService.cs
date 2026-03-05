using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public class ReferralStats
{
    public int TotalReferrals { get; set; }
    public int TotalCoinsEarned { get; set; }
    public List<Referral> RecentReferrals { get; set; } = new();
}

public interface IReferralService
{
    Task<string> GetOrCreateReferralCodeAsync(int userId);
    Task<int?> ResolveReferrerUserIdAsync(string referralCode);
    Task ProcessReferralAsync(int referrerUserId, int refereeUserId);
    Task<ReferralStats> GetReferralStatsAsync(int userId);
}
