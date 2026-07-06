namespace Maia.Core.Entities;

/// <summary>
/// MAIA-local authorization role. Fixed, seed-only set of three hierarchical
/// floors (User &lt; Operator &lt; Administrator) — see <see cref="Maia.Core.Enums.MaiaRole"/>.
/// RoleId is aligned with the enum's int value so RoleId == (int)MaiaRole.
/// </summary>
public class Role
{
    public int RoleId { get; set; }
    public required string Name { get; set; }

    public ICollection<User> Users { get; set; } = [];
}
