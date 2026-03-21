using System.Security.Cryptography;
using System.Text;

namespace ArtForgeAI.Services;

/// <summary>
/// HMAC-SHA256 transaction signing to detect database tampering.
///
/// Every coin transaction gets an integrity hash computed from its key fields
/// and a server-side secret. If someone modifies the database directly
/// (e.g., inflating coin balance), the HMAC chain breaks and the tampering
/// is detectable via audit.
///
/// Industry standard: similar to how banking systems sign ledger entries.
/// </summary>
public sealed class TransactionIntegrityService
{
    private readonly byte[] _hmacKey;

    public TransactionIntegrityService(IConfiguration config)
    {
        // The HMAC key should be stored securely (User Secrets, Azure Key Vault, etc.)
        // Falls back to a machine-derived key if not configured — still unique per machine
        var configuredKey = config["Security:TransactionHmacKey"];
        if (!string.IsNullOrEmpty(configuredKey))
        {
            _hmacKey = Convert.FromBase64String(configuredKey);
        }
        else
        {
            // Derive from hardware fingerprint — unique per machine, consistent across restarts
            var fingerprint = HardwareFingerprintService.GetFingerprint();
            _hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes($"ArtForgeAI-TxSign-{fingerprint}"));
        }
    }

    /// <summary>
    /// Computes an HMAC-SHA256 integrity hash for a coin transaction.
    /// Store this hash alongside the transaction record.
    /// </summary>
    public string ComputeTransactionHash(int userId, int amount, int balanceAfter, int transactionType, DateTime createdAt)
    {
        var data = $"{userId}|{amount}|{balanceAfter}|{transactionType}|{createdAt:O}";
        using var hmac = new HMACSHA256(_hmacKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Verifies a transaction's integrity hash. Returns false if the record has been tampered with.
    /// </summary>
    public bool VerifyTransactionHash(int userId, int amount, int balanceAfter, int transactionType, DateTime createdAt, string storedHash)
    {
        var expected = ComputeTransactionHash(userId, amount, balanceAfter, transactionType, createdAt);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(storedHash));
    }

    /// <summary>
    /// Computes an HMAC for the user's current balance — allows periodic balance integrity audits.
    /// </summary>
    public string ComputeBalanceHash(int userId, int balance)
    {
        var data = $"BAL|{userId}|{balance}";
        using var hmac = new HMACSHA256(_hmacKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash);
    }

    /// <summary>Generates a random HMAC key for initial configuration.</summary>
    public static string GenerateNewHmacKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(key);
    }
}
