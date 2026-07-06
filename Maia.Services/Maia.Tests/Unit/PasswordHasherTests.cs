using Maia.Core.Interfaces;
using Maia.Infrastructure.Security;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Phase 0 gate for the auth work: the PBKDF2 hasher round-trips and rejects
/// wrong passwords. Pins the contract the login path (Phase 1) will rely on —
/// hash is non-plaintext, salted (two hashes of the same password differ), and
/// verification is exact.
/// </summary>
public class PasswordHasherTests
{
    private readonly IPasswordHasher _hasher = new Pbkdf2PasswordHasher();

    [Fact]
    public void Hash_then_Verify_succeeds_for_the_same_password()
    {
        var hash = _hasher.Hash("ChangeMe!2026");

        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify(hash, "ChangeMe!2026"));
    }

    [Fact]
    public void Verify_fails_for_a_wrong_password()
    {
        var hash = _hasher.Hash("ChangeMe!2026");

        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify(hash, "wrong-password"));
    }

    [Fact]
    public void Hash_is_not_plaintext()
    {
        const string password = "ChangeMe!2026";

        var hash = _hasher.Hash(password);

        Assert.DoesNotContain(password, hash);
    }

    [Fact]
    public void Hash_is_salted_so_two_hashes_of_the_same_password_differ()
    {
        var a = _hasher.Hash("ChangeMe!2026");
        var b = _hasher.Hash("ChangeMe!2026");

        Assert.NotEqual(a, b);
        // …yet both still verify.
        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify(a, "ChangeMe!2026"));
        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify(b, "ChangeMe!2026"));
    }

    [Fact]
    public void Verify_succeeds_against_the_migration_seeded_admin_hash()
    {
        // The exact literal embedded in migration AddAuthTables for the bootstrap
        // admin. Pins that the seeded hash matches the documented default password
        // ("admin"), so first-login works.
        const string seededHash =
            "AQAAAAIAAYagAAAAEPnp2IHUgzfCWpWZjNEACdO0lM/CvWnIaW0l8KlxiWw58i93pgNCH9Hu1YAYpn+fpg==";

        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify(seededHash, "admin"));
    }
}
