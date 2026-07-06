namespace Maia.Core.Enums;

/// <summary>
/// The three authorization roles as ordered floors — a higher role inherits every
/// lower role's capability. Int values are deliberately ascending so policies can
/// compare with &gt;=, and they align with <see cref="Entities.Role"/>.RoleId
/// (RoleId == (int)MaiaRole) so the seed and the enum never drift.
/// </summary>
public enum MaiaRole
{
    /// <summary>Operational reads only.</summary>
    User = 1,

    /// <summary>User + config-screen reads + action tier + manual triggers.</summary>
    Operator = 2,

    /// <summary>Operator + all config writes + auto-heal + user/role management.</summary>
    Administrator = 3,
}
