using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArtForgeAI.Services;

/// <summary>
/// Industry-standard RSA-2048 license validation.
///
/// How it works:
///   1. The license file (.lic) contains a JSON payload + RSA signature.
///   2. The payload includes the hardware fingerprint, licensee, expiry, and allowed features.
///   3. Only the holder of the RSA private key (you) can generate valid licenses.
///   4. The app embeds the RSA public key and verifies the signature at startup.
///   5. The hardware fingerprint in the license must match the current machine.
///
/// This makes cloning impossible: even with the license file, the fingerprint won't match
/// on a different machine, and without the private key, forging a new license is infeasible.
/// </summary>
public sealed class LicenseService
{
    // ── RSA-2048 public key for signature verification ──
    // IMPORTANT: Replace this with YOUR generated public key (see LicenseGenerator tool).
    // This is a placeholder — the LicenseGenerator creates a matching key pair on first run.
    private const string EmbeddedPublicKeyXml = "<RSAKeyValue><Modulus>ncjCCjDbZbkmC8P9utz0FMrLjsgyVO1lfFb5EWdvC6+gpjS8HMd3IoJxPX0Qgfq3yc8nVqztAGxHCJvj81pf4cvnLrJrBqP7sY2M+j1l3wJXT8EFSOmyOE4s9SnrTqjG7haA0es6nxqyKp/P1EVnzOxarh6e20axlxcWUVRaRq3G6VpKJ+DWgbChZIzgIZB1nNQ4HXe/xq5kt+iQwLIgIVuybk+TKXqCrIHDDaplqRYu67BP9cOAnKfOf5XtkuvHMzrBQwoCFfJUXOBPGLrMIRVMwXNz2+lpy0s6yCvpw3uB2g4jyxoeMQvN48H/tksxlF1I4Mwp+3v+M7Pv1LdtuQ==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    private static readonly string LicenseFilePath =
        Path.Combine(AppContext.BaseDirectory, "license.lic");

    private LicensePayload? _cached;
    private bool _validated;

    public LicenseValidationResult Validate()
    {
        if (_validated && _cached != null)
            return LicenseValidationResult.Valid(_cached);

        // 1. License file must exist
        if (!File.Exists(LicenseFilePath))
            return LicenseValidationResult.Fail("License file not found. Place 'license.lic' in the application directory.");

        string fileContent;
        try
        {
            fileContent = File.ReadAllText(LicenseFilePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Fail($"Cannot read license file: {ex.Message}");
        }

        // 2. Parse envelope
        LicenseEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<LicenseEnvelope>(fileContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return LicenseValidationResult.Fail("License file is corrupted or invalid JSON.");
        }

        if (envelope == null || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature))
            return LicenseValidationResult.Fail("License file is incomplete.");

        // 3. Verify RSA-SHA256 signature
        if (EmbeddedPublicKeyXml.StartsWith("<REPLACE"))
        {
            // First-run: public key not yet embedded — allow startup for key generation
            return LicenseValidationResult.Fail("License system not initialized. Run the LicenseGenerator tool first.");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.FromXmlString(EmbeddedPublicKeyXml);

            var payloadBytes = Encoding.UTF8.GetBytes(envelope.Payload);
            var signatureBytes = Convert.FromBase64String(envelope.Signature);

            var isValid = rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!isValid)
                return LicenseValidationResult.Fail("License signature is invalid — possible tampering detected.");
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Fail($"Signature verification error: {ex.Message}");
        }

        // 4. Parse payload
        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(envelope.Payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return LicenseValidationResult.Fail("License payload is corrupted.");
        }

        if (payload == null)
            return LicenseValidationResult.Fail("License payload is empty.");

        // 5. Check expiry
        if (payload.ExpiresUtc < DateTime.UtcNow)
            return LicenseValidationResult.Fail($"License expired on {payload.ExpiresUtc:yyyy-MM-dd}. Contact support for renewal.");

        // 6. Hardware fingerprint must match
        var currentFingerprint = HardwareFingerprintService.GetFingerprint();
        if (!string.Equals(payload.HardwareId, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            return LicenseValidationResult.Fail(
                $"License is bound to a different machine (expected {payload.HardwareId[..8]}…, got {currentFingerprint[..8]}…). " +
                "Transfer your license by contacting support with your Hardware ID.");

        // 7. All checks passed
        _cached = payload;
        _validated = true;
        return LicenseValidationResult.Valid(payload);
    }

    /// <summary>Returns the current machine's hardware fingerprint for license generation.</summary>
    public static string GetMachineHardwareId() => HardwareFingerprintService.GetFingerprint();
}

// ── Models ──

public sealed class LicenseEnvelope
{
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}

public sealed class LicensePayload
{
    public string LicenseId { get; set; } = "";
    public string Licensee { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public DateTime IssuedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string Edition { get; set; } = "Enterprise"; // Free, Starter, Pro, Enterprise
    public int MaxUsers { get; set; } = 1;
    public string[] AllowedFeatures { get; set; } = [];
    public string[] AllowedDomains { get; set; } = []; // Domains where this license can run
}

public sealed class LicenseValidationResult
{
    public bool IsValid { get; private init; }
    public string? ErrorMessage { get; private init; }
    public LicensePayload? License { get; private init; }

    public static LicenseValidationResult Valid(LicensePayload license) => new()
    {
        IsValid = true,
        License = license
    };

    public static LicenseValidationResult Fail(string message) => new()
    {
        IsValid = false,
        ErrorMessage = message
    };
}
