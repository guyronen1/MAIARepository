namespace Maia.Core.Security;

/// <summary>
/// Minimal password floor for create / reset / change. A forced-rotated temp still
/// shouldn't be trivially guessable — this is the deferred "no complexity check" item
/// from the auth arc. Length-only in v1; composition rules (character classes, denylist)
/// can be added here later without touching any caller.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    /// <summary>Null when acceptable, else a human-readable reason for a 400.</summary>
    public static string? Validate(string? password) =>
        string.IsNullOrWhiteSpace(password) || password.Length < MinLength
            ? $"Password must be at least {MinLength} characters."
            : null;
}
