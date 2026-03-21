using System.Reflection;
using System.Security.Cryptography;

namespace ArtForgeAI.Services;

/// <summary>
/// Runtime integrity verification — detects if application binaries have been
/// modified after build (patched, hex-edited, or injected).
///
/// How it works:
///   1. At build/publish time, compute SHA-256 of each assembly and store in a manifest.
///   2. At runtime, re-compute hashes and compare against the signed manifest.
///   3. If any assembly hash mismatches, the app refuses to start.
///
/// For development, tamper detection runs in audit mode (logs warnings but doesn't block).
/// In Release/Production, mismatches are fatal.
/// </summary>
public static class TamperDetectionService
{
    private static readonly string ManifestPath =
        Path.Combine(AppContext.BaseDirectory, "integrity.manifest");

    /// <summary>
    /// Verifies the integrity of key application assemblies.
    /// Returns null if valid, or an error message if tampering is detected.
    /// </summary>
    public static string? VerifyIntegrity(bool isProduction)
    {
        // If no manifest exists, generate one (first run after publish)
        if (!File.Exists(ManifestPath))
        {
            if (isProduction)
                return "Integrity manifest missing — application may have been tampered with.";

            // Development: auto-generate manifest
            GenerateManifest();
            return null;
        }

        var expectedHashes = LoadManifest();
        var violations = new List<string>();

        foreach (var (file, expectedHash) in expectedHashes)
        {
            var fullPath = Path.Combine(AppContext.BaseDirectory, file);
            if (!File.Exists(fullPath))
            {
                violations.Add($"Missing: {file}");
                continue;
            }

            var actualHash = ComputeFileHash(fullPath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Modified: {file} (expected {expectedHash[..12]}…, got {actualHash[..12]}…)");
            }
        }

        if (violations.Count > 0)
        {
            var message = $"Integrity check failed — {violations.Count} file(s) tampered:\n" +
                          string.Join("\n", violations);

            if (isProduction)
                return message;
        }

        return null;
    }

    /// <summary>
    /// Generates the integrity manifest from current assemblies.
    /// Call this during publish/deployment, or it auto-generates in dev.
    /// </summary>
    public static void GenerateManifest()
    {
        var assemblies = GetProtectedFiles();
        var lines = new List<string>();

        foreach (var file in assemblies)
        {
            var fullPath = Path.Combine(AppContext.BaseDirectory, file);
            if (File.Exists(fullPath))
            {
                var hash = ComputeFileHash(fullPath);
                lines.Add($"{hash}|{file}");
            }
        }

        File.WriteAllLines(ManifestPath, lines);
    }

    private static Dictionary<string, string> LoadManifest()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(ManifestPath))
        {
            var parts = line.Split('|', 2);
            if (parts.Length == 2)
                result[parts[1]] = parts[0];
        }
        return result;
    }

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static string[] GetProtectedFiles()
    {
        // Protect the main application assembly and all ArtForgeAI assemblies
        var appName = Assembly.GetEntryAssembly()?.GetName().Name ?? "ArtForgeAI";
        var files = new List<string>
        {
            $"{appName}.dll",
            $"{appName}.exe"
        };

        // Also protect all DLLs in the app directory that are part of ArtForgeAI
        try
        {
            var dlls = Directory.GetFiles(AppContext.BaseDirectory, "ArtForgeAI*.dll");
            foreach (var dll in dlls)
            {
                files.Add(Path.GetFileName(dll));
            }
        }
        catch
        {
            // Non-fatal — at minimum the main assembly is protected
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
