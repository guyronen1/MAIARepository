namespace Maia.API.Auth;

/// <summary>Custom claim types emitted by <see cref="MaiaSessionAuthenticationHandler"/>.
/// (Username → ClaimTypes.Name, role → ClaimTypes.Role, userId → ClaimTypes.NameIdentifier
/// use the standard types; only MAIA-specific claims live here.)</summary>
public static class MaiaClaims
{
    /// <summary>Present (value "true") when the user still owes a password rotation.</summary>
    public const string MustChangePassword = "maia:must_change_password";
}
