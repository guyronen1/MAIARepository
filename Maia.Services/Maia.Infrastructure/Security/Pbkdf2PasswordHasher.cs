using Maia.Core.Entities;
using Maia.Core.Interfaces;
using Identity = Microsoft.AspNetCore.Identity;

namespace Maia.Infrastructure.Security;

/// <summary>
/// <see cref="IPasswordHasher"/> backed by ASP.NET Core Identity's
/// <c>PasswordHasher&lt;T&gt;</c> — PBKDF2 (FIPS-validated) with a versioned "v3"
/// hash format that embeds the PRF, iteration count, and random salt. On .NET 8 the
/// defaults are HMAC-SHA512 @ 100,000 iterations (decode any hash: byte0=0x01 version,
/// then PRF=2=HMACSHA512, then the iteration count) — not a bare/unsalted SHA, so it
/// can't be reproduced with T-SQL HASHBYTES.
/// The iteration count can be raised later without a schema migration: an old
/// hash still verifies and reports <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>.
///
/// The generic type parameter on <c>PasswordHasher&lt;T&gt;</c> is only passed to
/// the (optional, unused) rehash hooks, so a single shared dummy instance is fine.
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private readonly Identity.PasswordHasher<User> _inner = new();

    // The user argument is never read by PasswordHasher<T> for hashing/verifying —
    // it's only forwarded to rehash callbacks we don't use. One shared instance.
    private static readonly User Placeholder = new() { Username = string.Empty, PasswordHash = string.Empty };

    public string Hash(string password) => _inner.HashPassword(Placeholder, password);

    public PasswordVerificationResult Verify(string hash, string password) =>
        _inner.VerifyHashedPassword(Placeholder, hash, password) switch
        {
            Identity.PasswordVerificationResult.Success            => PasswordVerificationResult.Success,
            Identity.PasswordVerificationResult.SuccessRehashNeeded => PasswordVerificationResult.SuccessRehashNeeded,
            _                                                       => PasswordVerificationResult.Failed,
        };
}
