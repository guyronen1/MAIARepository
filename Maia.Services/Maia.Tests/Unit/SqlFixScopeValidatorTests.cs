using Maia.Core.Interfaces;
using Maia.Infrastructure.Fix;
using Xunit;

namespace Maia.Tests.Unit;

/// <summary>
/// Layer-1 write-guard for SqlScript fix payloads. Confirms a fix must be a
/// scoped UPDATE/DELETE ({sourceId} or {referenceId} in WHERE) or EXEC (as a param);
/// bulk / unscoped writes are rejected at save. Documents the known limit: it
/// catches accidental missing-WHERE, not deliberate tautology bypasses.
///
/// The critical security property: "has a WHERE" is NOT the same as "scoped by a
/// captured identity." A WHERE that references neither {sourceId} nor {referenceId}
/// is rejected even though a WHERE clause is present — this is the sneaky case
/// asserted explicitly by WhereWithoutEitherScopeToken_Rejected.
/// </summary>
public class SqlFixScopeValidatorTests
{
    private readonly ISqlFixScopeValidator _v = new SqlFixScopeValidator();

    private static bool Ok(string? r) => r is null;

    // ── Rejected: unscoped writes ────────────────────────────────────────────

    [Fact] // the exact payload from the live incident
    public void NoWhereUpdate_Rejected()
        => Assert.False(Ok(_v.Validate("update dbo.files set FileStatusCode=1")));

    [Fact]
    public void NoWhereDelete_Rejected()
        => Assert.False(Ok(_v.Validate("delete from dbo.Files")));

    [Fact] // WHERE present but not scoped to the failing row
    public void WhereWithoutSourceId_Rejected()
        => Assert.False(Ok(_v.Validate("update dbo.Files set FileStatusCode=1 where Active=1")));

    // THE SNEAKY CASE: a WHERE clause exists but references NEITHER {sourceId} NOR
    // {referenceId}. Adding {referenceId} support must NOT accidentally make
    // "any WHERE" acceptable — scoping by captured identity is the invariant.
    [Fact]
    public void WhereWithoutEitherScopeToken_Rejected()
        => Assert.False(Ok(_v.Validate("update dbo.Orders set Status='Done' where OrderDate > '2026-01-01'")));

    [Fact] // {failureId} is MAIA's PK, not a key into the operator's table — doesn't count
    public void FailureIdInWhere_Rejected()
        => Assert.False(Ok(_v.Validate("update dbo.Files set x=1 where id={failureId}")));

    [Fact]
    public void Select_Rejected()
        => Assert.False(Ok(_v.Validate("select * from dbo.Files")));

    [Fact]
    public void ExecWithoutSourceId_Rejected()
        => Assert.False(Ok(_v.Validate("EXEC sp_WipeAll")));

    [Fact]
    public void Unparseable_Rejected()
        => Assert.False(Ok(_v.Validate("this is not sql ((")));

    [Fact] // multi-statement: one scoped, one bulk → whole payload rejected
    public void MultiStatement_OneUnscoped_Rejected()
        => Assert.False(Ok(_v.Validate(
            "update dbo.Files set x=1 where id='{sourceId}'; delete from dbo.Events")));

    // ── Accepted: scoped writes ──────────────────────────────────────────────

    [Fact]
    public void ScopedUpdate_Ok()
        => Assert.True(Ok(_v.Validate("update dbo.Files set FileStatusCode=1 where id='{sourceId}'")));

    [Fact] // {sourceId} substitution is case-insensitive (matches the executor)
    public void ScopedUpdate_MixedCasePlaceholder_Ok()
        => Assert.True(Ok(_v.Validate("update dbo.Files set FileStatusCode=1 where id='{SourceId}'")));

    [Fact]
    public void ScopedDelete_Ok()
        => Assert.True(Ok(_v.Validate("delete from dbo.Files where id='{sourceId}'")));

    [Fact]
    public void ExecWithSourceIdParam_Ok()
        => Assert.True(Ok(_v.Validate("EXEC dbo.sp_FixOne @id='{sourceId}'")));

    [Fact] // "ConnectionName|SQL" prefix is stripped before parsing
    public void ConnectionPrefix_Stripped_Ok()
        => Assert.True(Ok(_v.Validate("B2BTest|update dbo.Files set x=1 where id='{sourceId}'")));

    [Fact]
    public void MultiStatement_AllScoped_Ok()
        => Assert.True(Ok(_v.Validate(
            "update dbo.Files set x=1 where id='{sourceId}'; delete from dbo.Events where FileId='{sourceId}'")));

    [Fact] // unquoted (numeric-key) placeholder still resolves inside the WHERE
    public void ScopedUpdate_UnquotedPlaceholder_Ok()
        => Assert.True(Ok(_v.Validate("update dbo.Files set x=1 where id={sourceId}")));

    // ── {referenceId}: multi-row child updates via FK — accepted ────────────────

    [Fact] // UPDATE scoped by {referenceId} (parent/FK key → bounded child rows)
    public void ReferenceId_InWhere_Ok()
        => Assert.True(Ok(_v.Validate(
            "update dbo.OrderLines set Status='Cancelled' where OrderId='{referenceId}'")));

    [Fact] // {referenceId} substitution is case-insensitive
    public void ReferenceId_MixedCase_Ok()
        => Assert.True(Ok(_v.Validate(
            "update dbo.OrderLines set Status='Cancelled' where OrderId='{ReferenceId}'")));

    [Fact] // EXEC scoped by {referenceId}
    public void ReferenceId_InExec_Ok()
        => Assert.True(Ok(_v.Validate("EXEC dbo.sp_CancelOrder @orderId='{referenceId}'")));

    [Fact] // {sourceId} and {referenceId} may both appear — union of scoping tokens
    public void BothScopeTokens_Ok()
        => Assert.True(Ok(_v.Validate(
            "update dbo.Lines set x=1 where LineId='{sourceId}' and OrderId='{referenceId}'")));
}
