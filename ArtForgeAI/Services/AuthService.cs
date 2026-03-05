using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class AuthService : IAuthService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICoinService _coinService;
    private readonly IReferralService _referralService;

    public AuthService(IDbContextFactory<AppDbContext> dbFactory, ICoinService coinService, IReferralService referralService)
    {
        _dbFactory = dbFactory;
        _coinService = coinService;
        _referralService = referralService;
    }

    public async Task<AppUser> FindOrCreateUserAsync(string googleId, string email, string name, string? avatar, string superAdminEmail, string? referralCode = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.GoogleId == googleId);

        if (user is null)
        {
            user = new AppUser
            {
                GoogleId = googleId,
                Email = email,
                DisplayName = name,
                AvatarUrl = avatar,
                Role = email.Equals(superAdminEmail, StringComparison.OrdinalIgnoreCase) ? AppRole.SuperAdmin : AppRole.User,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };
            db.AppUsers.Add(user);
            await db.SaveChangesAsync();

            // Generate referral code for new user
            user.ReferralCode = await _referralService.GetOrCreateReferralCodeAsync(user.Id);

            // Grant signup bonus coins
            await _coinService.GrantSignupBonusAsync(user.Id);

            // Process referral if code provided
            if (!string.IsNullOrEmpty(referralCode))
            {
                var referrerId = await _referralService.ResolveReferrerUserIdAsync(referralCode);
                if (referrerId.HasValue)
                    await _referralService.ProcessReferralAsync(referrerId.Value, user.Id);
            }
        }
        else
        {
            user.Email = email;
            user.DisplayName = name;
            user.AvatarUrl = avatar;
            user.LastLoginAt = DateTime.UtcNow;

            // Grant daily login bonus
            await _coinService.GrantDailyLoginBonusAsync(user.Id);

            // Auto-promote if email matches super admin config
            if (email.Equals(superAdminEmail, StringComparison.OrdinalIgnoreCase) && user.Role != AppRole.SuperAdmin)
                user.Role = AppRole.SuperAdmin;
        }

        await db.SaveChangesAsync();
        return user;
    }

    public async Task<AppUser?> GetUserByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AppUsers.FindAsync(id);
    }

    public async Task<List<AppUser>> GetAllUsersAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AppUsers.OrderByDescending(u => u.CreatedAt).ToListAsync();
    }

    public async Task<int> GetTotalUserCountAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AppUsers.CountAsync();
    }

    public async Task UpdateUserRoleAsync(int userId, AppRole role)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(userId);
        if (user is not null)
        {
            user.Role = role;
            await db.SaveChangesAsync();
        }
    }

    public async Task SetUserActiveStatusAsync(int userId, bool isActive)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(userId);
        if (user is not null)
        {
            user.IsActive = isActive;
            await db.SaveChangesAsync();
        }
    }

    public async Task<Dictionary<string, int>> GetUserRegistrationStatsAsync(int days = 30)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var since = DateTime.UtcNow.AddDays(-days);
        var stats = await db.AppUsers
            .Where(u => u.CreatedAt >= since)
            .GroupBy(u => u.CreatedAt.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .OrderBy(x => x.Date)
            .ToListAsync();

        return stats.ToDictionary(x => x.Date.ToString("yyyy-MM-dd"), x => x.Count);
    }
}
