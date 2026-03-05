using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class ReferralService : IReferralService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICoinService _coinService;
    private readonly IConfiguration _config;

    public ReferralService(IDbContextFactory<AppDbContext> dbFactory, ICoinService coinService, IConfiguration config)
    {
        _dbFactory = dbFactory;
        _coinService = coinService;
        _config = config;
    }

    public async Task<string> GetOrCreateReferralCodeAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(userId);
        if (user == null) return string.Empty;

        if (!string.IsNullOrEmpty(user.ReferralCode))
            return user.ReferralCode;

        // Generate unique 8-char alphanumeric code
        string code;
        do
        {
            code = GenerateCode(8);
        }
        while (await db.AppUsers.AnyAsync(u => u.ReferralCode == code));

        user.ReferralCode = code;
        await db.SaveChangesAsync();
        return code;
    }

    public async Task<int?> ResolveReferrerUserIdAsync(string referralCode)
    {
        if (string.IsNullOrWhiteSpace(referralCode)) return null;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var referrer = await db.AppUsers.FirstOrDefaultAsync(u => u.ReferralCode == referralCode);
        return referrer?.Id;
    }

    public async Task ProcessReferralAsync(int referrerUserId, int refereeUserId)
    {
        if (referrerUserId == refereeUserId) return;

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Check if already processed
        var exists = await db.Referrals.AnyAsync(r => r.RefereeUserId == refereeUserId);
        if (exists) return;

        var referrerBonus = _config.GetValue("Coins:ReferrerBonus", 15);
        var refereeBonus = _config.GetValue("Coins:RefereeBonus", 10);

        var referral = new Referral
        {
            ReferrerUserId = referrerUserId,
            RefereeUserId = refereeUserId,
            ReferrerBonusCoins = referrerBonus,
            RefereeBonusCoins = refereeBonus,
            IsRewarded = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Referrals.Add(referral);
        await db.SaveChangesAsync();

        // Credit both parties
        await _coinService.CreditCoinsAsync(referrerUserId, referrerBonus, CoinTransactionType.ReferralBonus,
            "Referral bonus", refereeUserId.ToString());
        await _coinService.CreditCoinsAsync(refereeUserId, refereeBonus, CoinTransactionType.RefereeBonus,
            "Referred signup bonus", referrerUserId.ToString());

        // Update referee's ReferredByUserId
        var referee = await db.AppUsers.FindAsync(refereeUserId);
        if (referee != null)
        {
            referee.ReferredByUserId = referrerUserId;
            await db.SaveChangesAsync();
        }
    }

    public async Task<ReferralStats> GetReferralStatsAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var referrals = await db.Referrals
            .Where(r => r.ReferrerUserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return new ReferralStats
        {
            TotalReferrals = referrals.Count,
            TotalCoinsEarned = referrals.Where(r => r.IsRewarded).Sum(r => r.ReferrerBonusCoins),
            RecentReferrals = referrals.Take(20).ToList()
        };
    }

    private static string GenerateCode(int length)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var random = Random.Shared;
        return new string(Enumerable.Range(0, length).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
