# Project Overview

The objective of the MAIA AI Assistant System is to monitor and manage automated job processing pipelines. It identifies failures, classifies issues based on log and database analysis, and provides either manual recommendations or automatic resolutions based on configured rules and operator input. The system supports offline environments, ensures reliable execution of DTSX jobs, and minimizes operational downtime through intelligent monitoring and decision logic.

# Tech Stack

- Language: C#
- Framework: .NET 8 (ASP.NET Core)
- Database: SQL Server (Entity Framework Core)
- Frontend: Angular 20.2, TypeScript 5.9
- Testing: xUnit (backend), Karma/Jasmine (frontend)

# Architecture

### 1. MaiaAIEngine.services (Backend Services)

- **API Layer**: RESTful ASP.NET Core API with controllers for jobs, recommendations, config, classification, fixes, pipeline, and scanning.
- **Background Worker**: A single `MonitoringWorker` BackgroundService drives the full pipeline using a per-job DB lease for horizontal-scale coordination. (Earlier AIClassifierWorker / LogParserWorker / FixSuggestionWorker scaffolds were removed — their functionality lives in `IClassifyJobsUseCase`, `IGenerateSuggestionsUseCase`, `IExecuteFixesUseCase`, and the scan strategies.)
- **Data Processing**: EF Core with SQL Server. Scan strategies for FileSystem, Database, and ApiEndpoint sources. Watermark-based incremental scanning for files (byte offset) and DB rows.
- **Security & Monitoring**: GlobalExceptionHandler middleware (RFC 7807 ProblemDetails). AuditLog entity for immutable audit trail. OperatorAction records human decisions on recommendations.
- **Deployment**: Containerized with Docker/Kubernetes for cloud deployment (Azure). Multi-instance safe via `MonitoredJobLeases` table.

### 2. MaiaAIEngineClient (Frontend/Client)

- **User Interface**: Angular 20 standalone components with lazy-loaded routes. Shell layout with sidebar navigation.
- **Integration Layer**: HTTP services in `core/services/` call the backend API via `environment.apiUrl`.
- **Cross-Platform**: Web browser; designed for operator use.

## Architecture Patterns

- **Clean Architecture**: Core → Application → Infrastructure → API. Controllers depend only on Core interfaces.
- **Lease-Coordinated Worker**: `MonitoringWorker` claims work from `MonitoredJobLeases` via atomic `UPDATE TOP(N) ... OUTPUT inserted.*` with `READPAST + UPDLOCK + ROWLOCK`. Multiple worker instances run side-by-side without racing on the same MonitoredJob.
- **Detect-then-Drain**: Scan strategies detect, classify, and suggest. They do NOT execute fixes. The worker tick drains pending fixes once after the parallel scan batch completes; operator approvals also drain synchronously via the approve endpoint.
- **Layered Architecture**: Presentation, business logic (use cases), and data layers are strictly separated.

## Technologies Stack

- Backend: C#, .NET 8, Entity Framework Core, ASP.NET Core
- Frontend: Angular 20.2, TypeScript 5.9, RxJS 7.8
- Infrastructure: SQL Server, Azure cloud services, Docker, CI/CD with GitHub Actions

## Workflow

1. `MonitoringWorker` does one synchronous startup drain of `ExecuteFixesUseCase` to handle approvals/auto-heals that arrived while the process was down.
2. On each tick the worker atomically claims up to N eligible jobs from `MonitoredJobLeases` (per-`ScanType` lease duration — FS=300s, DB=1800s, ApiEndpoint=60s). Idle ticks sleep 5s; nothing happens if nothing was claimed.
3. Claimed jobs run in parallel under `Parallel.ForEachAsync` (degree 4). Each job gets a fresh DI scope and a `CancellationTokenSource` armed with its lease duration as a hard timeout.
4. Per claimed job: `IScanStrategy.ScanAsync` runs scan → classify (`RuleBasedClassifier`) → suggest (`IFixCatalogue`-driven). No execute call inside the strategy.
5. After the parallel batch completes, the worker runs `ExecuteFixesUseCase.ExecuteAsync` once. It drains pending recommendations where `!IsExecuted && (OperatorApproved == true || AutoFixAvailable)` via `IFixEngine` (ApiCall / StoredProc / Script / Manual).
6. Operators approve/reject recommendations via `POST /api/recommendations/{id}/approve|reject`. Approve flips `OperatorApproved=true`, writes `OperatorAction` + `AuditLog`, and synchronously calls `ExecuteFixesUseCase.ExecuteAsync` so the fix runs on the same request.

---

# Database Tables

- `[dbo].[MonitoredJobs]`
- `[dbo].[MonitoredJobRules]`
- `[dbo].[MonitoredJobLeases]` — 1:1 with MonitoredJobs; runtime claim/release state for the lease-coordinated worker
- `[dbo].[ScanTypes]` — now carries `LeaseDurationSeconds` (FS=300, DB=1800, ApiEndpoint=60)
- `[dbo].[ScanCheckRules]`
- `[dbo].[ScanDbWatermarks]`
- `[dbo].[ScanFileWatermarks]`
- `[dbo].[JobFailures]`
- `[dbo].[AIRecommendations]`
- `[dbo].[ClassificationRules]`
- `[dbo].[ErrorTypes]`
- `[dbo].[JobTypes]`
- `[dbo].[AuditLog]`
- `[dbo].[FixPolicyRules]`
- `[dbo].[FixExecutionLog]`
- `[dbo].[OperatorActions]`

---

# Current Status

All backend engines, APIs, and config UI are built:
- Log scanner (FileSystem scan strategy) — supports `ErrorKeyword` check rules; strips `*` wildcards from TargetField before matching
- DB scan (Database scan strategy) — `ColumnRange` and `ValueEquals` check rules with watermark-based incremental scanning
- API endpoint scan strategy
- Check rules engine (ScanCheckRules per MonitoredJob)
- Classification rules (regex-based RuleBasedClassifier)
- Recommendation generation (GenerateSuggestionsUseCase)
- Fix execution (FixEngine with ApiCall, StoredProc, Script, Manual executors)
- All API controllers (Data, Config, Classification, Fix, Pipeline, Process, LogParser, JobScan, Recommendations)
- ConfigController: full CRUD for MonitoredJobs, ScanCheckRules, ClassificationRules (global + per-job), FixPolicyRules
- Angular 20 UI with: Dashboard, Failures list+detail, Recommendations, OperatorActions, ScanJobs, Config screens
- MonitoredJobs config screen: Add/edit/delete jobs, 3-tab panel per job (Scan Rules / Classification Rules / Fix Options), scan-type-aware forms
- Classification Rules config screen: dedicated page with filter bar, CRUD table, add/edit drawer
- **Lease-coordinated `MonitoringWorker`** — per-job DB lease (`MonitoredJobLeases`), atomic claim with `READPAST + UPDLOCK`, parallel scan execution (degree 4), per-job timeout from `ScanType.LeaseDurationSeconds`. Safe to run multiple instances.
- **Standalone operator-approval execution path** — `POST /api/recommendations/{id}/approve` and `/reject` (RecommendationsController). Approve synchronously drains `ExecuteFixesUseCase` so the fix runs on the same request. Both endpoints write `OperatorAction` + `AuditLog`.
- **Centralised fix drain** — scan strategies no longer call `ExecuteFixesUseCase`; the worker tick drains once after the parallel scan batch (plus once at startup). Operator approvals self-drain. The legacy `AIClassifierWorker` / `LogParserWorker` / `FixSuggestionWorker` scaffolds have been deleted.

# Active Goals / What We're Working On

(Pick the next one — `MonitoringWorker` lease + standalone approval flow + worker cleanup landed in the latest commit.)

- **EF migration for `MonitoredJobLeases` + `ScanTypes.LeaseDurationSeconds`** — entity + DbContext config + seed are in; need to run `dotnet ef migrations add AddMonitoredJobLeases` and append the backfill SQL (`INSERT INTO MonitoredJobLeases ... SELECT ... LEFT JOIN ... WHERE NULL`) before applying.
- **Auto-heal toggle on Recommendations screen**: operator reviews a recommendation, selects a fix action, and can mark it as auto-heal so it runs automatically next time that error occurs (`IsAutoHealEligible = true` on FixPolicyRule). Backend flag already drives `AutoFixAvailable` on new recommendations; UI wiring still to do.
- **Frontend approve/reject UI** — backend endpoints exist; Angular `RecommendationsComponent` needs buttons that hit `POST /api/recommendations/{id}/approve|reject` with an operator identity.

# Important Decisions Made

- Controllers inject only Core interfaces — no EF or Infrastructure types in the API layer (except `DbContextFactory` for read-only lookups).
- Angular uses standalone components with `inject()` functional DI (not constructor injection).
- `operator-actions` route reuses `RecommendationsComponent`; `/config/classification-rules` has its own dedicated `ClassificationRulesComponent`.
- Auto-heal flag (`IsAutoHealEligible` on FixPolicyRule) drives whether ExecuteFixesUseCase runs a recommendation automatically.
- `FileSystemScanStrategy`: if a job has `ErrorKeyword` scan rules, it scans log files line-by-line for the keyword; if no keyword rules, falls back to full pipeline over all log lines.
- Keyword TargetField values may contain glob wildcards (`*keyword*`) — always strip `*` before `Contains()` matching.
- `Observable<any>` cast used on conditional create/update calls in Angular to avoid TypeScript union type subscribe errors.
- **Per-job lease over leader election** — `MonitoredJobLeases` row per MonitoredJob, atomic `UPDATE TOP(N) ... OUTPUT inserted.* WITH (READPAST, UPDLOCK, ROWLOCK)` for claim. Survives restarts (`NextEligibleAt` is durable) and allows multiple worker instances.
- **Lease duration is per-`ScanType`, not per-job** — `ScanTypeDefinition.LeaseDurationSeconds`. Defaults: FileSystem 300s, Database 1800s, ApiEndpoint 60s. Doubles as the per-job execution timeout (`jobCts.CancelAfter`).
- **Detect-then-drain** — scan strategies (`FileSystemScanStrategy`, `DatabaseScanStrategy`, `ApiEndpointScanStrategy`) and `DirectoryPipelineUseCase` do NOT call `ExecuteFixesUseCase`. The worker tick is the single background drain; on-demand drains live in `POST /api/fix/execute-fixes`, `POST /api/recommendations/{id}/approve`, and `POST /api/jobscan/classify-pending`.
- **Approve runs global drain synchronously** — `POST /api/recommendations/{id}/approve` calls `ExecuteFixesUseCase.ExecuteAsync` on the request thread. Side effect: every other pending recommendation also drains in the same call. Accepted trade-off for low operator-action latency over surgical execution.
- **`ExecuteFixesUseCase` has no per-recommendation claim** — concurrent drains (worker tick + approve endpoint + manual `/execute-fixes`) can race and double-execute a fix. DB writes idempotent, but `fixEngine` side effects (API call / SP / script) are not. Accepted; revisit only if observed in practice.
- **`ScanResult.FixesExecuted` / `DirectoryPipelineResult.FixesExecuted` removed** — since scans no longer execute fixes, the "Fixed" stat is meaningless from a scan response. Angular "Fixed" tile dropped from `scan-jobs.component.ts`.
- **`POST /api/pipeline/run-directory` semantic change** — response no longer reflects executed fixes; drain happens on the next worker tick. Callers expecting synchronous fix counts must poll.

---

# File Structure

## Backend: MaiaAIEngine.services

```
Core/
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
│   ├── ScanCheckRule        per-job check: DB column range, file existence, etc.
│   ├── ScanDbWatermark      incremental DB scan watermark
│   ├── ScanFileWatermark    incremental file scan watermark
│   ├── ScanTypeDefinition   lookup for scan strategy types + LeaseDurationSeconds
│   └── FixPolicyRule        error type → action type + payload mapping
│
├── Enums/
│   ├── FixCategory          Retry | FileRepair | DbFix | Manual
│   ├── FixActionType        Manual | ApiCall | StoredProcedure | Script
│   ├── JobStatus            Failed | Classified | Recommended | Resolved | ManualRequired
│   ├── ScanType             FileSystem | Database | ApiEndpoint
│   ├── CheckType            check rule type enum
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
│   │                 IOperatorActionRepository
│   ├── IClassificationStrategy
│   ├── IFixEngine
│   ├── IFixHandler          FixCategory-based fallback handler
│   ├── IFixActionExecutor   FixActionType-based primary executor
│   ├── IFixCatalogue
│   ├── IScanStrategy        FileSystem / Database / ApiEndpoint strategies
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
    └── ScanResult

Application/
├── Classification/   ClassifyJobsUseCase
├── Remediation/      GenerateSuggestionsUseCase, ExecuteFixesUseCase
└── Pipeline/         DirectoryPipelineUseCase

Infrastructure/
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
│   └── ApiEndpointScanStrategy    → IScanStrategy (ApiEndpoint)
├── Fix/
│   ├── Handlers (FixCategory fallback)
│   │   ├── RetryFixHandler, FileRepairFixHandler, DbFixHandler, ManualFixHandler
│   └── Executors (FixActionType primary)
│       ├── ApiCallExecutor, StoredProcedureExecutor, ScriptExecutor, ManualActionExecutor
├── Workers/           MonitoringWorker (BackgroundService — claim → parallel scan → drain)
│                       Startup drain + post-tick drain via DrainPendingFixesAsync.
│                       Lease ID format: "host=<machine>;pid=<pid>;runId=<guid>"
├── Parsing/           SimpleLogParser, FileLogReader
└── Extensions/        ServiceCollectionExtensions  AddMaiaAI(connectionString)

AIEngineAPI/
├── Controllers/
│   ├── DataController             GET failures, recommendations, monitored-jobs (read-only)
│   ├── ConfigController           CRUD for monitored jobs, rules, fix policies
│   ├── ClassificationController   POST /classify
│   ├── FixController              POST /execute-fixes  (manual global drain)
│   ├── PipelineController         POST /run-pipeline
│   ├── ProcessController          POST /process
│   ├── LogParserController        POST /parse
│   ├── JobScanController          on-demand scan triggers + classify-pending
│   └── RecommendationsController  POST /api/recommendations/{id}/approve|reject
│                                  (approve drains synchronously)
├── Contracts/ (DTOs)
│   ├── JobFailureDto, RecommendationDto
│   ├── MonitoredJobDto / ScanCheckRuleDto / RuleOverrideDto
│   └── PipelineRequest, LogParseRequest
├── Middleware/        GlobalExceptionHandler (RFC 7807 ProblemDetails)
└── Extensions/        ServiceRegistration AddApplicationServices()
```

## Frontend: MaiaAIEngineClient

```
src/app/
├── core/
│   ├── models/
│   │   ├── monitored-job.model.ts  (MonitoredJob, ScanCheckRule, RuleOverride)
│   │   ├── failure.model.ts
│   │   ├── recommendation.model.ts
│   │   └── scan-result.model.ts
│   └── services/
│       ├── failures.service.ts
│       ├── recommendations.service.ts
│       ├── monitored-jobs.service.ts   (GET only currently — CRUD needed)
│       ├── scan.service.ts
│       └── config.service.ts
├── layout/
│   ├── shell/         ShellComponent — root layout
│   ├── top-bar/       TopBarComponent
│   └── side-menu/     SideMenuComponent
├── features/
│   ├── dashboard/                    DashboardComponent
│   ├── failures/
│   │   ├── failures-list.component   paginated list
│   │   └── failure-detail.component  detail + recommendations
│   ├── recommendations/              RecommendationsComponent (also handles operator-actions route)
│   ├── scan-jobs/                    ScanJobsComponent
│   └── config/
│       ├── monitored-jobs/           MonitoredJobsComponent — job CRUD + 3-tab panel (Scan Rules / Classification Rules / Fix Options)
│       └── classification-rules/     ClassificationRulesComponent — global rules with filter bar + CRUD drawer
├── app.routes.ts      lazy-loaded routes under ShellComponent
├── app.config.ts      providers: Router, HttpClient
└── app.ts             root standalone component
```

---

# End-to-End Process Flow

## A — Lease-coordinated worker (MonitoringWorker tick)

```
MonitoringWorker (BackgroundService — leased work, parallel scans, centralised drain)
│
├── STARTUP drain
│   └── IExecuteFixesUseCase.ExecuteAsync()      ← clears approvals/auto-heals queued
│                                                   while this process was down
│
└── loop until cancellation:
    │
    ├── IMonitoredJobLeaseRepository.ClaimAsync(leasedBy, batchSize=4)
    │     atomic UPDATE TOP(N) ... OUTPUT inserted.*
    │     FROM MonitoredJobLeases L WITH (READPAST, UPDLOCK, ROWLOCK)
    │     JOIN MonitoredJobs J, JOIN ScanTypes S
    │     WHERE J.IsActive AND NextEligibleAt <= now
    │       AND (LeasedUntil IS NULL OR LeasedUntil < now)
    │     SET LeasedBy=@me, LeasedUntil=now + S.LeaseDurationSeconds
    │
    ├── if claimed.Count == 0: Task.Delay(5s) and continue
    │
    ├── Parallel.ForEachAsync(claimed, degree=4):
    │   └── RunOneJobAsync(lease)
    │         ├── using scope = scopeFactory.CreateScope()              ← per-job DI
    │         ├── using jobCts; CancelAfter(lease.LeaseDurationSeconds) ← per-job timeout
    │         ├── strategy = IEnumerable<IScanStrategy> by job.ScanType
    │         │
    │         ├─[1] strategy.ScanAsync(job, jobCts.Token)
    │         │     • save JobFailure records
    │         │     • IClassifyJobsUseCase.ExecuteAsync(failures) → RuleBasedClassifier
    │         │       → UpdateClassificationAsync()
    │         │     • IGenerateSuggestionsUseCase.ExecuteAsync(classifications)
    │         │       → save AiRecommendation (AutoFixAvailable = IsAutoHealEligible)
    │         │     • NO execute call inside the strategy (centralised in worker)
    │         │
    │         └── finally:
    │             leaseRepo.ReleaseAsync(jobId, leasedBy, outcome,
    │                                    NextEligibleAt = now + PollingIntervalSeconds)
    │             outcome ∈ {Success, Failed, Timeout, Stolen}
    │             guarded by LeasedBy=@me — if stolen, returns false; row left untouched
    │
    └── POST-TICK drain  (gated on claimed.Count > 0)
        └── IExecuteFixesUseCase.ExecuteAsync()
              AiRecommendations WHERE IsExecuted=false AND (AutoFixAvailable OR OperatorApproved=true)
              → IFixEngine.ExecuteAsync(recommendation)
                  PRIMARY: FixPolicyRule → ApiCallExecutor | StoredProcedureExecutor | ScriptExecutor | ManualActionExecutor
                  FALLBACK: IFixHandler by FixCategory (Retry, FileRepair, DbFix, Manual)
              → save FixExecutionLog, AuditLog
              → UpdateStatusAsync → Resolved | ManualRequired
```

Lease semantics:
- One row per MonitoredJob in `MonitoredJobLeases`, seeded with the job (1:1, ON DELETE CASCADE).
- `READPAST` lets concurrent workers walk past each other's locked rows — no blocking, no deadlock.
- `LeasedUntil < now` is the steal condition: a crashed worker's lease expires and another worker picks it up.
- Release uses `LeasedBy = @me` guard so a stolen-then-finished worker doesn't overwrite the new owner's state.
- `IMonitoredJobLeaseRepository.HeartbeatAsync` exists but is not wired into the worker — only needed if a scan legitimately exceeds its lease duration.

## B — On-demand API calls

```
GET  /api/data/jobs?page=1&pageSize=50                         → JobFailureDto[]
GET  /api/data/failures/{id}/status                            → failure detail + recommendations
GET  /api/data/recommendations?page=1&pageSize=50              → RecommendationDto[]
GET  /api/data/monitored-jobs                                  → MonitoredJobDto[]
POST /api/classification/classify                              → IClassifyJobsUseCase
POST /api/fix/execute-fixes                                    → IExecuteFixesUseCase
POST /api/pipeline/run-pipeline                                → IDirectoryPipelineUseCase
POST /api/process/process                                      → full pipeline
POST /api/logparser/parse                                      → ILogParser

GET/POST/PUT/DELETE /api/config/monitored-jobs[/{id}]          → MonitoredJob CRUD
POST/PUT/DELETE     /api/config/scan-rules[/{id}]              → ScanCheckRule CRUD
POST/DELETE         /api/config/monitored-jobs/{id}/classification-rules[/{ruleId}]
GET/POST/PUT/DELETE /api/config/classification-rules[/{id}]    → global ClassificationRule CRUD
GET/POST/PUT/DELETE /api/config/fix-policy-rules[/{id}]        → FixPolicyRule CRUD (?jobTypeId= filter)
GET                 /api/config/job-types                      → lookup
GET                 /api/config/error-types                    → lookup

GET/POST /api/jobscan/{monitoredJobId}                         → trigger scan for one job
GET/POST /api/jobscan/by-name/{name}                           → trigger scan by job name
POST     /api/jobscan/scan-all                                 → scan all active jobs
POST     /api/jobscan/classify-pending                         → re-classify + drain pending fixes

POST     /api/recommendations/{id}/approve                     → OperatorApproved=true + write
                                                                 OperatorAction + AuditLog +
                                                                 SYNCHRONOUS ExecuteFixesUseCase drain
                                                                 body: { "operatorId": "<name>" }
POST     /api/recommendations/{id}/reject                      → OperatorApproved=false + write
                                                                 OperatorAction + AuditLog
                                                                 (no execute)
```

### Drain triggers (in priority/frequency order)

1. **MonitoringWorker startup** — once before the polling loop, restart recovery.
2. **MonitoringWorker post-tick** — once after each `Parallel.ForEachAsync` batch (gated on `claimed.Count > 0`).
3. **`POST /api/recommendations/{id}/approve`** — synchronous global drain on the request thread.
4. **`POST /api/fix/execute-fixes`** — manual operator trigger.
5. **`POST /api/jobscan/classify-pending`** — re-classify-then-drain.

`ExecuteFixesUseCase` has no per-recommendation claim; concurrent drains can race and double-execute. `fixEngine` side effects (API call / SP / script) are not idempotent.

## C — Database Schema

```
JobTypes ────────────────────────────────────────────────────┐
ErrorTypes ──────────────────────────────────────────────┐   │
                                                          │   │
ClassificationRules ── JobTypeId (FK) ───────────────────┼───┘
                    └─ ErrorTypeId (FK) ─────────────────┘
                              │
MonitoredJobs                 │
    ├── MonitoredJobRules ────┘  (RuleId FK → ClassificationRule)
    └── ScanCheckRules

JobFailures ─── JobTypeId (FK)
            └── ErrorTypeId (FK)
                    │
AiRecommendations ──┘  (FailureId FK)
    │
    ├── FixExecutionLogs  (RecommendationId FK)
    └── OperatorActions   (RecommendationId FK)

FixPolicyRules ─── JobTypeId (FK)
               └── ErrorTypeId (FK)
                   ActionType:    Manual | ApiCall | StoredProcedure | Script
                   ActionPayload: url / sp-name / script-command
                   IsAutoHealEligible: bool

AuditLogs  (standalone — EntityName + EntityId string refs)
ScanDbWatermarks, ScanFileWatermarks  (incremental scan state)
```

## D — Startup / DI Wiring

```
Program.cs
│
├── AddHttpClient("FixEngine")
├── AddMaiaAI(connectionString)          Infrastructure/Extensions
│   ├── AddDbContextFactory<AiDbContext>
│   ├── Scoped: 11 repository interfaces → Sql* implementations
│   │           (incl. IMonitoredJobLeaseRepository, IOperatorActionRepository)
│   ├── Scoped: IClassificationStrategy → RuleBasedClassifier
│   ├── Scoped: IFixCatalogue           → DbFixCatalogue
│   ├── Scoped: IFixEngine              → DefaultFixEngine
│   ├── Scoped: IFixHandler × 4 + IFixActionExecutor × 4
│   ├── Scoped: IScanStrategy × 3       (FileSystem, Database, ApiEndpoint — no execute injection)
│   ├── Scoped: ILogParser, ILogReader
│   └── Hosted: MonitoringWorker        (single BackgroundService; safe to run multiple instances)
│
├── AddApplicationServices()             AIEngineAPI/Extensions
│   ├── IClassifyJobsUseCase, IGenerateSuggestionsUseCase
│   ├── IExecuteFixesUseCase, IDirectoryPipelineUseCase
│
└── AddGlobalExceptionHandling()         ProblemDetails middleware
```
