namespace ArtForgeAI.Services;

/// <summary>
/// Domain/URL locking — prevents the app from being deployed on unauthorized domains.
///
/// How it works:
///   1. A list of allowed domains is configured (e.g., "artforgeai.com", "localhost").
///   2. On every request, the Host header is checked against the allowed list.
///   3. If the request comes from an unlicensed domain → blocked.
///
/// Why this matters:
///   - Even if someone clones the entire app + database, they can't serve it from their own domain.
///   - Prevents subdomain hijacking and reverse-proxy bypass attempts.
///   - Combined with license validation, this creates a two-factor deployment lock.
/// </summary>
public sealed class DomainLockService
{
    private readonly HashSet<string> _allowedDomains;
    private readonly bool _isEnabled;

    public DomainLockService(IConfiguration config)
    {
        var domains = config.GetSection("Security:AllowedDomains").Get<string[]>();
        _allowedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (domains != null && domains.Length > 0)
        {
            foreach (var domain in domains)
            {
                if (!string.IsNullOrWhiteSpace(domain))
                    _allowedDomains.Add(domain.Trim());
            }
            _isEnabled = true;
        }
        else
        {
            // No domains configured — add defaults for development
            _allowedDomains.Add("localhost");
            _isEnabled = false; // Disabled until configured
        }
    }

    public bool IsEnabled => _isEnabled;

    /// <summary>
    /// Checks if the request's Host header matches an allowed domain.
    /// Returns null if allowed, or an error message if blocked.
    /// </summary>
    public string? ValidateHost(string host)
    {
        if (!_isEnabled)
            return null; // Domain lock not configured — allow all

        // Strip port number (e.g., "localhost:7027" → "localhost")
        var hostname = host.Contains(':') ? host.Split(':')[0] : host;

        if (_allowedDomains.Contains(hostname))
            return null;

        // Check wildcard subdomains (e.g., "*.artforgeai.com" matches "app.artforgeai.com")
        foreach (var allowed in _allowedDomains)
        {
            if (allowed.StartsWith("*."))
            {
                var baseDomain = allowed[2..];
                if (hostname.EndsWith($".{baseDomain}", StringComparison.OrdinalIgnoreCase) ||
                    hostname.Equals(baseDomain, StringComparison.OrdinalIgnoreCase))
                    return null;
            }
        }

        return $"This application is not licensed to run on '{hostname}'. Authorized domains: {string.Join(", ", _allowedDomains)}";
    }
}
