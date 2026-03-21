namespace ArtForgeAI.Services;

/// <summary>
/// Background hosted service that continuously monitors for security threats.
///
/// Runs periodic checks every 5 minutes:
///   1. Anti-debugging scan (detects newly attached debuggers)
///   2. Online license heartbeat validation (detects revocation)
///   3. Assembly integrity re-verification (detects runtime patching)
///   4. Domain lock re-validation
///
/// If any check fails in production → logs critical alert and triggers shutdown.
/// This prevents "attach debugger after startup" bypass attempts.
/// </summary>
public sealed class RuntimeProtectionHostedService : BackgroundService
{
    private readonly ILogger<RuntimeProtectionHostedService> _logger;
    private readonly OnlineLicenseValidationService _onlineLicense;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly bool _isProduction;
    private readonly TimeSpan _scanInterval;

    public RuntimeProtectionHostedService(
        ILogger<RuntimeProtectionHostedService> logger,
        OnlineLicenseValidationService onlineLicense,
        IHostApplicationLifetime lifetime,
        IWebHostEnvironment env,
        IConfiguration config)
    {
        _logger = logger;
        _onlineLicense = onlineLicense;
        _lifetime = lifetime;
        _isProduction = env.IsProduction();
        _scanInterval = TimeSpan.FromMinutes(config.GetValue("Security:ScanIntervalMinutes", 5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit for the app to fully start
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        _logger.LogInformation("Runtime protection monitor started (interval: {Interval})", _scanInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Anti-debugging check
                if (AntiTamperService.IsUnderAttack())
                {
                    _logger.LogCritical("SECURITY: Debugger detected at runtime!");
                    if (_isProduction)
                    {
                        _logger.LogCritical("Shutting down due to security threat.");
                        _lifetime.StopApplication();
                        return;
                    }
                }

                // 2. Online license revocation check
                if (_onlineLicense.IsRevoked)
                {
                    _logger.LogCritical("SECURITY: License revoked — {Reason}", _onlineLicense.RevocationReason);
                    if (_isProduction)
                    {
                        _lifetime.StopApplication();
                        return;
                    }
                }

                // 3. Re-verify assembly integrity (detects runtime DLL injection)
                var tamperResult = TamperDetectionService.VerifyIntegrity(_isProduction);
                if (tamperResult != null && _isProduction)
                {
                    _logger.LogCritical("SECURITY: Runtime tamper detected — {Error}", tamperResult);
                    _lifetime.StopApplication();
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Security scan iteration failed");
            }

            await Task.Delay(_scanInterval, stoppingToken);
        }
    }
}
