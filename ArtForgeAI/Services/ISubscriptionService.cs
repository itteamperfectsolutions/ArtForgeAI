using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface ISubscriptionService
{
    Task<List<SubscriptionPlan>> GetAllPlansAsync();
    Task<UserSubscription?> GetActiveSubscriptionAsync(int userId);
    Task<string> GetCurrentPlanNameAsync(int userId);
    Task<UserSubscription> ActivateSubscriptionAsync(int userId, int planId, string? razorpaySubId = null);
    Task CancelSubscriptionAsync(int userId);
    Task<bool> HasFeatureAccessAsync(int userId, string featureKey);
    Task<List<string>> GetAccessibleFeaturesAsync(int userId);
    Task ProcessExpiredSubscriptionsAsync();
}
