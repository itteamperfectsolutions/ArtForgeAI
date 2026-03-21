using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Offline license generation tool — run this on YOUR machine only.
/// Never distribute this tool or the private key.
///
/// Usage:
///   LicenseGenerator keygen                          — Generate RSA-2048 key pair (first time only)
///   LicenseGenerator issue <hwid> <licensee> [days]  — Generate a license file
///   LicenseGenerator verify <license-file>           — Verify a license file
///
/// The private key (private.key) stays with you. The public key XML goes into
/// LicenseService.cs → EmbeddedPublicKeyXml constant.
/// </summary>

var keysDir = Path.Combine(AppContext.BaseDirectory, "keys");
Directory.CreateDirectory(keysDir);

var privateKeyPath = Path.Combine(keysDir, "private.key");
var publicKeyPath = Path.Combine(keysDir, "public.key");
var publicKeyXmlPath = Path.Combine(keysDir, "public.xml");

if (args.Length == 0)
{
    PrintUsage();
    return;
}

switch (args[0].ToLower())
{
    case "keygen":
        GenerateKeyPair();
        break;

    case "issue":
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: LicenseGenerator issue <hardware-id> <licensee> [days=365] [edition=Enterprise]");
            return;
        }
        var hwid = args[1];
        var licensee = args[2];
        var days = args.Length > 3 ? int.Parse(args[3]) : 365;
        var edition = args.Length > 4 ? args[4] : "Enterprise";
        IssueLicense(hwid, licensee, days, edition);
        break;

    case "verify":
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: LicenseGenerator verify <license-file>");
            return;
        }
        VerifyLicense(args[1]);
        break;

    default:
        PrintUsage();
        break;
}

void PrintUsage()
{
    Console.WriteLine("""
    ╔═══════════════════════════════════════════════════════╗
    ║          ArtForge AI — License Generator              ║
    ╠═══════════════════════════════════════════════════════╣
    ║                                                       ║
    ║  keygen                          Generate RSA keys    ║
    ║  issue <hwid> <name> [days] [ed] Create license       ║
    ║  verify <file>                   Verify a license     ║
    ║                                                       ║
    ║  First run: keygen → copy public.xml content into     ║
    ║  LicenseService.cs EmbeddedPublicKeyXml constant.     ║
    ║                                                       ║
    ╚═══════════════════════════════════════════════════════╝
    """);
}

void GenerateKeyPair()
{
    if (File.Exists(privateKeyPath))
    {
        Console.Write("Key pair already exists. Overwrite? (y/N): ");
        if (Console.ReadLine()?.Trim().ToLower() != "y")
        {
            Console.WriteLine("Cancelled.");
            return;
        }
    }

    using var rsa = RSA.Create(2048);

    // Export private key (KEEP SECRET — never distribute)
    var privateKeyXml = rsa.ToXmlString(true);
    File.WriteAllText(privateKeyPath, privateKeyXml);
    Console.WriteLine($"Private key saved to: {privateKeyPath}");
    Console.WriteLine("  *** KEEP THIS SECRET — never distribute ***");

    // Export public key (embed in application)
    var publicKeyXml = rsa.ToXmlString(false);
    File.WriteAllText(publicKeyPath, publicKeyXml);
    File.WriteAllText(publicKeyXmlPath, publicKeyXml);
    Console.WriteLine($"Public key saved to:  {publicKeyPath}");
    Console.WriteLine();
    Console.WriteLine("═══ COPY THIS INTO LicenseService.cs → EmbeddedPublicKeyXml ═══");
    Console.WriteLine(publicKeyXml);
    Console.WriteLine("════════════════════════════════════════════════════════════════");
}

void IssueLicense(string hardwareId, string licenseeName, int validDays, string editionName)
{
    if (!File.Exists(privateKeyPath))
    {
        Console.WriteLine("No private key found. Run 'keygen' first.");
        return;
    }

    var privateKeyXml = File.ReadAllText(privateKeyPath);

    var payload = new LicensePayload
    {
        LicenseId = Guid.NewGuid().ToString("N")[..16].ToUpper(),
        Licensee = licenseeName,
        HardwareId = hardwareId,
        IssuedUtc = DateTime.UtcNow,
        ExpiresUtc = DateTime.UtcNow.AddDays(validDays),
        Edition = editionName,
        MaxUsers = editionName.Equals("Enterprise", StringComparison.OrdinalIgnoreCase) ? 100 : 1,
        AllowedFeatures = ["*"] // All features — customize per edition as needed
    };

    var payloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    });

    // Sign with RSA-SHA256
    using var rsa = RSA.Create();
    rsa.FromXmlString(privateKeyXml);
    var signatureBytes = rsa.SignData(
        Encoding.UTF8.GetBytes(payloadJson),
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    var envelope = new
    {
        Payload = payloadJson,
        Signature = Convert.ToBase64String(signatureBytes)
    };

    var licenseJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
    var outputPath = Path.Combine(AppContext.BaseDirectory, $"license_{payload.LicenseId}.lic");
    File.WriteAllText(outputPath, licenseJson);

    Console.WriteLine();
    Console.WriteLine($"License generated successfully!");
    Console.WriteLine($"  ID:       {payload.LicenseId}");
    Console.WriteLine($"  Licensee: {payload.Licensee}");
    Console.WriteLine($"  HWID:     {payload.HardwareId[..16]}…");
    Console.WriteLine($"  Edition:  {payload.Edition}");
    Console.WriteLine($"  Expires:  {payload.ExpiresUtc:yyyy-MM-dd}");
    Console.WriteLine($"  File:     {outputPath}");
    Console.WriteLine();
    Console.WriteLine("Deploy this file as 'license.lic' in the application directory.");
}

void VerifyLicense(string filePath)
{
    if (!File.Exists(filePath))
    {
        Console.WriteLine($"File not found: {filePath}");
        return;
    }

    if (!File.Exists(publicKeyPath))
    {
        Console.WriteLine("No public key found. Run 'keygen' first.");
        return;
    }

    var content = File.ReadAllText(filePath);
    var envelope = JsonSerializer.Deserialize<LicenseEnvelope>(content, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

    if (envelope == null || string.IsNullOrEmpty(envelope.Payload) || string.IsNullOrEmpty(envelope.Signature))
    {
        Console.WriteLine("Invalid license format.");
        return;
    }

    var publicKeyXml = File.ReadAllText(publicKeyPath);
    using var rsa = RSA.Create();
    rsa.FromXmlString(publicKeyXml);

    var isValid = rsa.VerifyData(
        Encoding.UTF8.GetBytes(envelope.Payload),
        Convert.FromBase64String(envelope.Signature),
        HashAlgorithmName.SHA256,
        RSASignaturePadding.Pkcs1);

    if (isValid)
    {
        var payload = JsonSerializer.Deserialize<LicensePayload>(envelope.Payload, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Console.WriteLine("Signature: VALID");
        Console.WriteLine($"  ID:       {payload?.LicenseId}");
        Console.WriteLine($"  Licensee: {payload?.Licensee}");
        Console.WriteLine($"  HWID:     {payload?.HardwareId?[..16]}…");
        Console.WriteLine($"  Edition:  {payload?.Edition}");
        Console.WriteLine($"  Expires:  {payload?.ExpiresUtc:yyyy-MM-dd}");
        Console.WriteLine($"  Expired:  {(payload?.ExpiresUtc < DateTime.UtcNow ? "YES" : "No")}");
    }
    else
    {
        Console.WriteLine("Signature: INVALID — license has been tampered with!");
    }
}

// ── Shared models (same as in the main app) ──

record LicensePayload
{
    public string LicenseId { get; init; } = "";
    public string Licensee { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public DateTime IssuedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
    public string Edition { get; init; } = "Enterprise";
    public int MaxUsers { get; init; } = 1;
    public string[] AllowedFeatures { get; init; } = [];
}

record LicenseEnvelope
{
    public string Payload { get; init; } = "";
    public string Signature { get; init; } = "";
}
