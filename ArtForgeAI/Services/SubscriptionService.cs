using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICoinService _coinService;

    public SubscriptionService(IDbContextFactory<AppDbContext> dbFactory, ICoinService coinService)
    {
        _dbFactory = dbFactory;
        _coinService = coinService;
    }

    public async Task<List<SubscriptionPlan>> GetAllPlansAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SubscriptionPlans
            .Where(p => p.IsActive)
            .OrderBy(p => p.SortOrder)
            .ToListAsync();
    }

    public async Task<UserSubscription?> GetActiveSubscriptionAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.UserSubscriptions
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active && s.EndDate > DateTime.UtcNow)
            .OrderByDescending(s => s.EndDate)
            .FirstOrDefaultAsync();
    }

    public async Task<string> GetCurrentPlanNameAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var sub = await db.UserSubscriptions
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active && s.EndDate > DateTime.UtcNow)
            .OrderByDescending(s => s.EndDate)
            .FirstOrDefaultAsync();

        if (sub == null) return "Free";

        var plan = await db.SubscriptionPlans.FindAsync(sub.PlanId);
        return plan?.Name ?? "Free";
    }

    public async Task<UserSubscription> ActivateSubscriptionAsync(int userId, int planId, string? razorpaySubId = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Cancel any existing active subscription
        var existing = await db.UserSubscriptions
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
            .ToListAsync();

        foreach (var e in existing)
            e.Status = SubscriptionStatus.Cancelled;

        var plan = await db.SubscriptionPlans.FindAsync(planId);
        if (plan == null) throw new InvalidOperationException("Plan not found");

        var sub = new UserSubscription
        {
            UserId = userId,
            PlanId = planId,
            Status = SubscriptionStatus.Active,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddDays(plan.DurationDays),
            AutoRenew = true,
            RazorpaySubscriptionId = razorpaySubId,
            CreatedAt = DateTime.UtcNow
        };
        db.UserSubscriptions.Add(sub);

        // Update user's active subscription
        var user = await db.AppUsers.FindAsync(userId);
        if (user != null)
            user.ActiveSubscriptionId = sub.Id;

        await db.SaveChangesAsync();

        // Update user's ActiveSubscriptionId with the actual generated Id
        if (user != null)
        {
            user.ActiveSubscriptionId = sub.Id;
            await db.SaveChangesAsync();
        }

        // Grant monthly coins
        if (plan.MonthlyCoins > 0)
        {
            await _coinService.CreditCoinsAsync(userId, plan.MonthlyCoins, CoinTransactionType.SubscriptionGrant,
                $"{plan.Name} plan monthly coins");
        }

        return sub;
    }

    public async Task CancelSubscriptionAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var active = await db.UserSubscriptions
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
            .ToListAsync();

        foreach (var sub in active)
        {
            sub.Status = SubscriptionStatus.Cancelled;
            sub.AutoRenew = false;
        }

        var user = await db.AppUsers.FindAsync(userId);
        if (user != null)
            user.ActiveSubscriptionId = null;

        await db.SaveChangesAsync();
    }

    public async Task<bool> HasFeatureAccessAsync(int userId, string featureKey)
    {
        // SuperAdmin check done at caller level with claims
        var features = await GetAccessibleFeaturesAsync(userId);
        return features.Contains(featureKey);
    }

    public async Task<List<string>> GetAccessibleFeaturesAsync(int userId)
    {
        var planName = await GetCurrentPlanNameAsync(userId);

        if (FeatureAccess.PlanFeatures.TryGetValue(planName, out var features))
            return features.ToList();

        // Default to Free plan
        return FeatureAccess.PlanFeatures["Free"].ToList();
    }

    public async Task ProcessExpiredSubscriptionsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var expired = await db.UserSubscriptions
            .Where(s => s.Status == SubscriptionStatus.Active && s.EndDate <= DateTime.UtcNow)
            .ToListAsync();

        foreach (var sub in expired)
        {
            sub.Status = SubscriptionStatus.Expired;

            var user = await db.AppUsers.FindAsync(sub.UserId);
            if (user != null && user.ActiveSubscriptionId == sub.Id)
                user.ActiveSubscriptionId = null;
        }

        await db.SaveChangesAsync();
    }
}
