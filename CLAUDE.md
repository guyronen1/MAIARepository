# Project Overview

The objective of the MAIA AI Assistant System is to monitor and manage automated job processing pipelines. It identifies failures, classifies issues based on log and database analysis, and provides either manual recommendations or automatic resolutions based on configured rules and operator input. The system supports offline environments, ensures reliable execution of DTSX jobs, and minimizes operational downtime through intelligent monitoring and decision logic.

# Documentation Drift Note

MAIA_Specification_v2.pdf in the project root is a snapshot from before the lease-coordinated worker rewrite, approval endpoints, scan-strategy execute removal, auto-heal toggle UI, ScanRunHistory, and JobTypeId-aware policy lookup. The PDF "Feature Status" page lists items as In Progress that are now built (auto-heal toggle, MonitoredJob config forms). The PDF also describes four background workers; only two run today (MonitoringWorker, ScanHistoryRetentionWorker).

CLAUDE.md is the live source of truth for current architecture, endpoints, and decisions. Update the PDF only when there's a formal release milestone.

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
- `[dbo].[ScanRunHistory]` — append-only history; one row per completed worker scan (timing + counts + outcome). Bounded by `ScanHistoryRetentionWorker` (default 30 days).

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
- **Auto-heal toggle on Recommendations screen** — Angular toggle binds to `policyIsAutoHealEligible` (live policy value) and mutates `FixPolicyRule.IsAutoHealEligible` via PUT `/api/config/fix-policy-rules/{id}`. Toggle disabled when no enabled policy matches the rec's ErrorType. Separate read-only "Auto-run" badge shows the frozen `AutoFixAvailable` snapshot on the rec itself — two distinct concepts in the UI.
- **Per-tick scan history** — every worker scan writes one `ScanRunHistory` row (started, completed, duration, outcome, error, counts of failures/classifications/recommendations). Queryable via `GET /api/data/scan-runs` with filters + pagination. `ScanHistoryRetentionWorker` prunes rows older than `ScanHistory:RetentionDays` (default 30) every `CleanupIntervalHours` (default 6). On-demand sweep via `POST /api/admin/scan-history/cleanup`.
- **SQL script-based fixes hit the right database** — `SqlScriptExecutor` resolves the target connection from the failure's `MonitoredJob.ConnectionName` (or `ConnectionName|SQL` payload prefix), not from `AiDbContext`. Supports `{failureId}` (int) and `{sourceId}` (string — source row's natural key) placeholders. Verified end-to-end against `B2B_Test.dbo.Files`.
- **DB scan watermark** — advances within rule-filtered rows only (`QueryFilteredMaxAsync`), and stores ISO-format strings (`CONVERT(NVARCHAR(50), col, 121)`). Fixes the prior bug where a single future-dated healthy row poisoned the baseline.
- **FixPolicyRule lookup is (JobTypeId, ErrorTypeId)-keyed everywhere** — `IFixPolicyRepository.GetForAsync(jobTypeId, errorTypeId)`, `IFixCatalogue(Repository).GetEntryAsync(errorTypeCode, jobTypeId)`, and the policy-snapshot projection in `SqlRecommendationRepository.GetPagedAsync` all filter by both columns. The wildcard `JobTypeId IS NULL` arm was rejected — column is non-nullable in the schema, so a "generic" policy doesn't exist as a concept. To cover multiple JobTypes for the same ErrorType, add separate `FixPolicyRule` rows. `DefaultFixEngine` emits `Policy lookup miss: jobTypeId={} errorTypeId={} recommendationId={}` at Information level so operators can grep for unconfigured pairs.
- **ErrorType CRUD via API** — `POST/PUT/DELETE /api/config/error-types[/{id}]` in `ConfigController`. Delete is a soft-delete (`IsActive=false`) because four tables (`JobFailures`, `ClassificationRules`, `FixPolicyRules`, `AiRecommendations`) reference `ErrorTypeId` with `RESTRICT` cascade. Code must be unique; rename collisions return 409. UI screen at `/config/error-types` (`ErrorTypesComponent`) mirrors the Classification Rules screen layout.
- **Classification patterns support `*` wildcards** — `RuleBasedClassifier` treats `*` as "any text" while every other character (including regex metacharacters like `.`, `+`, `[`, `\`) is literal. Patterns without `*` keep the fast `string.Contains` path; patterns with `*` compile to a regex (`.*` per `*`, every other character `Regex.Escape`d) with a 50ms timeout. UI labels updated from "Regex Pattern" to "Match Pattern" with hint copy reflecting the wildcard semantics. Same matcher used regardless of scan type (FS/DB/API).
- **File-system keyword-mode scan now emits a failure per new error line** — `FileSystemScanStrategy` iterates every line in the post-watermark chunk and creates a separate `JobFailure` per match (cap 100 per keyword per scan; exact-text dedup within the same batch). The previous `IJobRepository.HasOpenFailureAsync` dedup that blocked all new failures while an earlier one was still `Status=Failed` has been removed — the watermark already prevents replays of old content, so the dedup was harmful for tail-following logs.
- **Classifier consumes `JobFailure.ErrorMessage`, not the log file** — `ClassifyJobsUseCase` no longer re-reads `SourceLogPath`. The scan strategy is authoritative for what error was detected (the captured line for FS, the synthetic row message for DB); the classifier just labels it. `SqlJobRepository.UpdateClassificationAsync` updates `ErrorTypeId` only — it no longer overwrites `ErrorMessage` with the classifier's `RawError`. `RawError` still flows into the recommendation's `Explanation` field for diagnostic visibility.
- **Phase 1 dashboard upgrade** — 4 KPI tiles (Active / Awaiting Action / Resolved Today / Manual Required) with `Auto: N · Manual: N` breakdown under Resolved Today. Errors Over Time stacked-area chart powered by chart.js@^4 directly (no ng2-charts wrapper). Deterministic per-`errorTypeId` color hash so the same error class gets the same color across page loads. `?range=24h|7d|30d` toggle, legend at bottom (prepares for Phase 2 side-by-side layout). Inline status strip folded into the page title row (no full-width banner); auto-dismiss 30s with hover-pause / mouseleave-restart. Compact single-line Monitored Jobs rows with click-to-expand detail. "Run All Scans" dropped from dashboard. `dir="auto"` on all user-data text (job/step names, error messages, recommendation actions, descriptions) for RTL content.
- **Single 4-endpoint dashboard coordinator (`WorkerStatusService`)** — one 5s timer fires four independent fetches per tick (`/worker-status`, `/dashboard-stats`, `/failures?page=1&pageSize=10`, `/monitored-jobs`). Each endpoint has its own `PolledData<T>` `BehaviorSubject` and its own in-flight gate; a slow request blocks only its own endpoint, never the others. On failure the slice keeps its prior `value`, flips `isStale=true`, and stores `lastError` — no subject ever errors out, so the timer survives all failure modes. `takeUntil(cancel$)` aborts in-flight requests on `stop()`. Refcounted start/stop unchanged; 5s cadence configurable via `environment.dashboardRefreshIntervalMs`. The dashboard's manual Refresh button is gone — vestigial once polling covers every panel.
- **Failures drawer (replaces standalone detail page)** — clicking a failure on `/failures`, `/dashboard` recent-failures, or `/recommendations` opens a 760px right-anchored drawer overlay (slide-in 220ms, click-outside + ✕ + Esc close). Drawer hosts `<app-failure-detail>` as a child via input-driven `failureId = input.required<number>()` — no remount when ↑/↓ navigates between adjacent failures. Detail polls `/api/data/failures/{id}/status` every 5s while open; refreshes are silent (no spinner, no scroll jump). If an Approve/Reject becomes inapplicable mid-review (background drain resolves the rec), buttons stay present-but-disabled with an "Already executed / approved / rejected" note rather than vanishing under the operator's cursor.
- **Drawer keyboard navigation** — Esc closes (drawer-only); ↑/↓ moves between rows of the current filtered page with `scrollIntoView({ block: 'nearest' })`; Enter on a focused row opens the drawer; Tab cycles drawer's actionable controls (browser default). At page boundary: ↓ on last row auto-loads next page + focuses its first row, ↑ on first row of page ≥ 2 auto-loads previous page + focuses its last row, with a 1.3s "Page N of M" toast. ↓ at the last row of the last page shows a 1.8s "End of list" toast and stops — never wraps.
- **URL as source of truth on /failures** — `view`, `status`, `q`, `page`, `selected` round-trip via `route.queryParamMap` ↔ `patchUrl()`. Default values are stripped from the URL (`page=1`, empty `q`/`status`/`view`, `selected=null`) so shareable links stay clean: `/failures?status=Failed&selected=123` not `/failures?q=&status=Failed&page=1&selected=123`. Search debounced 250ms; filter/page changes use `replaceUrl: true` to avoid stacking history entries. Filter drift (selected failure no longer matches the filter) shows a "no longer in filter" hint in the drawer header; drawer stays open until operator closes.
- **Legacy detail URL preserved** — `/failures/:id` is a function redirect to `/failures?selected=:id` (Angular 17+ redirect function form). External bookmarks / shared Slack links keep working. The dedicated `FailureDetailComponent` route is gone; the component itself lives on as the drawer's child content.
- **`NavigationHistoryService` + smart back button** — singleton service subscribes to `NavigationEnd` and tracks the previous *distinct* path (query-param-only changes don't shift the referrer, so ↑/↓ drawer navigation doesn't lose the back-target). Drawer header renders `← Back to {Label}` on the leftmost edge when the referrer is a known top-level destination (Dashboard / Recommendations / Operator Actions / Scan Jobs / Monitored Jobs / Classification Rules / Error Types / Failures); hides otherwise. Click → `Location.back()`. Eagerly instantiated in `ShellComponent` so history tracking starts at app boot.
- **`GET /api/data/analytics/failures-over-time`** — time-bucketed failure counts broken out by `ErrorTypeId`, for the dashboard's Errors Over Time chart. `?range=24h|7d|30d&bucketSize=hour|day`; 24h defaults to hour buckets, 7d/30d to day buckets. Unclassified failures (`ErrorTypeId IS NULL`) collapse into a synthetic `errorTypeId=0 / "(unclassified)"` series so the frontend can stack them alongside named series.
- **`/api/data/dashboard-stats` extended with today fields** — `resolvedToday`, `autoFixedToday`, `manuallyFixedToday`. "Today" = `DateTime.Today` (server-local midnight), consistent with the local-time convention. Used by the Resolved Today KPI and its breakdown line.
- **`/api/data/failures` view filters extended** — added `view=resolved` and `view=manual-required` to support the Resolved Today and Manual Required KPI drill-downs from the dashboard.

# Active Goals / What We're Working On

(Nothing in flight. Last completed: Failures drawer + URL-driven state + keyboard nav + smart back button.)

# Known follow-ups (not blocking)

- **Operator identity is hardcoded `'operator'`** in approve/reject calls. Replace with real authenticated user when auth lands.
- **No authn/authz on any controller.** All endpoints are currently open. Operators, admins, and auditors should have distinct permission sets. Needs role-based authorization before production. `AuditLog.Actor` (and `OperatorAction.OperatorId`) should be wired to the real identity.
- **AuditLog coverage gap on config changes.** `PUT /api/config/fix-policy-rules/{id}` (and likely the other `ConfigController` PUTs/POSTs/DELETEs) may not write to `AuditLog`. Config changes that affect auto-execution behavior should be auditable. Verify and close the gaps.
- **`DbFixHandler.HandleAsync` is a TODO stub returning `false`.** Surfaced during the JobTypeId fix — when a `DbFix`-category recommendation has no matching `FixPolicyRule`, execution falls through to this handler and silently fails. Either implement it or flip those failures to `ManualRequired` explicitly with a clearer message.
- **Frontend `monitored-jobs.service.ts` is GET-only** while the backend has full CRUD on `/api/config/monitored-jobs`. The config UI uses `config.service.ts`, so this isn't broken — just inconsistent. Consolidate or remove.
- **`ILogReader` / `FileLogReader` are no longer consumed.** `ClassifyJobsUseCase` was the only caller; it now classifies against `JobFailure.ErrorMessage`. The interface, implementation, and DI registration in `AddMaiaAI` can be removed once we're sure nothing external relies on them.
- **Recommendations screen drawer (Phase 2).** Same `?selected=` overlay pattern as the failures drawer; deferred so operators get a few days of feedback on the failures version first.
- **Sort UI on failures-list.** None today. URL hygiene reserves the slot but no UI is wired up.
- **Style budget warnings.** `features/dashboard/dashboard.component.ts` (~4.79kB vs 4kB budget) and `features/config/monitored-jobs/monitored-jobs.component.ts` (~4.34kB) exceed the Angular per-component SCSS budget. Pre-existing pattern in this codebase, not regressions from current work; raise the budget or extract shared styles.

# Important Decisions Made

- Controllers inject only Core interfaces — no EF or Infrastructure types in the API layer (except `DbContextFactory` for read-only lookups).
- Angular uses standalone components with `inject()` functional DI (not constructor injection).
- `operator-actions` route reuses `RecommendationsComponent`; `/config/classification-rules` has its own dedicated `ClassificationRulesComponent`.
- Auto-heal flag (`IsAutoHealEligible` on FixPolicyRule) drives whether ExecuteFixesUseCase runs a recommendation automatically.
- `FileSystemScanStrategy`: if a job has `ErrorKeyword` scan rules, it scans log files line-by-line for the keyword and creates one failure per matching line in the new-bytes chunk (post-watermark); if no keyword rules, falls back to full pipeline over all log lines.
- Keyword TargetField values may contain glob wildcards (`*keyword*`) — always strip `*` before `Contains()` matching.
- `Observable<any>` cast used on conditional create/update calls in Angular to avoid TypeScript union type subscribe errors.
- **Per-job lease over leader election** — `MonitoredJobLeases` row per MonitoredJob, atomic `UPDATE TOP(N) ... OUTPUT inserted.* WITH (READPAST, UPDLOCK, ROWLOCK)` for claim. Survives restarts (`NextEligibleAt` is durable) and allows multiple worker instances.
- **Lease duration is per-`ScanType`, not per-job** — `ScanTypeDefinition.LeaseDurationSeconds`. Defaults: FileSystem 300s, Database 1800s, ApiEndpoint 60s. Doubles as the per-job execution timeout (`jobCts.CancelAfter`).
- **Detect-then-drain** — scan strategies (`FileSystemScanStrategy`, `DatabaseScanStrategy`, `ApiEndpointScanStrategy`) and `DirectoryPipelineUseCase` do NOT call `ExecuteFixesUseCase`. The worker tick is the single background drain; on-demand drains live in `POST /api/fix/execute-fixes`, `POST /api/recommendations/{id}/approve`, and `POST /api/jobscan/classify-pending`.
- **Approve runs global drain synchronously** — `POST /api/recommendations/{id}/approve` calls `ExecuteFixesUseCase.ExecuteAsync` on the request thread. Side effect: every other pending recommendation also drains in the same call. Accepted trade-off for low operator-action latency over surgical execution.
- **`ExecuteFixesUseCase` has no per-recommendation claim** — concurrent drains (worker tick + approve endpoint + manual `/execute-fixes`) can race and double-execute a fix. DB writes idempotent, but `fixEngine` side effects (API call / SP / script) are not. Accepted; revisit only if observed in practice.
- **`ScanResult.FixesExecuted` / `DirectoryPipelineResult.FixesExecuted` removed** — since scans no longer execute fixes, the "Fixed" stat is meaningless from a scan response. Angular "Fixed" tile dropped from `scan-jobs.component.ts`.
- **`POST /api/pipeline/run-directory` semantic change** — response no longer reflects executed fixes; drain happens on the next worker tick. Callers expecting synchronous fix counts must poll.
- **"Already classified" is `ErrorTypeId IS NOT NULL`, not a status transition** — `JobStatus` only has Failed/Resolved/ManualRequired. `ClassifyJobsUseCase.ExecuteAsync()` filters by `Status=Failed AND ErrorTypeId IS NULL` (`IJobRepository.GetUnclassifiedAsync`) so re-running classify never re-classifies the same failure. Failures stay `Failed` until executor flips them to Resolved or ManualRequired.
- **`GenerateSuggestionsUseCase` is idempotent per FailureId** — calls `IRecommendationRepository.ExistsForFailureAsync` and skips failures that already have any recommendation. Prevents pile-up when classify/suggest is re-run on the same set.
- **`FileSystemScanStrategy` keyword mode uses watermarks AS the only dedup primitive** — each file is read from `IScanWatermarkRepository.GetFileOffsetAsync(monitoredJobId, file)` and the offset is advanced per scan. Whole-file rescan only happens after rotation/truncation (offset > stream.Length). There is intentionally NO `HasOpenFailureAsync` check on top — that would block new errors any time an older one was still pending. Every matching line in the new chunk becomes a `JobFailure` (capped at 100 per keyword per scan, dedup by exact text within the batch).
- **`SqlScriptExecutor` resolves the target connection from the failure** — opens a raw `SqlConnection` against (priority order): `ConnectionName|SQL` payload prefix → `failure.MonitoredJob.ConnectionName` → `DefaultConnection`. So DB-scan jobs that monitor an external DB also fix it (no need to encode 3-part names in the SQL). Supports `{failureId}` (int PK) and `{sourceId}` (string — the source row's natural key, e.g. `Files.id` GUID); replace is case-insensitive. Returns false when `rowsAffected == 0` so a no-op fix surfaces as ManualRequired.
- **Timestamps are local time** — `DateTime.Now` in C# and `DEFAULT GETDATE()` in SQL across all migrations and code. JSON serializes without a `Z` suffix → browser parses as local. Pre-2026-05-24 rows still hold UTC clock values; a one-time `DATEADD(HOUR, +offset, …)` script can shift historical data if needed.
- **Lease-claim SQL must use local time, not UTC** — `SqlMonitoredJobLeaseRepository.ClaimSql` declares `@now = SYSDATETIME()` (local). `ReleaseAsync` writes `NextEligibleAt = DateTime.Now.AddSeconds(...)` (also local). A previous `SYSUTCDATETIME()` mismatch wedged the worker for ~3 hours per restart on Israel local time.
- **SQL `DEFAULT` constraints rebuilt in place** via `MaiaAIEngine.services/Scripts/rebuild-datetime-defaults.sql`. Idempotent — selects only constraints whose definition still contains `utcdate` and rewrites them with stable names (`DF_<Table>_<Column>`) using `GETDATE()`. Run once per environment that had the old `GETUTCDATE()` defaults applied; no EF migration needed.
- **Recommendation→FixPolicyRule link is a runtime lookup, not a FK** — `AiRecommendation` carries no `FixPolicyRuleId`. `SqlRecommendationRepository.GetPagedAsync` joins the newest enabled policy by ErrorTypeId at query time and projects into `RecommendationListItem` (`FixPolicyRuleId`, `PolicyIsAutoHealEligible`). `RecommendedAt` / `AutoFixAvailable` on the rec are immutable snapshots from generation time; the policy fields on the DTO reflect live state. Toggling the policy never mutates existing recs (snapshot integrity).
- **Auto-heal toggle is policy-only, not per-recommendation** — the Angular toggle on the Recommendations screen mutates `FixPolicyRule.IsAutoHealEligible` via the existing `PUT /api/config/fix-policy-rules/{id}` (full-object PUT, two-step fetch-then-update). Affects FUTURE recommendations only. To run an existing rec now, operator uses `/approve` instead. Two clear actions, two clear semantics.
- **DB scan watermark advances within the rule's filter, not the whole table** — `DatabaseScanStrategy.QueryFilteredMaxAsync` applies the rule's `WHERE` clause to the `MAX(WatermarkColumn)` query. The prior `QueryCurrentMaxAsync` took MAX over every row, so a single future-dated healthy row could jump the watermark past any current-dated unhealthy row and silently skip future failures.
- **DB scan watermark stored in ISO format** — `CONVERT(NVARCHAR(50), col, 121)` produces `2026-05-25 17:02:03.4452905`-style values (full `datetime2` precision). Previously `CAST AS NVARCHAR(100)` gave SQL Server's default `May 25 2026 5:02PM`, which round-tripped fine for comparisons but was unreadable in ad-hoc queries.
- **`SqlScriptExecutor` returns `false` on zero rows affected** — a SQL that ran successfully but matched nothing flips the recommendation to `ManualRequired` so operator can investigate, rather than silently marking it "fixed".
- **`ScanRunHistory` schema decisions**:
  - Clustered PK on `ScanRunId` (identity), not on `(MonitoredJobId, StartedAt)`. Append-only inserts → no page splits; identity is a monotonic shadow of StartedAt → clean range-delete for retention.
  - Two nonclustered indexes: `IX_ScanRunHistory_Job_StartedAt (MonitoredJobId, StartedAt DESC) INCLUDE (counts + outcome + duration)` covers "last N runs of job X" without bookmark lookups. `IX_ScanRunHistory_Failures (StartedAt DESC) INCLUDE (job, outcome, error) WHERE Outcome <> 'Success'` is a filtered index for "recent failures across all jobs" — tiny because most rows are Success.
  - `Outcome` stored as `nvarchar(50)` via `HasConversion<string>()` (matches all other enum-as-string columns in this schema). Filter on the index is `[Outcome] <> 'Success'`, not `<> 0`.
- **Retention sweep logic lives in a shared service, not the worker** — `IScanHistoryRetentionService` is invoked by both `ScanHistoryRetentionWorker` (scheduled) and `AdminController.RunScanHistoryCleanup` (on-demand). Config (`Enabled`, `RetentionDays`, `CleanupBatchSize`, `InterBatchDelayMs`) is re-read every invocation so changes to `appsettings.json` take effect on the next sweep without restart.
- **Failure detail is a drawer, not a separate page.** Operators triage queues, not page-at-a-time. The drawer keeps the list visible, preserves filter/sort/pagination/scroll state, and enables ↑/↓ keyboard nav through the queue. Standalone full-page detail was rejected — `Location.back()` worked but lost list context every drill-down.
- **Drawer lives on /failures, not as a global shell-level overlay.** Considered making the drawer page-agnostic (open from dashboard → drawer overlays dashboard, close → stay on dashboard). Rejected because: (a) `↑/↓` keyboard nav only makes sense when there's a filtered list behind the drawer, and only `/failures` has that — on dashboard the "Recent Failures" panel is just the top 10; (b) operator's mental model is one place for failure investigation; (c) the underlying complaint was "I can't get back," which a smart back button solves without fragmenting the drawer's location across pages.
- **URL is the single source of truth for failures-list state.** `view` / `status` / `q` / `page` / `selected` all round-trip via query params. URL hygiene: default values (`page=1`, empty filters, `selected=null`) stripped by the `URL_DEFAULTS` map in `patchUrl()`. Search debounced 250ms; navigates use `replaceUrl: true` so filter typing doesn't stack history entries. The new `?selected=` for the drawer slots into the same model — refresh-safe and shareable.
- **Drawer detail polls live every 5s while open**, independent of the dashboard's `WorkerStatusService`. Re-fetches are silent (no `loading.set(true)` on the polled tick) so the DOM updates only the bound parts — scroll position, focus, and operator cursor stay put. If a background drain resolves a recommendation mid-review, Approve/Reject stay present-but-disabled with an "Already executed / approved / rejected" note rather than vanishing under the operator's cursor. The poll lives on `FailureDetailComponent`'s `effect` with `onCleanup`; it tears down automatically when `failureId` changes or the component is destroyed.
- **`FailureDetailComponent` is input-driven and re-used inside the drawer.** `failureId = input.required<number>()` plus an `effect` that re-fetches on every change. Same template, no remount on ↑/↓ navigation — the drawer transition stays smooth and the DOM doesn't flash. The `ActivatedRoute` dependency was removed; the host (drawer in `FailuresListComponent`) owns URL/state.
- **`/api/data/dashboard-snapshot` was implemented and reverted in the same session.** Tried collapsing the four polled endpoints into one combined endpoint with per-section partial-failure flags. Reverted because the four endpoints each have independent failure modes (network, DB, individual query timeouts) and the snapshot couples them artificially; four parallel requests with per-endpoint in-flight gates is the right model. The `BuildWorkerStatusAsync` / `BuildDashboardStatsAsync` helper extraction was rolled back too — endpoints are inlined again, no dead code.
- **`PolledData<T>` wrapper exposes `isStale` but no UI renders it yet.** Each `BehaviorSubject` in `WorkerStatusService` emits `{ value, isStale, lastUpdatedAt, lastError? }`. On fetch failure the slice keeps the prior `value`, flips `isStale=true`, and stores `lastError` — no subject ever errors out, so the timer survives all failure modes. Consumers unwrap `.value` via `computed(() => signal().value)`; the stale indicator is reserved for a polish round if operators report seeing stale data.
- **Chart library: chart.js@^4 directly, no ng2-charts wrapper.** Direct usage gives full control over component registration (only the ones we use — keeps the bundle lean), config, and event handling. Wrapper would add ~30kB and an abstraction layer for no real benefit at this scope.
- **Errors Over Time legend at bottom, not right.** Wider chart canvas + prepares for a Phase 2 split where Errors Over Time pairs with another chart at ~60% width. Right-side legend works at full width but cramps the canvas when narrower. Bottom legend works at any width — no chart config change needed when the layout shrinks.
- **Errors Over Time chart colors are deterministic per `errorTypeId`** — `colorFor(errorTypeId)` returns `PALETTE[Math.abs(errorTypeId) % PALETTE.length]` (with a gray for `errorTypeId=0` "unclassified"). Same error class gets the same color across page loads regardless of arrival order, so operators build muscle memory.
- **Dashboard stats "today" is server-local midnight, not UTC.** `DateTime.Today` (== `CAST(GETDATE() AS DATE)`). Matches the rest of the schema's local-time convention; an operator on Israel time sees their workday-local numbers, not a 7-hour-shifted view.
- **`NavigationHistoryService` tracks distinct *paths*, not URLs.** Query-param-only navigation (drawer ↑/↓, filter changes, pagination) doesn't shift the previous-route pointer. Without this guard, pressing ↓ once would hide the back button forever — the referrer would become `/failures?selected=124`, no longer a different page.
- **`NavigationHistoryService` is eagerly instantiated in `ShellComponent`.** Service has to be alive before the first `NavigationEnd` fires, otherwise the initial referrer is lost. `providedIn: 'root'` alone isn't enough — Angular instantiates lazily on first inject, and the first inject might happen after the first navigation. `ShellComponent` has a `private _navHistory = inject(NavigationHistoryService);` field purely for the side effect.
- **Smart-back label map is hand-maintained, not derived from `app.routes.ts`.** Only top-level menu destinations get labels (Dashboard, Failures, Recommendations, Operator Actions, Scan Jobs, the three Config screens). Other paths (e.g. the legacy `/failures/:id` redirect target) return null → back button hides rather than rendering a confusing label. Auto-derivation from routes was rejected — route paths aren't always human-friendly and the menu's display strings live in `side-menu.component.ts` anyway.

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
│   ├── FixPolicyRule        error type → action type + payload mapping
│   └── ScanRunHistory       append-only per-tick scan log (timing, outcome, counts)
│
├── Enums/
│   ├── FixCategory          Retry | FileRepair | DbFix | Manual
│   ├── FixActionType        Manual | ApiCall | StoredProcedure | Script
│   ├── JobStatus            Failed | Resolved | ManualRequired   (no "Classified" state — see decisions below)
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
│   │                 IOperatorActionRepository, IScanRunHistoryRepository
│   ├── IClassificationStrategy
│   ├── IFixEngine
│   ├── IFixHandler          FixCategory-based fallback handler
│   ├── IFixActionExecutor   FixActionType-based primary executor
│   ├── IFixCatalogue
│   ├── IScanStrategy        FileSystem / Database / ApiEndpoint strategies
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

Application/
├── Classification/   ClassifyJobsUseCase
├── Remediation/      GenerateSuggestionsUseCase, ExecuteFixesUseCase
├── Pipeline/         DirectoryPipelineUseCase
└── Maintenance/      ScanHistoryRetentionService — bounded DELETE loop, config-driven

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

AIEngineAPI/
├── Controllers/
│   ├── DataController             GET failures, recommendations, monitored-jobs, scan-runs (read-only)
│   ├── ConfigController           CRUD for monitored jobs, rules, fix policies; GET fix-policy-rules/{id}
│   ├── ClassificationController   POST /classify
│   ├── FixController              POST /execute-fixes  (manual global drain)
│   ├── PipelineController         POST /run-pipeline
│   ├── ProcessController          POST /process
│   ├── LogParserController        POST /parse
│   ├── JobScanController          on-demand scan triggers + classify-pending
│   ├── RecommendationsController  POST /api/recommendations/{id}/approve|reject
│   │                              (approve drains synchronously)
│   └── AdminController            POST /api/admin/scan-history/cleanup (manual retention sweep)
├── Contracts/ (DTOs)
│   ├── JobFailureDto, RecommendationDto, ScanRunDto
│   ├── MonitoredJobDto / ScanCheckRuleDto / RuleOverrideDto
│   └── PipelineRequest, LogParseRequest
├── Middleware/        GlobalExceptionHandler (RFC 7807 ProblemDetails)
└── Extensions/        ServiceRegistration AddApplicationServices()
                       (Registers use cases + IScanHistoryRetentionService.)
```

## Frontend: MaiaAIEngineClient

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
│       └── navigation-history.service.ts  tracks previous distinct path for
│                                          the drawer's smart back button.
│                                          Eagerly instantiated in ShellComponent.
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
│   ├── recommendations/              RecommendationsComponent (also handles operator-actions route);
│   │                                 openFailure(id) → /failures?selected=:id (drawer)
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
    │             historyRepo.SaveAsync(ScanRunHistory) — always, even on Stolen/Timeout/Failed.
    │             Captures startedAt/completedAt/durationMs/outcome/error and the three counts.
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
GET  /api/data/failures?page=1&pageSize=50&view=               → JobFailureDto[] (paged)
                                                                 view ∈ active | unclassified |
                                                                       awaiting-action | auto-fixed |
                                                                       operator-fixed | resolved |
                                                                       manual-required
GET  /api/data/failures/{id}/status                            → failure detail + recommendations
                                                                 (polled by the drawer every 5s)
GET  /api/data/recommendations?page=1&pageSize=50              → RecommendationDto[] (+ policy snapshot)
GET  /api/data/monitored-jobs                                  → MonitoredJobDto[]
GET  /api/data/worker-status                                   → WorkerStatus payload
                                                                 (lease state, activeScans,
                                                                  recentScansLast30s, jobSummary, jobs)
GET  /api/data/dashboard-stats                                 → KPI aggregates
                                                                 (total/active/resolved/manualRequired/
                                                                  unclassified/awaitingAction/autoFixed/
                                                                  manuallyFixed + today fields:
                                                                  resolvedToday/autoFixedToday/
                                                                  manuallyFixedToday)
GET  /api/data/analytics/failures-over-time?range=24h|7d|30d   → time-bucketed failure counts by
                                              &bucketSize=        ErrorTypeId for the Errors Over Time
                                                                  chart. 24h→hour buckets, 7d/30d→day.
                                                                  Unclassified collapses to
                                                                  errorTypeId=0 / "(unclassified)".
GET  /api/data/scan-runs?monitoredJobId=&outcome=              → ScanRunDto[]  (paged, max 200)
                          &fromDate=&toDate=&page=1&pageSize=50
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
                                                                 GET /{id} returns a single rule
                                                                 (used by the auto-heal toggle's two-step PUT flow)
GET                 /api/config/job-types                      → lookup
GET/POST/PUT/DELETE /api/config/error-types[/{id}]             → ErrorType CRUD
                                                                 GET supports ?includeInactive=true
                                                                 DELETE is soft (IsActive=false) — RESTRICT FKs

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

POST     /api/admin/scan-history/cleanup                        → Run retention sweep on demand.
                                                                 Returns { rowsDeleted, durationMs,
                                                                           cutoff, skipped }.
                                                                 Same code path as the
                                                                 ScanHistoryRetentionWorker schedule.
```

### Drain triggers (in priority/frequency order)

1. **MonitoringWorker startup** — once before the polling loop, restart recovery.
2. **MonitoringWorker post-tick** — once after each `Parallel.ForEachAsync` batch (gated on `claimed.Count > 0`).
3. **`POST /api/recommendations/{id}/approve`** — synchronous global drain on the request thread.
4. **`POST /api/fix/execute-fixes`** — manual operator trigger.
5. **`POST /api/jobscan/classify-pending`** — re-classify-then-drain.

`ExecuteFixesUseCase` has no per-recommendation claim; concurrent drains can race and double-execute. `fixEngine` side effects (API call / SP / script) are not idempotent.

### Frontend dashboard polling cadence

`WorkerStatusService` runs a single 5s timer (`environment.dashboardRefreshIntervalMs`)
that fires four independent fetches per tick. Each endpoint has its own in-flight
gate; a slow request blocks only its own endpoint, never the others.

```
every 5s   ─┬─► GET /api/data/worker-status      → status$         (PolledData<WorkerStatus>)
            ├─► GET /api/data/dashboard-stats    → stats$          (PolledData<DashboardStats>)
            ├─► GET /api/data/failures?page=1
            │       &pageSize=10                 → recentFailures$ (PolledData<JobFailure[]>)
            └─► GET /api/data/monitored-jobs     → monitoredJobs$  (PolledData<MonitoredJob[]>)
```

On fetch failure the slice keeps its prior `value`, flips `isStale=true`, and stores
`lastError` — no subject ever errors out, so the timer survives any failure mode.
Refcounted start/stop — multiple consumers (dashboard, scan-jobs, top-bar) activate
polling independently; when refcount hits 0 the timer stops and in-flight requests
abort via `takeUntil(cancel$)`.

The failures drawer's `<app-failure-detail>` runs its own 5s poll on
`/api/data/failures/{id}/status` while open. Independent of the dashboard service;
silent re-fetches so the DOM updates only the bound parts (scroll position +
focus stay put).

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
│   ├── Scoped: 12 repository interfaces → Sql* implementations
│   │           (incl. IMonitoredJobLeaseRepository, IOperatorActionRepository,
│   │            IScanRunHistoryRepository)
│   ├── Scoped: IClassificationStrategy → RuleBasedClassifier
│   ├── Scoped: IFixCatalogue           → DbFixCatalogue
│   ├── Scoped: IFixEngine              → DefaultFixEngine
│   ├── Scoped: IFixHandler × 4 + IFixActionExecutor × 5
│   │           (incl. SqlScriptExecutor for raw-SQL fixes)
│   ├── Scoped: IScanStrategy × 3       (FileSystem, Database, ApiEndpoint — no execute injection)
│   ├── Scoped: ILogParser, ILogReader
│   └── Hosted: MonitoringWorker, ScanHistoryRetentionWorker
│
├── AddApplicationServices()             AIEngineAPI/Extensions
│   ├── IClassifyJobsUseCase, IGenerateSuggestionsUseCase
│   ├── IExecuteFixesUseCase, IDirectoryPipelineUseCase
│   └── IScanHistoryRetentionService
│
└── AddGlobalExceptionHandling()         ProblemDetails middleware

# appsettings.json — operational knobs
{
  "ScanHistory": {
    "Enabled":              true,    // master kill switch for retention
    "RetentionDays":        30,
    "CleanupIntervalHours": 6,       // ScanHistoryRetentionWorker schedule
    "CleanupBatchSize":     5000,    // DELETE TOP (N) per round
    "InterBatchDelayMs":    200      // pause between rounds to release locks
  }
}
```
