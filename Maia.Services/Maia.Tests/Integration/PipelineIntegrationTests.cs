using Maia.Application.Classification;
using Maia.Application.Remediation;
using Maia.Core.Entities;
using Maia.Core.Enums;
using Maia.Infrastructure.Classification;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.DataAccess.Repositories;
using Maia.Infrastructure.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maia.Tests.Integration;

/// <summary>
/// End-to-end pipeline tests using EF Core InMemory.
///
/// Trade-off: InMemory does not enforce FK constraints or SQL Server DDL behaviour.
/// For production-grade parity use Testcontainers.MsSql (docker-based SQL Server).
/// That upgrade requires no code changes here — swap the DbContextOptions in InitializeAsync.
/// </summary>
public class PipelineIntegrationTests : IAsyncLifetime
{
    private MaiaDbContext _db = null!;
    private IDbContextFactory<MaiaDbContext> _factory = null!;
    private string _tempLogFile = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<MaiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db      = new MaiaDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        // Re-seed the classification RULE this pipeline depends on. The DTSX
        // "DTS_E_CANNOTACQUIRECONNECTION" → DbConnection rule was dropped in
        // AlignSeedModelRemoveDemoData (ErrorType lookups are still HasData-seeded).
        // Point the rule at the seeded "DbConnection" ErrorType — add it only if
        // absent — and let PKs auto-generate to avoid clashing with the seed.
        // "DbConnection" maps to Retry + auto-heal in the built-in FixCatalogue.
        var dbConn = await _db.ErrorTypes.FirstOrDefaultAsync(e => e.Code == "DbConnection");
        if (dbConn is null)
        {
            dbConn = new ErrorType { Code = "DbConnection", DisplayName = "DB Connection", Severity = Severity.High };
            _db.ErrorTypes.Add(dbConn);
            await _db.SaveChangesAsync();
        }
        _db.ClassificationRules.Add(new ClassificationRule
        {
            JobTypeId = 1, ErrorTypeId = dbConn.ErrorTypeId,
            Pattern = "DTS_E_CANNOTACQUIRECONNECTION", Confidence = 0.9m, Priority = 1,
        });
        await _db.SaveChangesAsync();

        _factory     = new TestDbContextFactory(options);
        _tempLogFile = Path.GetTempFileName();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (File.Exists(_tempLogFile)) File.Delete(_tempLogFile);
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullPipeline_FailedJob_ProducesRecommendation()
    {
        // Pattern matches seeded DTSX rule: DTS_E_CANNOTACQUIRECONNECTION → DbConnection → Retry
        const string errorLine = "DTS_E_CANNOTACQUIRECONNECTION: Cannot acquire connection";
        await File.WriteAllTextAsync(_tempLogFile,
            $"Starting DTSX job\n{errorLine}\nJob aborted");

        _db.JobFailures.Add(new JobFailure
        {
            JobId = 1, JobTypeId = 1,
            Status = JobStatus.Failed, SourceLogPath = _tempLogFile,
            // ErrorMessage is what the classifier sees — populated by scan strategies in prod.
            ErrorMessage = errorLine,
        });
        await _db.SaveChangesAsync();

        var (classifier, suggestionSvc) = BuildPipeline();

        var results = await classifier.ExecuteAsync();
        await suggestionSvc.ExecuteAsync(results);

        var recommendations = await _db.AIRecommendations.ToListAsync();
        Assert.Single(recommendations);

        var rec = recommendations[0];
        Assert.Equal(1, rec.FailureId);
        Assert.Equal(FixCategory.Retry, rec.FixCategory);
        Assert.True(rec.AutoFixAvailable);
        Assert.True(rec.ConfidenceScore > 0.5m);
    }

    [Fact]
    public async Task FullPipeline_CleanLog_ProducesNoRecommendation()
    {
        await File.WriteAllTextAsync(_tempLogFile,
            "Starting DTSX job\nAll rows processed\nCompleted successfully");

        _db.JobFailures.Add(new JobFailure
        {
            JobId = 2, JobTypeId = 1,
            Status = JobStatus.Failed, SourceLogPath = _tempLogFile,
        });
        await _db.SaveChangesAsync();

        var (classifier, suggestionSvc) = BuildPipeline();

        var results = await classifier.ExecuteAsync();
        await suggestionSvc.ExecuteAsync(results);

        Assert.Empty(await _db.AIRecommendations.ToListAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────

    private (ClassifyJobsUseCase, GenerateSuggestionsUseCase) BuildPipeline()
    {
        var jobRepo    = new SqlJobRepository(_factory);
        var ruleRepo   = new SqlClassificationRuleRepository(_factory);
        var monJobRepo = new SqlMonitoredJobRepository(_factory);
        var recRepo    = new SqlRecommendationRepository(_factory);
        var parser     = new SimpleLogParser();
        var logReader  = new FileLogReader(NullLogger<FileLogReader>.Instance);
        var strategy   = new RuleBasedClassifier(ruleRepo, monJobRepo, parser);
        var catalogue  = new FixCatalogue();

        var classifier = new ClassifyJobsUseCase(
            jobRepo, strategy,
            NullLogger<ClassifyJobsUseCase>.Instance);

        var suggestionSvc = new GenerateSuggestionsUseCase(
            recRepo, catalogue,
            NullLogger<GenerateSuggestionsUseCase>.Instance);

        return (classifier, suggestionSvc);
    }

    // ── Minimal factory shim ─────────────────────────────────────────────────

    private sealed class TestDbContextFactory(DbContextOptions<MaiaDbContext> options)
        : IDbContextFactory<MaiaDbContext>
    {
        public MaiaDbContext CreateDbContext() => new(options);
    }
}
