using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArtForgeAI.Services;

/// <summary>
/// Online "phone-home" license validation — the gold standard for SaaS clone prevention.
///
/// How it works:
///   1. On startup, the app contacts YOUR license server with its Hardware ID + License ID.
///   2. The server checks: is this license valid? is it already active on another machine?
///   3. If the same license is used on 2+ machines simultaneously → revoke.
///   4. A background heartbeat pings every 30 minutes to detect cloned instances.
///   5. If the server is unreachable, the app has a configurable grace period (default: 72 hours).
///
/// The license server is a simple API you host (even a free Azure Function or Cloudflare Worker).
/// </summary>
public sealed class OnlineLicenseValidationService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OnlineLicenseValidationService> _logger;
    private readonly string _licenseServerUrl;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _gracePeriod;
    private Timer? _heartbeatTimer;
    private DateTime? _lastSuccessfulCheck;
    private bool _isRevoked;
    private string? _revocationReason;

    public bool IsRevoked => _isRevoked;
    public string? RevocationReason => _revocationReason;
    public DateTime? LastSuccessfulCheck => _lastSuccessfulCheck;

    public OnlineLicenseValidationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<OnlineLicenseValidationService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("LicenseServer");
        _logger = logger;

        _licenseServerUrl = config["Security:LicenseServerUrl"] ?? "";
        _heartbeatInterval = TimeSpan.FromMinutes(config.GetValue("Security:HeartbeatMinutes", 30));
        _gracePeriod = TimeSpan.FromHours(config.GetValue("Security:GracePeriodHours", 72));
    }

    /// <summary>
    /// Performs the initial online activation check.
    /// Call at startup after offline license validation passes.
    /// </summary>
    public async Task<OnlineValidationResult> ActivateAsync(string licenseId, string hardwareId)
    {
        if (string.IsNullOrEmpty(_licenseServerUrl))
        {
            // No license server configured — skip online validation (offline-only mode)
            _lastSuccessfulCheck = DateTime.UtcNow;
            return OnlineValidationResult.Ok("Offline mode — no license server configured.");
        }

        try
        {
            var payload = new
            {
                LicenseId = licenseId,
                HardwareId = hardwareId,
                AppVersion = typeof(OnlineLicenseValidationService).Assembly.GetName().Version?.ToString() ?? "1.0",
                MachineName = Environment.MachineName,
                Timestamp = DateTime.UtcNow,
                Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            };

            var response = await _httpClient.PostAsJsonAsync(
                $"{_licenseServerUrl.TrimEnd('/')}/api/license/activate", payload);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ServerLicenseResponse>();
                if (result is { Valid: true })
                {
                    _lastSuccessfulCheck = DateTime.UtcNow;
                    StartHeartbeat(licenseId, hardwareId);
                    return OnlineValidationResult.Ok("License activated online.");
                }

                // Server says invalid
                _isRevoked = true;
                _revocationReason = result?.Reason ?? "License rejected by server.";
                return OnlineValidationResult.Revoked(_revocationReason);
            }

            // Server returned error — check grace period
            return HandleServerUnavailable("Server returned error status.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Online license activation failed — entering grace period");
            return HandleServerUnavailable($"Cannot reach license server: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts the periodic heartbeat timer.
    /// Each heartbeat verifies the license is still valid and not cloned.
    /// </summary>
    private void StartHeartbeat(string licenseId, string hardwareId)
    {
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = new Timer(async _ =>
        {
            try
            {
                var payload = new
                {
                    LicenseId = licenseId,
                    HardwareId = hardwareId,
                    Timestamp = DateTime.UtcNow,
                    Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                };

                var response = await _httpClient.PostAsJsonAsync(
                    $"{_licenseServerUrl.TrimEnd('/')}/api/license/heartbeat", payload);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync<ServerLicenseResponse>();
                    if (result is { Valid: true })
                    {
                        _lastSuccessfulCheck = DateTime.UtcNow;
                        _logger.LogDebug("License heartbeat OK");
                    }
                    else
                    {
                        _isRevoked = true;
                        _revocationReason = result?.Reason ?? "License revoked by server.";
                        _logger.LogCritical("LICENSE REVOKED: {Reason}", _revocationReason);
                    }
                }
                else
                {
                    CheckGracePeriod();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "License heartbeat failed");
                CheckGracePeriod();
            }
        }, null, _heartbeatInterval, _heartbeatInterval);
    }

    private void CheckGracePeriod()
    {
        if (_lastSuccessfulCheck == null)
            return;

        var elapsed = DateTime.UtcNow - _lastSuccessfulCheck.Value;
        if (elapsed > _gracePeriod)
        {
            _isRevoked = true;
            _revocationReason = $"License server unreachable for {elapsed.TotalHours:F0} hours (grace period: {_gracePeriod.TotalHours:F0}h). Connect to the internet to re-validate.";
            _logger.LogCritical("GRACE PERIOD EXPIRED: {Reason}", _revocationReason);
        }
        else
        {
            _logger.LogWarning("License server unreachable. Grace period remaining: {Hours:F1}h",
                (_gracePeriod - elapsed).TotalHours);
        }
    }

    private OnlineValidationResult HandleServerUnavailable(string reason)
    {
        if (_lastSuccessfulCheck != null)
        {
            CheckGracePeriod();
            if (_isRevoked)
                return OnlineValidationResult.Revoked(_revocationReason!);
            return OnlineValidationResult.GracePeriod(reason);
        }

        // First-ever activation and server is down — still allow with grace period
        _lastSuccessfulCheck = DateTime.UtcNow;
        return OnlineValidationResult.GracePeriod(reason);
    }

    public void Dispose()
    {
        _heartbeatTimer?.Dispose();
    }
}

public sealed class OnlineValidationResult
{
    public enum Status { Valid, GracePeriod, Revoked }

    public Status ValidationStatus { get; private init; }
    public string Message { get; private init; } = "";

    public bool IsAllowed => ValidationStatus != Status.Revoked;

    public static OnlineValidationResult Ok(string message) => new()
        { ValidationStatus = Status.Valid, Message = message };

    public static OnlineValidationResult GracePeriod(string message) => new()
        { ValidationStatus = Status.GracePeriod, Message = message };

    public static OnlineValidationResult Revoked(string message) => new()
        { ValidationStatus = Status.Revoked, Message = message };
}

public sealed class ServerLicenseResponse
{
    public bool Valid { get; set; }
    public string? Reason { get; set; }
}
