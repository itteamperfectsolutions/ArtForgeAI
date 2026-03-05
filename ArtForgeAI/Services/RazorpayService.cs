using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ArtForgeAI.Services;

public class RazorpayService : IRazorpayService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly HttpClient _httpClient;
    private readonly RazorpayOptions _options;
    private readonly ICoinService _coinService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<RazorpayService> _logger;

    public RazorpayService(
        IDbContextFactory<AppDbContext> dbFactory,
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ICoinService coinService,
        ISubscriptionService subscriptionService,
        ILogger<RazorpayService> logger)
    {
        _dbFactory = dbFactory;
        _httpClient = httpClient;
        _options = options.Value;
        _coinService = coinService;
        _subscriptionService = subscriptionService;
        _logger = logger;

        // Configure basic auth for Razorpay API
        var authBytes = Encoding.ASCII.GetBytes($"{_options.KeyId}:{_options.KeySecret}");
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    public async Task<RazorpayOrderResult> CreateOrderAsync(int userId, PaymentPurpose purpose, int? coinPackId, int? subscriptionPlanId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        decimal amount;
        decimal gst;
        decimal total;

        if (purpose == PaymentPurpose.CoinPurchase && coinPackId.HasValue)
        {
            var pack = await db.CoinPacks.FindAsync(coinPackId.Value)
                ?? throw new InvalidOperationException("Coin pack not found");
            amount = pack.PriceInr;
            gst = pack.GstAmount;
            total = pack.TotalPriceInr;
        }
        else if (purpose == PaymentPurpose.Subscription && subscriptionPlanId.HasValue)
        {
            var plan = await db.SubscriptionPlans.FindAsync(subscriptionPlanId.Value)
                ?? throw new InvalidOperationException("Subscription plan not found");
            amount = plan.PriceInr;
            gst = plan.GstAmount;
            total = plan.TotalPriceInr;
        }
        else
        {
            throw new ArgumentException("Invalid payment parameters");
        }

        // Create payment record in DB
        var payment = new Payment
        {
            UserId = userId,
            Purpose = purpose,
            Status = PaymentStatus.Created,
            AmountInr = amount,
            GstAmount = gst,
            TotalAmountInr = total,
            CoinPackId = coinPackId,
            SubscriptionPlanId = subscriptionPlanId,
            CreatedAt = DateTime.UtcNow
        };
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        // Create Razorpay order
        var amountPaise = (int)(total * 100);
        var orderPayload = new
        {
            amount = amountPaise,
            currency = "INR",
            receipt = $"pay_{payment.Id}",
            notes = new { userId, paymentId = payment.Id, purpose = purpose.ToString() }
        };

        var content = new StringContent(JsonSerializer.Serialize(orderPayload), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("https://api.razorpay.com/v1/orders", content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = $"Order creation failed: {responseBody}";
            await db.SaveChangesAsync();
            throw new InvalidOperationException($"Razorpay order creation failed: {responseBody}");
        }

        var orderData = JsonDocument.Parse(responseBody);
        var orderId = orderData.RootElement.GetProperty("id").GetString()!;

        payment.RazorpayOrderId = orderId;
        await db.SaveChangesAsync();

        return new RazorpayOrderResult
        {
            OrderId = orderId,
            AmountPaise = amountPaise,
            Currency = "INR",
            KeyId = _options.KeyId,
            PaymentId = payment.Id
        };
    }

    public bool VerifyPaymentSignature(string orderId, string paymentId, string signature)
    {
        var payload = $"{orderId}|{paymentId}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.KeySecret));
        var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var computedSignature = BitConverter.ToString(computedHash).Replace("-", "").ToLowerInvariant();
        return computedSignature == signature;
    }

    public async Task<bool> CompletePaymentAsync(int paymentDbId, string razorpayPaymentId, string razorpaySignature)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var payment = await db.Payments.FindAsync(paymentDbId);
        if (payment == null) return false;

        // Verify signature
        if (!VerifyPaymentSignature(payment.RazorpayOrderId!, razorpayPaymentId, razorpaySignature))
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureReason = "Signature verification failed";
            await db.SaveChangesAsync();
            return false;
        }

        payment.RazorpayPaymentId = razorpayPaymentId;
        payment.RazorpaySignature = razorpaySignature;
        payment.Status = PaymentStatus.Captured;
        payment.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Fulfill the payment
        if (payment.Purpose == PaymentPurpose.CoinPurchase && payment.CoinPackId.HasValue)
        {
            var pack = await db.CoinPacks.FindAsync(payment.CoinPackId.Value);
            if (pack != null)
            {
                var totalCoins = pack.CoinAmount + pack.BonusCoins;
                await _coinService.CreditCoinsAsync(payment.UserId, totalCoins, CoinTransactionType.Purchase,
                    $"Purchased {pack.Name} pack", payment.Id.ToString());
            }
        }
        else if (payment.Purpose == PaymentPurpose.Subscription && payment.SubscriptionPlanId.HasValue)
        {
            await _subscriptionService.ActivateSubscriptionAsync(payment.UserId, payment.SubscriptionPlanId.Value);
        }

        return true;
    }

    public async Task HandleWebhookAsync(string payload, string signature)
    {
        // Verify webhook signature
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
        var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var computedSignature = BitConverter.ToString(computedHash).Replace("-", "").ToLowerInvariant();

        if (computedSignature != signature)
        {
            _logger.LogWarning("Razorpay webhook signature mismatch");
            return;
        }

        var doc = JsonDocument.Parse(payload);
        var eventType = doc.RootElement.GetProperty("event").GetString();

        _logger.LogInformation("Razorpay webhook received: {Event}", eventType);

        if (eventType == "payment.captured")
        {
            var paymentEntity = doc.RootElement.GetProperty("payload").GetProperty("payment").GetProperty("entity");
            var orderId = paymentEntity.GetProperty("order_id").GetString();
            var rpPaymentId = paymentEntity.GetProperty("id").GetString();

            await using var db = await _dbFactory.CreateDbContextAsync();
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.RazorpayOrderId == orderId);

            if (payment != null && payment.Status == PaymentStatus.Created)
            {
                payment.RazorpayPaymentId = rpPaymentId;
                payment.Status = PaymentStatus.Captured;
                payment.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                // Fulfill
                if (payment.Purpose == PaymentPurpose.CoinPurchase && payment.CoinPackId.HasValue)
                {
                    var pack = await db.CoinPacks.FindAsync(payment.CoinPackId.Value);
                    if (pack != null)
                    {
                        await _coinService.CreditCoinsAsync(payment.UserId, pack.CoinAmount + pack.BonusCoins,
                            CoinTransactionType.Purchase, $"Purchased {pack.Name} pack (webhook)", payment.Id.ToString());
                    }
                }
                else if (payment.Purpose == PaymentPurpose.Subscription && payment.SubscriptionPlanId.HasValue)
                {
                    await _subscriptionService.ActivateSubscriptionAsync(payment.UserId, payment.SubscriptionPlanId.Value);
                }
            }
        }
    }
}
