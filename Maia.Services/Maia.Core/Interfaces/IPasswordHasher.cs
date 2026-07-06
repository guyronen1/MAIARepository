namespace Maia.Core.Interfaces;

/// <summary>
/// Password hashing abstraction. Keeps Core framework-agnostic — the concrete
/// implementation (Infrastructure) wraps ASP.NET Core Identity's
/// <c>PasswordHasher&lt;T&gt;</c> (PBKDF2-HMAC-SHA256, versioned format).
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Produce a versioned hash string (embeds algorithm, salt, iterations).</summary>
    string Hash(string password);

    /// <summary>Verify a candidate password against a stored hash. Returns
    /// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> when the hash
    /// is valid but used older parameters (e.g. a raised iteration count), so the
    /// caller can transparently re-hash on a successful login.</summary>
    PasswordVerificationResult Verify(string hash, string password);
}

/// <summary>Framework-agnostic mirror of Identity's PasswordVerificationResult.</summary>
public enum PasswordVerificationResult
{
    Failed,
    Success,
    SuccessRehashNeeded,
}
