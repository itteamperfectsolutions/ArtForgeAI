using ArtForgeAI.Data;
using ArtForgeAI.Models;
using Microsoft.EntityFrameworkCore;

namespace ArtForgeAI.Services;

public class CoinService : ICoinService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _config;

    public CoinService(IDbContextFactory<AppDbContext> dbFactory, IConfiguration config)
    {
        _dbFactory = dbFactory;
        _config = config;
    }

    public async Task<int> GetBalanceAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(userId);
        return user?.CoinBalance ?? 0;
    }

    public async Task<bool> CreditCoinsAsync(int userId, int amount, CoinTransactionType type, string? description = null, string? referenceId = null)
    {
        if (amount <= 0) return false;
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Atomic update
        var rows = await db.Database.ExecuteSqlRawAsync(
            "UPDATE AppUsers SET CoinBalance = CoinBalance + {0} WHERE Id = {1}",
            amount, userId);

        if (rows == 0) return false;

        var user = await db.AppUsers.FindAsync(userId);
        var tx = new CoinTransaction
        {
            UserId = userId,
            Type = type,
            Amount = amount,
            BalanceAfter = user?.CoinBalance ?? amount,
            Description = description,
            ReferenceId = referenceId,
            CreatedAt = DateTime.UtcNow
        };
        db.CoinTransactions.Add(tx);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DebitCoinsAsync(int userId, int amount, CoinTransactionType type, string? description = null, string? referenceId = null)
    {
        if (amount <= 0) return false;
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Atomic debit with balance check
        var rows = await db.Database.ExecuteSqlRawAsync(
            "UPDATE AppUsers SET CoinBalance = CoinBalance - {0} WHERE Id = {1} AND CoinBalance >= {0}",
            amount, userId);

        if (rows == 0) return false;

        var user = await db.AppUsers.FindAsync(userId);
        var tx = new CoinTransaction
        {
            UserId = userId,
            Type = type,
            Amount = -amount,
            BalanceAfter = user?.CoinBalance ?? 0,
            Description = description,
            ReferenceId = referenceId,
            CreatedAt = DateTime.UtcNow
        };
        db.CoinTransactions.Add(tx);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> HasSufficientCoinsAsync(int userId, int amount)
    {
        var balance = await GetBalanceAsync(userId);
        return balance >= amount;
    }

    public async Task<List<CoinTransaction>> GetTransactionHistoryAsync(int userId, int page = 1, int pageSize = 20)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CoinTransactions
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<int> GetTransactionCountAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CoinTransactions.CountAsync(t => t.UserId == userId);
    }

    public async Task GrantSignupBonusAsync(int userId)
    {
        var bonus = _config.GetValue("Coins:SignupBonus", 10);
        await CreditCoinsAsync(userId, bonus, CoinTransactionType.SignupBonus, "Welcome bonus!");
    }

    public async Task GrantDailyLoginBonusAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(userId);
        if (user == null) return;

        var today = DateTime.UtcNow.Date;
        if (user.LastDailyLoginReward.HasValue && user.LastDailyLoginReward.Value.Date >= today)
            return; // Already claimed today

        user.LastDailyLoginReward = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var bonus = _config.GetValue("Coins:DailyLoginBonus", 1);
        await CreditCoinsAsync(userId, bonus, CoinTransactionType.DailyLogin, "Daily login reward");
    }
}
