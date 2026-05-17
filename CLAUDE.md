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
- **Background Workers**: MonitoringWorker drives the main pipeline. AIClassifierWorker, LogParserWorker, and FixSuggestionWorker handle async processing.
- **Data Processing**: EF Core with SQL Server. Scan strategies for FileSystem, Database, and ApiEndpoint sources.
- **Security & Monitoring**: GlobalExceptionHandler middleware (RFC 7807 ProblemDetails). AuditLog entity for immutable audit trail.
- **Deployment**: Containerized with Docker/Kubernetes for cloud deployment (Azure).

### 2. MaiaAIEngineClient (Frontend/Client)

- **User Interface**: Angular 20 standalone components with lazy-loaded routes. Shell layout with sidebar navigation.
- **Integration Layer**: HTTP services in `core/services/` call the backend API via `environment.apiUrl`.
- **Cross-Platform**: Web browser; designed for operator use.

## Architecture Patterns

- **Clean Architecture**: Core → Application → Infrastructure → API. Controllers depend only on Core interfaces.
- **Event-Driven**: Background workers for async classification, log parsing, and fix suggestion generation.
- **Layered Architecture**: Presentation, business logic (use cases), and data layers are strictly separated.

## Technologies Stack

- Backend: C#, .NET 8, Entity Framework Core, ASP.NET Core
- Frontend: Angular 20.2, TypeScript 5.9, RxJS 7.8
- Infrastructure: SQL Server, Azure cloud services, Docker, CI/CD with GitHub Actions

## Workflow

1. MonitoringWorker ticks every 60s → scans logs/DB for new failures → saves JobFailures.
2. RuleBasedClassifier matches regex ClassificationRules → updates failure with JobType + ErrorType.
3. GenerateSuggestionsUseCase creates AiRecommendations (AutoFixAvailable = IsAutoHealEligible).
4. ExecuteFixesUseCase runs approved/auto-heal recommendations via FixEngine (ApiCall / StoredProc / Script / Manual).
5. Operator UI shows failures, recommendations, and allows approvals or setting auto-heal.

---

# Database Tables

- `[dbo].[MonitoredJobs]`
- `[dbo].[MonitoredJobRules]`
- `[dbo].[ScanTypes]`
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
- All API controllers (Data, Config, Classification, Fix, Pipeline, Process, LogParser, JobScan)
- ConfigController: full CRUD for MonitoredJobs, ScanCheckRules, ClassificationRules (global + per-job), FixPolicyRules
- Angular 20 UI with: Dashboard, Failures list+detail, Recommendations, OperatorActions, ScanJobs, Config screens
- MonitoredJobs config screen: Add/edit/delete jobs, 3-tab panel per job (Scan Rules / Classification Rules / Fix Options), scan-type-aware forms
- Classification Rules config screen: dedicated page with filter bar, CRUD table, add/edit drawer

# Active Goals / What We're Working On

- **Auto-heal toggle on Recommendations screen**: operator reviews a recommendation, selects a fix action, and can mark it as auto-heal so it runs automatically next time that error occurs (`IsAutoHealEligible = true` on FixPolicyRule)

# Important Decisions Made

- Controllers inject only Core interfaces — no EF or Infrastructure types in the API layer.
- Angular uses standalone components with `inject()` functional DI (not constructor injection).
- `operator-actions` route reuses `RecommendationsComponent`; `/config/classification-rules` has its own dedicated `ClassificationRulesComponent`.
- Auto-heal flag (`IsAutoHealEligible` on FixPolicyRule) drives whether ExecuteFixesUseCase runs a recommendation automatically.
- `FileSystemScanStrategy`: if a job has `ErrorKeyword` scan rules, it scans log files line-by-line for the keyword; if no keyword rules, falls back to full pipeline over all log lines.
- Keyword TargetField values may contain glob wildcards (`*keyword*`) — always strip `*` before `Contains()` matching.
- `Observable<any>` cast used on conditional create/update calls in Angular to avoid TypeScript union type subscribe errors.

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
│   ├── ScanCheckRule        per-job check: DB column range, file existence, etc.
│   ├── ScanDbWatermark      incremental DB scan watermark
│   ├── ScanFileWatermark    incremental file scan watermark
│   ├── ScanTypeDefinition   lookup for scan strategy types
│   └── FixPolicyRule        error type → action type + payload mapping
│
├── Enums/
│   ├── FixCategory          Retry | FileRepair | DbFix | Manual
│   ├── FixActionType        Manual | ApiCall | StoredProcedure | Script
│   ├── JobStatus            Failed | Classified | Recommended | Resolved | ManualRequired
│   ├── ScanType             FileSystem | Database | ApiEndpoint
│   ├── CheckType            check rule type enum
│   ├── Severity             severity levels for check rules
│   └── TriggerType          Auto | Manual
│
├── Interfaces/
│   ├── Repositories: IJobRepository, IRecommendationRepository,
│   │                 IFixLogRepository, IAuditRepository,
│   │                 IClassificationRuleRepository, IMonitoredJobRepository,
│   │                 IFixCatalogueRepository, IFixPolicyRepository,
│   │                 IScanWatermarkRepository
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
│   └── Repositories/              Sql* implementations for all 9 interfaces
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
├── Workers/           MonitoringWorker (BackgroundService — main pipeline driver)
├── Parsing/           SimpleLogParser, FileLogReader
└── Extensions/        ServiceCollectionExtensions  AddMaiaAI(connectionString)

AIEngineAPI/
├── Controllers/
│   ├── DataController             GET failures, recommendations, monitored-jobs
│   ├── ConfigController           CRUD for monitored jobs, rules, fix policies
│   ├── ClassificationController   POST /classify
│   ├── FixController              POST /execute-fixes
│   ├── PipelineController         POST /run-pipeline
│   ├── ProcessController          POST /process
│   ├── LogParserController        POST /parse
│   └── JobScanController          scan job management
├── Contracts/ (DTOs)
│   ├── JobFailureDto, RecommendationDto
│   ├── MonitoredJobDto / ScanCheckRuleDto / RuleOverrideDto
│   └── PipelineRequest, LogParseRequest
├── Middleware/        GlobalExceptionHandler (RFC 7807 ProblemDetails)
└── Extensions/        ServiceRegistration AddApplicationServices()

Worker Projects (separate executables):
├── AIClassifierWorker
├── LogParserWorker
└── FixSuggestionWorker
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

## A — Background auto-heal (MonitoringWorker tick)

```
MonitoringWorker (BackgroundService — ticks every 60s)
│
├── IMonitoredJobRepository.GetActiveAsync()
│
└── foreach MonitoredJob (skipped if polling interval not elapsed)
    │
    └── IDirectoryPipelineUseCase.ExecuteAsync(dir, pattern)
        │
        ├─[1] IScanStrategy.ScanAsync() — FileSystem/Database/ApiEndpoint
        │       → save JobFailure records to DB
        │
        ├─[2] IClassifyJobsUseCase.ExecuteAsync(failures)
        │       RuleBasedClassifier regex match → ClassificationResults
        │       → UpdateClassificationAsync()
        │
        ├─[3] IGenerateSuggestionsUseCase.ExecuteAsync(classifications)
        │       IFixCatalogue.GetEntryAsync(errorTypeCode)
        │       → save AiRecommendation (AutoFixAvailable = IsAutoHealEligible)
        │
        └─[4] IExecuteFixesUseCase.ExecuteAsync()
                AiRecommendations WHERE IsExecuted=false AND (AutoFixAvailable OR OperatorApproved)
                → IFixEngine.ExecuteAsync(recommendation)
                    PRIMARY: FixPolicyRule → ApiCallExecutor | StoredProcedureExecutor | ScriptExecutor | ManualActionExecutor
                    FALLBACK: IFixHandler by FixCategory (Retry, FileRepair, DbFix, Manual)
                → save FixExecutionLog, AuditLog
                → UpdateStatusAsync → Resolved | ManualRequired
```

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
POST     /api/jobscan/classify-pending                         → re-classify unclassified failures
```

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
│   ├── Scoped: 9 repository interfaces → Sql* implementations
│   ├── Scoped: IClassificationStrategy → RuleBasedClassifier
│   ├── Scoped: IFixCatalogue           → DbFixCatalogue
│   ├── Scoped: IFixEngine              → DefaultFixEngine
│   ├── Scoped: IFixHandler × 4 + IFixActionExecutor × 4
│   ├── Scoped: IScanStrategy × 3       (FileSystem, Database, ApiEndpoint)
│   ├── Scoped: ILogParser, ILogReader
│   └── Hosted: MonitoringWorker
│
├── AddApplicationServices()             AIEngineAPI/Extensions
│   ├── IClassifyJobsUseCase, IGenerateSuggestionsUseCase
│   ├── IExecuteFixesUseCase, IDirectoryPipelineUseCase
│
└── AddGlobalExceptionHandling()         ProblemDetails middleware
```
