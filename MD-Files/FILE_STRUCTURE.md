> Part of MAIA CLAUDE.md, split out for size. Root index: ../CLAUDE.md

# File Structure

## Backend: Maia.Services

```
Maia.Core/
├── Entities/
│   ├── JobType              lookup: DTSX, SSIS, …
│   ├── ErrorType            lookup: Timeout, FileNotFound, …
│   ├── ClassificationRule   pattern → (JobType + ErrorType) mapping
│   ├── JobFailure           a detected failure instance
│   ├── AiRecommendation     generated fix suggestion for a failure
│   ├── OperatorAction       human approval/rejection record
│   ├── FixExecutionLog      result of an automated fix attempt
│   ├── AuditLog             immutable audit trail entry
│   ├── MonitoredJob         job to watch (LogPathTemplate + interval)
│   ├── MonitoredJobRule     M:N — MonitoredJob ↔ ClassificationRule
│   ├── MonitoredJobLease    1:1 with MonitoredJob; runtime claim/release state for the worker
│   ├── ScanCheckRule        per-job check: DB column range, file existence, FileContent extraction, etc.
│   │                          (FileContent: TargetField = filename pattern; + ExtractorType,
│   │                           ExtractorLocator, IdentifierLocator, ExtractorPredicateType/Value)
│   ├── ScanDbWatermark      incremental DB scan watermark
│   ├── ScanFileWatermark    incremental file scan watermark (byte offset, log tailing)
│   ├── ScanContentWatermark FileContent scan watermark (per-file mtime, whole-file dedup)
│   ├── ScanTypeDefinition   lookup for scan strategy types + LeaseDurationSeconds
│   ├── FixPolicyRule        error type → action type + payload mapping
│   │                          (MonitoredJobId NULL = JobType default; set = per-job override)
│   │                          (ActionType=Composite → ActionPayload null, steps in FixPolicyRuleSteps)
│   ├── FixPolicyRuleStep    ordered step within a Composite rule
│   │                          (ActionType + ActionPayload + optional Description per step)
│   └── ScanRunHistory       append-only per-tick scan log (timing, outcome, counts +
│                              IdentifierExtractionFailures, OversizeFileSkips for FileContent)
│
├── Enums/
│   ├── FixCategory          Retry | FileRepair | DbFix | Manual
│   ├── FixActionType        Manual | ApiCall | StoredProcedure | Script | SqlScript | CopyFile | Composite
│   ├── JobStatus            Failed | Resolved | ManualRequired   (no "Classified" state — see decisions below)
│   ├── ScanType             FileSystem | Database | ApiEndpoint | FileContent
│   ├── CheckType            ColumnRange | ErrorKeyword | StatusCode | ResponseContains | ValueEquals | FileContent
│   ├── FileFormat           Xml   (FileContent extractor format; v2: Csv/Json/Excel)
│   ├── ScanPredicateType    Equals | NotEquals | Contains | NotContains   (FileContent value test)
│   ├── Severity             severity levels for check rules
│   ├── TriggerType          Auto | Manual
│   └── JobRunOutcome        Success | Failed | Timeout | Stolen   (lease release outcome)
│
├── Interfaces/
│   ├── Repositories: IJobRepository, IRecommendationRepository,
│   │                 IFixLogRepository, IAuditRepository,
│   │                 IClassificationRuleRepository, IMonitoredJobRepository,
│   │                 IFixCatalogueRepository, IFixPolicyRepository,
│   │                 IScanWatermarkRepository, IMonitoredJobLeaseRepository,
│   │                 IOperatorActionRepository, IScanRunHistoryRepository
│   ├── IClassificationStrategy
│   ├── IFixEngine
│   ├── IFixHandler          FixCategory-based fallback handler
│   ├── IFixActionExecutor   FixActionType-based primary executor
│   ├── IFileContentExtractor  one per FileFormat (XML in v1); FileContent scan dispatches by ExtractorType
│   ├── IFixCatalogue
│   ├── IPlaceholderResolver  centralised payload {token} substitution
│   ├── IScanStrategy        FileSystem / Database / ApiEndpoint / FileContent strategies
│   ├── IScanHistoryRetentionService  shared by retention worker + admin endpoint
│   ├── ILogParser / ILogReader
│   └── UseCases/
│       ├── IClassifyJobsUseCase
│       ├── IGenerateSuggestionsUseCase
│       ├── IExecuteFixesUseCase
│       └── IDirectoryPipelineUseCase
│
└── Results/
    ├── PagedResult<T>
    ├── ClassificationResult
    ├── DirectoryPipelineResult
    ├── FixCatalogueEntry
    ├── ScanResult
    ├── RecommendationListItem  entity + policy-snapshot projection from rec repo
    └── RetentionSweepResult    output of one retention sweep

Maia.Application/
├── Classification/   ClassifyJobsUseCase
├── Remediation/      GenerateSuggestionsUseCase, ExecuteFixesUseCase
├── Pipeline/         DirectoryPipelineUseCase
└── Maintenance/      ScanHistoryRetentionService — bounded DELETE loop, config-driven

Maia.Infrastructure/
├── DataAccess/
│   ├── AiDbContext                EF Core DbContext (SQL Server)
│   └── Repositories/              Sql* implementations for all 11 interfaces
│       └── SqlMonitoredJobLeaseRepository  atomic claim via UPDATE TOP(N) ... OUTPUT inserted.* WITH (READPAST, UPDLOCK, ROWLOCK)
├── Classification/
│   ├── RuleBasedClassifier        → IClassificationStrategy
│   ├── DefaultFixEngine           → IFixEngine
│   ├── FixCatalogue               built-in dictionary fallback
│   └── DbFixCatalogue             DB-first, dict fallback
├── Scanning/
│   ├── FileSystemScanStrategy     → IScanStrategy (FileSystem)
│   ├── DatabaseScanStrategy       → IScanStrategy (Database)
│   ├── ApiEndpointScanStrategy    → IScanStrategy (ApiEndpoint)
│   ├── FileContentScanStrategy    → IScanStrategy (FileContent) — file-outer/rule-inner walk
│   └── XmlContentExtractor        → IFileContentExtractor (Xml; XPath, namespace-blind, 5MB cap)
├── Fix/
│   ├── Handlers (FixCategory fallback)
│   │   ├── RetryFixHandler, FileRepairFixHandler, DbFixHandler, ManualFixHandler
│   └── Executors (FixActionType primary)
│       ├── ApiCallExecutor, StoredProcedureExecutor, ScriptExecutor,
│       │   SqlScriptExecutor, ManualActionExecutor, CopyFileExecutor
│       └── (Composite is orchestrated inline by DefaultFixEngine,
│            not a separate IFixActionExecutor)
├── Placeholders/
│   └── PlaceholderResolver           → IPlaceholderResolver
│                                       Substitutes {failureId}, {sourceId},
│                                       {sourceLogPath}, {sourceFilePath},
│                                       {jobFolder}, {inputFolder}.
├── Workers/
│   ├── MonitoringWorker             BackgroundService — claim → parallel scan → drain.
│   │                                Startup + post-tick drain via DrainPendingFixesAsync.
│   │                                Lease ID format: "host=<machine>;pid=<pid>;runId=<guid>"
│   │                                Writes one ScanRunHistory row per scan completion (finally block).
│   └── ScanHistoryRetentionWorker   BackgroundService — runs on startup + every CleanupIntervalHours.
│                                    Thin scheduler around IScanHistoryRetentionService.
├── Parsing/           SimpleLogParser, FileLogReader
└── Extensions/        ServiceCollectionExtensions  AddMaiaAI(connectionString)
                       Registers both BackgroundServices.

Scripts/
└── rebuild-datetime-defaults.sql   One-shot SQL: rebuild GETUTCDATE → GETDATE defaults
                                    (idempotent, safe on already-fixed environments).

Maia.API/
├── Controllers/
│   ├── DataController             GET failures, recommendations, monitored-jobs, scan-runs,
│   │                              operator-actions (read-only)
│   ├── ConfigController           CRUD for monitored jobs, rules, fix policies; GET fix-policy-rules/{id}
│   ├── ClassificationController   POST /classify
│   ├── FixController              POST /execute-fixes  (manual global drain)
│   ├── PipelineController         POST /run-pipeline
│   ├── ProcessController          POST /process
│   ├── LogParserController        POST /parse
│   ├── JobScanController          on-demand scan triggers + classify-pending
│   ├── RecommendationsController  POST /api/recommendations/{id}/approve|reject|retry
│   │                              (approve drains synchronously)
│   └── AdminController            POST /api/admin/scan-history/cleanup (manual retention sweep)
├── Contracts/ (DTOs)
│   ├── JobFailureDto, RecommendationDto, ScanRunDto
│   ├── MonitoredJobDto / ScanCheckRuleDto / RuleOverrideDto
│   └── PipelineRequest, LogParseRequest
├── Middleware/        GlobalExceptionHandler (RFC 7807 ProblemDetails)
└── Extensions/        ServiceRegistration AddApplicationServices()
                       (Registers use cases + IScanHistoryRetentionService.)

Maia.Tests/            xUnit + Moq; refs Maia.Core/Application/Infrastructure/API
├── GlobalUsings.cs
├── Unit/              isolated logic — scan strategies, FilenamePattern,
│                      ClassificationMatcher, PlaceholderResolver, PasswordHasher,
│                      DefaultFixEngine (composite), Copy/SqlScript executors,
│                      use cases, NgramClusterAnalyzer, XmlContentExtractor, …
├── Integration/       WebApplicationFactory (Microsoft.AspNetCore.Mvc.Testing) +
│                      EF Core Sqlite/InMemory — controller-level tests:
│                      AuthTestFactory, AuthorizationMatrixTests, PolicyGapStatusTests,
│                      FixPolicyLookupTests, FixFailedViewTests, ScanSourceCrudTests,
│                      PipelineIntegrationTests
└── Samples/           XML fixtures for FileContent tests
                       (invoice-ok/-error, malformed, WARNING_20260606)
```

## Frontend: Maia.Client

```
src/app/
├── core/
│   ├── models/
│   │   ├── monitored-job.model.ts  (MonitoredJob, ScanCheckRule, RuleOverride)
│   │   ├── failure.model.ts
│   │   ├── recommendation.model.ts
│   │   ├── scan-result.model.ts
│   │   └── worker-status.model.ts  (WorkerStatus, ActiveScan, RecentScan, JobLastScanRow)
│   └── services/
│       ├── failures.service.ts       (FailuresOverTimeResponse + getFailuresOverTime here too)
│       ├── recommendations.service.ts
│       ├── monitored-jobs.service.ts (GET only currently — CRUD needed)
│       ├── scan.service.ts
│       ├── config.service.ts
│       ├── worker-status.service.ts  4-endpoint polling coordinator;
│       │                             emits PolledData<T> on status$ / stats$ /
│       │                             recentFailures$ / monitoredJobs$.
│       ├── navigation-history.service.ts  tracks previous distinct path for
│       │                                  the drawer's smart back button.
│       │                                  Eagerly instantiated in ShellComponent.
│       ├── theme.service.ts             light/dark theme: mode signal
│       │                                (system|light|dark, localStorage-persisted),
│       │                                stamps data-theme on <html>. Toggled from the
│       │                                top-bar account menu; dark tokens in styles.scss.
│       ├── search.service.ts            command-palette: open-state signal +
│       │                                query(text) → role-filtered results
│       │                                (nav destinations + failure-by-id + jobs).
│       │                                query() is reusable as a future LLM
│       │                                "navigate" tool.
│       └── language.service.ts          UI language scaffold: current signal
│                                        (en enabled; he "soon"), localStorage,
│                                        stamps lang/dir on <html>. Picker in the top-bar
│                                        account menu. Translations = deferred item 11.
├── layout/
│   ├── shell/         ShellComponent — root layout; eagerly injects NavigationHistoryService
│   ├── top-bar/       TopBarComponent
│   └── side-menu/     SideMenuComponent
├── features/
│   ├── dashboard/
│   │   ├── dashboard.component.ts          DashboardComponent — 4 KPIs (with Resolved Today
│   │   │                                   breakdown), inline status strip, compact monitored-
│   │   │                                   jobs rows with click-to-expand, scan toast
│   │   └── errors-over-time-chart.component.ts  Stacked-area chart (chart.js@^4, deterministic
│   │                                            errorTypeId→color hash, 24h/7d/30d toggle)
│   ├── failures/
│   │   ├── failures-list.component   paginated list + drawer host; URL-driven state
│   │   │                             (?view, ?status, ?q, ?page, ?selected). Keyboard nav,
│   │   │                             auto-load adjacent page at row boundaries.
│   │   └── failure-detail.component  pure detail content (input-driven failureId);
│   │                                 rendered inside the drawer. Polls /failures/{id}/status
│   │                                 every 5s while mounted; "Already executed/approved/
│   │                                 rejected" graceful disable when state changes mid-review.
│   ├── recommendations/              RecommendationsComponent — pending-action queue;
│   │                                 in-place failure drawer via ?selected
│   ├── operator-actions/             OperatorActionsComponent — decision HISTORY
│   │                                 (Approve/Reject/Retry log via GET /api/data/operator-actions);
│   │                                 in-place failure drawer via ?selected
│   ├── scan-jobs/                    ScanJobsComponent
│   └── config/
│       ├── monitored-jobs/           MonitoredJobsComponent — job CRUD + 3-tab panel (Scan Rules / Classification Rules / Fix Options)
│       ├── classification-rules/     ClassificationRulesComponent — global rules with filter bar + CRUD drawer
│       └── error-types/              ErrorTypesComponent — ErrorType CRUD (Code, DisplayName, Severity, Active)
├── app.routes.ts      lazy-loaded routes under ShellComponent.
│                      /failures/:id is a function redirect → /failures?selected=:id
│                      (legacy bookmark support).
├── app.config.ts      providers: Router, HttpClient
└── app.ts             root standalone component
```

---

