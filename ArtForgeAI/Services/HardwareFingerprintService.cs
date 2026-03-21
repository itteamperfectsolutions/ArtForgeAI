using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace ArtForgeAI.Services;

/// <summary>
/// Generates a stable, unique hardware fingerprint for the current machine.
/// Combines CPU ID, motherboard serial, OS serial, and primary MAC address
/// into a single SHA-256 hash that is reproducible across restarts but
/// unique per machine — used as the hardware anchor for license validation.
/// </summary>
public static class HardwareFingerprintService
{
    private static string? _cachedFingerprint;

    /// <summary>
    /// Returns the SHA-256 hardware fingerprint for this machine.
    /// Result is cached for the lifetime of the process.
    /// </summary>
    public static string GetFingerprint()
    {
        if (_cachedFingerprint != null)
            return _cachedFingerprint;

        var components = new StringBuilder();

        // 1. CPU — ProcessorId is burned into the silicon
        components.Append(GetWmiValue("Win32_Processor", "ProcessorId"));

        // 2. Motherboard — SerialNumber is unique per board
        components.Append(GetWmiValue("Win32_BaseBoard", "SerialNumber"));

        // 3. BIOS — SerialNumber is set at manufacture
        components.Append(GetWmiValue("Win32_BIOS", "SerialNumber"));

        // 4. OS installation — SerialNumber changes on reinstall (extra entropy)
        components.Append(GetWmiValue("Win32_OperatingSystem", "SerialNumber"));

        // 5. Primary MAC address — stable across reboots
        components.Append(GetPrimaryMacAddress());

        // 6. Machine name as salt (prevents identical-hardware collisions in labs)
        components.Append(Environment.MachineName);

        // SHA-256 the concatenation — irreversible, fixed length
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(components.ToString()));
        _cachedFingerprint = Convert.ToHexString(hash);
        return _cachedFingerprint;
    }

    /// <summary>
    /// Returns a truncated display-safe version (first 16 hex chars) for UI/logs.
    /// </summary>
    public static string GetShortFingerprint() => GetFingerprint()[..16];

    private static string GetWmiValue(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var obj in searcher.Get())
            {
                var val = obj[property]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(val))
                    return val;
            }
        }
        catch
        {
            // WMI unavailable — degrade gracefully, other components still anchor the fingerprint
        }
        return string.Empty;
    }

    private static string GetPrimaryMacAddress()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(n => n.Speed)
                .FirstOrDefault();

            return nic?.GetPhysicalAddress().ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
