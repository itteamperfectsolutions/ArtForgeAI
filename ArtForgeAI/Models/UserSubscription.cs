using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public enum SubscriptionStatus
{
    Active = 0,
    Expired = 1,
    Cancelled = 2
}

public class UserSubscription
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public int PlanId { get; set; }

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;

    public DateTime StartDate { get; set; } = DateTime.UtcNow;

    public DateTime EndDate { get; set; }

    public bool AutoRenew { get; set; } = true;

    [MaxLength(100)]
    public string? RazorpaySubscriptionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
