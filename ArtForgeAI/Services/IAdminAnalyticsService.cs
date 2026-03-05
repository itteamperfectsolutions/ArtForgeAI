namespace ArtForgeAI.Services;

public class AdminDashboardStats
{
    public int TotalUsers { get; set; }
    public int ActiveToday { get; set; }
    public int TotalGenerations { get; set; }
    public int GenerationsToday { get; set; }
    public int ActiveStylePresets { get; set; }
    public int ActiveImageSizes { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal RevenueThisMonth { get; set; }
    public int TotalSubscriptions { get; set; }
    public int ActiveSubscriptions { get; set; }
    public int TotalCoinsPurchased { get; set; }
    public int TotalCoinsSpent { get; set; }
}

public class DailyGenerationStat
{
    public string Date { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class TopUserStat
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public int GenerationCount { get; set; }
}

public class RecentPaymentStat
{
    public int PaymentId { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public interface IAdminAnalyticsService
{
    Task<AdminDashboardStats> GetDashboardStatsAsync();
    Task<List<DailyGenerationStat>> GetGenerationStatsAsync(int days = 30);
    Task<List<TopUserStat>> GetTopUsersAsync(int count = 10);
    Task<Dictionary<string, int>> GetSubscriptionBreakdownAsync();
    Task<List<RecentPaymentStat>> GetRecentPaymentsAsync(int count = 20);
}
