namespace Maia.Core.Configuration;

/// <summary>
/// Auth knobs, bound from the "Auth" section of appsettings.json. Lives in Core so
/// both the Application service and the API auth handler can depend on it without a
/// cross-layer reference. Defaults match the v1 spec (3h sliding idle).
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Idle window in seconds. Activity within it slides expiry forward;
    /// exceeding it expires the session lazily on next access. Default 3h.</summary>
    public int IdleTimeoutSeconds { get; set; } = 10800;

    /// <summary>Name of the httpOnly session cookie.</summary>
    public string CookieName { get; set; } = "maia_session";

    /// <summary>Mark the cookie Secure (HTTPS-only). False for dev over http://localhost;
    /// set true in any real deployment.</summary>
    public bool CookieSecure { get; set; } = false;

    /// <summary>Don't write LastActivityAt on every request — only when it has drifted
    /// at least this many seconds. Keeps the per-request cost to a read (the cookie
    /// refresh is header-only), not a write. Default 60s.</summary>
    public int ActivitySlideThrottleSeconds { get; set; } = 60;
}
