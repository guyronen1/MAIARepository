using Maia.Core.Interfaces;
using Maia.Core.Analysis;
using Maia.Infrastructure.Analysis;
using Maia.Infrastructure.Classification;
using Maia.Infrastructure.DataAccess;
using Maia.Infrastructure.DataAccess.Repositories;
using Maia.Infrastructure.Fix;
using Maia.Infrastructure.Parsing;
using Maia.Infrastructure.Placeholders;
using Maia.Infrastructure.Scanning;
using Maia.Infrastructure.Security;
using Maia.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maia.Infrastructure.Extensions;

/// <summary>
/// Registers all Infrastructure services: database, repositories, strategies, parsers, workers.
/// Use case (Application layer) registrations live in Maia.API.Extensions.ServiceRegistration
/// so Infrastructure has no upward dependency on Application.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMaia(
        this IServiceCollection services,
        string connectionString)
    {
        // ── Database ────────────────────────────────────────────────────────
        services.AddDbContextFactory<MaiaDbContext>(opts =>
            opts.UseSqlServer(connectionString));

        // ── Repositories ────────────────────────────────────────────────────
        services.AddScoped<IJobRepository,                SqlJobRepository>();
        services.AddScoped<IRecommendationRepository,     SqlRecommendationRepository>();
        services.AddScoped<IFixLogRepository,             SqlFixLogRepository>();
        services.AddScoped<IAuditRepository,              SqlAuditRepository>();
        services.AddScoped<IClassificationRuleRepository, SqlClassificationRuleRepository>();
        services.AddScoped<IMonitoredJobRepository,       SqlMonitoredJobRepository>();
        services.AddScoped<IFixCatalogueRepository,       SqlFixCatalogueRepository>();
        services.AddScoped<IFixPolicyRepository,          SqlFixPolicyRepository>();
        services.AddScoped<IScanWatermarkRepository,      SqlScanWatermarkRepository>();
        services.AddScoped<IMonitoredJobLeaseRepository,  SqlMonitoredJobLeaseRepository>();
        services.AddScoped<IOperatorActionRepository,     SqlOperatorActionRepository>();
        services.AddScoped<IScanRunHistoryRepository,     SqlScanRunHistoryRepository>();
        services.AddScoped<IUserRepository,               SqlUserRepository>();
        services.AddScoped<ISessionRepository,            SqlSessionRepository>();

        // ── Auth: password hashing (PBKDF2 via Identity's PasswordHasher<T>) ─
        // Stateless + thread-safe → singleton.
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        // ── Classification strategy (swap for ML/LLM here) ──────────────────
        services.AddScoped<IClassificationStrategy, RuleBasedClassifier>();

        // ── Unconfigured-failure cluster analyzer (v2 seam: add embedding/LLM
        //    implementations here and the /unconfigured controller swaps them). ─
        services.AddScoped<IUnconfiguredClusterAnalyzer, NgramClusterAnalyzer>();

        // ── Fix catalogue: DB-driven with built-in fallback ─────────────────
        services.AddScoped<IFixCatalogue, DbFixCatalogue>();

        // ── Fix engine: strategy dispatcher ─────────────────────────────────
        services.AddScoped<IFixEngine, DefaultFixEngine>();

        // ── Fix handlers: FixCategory fallback (Open/Closed) ────────────────
        services.AddScoped<IFixHandler, RetryFixHandler>();
        services.AddScoped<IFixHandler, FileRepairFixHandler>();
        services.AddScoped<IFixHandler, DbFixHandler>();
        services.AddScoped<IFixHandler, ManualFixHandler>();

        // ── Placeholder substitution (used by every executor) ────────────────
        services.AddScoped<IPlaceholderResolver, PlaceholderResolver>();

        // ── Fix action executors: one per FixActionType ──────────────────────
        services.AddScoped<IFixActionExecutor, ApiCallExecutor>();
        services.AddScoped<IFixActionExecutor, StoredProcedureExecutor>();
        services.AddScoped<IFixActionExecutor, ScriptExecutor>();
        services.AddScoped<IFixActionExecutor, SqlScriptExecutor>();
        services.AddScoped<IFixActionExecutor, ManualActionExecutor>();
        services.AddScoped<IFixActionExecutor, CopyFileExecutor>();
        // Composite is orchestrated inline by DefaultFixEngine — no separate
        // executor class. Engine iterates policy.Steps, dispatches each step
        // to its single-action executor, and writes per-step FixExecutionLog.

        // ── Parsing & I/O ────────────────────────────────────────────────────
        services.AddScoped<ILogParser, SimpleLogParser>();
        services.AddScoped<ILogReader, FileLogReader>();

        // ── File-content extractors (one per FileFormat, resolved via
        //    IEnumerable<IFileContentExtractor>; FileContentScanStrategy dispatches
        //    by ExtractorType). Add CSV/JSON/Excel here in v2. ──────────────────
        services.AddScoped<IFileContentExtractor, XmlContentExtractor>();

        // SqlQuery (CheckType) execution seam — testability wrapper around
        // SqlConnection used only by DatabaseScanStrategy's SqlQuery branch.
        services.AddScoped<ISqlQueryRunner, SqlQueryRunner>();

        // Save-time guard for SqlScript fix payloads (layer-1 write block). Stateless.
        services.AddSingleton<ISqlFixScopeValidator, SqlFixScopeValidator>();

        // ── Scan strategies (one per ScanType, resolved via IEnumerable<IScanStrategy>) ─
        services.AddScoped<IScanStrategy, FileSystemScanStrategy>();
        services.AddScoped<IScanStrategy, DatabaseScanStrategy>();
        services.AddScoped<IScanStrategy, ApiEndpointScanStrategy>();
        services.AddScoped<IScanStrategy, FileContentScanStrategy>();
        services.AddHttpClient();

        // ── Worker control (pause/resume — singleton shared by worker + controller) ─
        services.AddSingleton<IWorkerControlService, WorkerControlService>();

        // ── Background workers ───────────────────────────────────────────────
        services.AddHostedService<MonitoringWorker>();
        services.AddHostedService<ScanHistoryRetentionWorker>();

        return services;
    }
}
