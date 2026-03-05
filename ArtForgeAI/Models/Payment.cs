using System.ComponentModel.DataAnnotations;

namespace ArtForgeAI.Models;

public enum PaymentPurpose
{
    CoinPurchase = 0,
    Subscription = 1
}

public enum PaymentStatus
{
    Created = 0,
    Authorized = 1,
    Captured = 2,
    Failed = 3,
    Refunded = 4
}

public class Payment
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public PaymentPurpose Purpose { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Created;

    public decimal AmountInr { get; set; }

    public decimal GstAmount { get; set; }

    public decimal TotalAmountInr { get; set; }

    [MaxLength(100)]
    public string? RazorpayOrderId { get; set; }

    [MaxLength(100)]
    public string? RazorpayPaymentId { get; set; }

    [MaxLength(500)]
    public string? RazorpaySignature { get; set; }

    public int? CoinPackId { get; set; }

    public int? SubscriptionPlanId { get; set; }

    [MaxLength(500)]
    public string? FailureReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }
}
