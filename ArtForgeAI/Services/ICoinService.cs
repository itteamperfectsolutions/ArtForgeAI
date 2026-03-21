using ArtForgeAI.Models;

namespace ArtForgeAI.Services;

public interface ICoinService
{
    Task<int> GetBalanceAsync(int userId);
    Task<bool> CreditCoinsAsync(int userId, int amount, CoinTransactionType type, string? description = null, string? referenceId = null);
    Task<bool> DebitCoinsAsync(int userId, int amount, CoinTransactionType type, string? description = null, string? referenceId = null);
    Task<bool> HasSufficientCoinsAsync(int userId, int amount);
    Task<List<CoinTransaction>> GetTransactionHistoryAsync(int userId, int page = 1, int pageSize = 20);
    Task<int> GetTransactionCountAsync(int userId);
    Task GrantSignupBonusAsync(int userId);
    Task GrantDailyLoginBonusAsync(int userId);

    /// <summary>
    /// Returns feature descriptions ordered by usage count (today first, then yesterday as fallback).
    /// </summary>
    Task<List<string>> GetFeatureUsageOrderAsync(int userId);
}
