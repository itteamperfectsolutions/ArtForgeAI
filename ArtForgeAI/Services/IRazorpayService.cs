using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public class RazorpayOrderResult
{
    public string OrderId { get; set; } = string.Empty;
    public int AmountPaise { get; set; }
    public string Currency { get; set; } = "INR";
    public string KeyId { get; set; } = string.Empty;
    public int PaymentId { get; set; }
}

public interface IRazorpayService
{
    Task<RazorpayOrderResult> CreateOrderAsync(int userId, PaymentPurpose purpose, int? coinPackId, int? subscriptionPlanId);
    bool VerifyPaymentSignature(string orderId, string paymentId, string signature);
    Task<bool> CompletePaymentAsync(int paymentDbId, string razorpayPaymentId, string razorpaySignature);
    Task HandleWebhookAsync(string payload, string signature);
}
