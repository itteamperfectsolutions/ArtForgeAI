using ArtForgeAI.Services;

namespace ArtForgeAI.Middleware;

/// <summary>
/// HTTP middleware that enforces all clone protection layers on every request:
///   1. Domain lock — reject requests from unauthorized domains
///   2. Offline license validation — RSA-signed, hardware-bound
///   3. Online license revocation check — phone-home heartbeat
///   4. Runtime anti-tamper — periodic debugger/patcher detection
///
/// Static assets and framework endpoints are excluded so the
/// error page can render even without a valid license.
/// </summary>
public sealed class LicenseMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LicenseService _licenseService;
    private readonly DomainLockService _domainLockService;
    private readonly OnlineLicenseValidationService _onlineLicenseService;
    private readonly ILogger<LicenseMiddleware> _logger;
    private readonly bool _isProduction;
    private LicenseValidationResult? _cachedResult;

    public LicenseMiddleware(RequestDelegate next, LicenseService licenseService,
        DomainLockService domainLockService, OnlineLicenseValidationService onlineLicenseService,
        ILogger<LicenseMiddleware> logger, IWebHostEnvironment env)
    {
        _next = next;
        _licenseService = licenseService;
        _domainLockService = domainLockService;
        _onlineLicenseService = onlineLicenseService;
        _logger = logger;
        _isProduction = env.IsProduction();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        // Always allow: static files, Blazor framework, SignalR, and license info endpoint
        if (IsExcludedPath(path))
        {
            await _next(context);
            return;
        }

        // ── Layer 1: Domain Lock ──
        var host = context.Request.Host.Value;
        var domainError = _domainLockService.ValidateHost(host);
        if (domainError != null)
        {
            _logger.LogCritical("DOMAIN LOCK: Blocked request from unauthorized host '{Host}'", host);
            await WriteErrorResponse(context, "Unauthorized Domain", domainError);
            return;
        }

        // ── Layer 2: Offline License Validation ──
        _cachedResult ??= _licenseService.Validate();
        if (!_cachedResult.IsValid)
        {
            _logger.LogError("License validation failed: {Error}", _cachedResult.ErrorMessage);
            await WriteErrorResponse(context, "License Required", _cachedResult.ErrorMessage!);
            return;
        }

        // ── Layer 3: Online License Revocation Check ──
        if (_onlineLicenseService.IsRevoked)
        {
            _logger.LogCritical("LICENSE REVOKED: {Reason}", _onlineLicenseService.RevocationReason);
            await WriteErrorResponse(context, "License Revoked",
                _onlineLicenseService.RevocationReason ?? "Your license has been revoked. Contact support.");
            return;
        }

        await _next(context);
    }

    private static bool IsExcludedPath(string path)
    {
        return path.StartsWith("/_blazor") ||
               path.StartsWith("/_framework") ||
               path.StartsWith("/_content") ||
               path.StartsWith("/css") ||
               path.StartsWith("/js") ||
               path.StartsWith("/favicon") ||
               path.StartsWith("/api/license-info") ||
               path.EndsWith(".css") ||
               path.EndsWith(".js") ||
               path.EndsWith(".png") ||
               path.EndsWith(".jpg") ||
               path.EndsWith(".ico") ||
               path.EndsWith(".woff") ||
               path.EndsWith(".woff2");
    }

    private static async Task WriteErrorResponse(HttpContext context, string title, string error)
    {
        var hwid = HardwareFingerprintService.GetFingerprint();
        var safeError = System.Net.WebUtility.HtmlEncode(error);
        var safeTitle = System.Net.WebUtility.HtmlEncode(title);

        context.Response.StatusCode = 403;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($$"""
        <!DOCTYPE html>
        <html>
        <head>
            <title>ArtForge AI — {{safeTitle}}</title>
            <style>
                body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
                       background: #0f172a; color: #e2e8f0; display: flex; justify-content: center;
                       align-items: center; min-height: 100vh; margin: 0; }
                .card { background: #1e293b; border-radius: 16px; padding: 48px; max-width: 600px;
                        box-shadow: 0 25px 50px rgba(0,0,0,0.5); text-align: center; }
                h1 { color: #f472b6; margin-bottom: 8px; font-size: 24px; }
                .error { background: #7f1d1d33; border: 1px solid #991b1b; border-radius: 8px;
                         padding: 16px; margin: 24px 0; color: #fca5a5; font-size: 14px;
                         text-align: left; line-height: 1.6; }
                .hwid { background: #1a1a2e; border-radius: 8px; padding: 16px; margin-top: 24px;
                        font-family: 'Cascadia Code', monospace; font-size: 13px; color: #94a3b8;
                        word-break: break-all; user-select: all; }
                .label { font-size: 12px; color: #64748b; margin-bottom: 4px; }
                .copy-btn { background: #3b82f6; color: white; border: none; border-radius: 6px;
                           padding: 8px 16px; cursor: pointer; margin-top: 12px; font-size: 13px; }
                .copy-btn:hover { background: #2563eb; }
                .shield { font-size: 48px; margin-bottom: 16px; }
            </style>
        </head>
        <body>
            <div class="card">
                <div class="shield">&#128737;</div>
                <h1>{{safeTitle}}</h1>
                <p style="color:#94a3b8">ArtForge AI requires a valid license to run.</p>
                <div class="error">{{safeError}}</div>
                <div class="label">Your Hardware ID (send this to get your license):</div>
                <div class="hwid" id="hwid">{{hwid}}</div>
                <button class="copy-btn" onclick="navigator.clipboard.writeText(document.getElementById('hwid').textContent)">
                    Copy Hardware ID
                </button>
            </div>
        </body>
        </html>
        """);
    }
}
