using ArtForgeAI.Data;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class AdminAnalyticsService : IAdminAnalyticsService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public AdminAnalyticsService(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AdminDashboardStats> GetDashboardStatsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var today = DateTime.UtcNow.Date;

        return new AdminDashboardStats
        {
            TotalUsers = await db.AppUsers.CountAsync(),
            ActiveToday = await db.AppUsers.CountAsync(u => u.LastLoginAt >= today),
            TotalGenerations = await db.ImageGenerations.CountAsync(),
            GenerationsToday = await db.ImageGenerations.CountAsync(g => g.CreatedAt >= today),
            ActiveStylePresets = await db.StylePresets.CountAsync(s => s.IsActive),
            ActiveImageSizes = await db.ImageSizeMasters.CountAsync(s => s.IsActive),
            TotalRevenue = await db.Payments.Where(p => p.Status == ArtForgeAI.Models.PaymentStatus.Captured).SumAsync(p => p.TotalAmountInr),
            RevenueThisMonth = await db.Payments.Where(p => p.Status == ArtForgeAI.Models.PaymentStatus.Captured && p.CompletedAt != null && p.CompletedAt.Value.Month == DateTime.UtcNow.Month && p.CompletedAt.Value.Year == DateTime.UtcNow.Year).SumAsync(p => p.TotalAmountInr),
            TotalSubscriptions = await db.UserSubscriptions.CountAsync(),
            ActiveSubscriptions = await db.UserSubscriptions.CountAsync(s => s.Status == ArtForgeAI.Models.SubscriptionStatus.Active && s.EndDate > DateTime.UtcNow),
            TotalCoinsPurchased = await db.CoinTransactions.Where(t => t.Amount > 0).SumAsync(t => t.Amount),
            TotalCoinsSpent = await db.CoinTransactions.Where(t => t.Amount < 0).SumAsync(t => Math.Abs(t.Amount))
        };
    }

    public async Task<List<DailyGenerationStat>> GetGenerationStatsAsync(int days = 30)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var since = DateTime.UtcNow.AddDays(-days).Date;

        var stats = await db.ImageGenerations
            .Where(g => g.CreatedAt >= since)
            .GroupBy(g => g.CreatedAt.Date)
            .Select(g => new DailyGenerationStat { Date = g.Key.ToString("MMM dd"), Count = g.Count() })
            .OrderBy(x => x.Date)
            .ToListAsync();

        return stats;
    }

    public async Task<List<TopUserStat>> GetTopUsersAsync(int count = 10)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var topUserIds = await db.ImageGenerations
            .Where(g => g.UserId != "default")
            .GroupBy(g => g.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(count)
            .ToListAsync();

        var result = new List<TopUserStat>();
        foreach (var item in topUserIds)
        {
            if (int.TryParse(item.UserId, out var uid))
            {
                var user = await db.AppUsers.FindAsync(uid);
                if (user is not null)
                {
                    result.Add(new TopUserStat
                    {
                        UserId = user.Id,
                        DisplayName = user.DisplayName,
                        Email = user.Email,
                        AvatarUrl = user.AvatarUrl,
                        GenerationCount = item.Count
                    });
                }
            }
        }
        return result;
    }

    public async Task<Dictionary<string, int>> GetSubscriptionBreakdownAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var activeSubs = await db.UserSubscriptions
            .Where(s => s.Status == ArtForgeAI.Models.SubscriptionStatus.Active && s.EndDate > DateTime.UtcNow)
            .Join(db.SubscriptionPlans, s => s.PlanId, p => p.Id, (s, p) => p.Name)
            .GroupBy(name => name)
            .Select(g => new { Plan = g.Key, Count = g.Count() })
            .ToListAsync();
        return activeSubs.ToDictionary(x => x.Plan, x => x.Count);
    }

    public async Task<List<RecentPaymentStat>> GetRecentPaymentsAsync(int count = 20)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var payments = await db.Payments
            .OrderByDescending(p => p.CreatedAt)
            .Take(count)
            .ToListAsync();

        var result = new List<RecentPaymentStat>();
        foreach (var p in payments)
        {
            var user = await db.AppUsers.FindAsync(p.UserId);
            result.Add(new RecentPaymentStat
            {
                PaymentId = p.Id,
                UserId = p.UserId,
                UserName = user?.DisplayName ?? "Unknown",
                Purpose = p.Purpose.ToString(),
                Amount = p.TotalAmountInr,
                Status = p.Status.ToString(),
                CreatedAt = p.CreatedAt
            });
        }
        return result;
    }
}
