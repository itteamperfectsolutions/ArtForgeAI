using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// Minimal license validation server — deploy this as a standalone service.
///
/// Endpoints:
///   POST /api/license/activate   — Called once on app startup
///   POST /api/license/heartbeat  — Called every 30 minutes by the app
///   GET  /api/license/status     — Admin endpoint to see all active licenses
///   POST /api/license/revoke     — Admin endpoint to revoke a license
///
/// This tracks which Hardware IDs are using which licenses.
/// If the same license appears on 2+ different hardware IDs → clone detected → revoke.
///
/// For production: replace the in-memory store with a database (SQLite, Cosmos, etc.)
/// and deploy behind HTTPS (Azure App Service, Cloudflare Worker, etc.)
/// </summary>

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// In-memory store (replace with a database for production)
var activeLicenses = new ConcurrentDictionary<string, LicenseActivation>();
var revokedLicenses = new ConcurrentDictionary<string, string>(); // licenseId → reason

// Admin API key (set via environment variable in production)
var adminKey = app.Configuration["AdminApiKey"] ?? "change-me-in-production";

// ── Activation endpoint ──
app.MapPost("/api/license/activate", (ActivationRequest request) =>
{
    if (string.IsNullOrEmpty(request.LicenseId) || string.IsNullOrEmpty(request.HardwareId))
        return Results.BadRequest(new { Valid = false, Reason = "Missing license ID or hardware ID." });

    // Check if revoked
    if (revokedLicenses.TryGetValue(request.LicenseId, out var revokeReason))
        return Results.Ok(new { Valid = false, Reason = $"License revoked: {revokeReason}" });

    // Check for clone: is this license already active on a DIFFERENT machine?
    if (activeLicenses.TryGetValue(request.LicenseId, out var existing))
    {
        if (!string.Equals(existing.HardwareId, request.HardwareId, StringComparison.OrdinalIgnoreCase))
        {
            // CLONE DETECTED: Same license on different hardware
            var reason = $"Clone detected: license active on machine {existing.HardwareId[..8]}… " +
                         $"(activated {existing.ActivatedAt:yyyy-MM-dd HH:mm} UTC), " +
                         $"now attempted on {request.HardwareId[..8]}…";

            // Auto-revoke the license
            revokedLicenses[request.LicenseId] = reason;
            activeLicenses.TryRemove(request.LicenseId, out _);

            app.Logger.LogCritical("CLONE DETECTED: {Reason}", reason);
            return Results.Ok(new { Valid = false, Reason = reason });
        }

        // Same machine — update last seen
        existing.LastHeartbeat = DateTime.UtcNow;
        existing.MachineName = request.MachineName;
        return Results.Ok(new { Valid = true, Reason = "License re-activated." });
    }

    // New activation
    activeLicenses[request.LicenseId] = new LicenseActivation
    {
        LicenseId = request.LicenseId,
        HardwareId = request.HardwareId,
        MachineName = request.MachineName,
        AppVersion = request.AppVersion,
        ActivatedAt = DateTime.UtcNow,
        LastHeartbeat = DateTime.UtcNow
    };

    app.Logger.LogInformation("License activated: {LicenseId} on {HardwareId}", request.LicenseId, request.HardwareId[..8]);
    return Results.Ok(new { Valid = true, Reason = "License activated successfully." });
});

// ── Heartbeat endpoint ──
app.MapPost("/api/license/heartbeat", (HeartbeatRequest request) =>
{
    if (string.IsNullOrEmpty(request.LicenseId) || string.IsNullOrEmpty(request.HardwareId))
        return Results.BadRequest(new { Valid = false, Reason = "Missing parameters." });

    // Check if revoked
    if (revokedLicenses.TryGetValue(request.LicenseId, out var revokeReason))
        return Results.Ok(new { Valid = false, Reason = $"License revoked: {revokeReason}" });

    // Update heartbeat
    if (activeLicenses.TryGetValue(request.LicenseId, out var activation))
    {
        if (!string.Equals(activation.HardwareId, request.HardwareId, StringComparison.OrdinalIgnoreCase))
        {
            // Hardware changed — possible clone
            return Results.Ok(new { Valid = false, Reason = "Hardware mismatch — license bound to different machine." });
        }

        activation.LastHeartbeat = DateTime.UtcNow;
        return Results.Ok(new { Valid = true });
    }

    // Not activated — tell client to re-activate
    return Results.Ok(new { Valid = false, Reason = "License not activated. Restart the application." });
});

// ── Admin: View all active licenses ──
app.MapGet("/api/license/status", (HttpContext ctx) =>
{
    if (!ValidateAdmin(ctx)) return Results.Unauthorized();

    var active = activeLicenses.Values.Select(a => new
    {
        a.LicenseId,
        HardwareIdShort = a.HardwareId[..16],
        a.MachineName,
        a.AppVersion,
        a.ActivatedAt,
        a.LastHeartbeat,
        MinutesSinceHeartbeat = (DateTime.UtcNow - a.LastHeartbeat).TotalMinutes
    });

    return Results.Ok(new { ActiveLicenses = active, RevokedLicenses = revokedLicenses });
});

// ── Admin: Revoke a license ──
app.MapPost("/api/license/revoke", (HttpContext ctx, RevokeRequest request) =>
{
    if (!ValidateAdmin(ctx)) return Results.Unauthorized();

    revokedLicenses[request.LicenseId] = request.Reason ?? "Revoked by admin.";
    activeLicenses.TryRemove(request.LicenseId, out _);

    app.Logger.LogWarning("License revoked by admin: {LicenseId} — {Reason}", request.LicenseId, request.Reason);
    return Results.Ok(new { Success = true, Message = $"License {request.LicenseId} revoked." });
});

// ── Admin: Un-revoke a license ──
app.MapPost("/api/license/unrevoke", (HttpContext ctx, UnrevokeRequest request) =>
{
    if (!ValidateAdmin(ctx)) return Results.Unauthorized();

    revokedLicenses.TryRemove(request.LicenseId, out _);
    return Results.Ok(new { Success = true, Message = $"License {request.LicenseId} un-revoked." });
});

app.Run();

bool ValidateAdmin(HttpContext ctx)
{
    var key = ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
    return key == adminKey;
}

// ── Request/Response Models ──

record ActivationRequest
{
    public string LicenseId { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string? MachineName { get; init; }
    public string? AppVersion { get; init; }
    public DateTime Timestamp { get; init; }
    public string? Nonce { get; init; }
}

record HeartbeatRequest
{
    public string LicenseId { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string? Nonce { get; init; }
}

record RevokeRequest
{
    public string LicenseId { get; init; } = "";
    public string? Reason { get; init; }
}

record UnrevokeRequest
{
    public string LicenseId { get; init; } = "";
}

class LicenseActivation
{
    public string LicenseId { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public string? MachineName { get; set; }
    public string? AppVersion { get; set; }
    public DateTime ActivatedAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
}
