using System.Security.Claims;
using Maia.Core.Enums;
using Maia.Core.Interfaces;

namespace Maia.API.Auth;

/// <summary><see cref="ICurrentUserAccessor"/> over the request's authenticated
/// principal (populated by <see cref="MaiaSessionAuthenticationHandler"/>).</summary>
public sealed class HttpContextCurrentUserAccessor(IHttpContextAccessor accessor) : ICurrentUserAccessor
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public int? UserId =>
        int.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public string? UserName => IsAuthenticated ? User?.Identity?.Name : null;

    public MaiaRole? Role =>
        Enum.TryParse<MaiaRole>(User?.FindFirstValue(ClaimTypes.Role), out var r) ? r : null;

    public bool MustChangePassword => User?.HasClaim(MaiaClaims.MustChangePassword, "true") ?? false;
}
