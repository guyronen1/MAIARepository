using Maia.Application.Security;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Core.Interfaces;
using Maia.Core.Security;
using Moq;

namespace Maia.Tests.Unit;

/// <summary>
/// Pins who counts as unrestricted for per-user job visibility: Administrators and
/// "All jobs" users see everything; a restricted user sees only assigned jobs (never
/// orphan failures); anonymous / unknown users see nothing.
/// </summary>
public class JobAccessScopeTests
{
    private readonly Mock<ICurrentUserAccessor> _current = new();
    private readonly Mock<IUserRepository>      _users   = new();

    private JobAccessScope Scope() => new(_current.Object, _users.Object);

    private void SignedIn(int userId, MaiaRole role)
    {
        _current.SetupGet(c => c.IsAuthenticated).Returns(true);
        _current.SetupGet(c => c.UserId).Returns(userId);
        _current.SetupGet(c => c.Role).Returns(role);
    }

    private void UserRow(int userId, bool restricted, params int[] assigned)
    {
        _users.Setup(u => u.GetByIdAsync(userId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new User { UserId = userId, Username = "u", PasswordHash = "x", RestrictToAssignedJobs = restricted });
        _users.Setup(u => u.GetAssignedJobIdsAsync(userId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(assigned.ToHashSet());
    }

    [Fact]
    public async Task Anonymous_sees_nothing()
    {
        var scope = Scope();
        Assert.Equal(JobVisibility.None, await scope.GetAsync());
        Assert.False(await scope.CanSeeAsync(1));
        Assert.Empty((await scope.GetVisibleJobIdsAsync())!);
    }

    [Fact]
    public async Task Administrator_is_unrestricted_even_when_flagged_restricted()
    {
        SignedIn(1, MaiaRole.Administrator);
        UserRow(1, restricted: true, 5);

        var scope = Scope();
        Assert.True((await scope.GetAsync()).Unrestricted);
        Assert.Null(await scope.GetVisibleJobIdsAsync());
        Assert.True(await scope.CanSeeAsync(999));
        Assert.True(await scope.CanSeeAsync(null));   // orphans included
        _users.Verify(u => u.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task All_jobs_operator_is_unrestricted()
    {
        SignedIn(2, MaiaRole.Operator);
        UserRow(2, restricted: false);

        var scope = Scope();
        Assert.True((await scope.GetAsync()).Unrestricted);
        Assert.True(await scope.CanSeeAsync(123));
    }

    [Fact]
    public async Task Restricted_user_sees_only_assigned_jobs_and_no_orphans()
    {
        SignedIn(3, MaiaRole.User);
        UserRow(3, restricted: true, 10, 20);

        var scope = Scope();
        Assert.Equal(JobVisibility.AssignedTo(3), await scope.GetAsync());
        Assert.True(await scope.CanSeeAsync(10));
        Assert.True(await scope.CanSeeAsync(20));
        Assert.False(await scope.CanSeeAsync(30));
        Assert.False(await scope.CanSeeAsync(null));
    }

    [Fact]
    public async Task Resolves_once_per_request()
    {
        SignedIn(3, MaiaRole.Operator);
        UserRow(3, restricted: true, 10);

        var scope = Scope();
        await scope.CanSeeAsync(10);
        await scope.CanSeeAsync(11);
        await scope.GetAsync();

        _users.Verify(u => u.GetByIdAsync(3, It.IsAny<CancellationToken>()), Times.Once);
        _users.Verify(u => u.GetAssignedJobIdsAsync(3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unknown_user_row_sees_nothing()
    {
        SignedIn(4, MaiaRole.Operator);
        _users.Setup(u => u.GetByIdAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        var scope = Scope();
        Assert.Equal(JobVisibility.None, await scope.GetAsync());
        Assert.False(await scope.CanSeeAsync(1));
    }
}
