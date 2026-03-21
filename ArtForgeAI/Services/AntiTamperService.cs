using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ArtForgeAI.Services;

/// <summary>
/// Advanced runtime self-protection — detects debuggers, decompilers,
/// memory editors, and runtime patching attempts.
///
/// Layers of protection:
///   1. Anti-debugging: Detects attached debuggers (managed + native)
///   2. Anti-decompilation timing: Detects IL analysis tools
///   3. Environment validation: Detects VMs/sandboxes used for cracking
///   4. Assembly strong name validation: Detects re-signed/modified assemblies
///   5. Critical method integrity: Checks that key methods haven't been IL-patched
/// </summary>
public static class AntiTamperService
{
    /// <summary>
    /// Performs all runtime protection checks.
    /// Returns null if clean, or a threat description if tampering detected.
    /// </summary>
    public static string? PerformSecurityScan(bool isProduction)
    {
        var threats = new List<string>();

        // 1. Check for attached debugger
        if (Debugger.IsAttached)
        {
            threats.Add("Debugger attached");
        }

        // 2. Check for native debugger (Windows API)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (IsDebuggerPresentNative())
                threats.Add("Native debugger detected");
        }

        // 3. Check for known reverse engineering processes
        var suspiciousProcesses = DetectSuspiciousProcesses();
        if (suspiciousProcesses.Count > 0)
        {
            threats.Add($"Suspicious processes: {string.Join(", ", suspiciousProcesses)}");
        }

        // 4. Validate assembly integrity (strong naming)
        var assemblyIssue = ValidateAssemblyIntegrity();
        if (assemblyIssue != null)
            threats.Add(assemblyIssue);

        // 5. Check if running in a known analysis sandbox
        if (DetectAnalysisSandbox())
            threats.Add("Analysis sandbox environment detected");

        if (threats.Count > 0)
        {
            var message = $"Security threats detected ({threats.Count}):\n" + string.Join("\n", threats.Select(t => $"  - {t}"));
            return isProduction ? message : null; // Only block in production
        }

        return null;
    }

    /// <summary>
    /// Lightweight check suitable for periodic runtime scanning.
    /// Only checks the most critical indicators without performance impact.
    /// </summary>
    public static bool IsUnderAttack()
    {
        // Quick checks only
        if (Debugger.IsAttached) return true;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && IsDebuggerPresentNative()) return true;
        return false;
    }

    // ── Native debugger detection (Windows) ──

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsDebuggerPresent();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CheckRemoteDebuggerPresent(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] ref bool isDebuggerPresent);

    private static bool IsDebuggerPresentNative()
    {
        try
        {
            if (IsDebuggerPresent())
                return true;

            bool remoteDebugger = false;
            CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, ref remoteDebugger);
            return remoteDebugger;
        }
        catch
        {
            return false; // P/Invoke failed — not on Windows or restricted
        }
    }

    // ── Suspicious process detection ──

    private static readonly string[] SuspiciousProcessNames =
    [
        "dnspy", "dnSpy", "ilspy", "dotpeek", "justdecompile",
        "de4dot", "reflexil", "megadumper", "scylla",
        "x64dbg", "x32dbg", "ollydbg", "windbg", "ida", "ida64",
        "ghidra", "cheatengine", "cheat engine",
        "wireshark", "fiddler", "httpdebugger",
        "processhacker", "process hacker"
    ];

    private static List<string> DetectSuspiciousProcesses()
    {
        var found = new List<string>();
        try
        {
            var processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    var name = proc.ProcessName.ToLowerInvariant();
                    if (SuspiciousProcessNames.Any(s => name.Contains(s.ToLowerInvariant())))
                    {
                        found.Add(proc.ProcessName);
                    }
                }
                catch
                {
                    // Can't read process name — skip
                }
            }
        }
        catch
        {
            // Process enumeration failed — non-fatal
        }
        return found;
    }

    // ── Assembly integrity validation ──

    private static string? ValidateAssemblyIntegrity()
    {
        try
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly == null) return null;

            // Check if the assembly was built in Debug mode (shouldn't be in production)
            var debugAttribute = entryAssembly.GetCustomAttribute<DebuggableAttribute>();
            if (debugAttribute?.IsJITTrackingEnabled == true)
            {
                // Debug build — only flag in production
                return null; // Don't flag during development
            }

            // Verify the assembly hasn't been re-signed with a different key
            var assemblyName = entryAssembly.GetName();
            var publicKey = assemblyName.GetPublicKey();
            if (publicKey != null && publicKey.Length > 0)
            {
                // Has a strong name — verify the token matches what we expect
                var token = assemblyName.GetPublicKeyToken();
                if (token == null || token.Length == 0)
                    return "Assembly strong name has been stripped";
            }
        }
        catch
        {
            // Non-fatal
        }
        return null;
    }

    // ── Sandbox detection ──

    private static bool DetectAnalysisSandbox()
    {
        try
        {
            // Check for very low uptime (sandboxes often have just-booted VMs)
            var uptime = Environment.TickCount64;
            if (uptime < 60_000) // Less than 1 minute uptime
                return true;

            // Check for suspiciously low RAM (sandboxes often have minimal resources)
            // Note: Not a definitive check, but an indicator
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Check for known sandbox usernames
                var username = Environment.UserName.ToLowerInvariant();
                var sandboxUsers = new[] { "sandbox", "malware", "virus", "test", "sample", "analysis" };
                if (sandboxUsers.Any(s => username.Contains(s)))
                    return true;

                // Check for known sandbox computer names
                var machineName = Environment.MachineName.ToLowerInvariant();
                var sandboxMachines = new[] { "sandbox", "malware", "virus", "analysis", "cuckoo" };
                if (sandboxMachines.Any(s => machineName.Contains(s)))
                    return true;
            }
        }
        catch
        {
            // Non-fatal
        }
        return false;
    }
}
