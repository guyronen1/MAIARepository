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

- Backend: C#, .NET 8, Entity Framework Core, ASP.NET Core, Serilog (file + console sinks)
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
- `[dbo].[ScanTypes]` — now carries `LeaseDurationSeconds` (FS=300, DB=1800, ApiEndpoint=60, FileContent=300)
- `[dbo].[ScanCheckRules]` — incl. FileContent fields (`ExtractorType`, `ExtractorLocator`, `IdentifierLocator`, `ExtractorPredicateType`, `ExtractorPredicateValue`)
- `[dbo].[ScanDbWatermarks]`
- `[dbo].[ScanFileWatermarks]`
- `[dbo].[ScanContentWatermarks]` — FileContent per-file mtime watermark; unique `(MonitoredJobId, FilePath)`, cascade delete
- `[dbo].[JobFailures]`
- `[dbo].[AIRecommendations]`
- `[dbo].[ClassificationRules]`
- `[dbo].[ErrorTypes]`
- `[dbo].[JobTypes]`
- `[dbo].[AuditLog]` — `FailureId` nullable; `EntityType` + `EntityId` columns discriminate failure-scoped events from config audits
- `[dbo].[FixPolicyRules]`
- `[dbo].[FixPolicyRuleSteps]` — ordered child rows for `ActionType=Composite` policies; FK to `FixPolicyRules` with `ON DELETE CASCADE`; unique `(RuleId, StepOrder)`
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
- **FixPolicyRule lookup is two-layered: per-MonitoredJob override first, then JobType-level default** — `IFixPolicyRepository.GetForAsync(jobTypeId, errorTypeId, monitoredJobId?)`, `IFixCatalogue(Repository).GetEntryAsync(errorTypeCode, jobTypeId, monitoredJobId?)`, and the policy-snapshot projection in `SqlRecommendationRepository.GetPagedAsync` all apply the same priority: an enabled rule with `MonitoredJobId == failure.MonitoredJobId` wins over one with `MonitoredJobId IS NULL` for the same ErrorType. Default (NULL) covers every job of its `JobTypeId`; override targets one specific MonitoredJob. The wildcard `JobTypeId IS NULL` arm was rejected — column is non-nullable, so a "generic across JobTypes" policy doesn't exist; add separate `FixPolicyRule` rows per JobType. `DefaultFixEngine` emits `Policy lookup miss: jobTypeId={} errorTypeId={} recommendationId={}` at Information level for grep-ability of unconfigured pairs.
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
- **ConfigController audit coverage** — every `POST` / `PUT` / `DELETE` on `ConfigController` now writes an `AuditLog` row after the primary save succeeds. Covers all five managed entities: `ErrorType`, `MonitoredJob`, `ScanCheckRule`, `ClassificationRule` (global + per-job + link/unlink), `FixPolicyRule`. Audit-write failures log at `Error` level but never fail the request — degraded audit beats a rolled-back user action.
- **`AuditLog` schema restructured for generic entity audit** — `FailureId` is now nullable (`int?`); new `EntityType` (`nvarchar(100)`) and `EntityId` (`nvarchar(100)`) columns discriminate what each row is about (`FixPolicyRule`, `MonitoredJob`, `AiRecommendation`, etc.). Existing rows were deleted in migration `20260528161609_AuditLogConfigSupport` — dev data only, half-modelled, clean slate beats half-correct backfill. Failure-scoped rows continue to populate `FailureId`; config rows leave it null.
- **`EventType` convention is `{EntityType}{ActionVerb}`** — `FixPolicyCreated` / `FixPolicyUpdated` / `FixPolicyDeleted`, `MonitoredJobCreated` / etc. Existing `OperatorApproved` / `OperatorRejected` values stay as-is (don't mutate audit history). For relationship changes (link/unlink), the event is recorded against the host entity: `ClassificationRuleLinked` / `ClassificationRuleUnlinked` with `EntityType="MonitoredJob"` and `EntityId=<jobId>`, so an auditor scanning a job's history sees both entity edits and link events together.
- **`Detail` format**: Create rows describe the new entity (`Created FixPolicyRule (JobTypeId=1, ErrorTypeId=2, FixCategory=Manual, ActionType=Manual, IsAutoHealEligible=false, Enabled=true)`); Update rows are a `field: before → after` diff joined by `, ` — only fields that actually changed, "No changes" when nothing did; Delete rows snapshot enough identifying info to stay intelligible after the entity is gone.
- **`operatorId` required on every config write** — POST/PUT take it in the request body, DELETE / link endpoints take it via `?operatorId=` query string. Frontend `ConfigService` injects the constant `'operator'` (single-point-of-change for when auth lands). Matches the pattern `RecommendationsController` already used.
- **New `JobStatus.AwaitingManualAction` + `Stage = "Acknowledged"`** — when an operator approves a recommendation whose `FixCategory == Manual` (no automated executor), `ExecuteFixesUseCase` now routes the failure to `AwaitingManualAction` instead of `ManualRequired`. The operator's click is visible (status changes, distinct blue badge `badge-awaitingmanualaction`, stage pipeline shows "Acknowledged" between "Recommended" and "Fixed"). The drawer's `[Approve]` button reads `[Acknowledge]` for Manual recs and a `[✓ Mark Resolved]` action appears on the Failure Details card when status is `AwaitingManualAction` — operator confirms the off-system work is done via `POST /api/failures/{id}/mark-resolved` (idempotent: re-marking already-Resolved returns 204). New endpoint sits on a new `FailuresController` (operator-action namespace) and writes `EventType="ManuallyResolved"` audit.
- **Rejecting the last pending recommendation transitions the failure to `ManualRequired`** — `RecommendationsController.Reject` now post-checks: if the failure is still in `Failed` status AND no other recs on it are pending (`OperatorApproved IS NULL AND !IsExecuted`), flip it to `ManualRequired` + write a `ManualActionRequired` audit row. Otherwise the operator's rejection was invisible — failure sat at `Failed`/Stage=Recommended forever even after they explicitly declined the suggestion. Guard intentionally skips multi-rec failures (some other rec might still be the actionable path) and skips failures already past Failed (AwaitingManualAction / Resolved must not regress).
- **Two alternative middle stages: `Acknowledged` vs `Manual`** — after `Recommended`, a failure branches into one of two parallel states depending on what the operator did: `Acknowledged` (approved a Manual rec, off-system action pending → Status=AwaitingManualAction) or `Manual` (rejected, or auto-heal hit a dead end → Status=ManualRequired). Both converge at `Fixed` once the operator hits Mark Resolved. The drawer's stages pipeline renders only the actually-reached middle stage — never both — so the visualization doesn't claim the operator did contradictory things. Frontend `stages` is now a computed signal that swaps the middle entry based on `failure().status`, and `isStageCompleted` derives its order array from the rendered list (not a hardcoded one) so it stays in sync.
- **Mark Resolved button extended to cover `ManualRequired` too** — same single button on the drawer's Failure Details card, visible whenever Status is in `{AwaitingManualAction, ManualRequired}`. Both states mean "operator is on the hook to handle this manually"; the button is their exit ramp to `Resolved`. `POST /api/failures/{id}/mark-resolved` is unchanged (already idempotent) — only the UI predicate widened.
- **`IFixEngine.ExecuteAsync` returns `FixOutcome`, not `bool`** — `Success` / `NoAutomatedAction` / `Failed`. The intermediate `NoAutomatedAction` lets the caller route operator-approved Manual fixes to `AwaitingManualAction` while routing auto-heal-path Manual fixes (no operator approval) and actual executor failures both to `ManualRequired`. Earlier `bool` collapse plus a `rec.FixCategory == Manual` check missed the `(FixCategory=Retry, ActionType=Manual)` case — operator-approved fixes against `RuleId 1009`-style policies wrongly went to `ManualRequired`. `ActionType` (on the policy) is the truth; `FixOutcome` encodes it.
- **Honest log + audit copy for the operator-approved-manual branch** — `FixExecutionLog.Success=true` (not the misleading `false`), `ResultDetail="Approved by operator — manual action required to complete."`, audit `EventType="ManualActionRequired"` (was: `FixFailed`), logger at `Information` not `Warning`. Distinguishes "system stuck, needs operator" (FixFailed → ManualRequired) from "operator decided, performing off-system work" (ManualActionRequired → AwaitingManualAction) in the file log + audit trail.
- **At most one enabled `FixPolicyRule` per *layer* — separate uniqueness for defaults and overrides** — enforced at three layers. (a) DB: two filtered unique indexes (migration `20260531152630_AddFixPolicyMonitoredJobOverride` replaces the original single-index migration). `UX_FixPolicyRules_DefaultActiveKey` on `(JobTypeId, ErrorTypeId) WHERE Enabled = 1 AND MonitoredJobId IS NULL` — at most one default per (JobType, ErrorType). `UX_FixPolicyRules_OverrideActiveKey` on `(MonitoredJobId, ErrorTypeId) WHERE Enabled = 1 AND MonitoredJobId IS NOT NULL` — at most one override per (MonitoredJob, ErrorType). A default and an override for the same (JobType, ErrorType) coexist by design — they're complementary. (b) Backend: `CreateFixPolicyRule` + `UpdateFixPolicyRule` do a two-pronged 409 pre-flight (override-layer collision vs default-layer collision, keyed differently) returning `{ error: "DuplicateFixPolicy", message, conflictingPolicyId }`. (c) UI: inline soft warning + post-save 409 banner with "Open existing policy" affordance. Disabled rows can duplicate freely — staged-replacement flow unchanged.
- **All audit-write paths now emit the same shape** — `ExecuteFixesUseCase` (FixExecuted / FixFailed) and `RecommendationsController` (OperatorApproved / OperatorRejected) both populate `EntityType="AiRecommendation"` + `EntityId=<recommendationId>` alongside the legacy `FailureId`, matching what the `ConfigController` audits do. An auditor can now filter "everything that touched AiRecommendation X" uniformly across the table.
- **File logging via Serilog** — `Serilog.AspNetCore` replaces the default `Microsoft.Extensions.Logging` providers in `Program.cs` (`builder.Host.UseSerilog(...)`); config lives entirely in the `Serilog` section of `appsettings.json`. Two sinks: **Console** (same visibility as before for `dotnet run`) and **File** at `logs/maia-api-.log` with daily rolling + 30-day retention. Existing `ILogger<T>` injections work unchanged — no callsite changes anywhere. Framework noise (`Microsoft.*`, `System.*`) overridden to `Warning`; application code stays at `Information`. Verbosity is now config-only (no rebuild to crank to `Debug`). The `logs/` folder is created next to the running binary; `*.log` already gitignored.
- **Dashboard "Fix Failures Today" KPI + drill-down + per-row "Failed to Execute" badge** — 5th KPI tile on `/dashboard` shows count of distinct failures currently in `ManualRequired` with at least one `Success=false` `FixExecutionLog` since today-midnight. Clicking drills to `/failures?view=fix-failed` (same predicate, list view). The failures list table renders a `Failed to Execute` badge next to the status column on ANY row matching `JobFailure.HasRecentFixFailure=true` (Status-agnostic — surfaces when an executor failed today even on rows still showing `Failed`, not just `ManualRequired`). Backend: new `IJobRepository.GetIdsWithRecentFixFailureAsync(failureIds, since)` batches one extra query after paging to populate the flag — no per-row N+1. `view=fix-failed` is a new branch in `GetPagedAsync`; `fixFailedToday` is a new field on the `/dashboard-stats` payload.
- **Recommendation atomic claim + per-step timeout standardisation + FS-scan filename DSL alignment** — three related production-hardening changes. (1) `IRecommendationRepository.ClaimPendingAsync` does atomic `UPDATE TOP(N) ... OUTPUT ... WITH (READPAST, UPDLOCK, ROWLOCK)` against `AIRecommendations` (new `ClaimedBy` + `ClaimedAt` columns + filtered index, migration `20260601182726_RecommendationAtomicClaim`); claim eligibility requires `Failure.Status = 'Failed'` so a failed executor that moved the failure to `ManualRequired` no longer re-runs every drain tick (the pre-existing infinite-retry bug). Claim cleared by `MarkExecutedAsync` (success) or `ReleaseClaimAsync` (failure → eligible for retry after the 5-min timeout). Same `host;pid;runId` owner-id shape as `MonitoredJobLeases`. (2) New `Infrastructure/Fix/ExecutorTimeouts.cs` centralises per-step timeouts (Default = 60s, Script = 120s) and provides `LinkedWithTimeout(ct, timeout)`. Every executor wraps work in a linked CTS so cancellation propagates: `SqlScriptExecutor` + `StoredProcedureExecutor` set `CommandTimeout`, `ApiCallExecutor` uses per-request CTS (and finally moved to `IPlaceholderResolver` — was the last hold-out doing inline `string.Replace`), `CopyFileExecutor` replaced sync `File.Copy` with `FileStream.CopyToAsync` so cancellation propagates per 81KB chunk. Each catches `OperationCanceledException` with a `when (cts.IsCancellationRequested && !ct.IsCancellationRequested)` guard to distinguish timeout from outer cancel (different log levels). (3) New `Core/Scanning/FilenamePattern.cs` matches the classification-rule wildcard DSL (`*` only, case-insensitive, no-`*` = substring), replacing `Directory.GetFiles(folder, pattern)` in both `FileSystemScanStrategy` and `DirectoryPipelineUseCase`. Closes four pre-existing bugs: no-`*` patterns were exact-filename match instead of substring, `?` was accepted as a wildcard, `*` alone hit the Win32 legacy quirk, and case-sensitivity was OS-dependent.
- **Composite fix policies + `CopyFile` executor + path-aware placeholders** — operators can configure a `FixPolicyRule` with `ActionType=Composite`, backed by an ordered `FixPolicyRuleStep` child table (FK `ON DELETE CASCADE`, unique `(RuleId, StepOrder)`). The engine runs every step in `StepOrder` ascending (best-effort: any step failure → `ManualRequired`, remaining steps still run) and writes one `FixExecutionLog` row per step. Steps cannot be `Manual` or `Composite` (no nesting). New `CopyFileExecutor` (atomic copy via `.tmp` + rename, overwrite by default, UNC supported) sits alongside the existing single-action executors and can be used as a step OR as a top-level policy. All executors route payload substitution through `IPlaceholderResolver`, which centralises the token table — `{failureId}`, `{sourceId}`, `{sourceLogPath}` (existing) plus `{sourceFilePath}`, `{jobFolder}`, `{inputFolder}` (new). The scan strategies populate `JobFailure.SourceFilePath` per-failure: FS scans extract it from the matching log line via `ScanCheckRule.InputPathPattern` (regex with capture group #1, with `MonitoredJob.InputFolder` joined when the capture is relative); DB scans read it from `ScanCheckRule.FilePathColumn` (supports `alias.Column` form when the operator put a JOIN in `SourceTable`). UI: `Monitored Jobs` config — `InputFolder` field on FS jobs, `InputPathPattern` on FS scan rules, `FilePathColumn` on DB scan rules, full step editor in the Fix Options drawer (per-step ActionType + payload + optional Description; `SqlScript` steps get a 2-row monospace textarea; auto-clears `actionPayload` when switching TO `Composite` and clears `Steps` when switching away). **Failure drawer** (`failure-detail.component.ts`) — composite recs render a description-only bulleted step list inline on the rec card; lazy-fetches `FixPolicyRule.Steps` once per `ruleId` and re-fetches on every 5s poll refresh so policy edits are reflected live. The recommendations TABLE stays compact (no badge) — operators see the badge + step list in the drawer where they actually review-before-approve.

# Active Goals / What We're Working On

**`FileContentScanStrategy` + `XmlContentExtractor` — SHIPPED (2026-06-07).**
The 4th scan strategy is built, tested (38 new unit/integration tests), and
smoke-tested end-to-end. See the "Last completed (2026-06-07)" entry and the
FileContent decisions block below. The open questions from the design phase are
now resolved: watermarking is a **new `ScanContentWatermarks` table** (mtime-based,
content-hash deferred to v2); output shape is **`JobFailure`** (downstream pipeline
unchanged). v2 extractors (CSV/JSON/Excel), content-hash watermarking, and
composite scan rules are deferred (see Known follow-ups).

**Backlog (already documented in Known follow-ups below):**

- Authn / authz on controllers + real operator identity replacing hardcoded
  `'operator'`
- Audit-viewing UI (per-entity tab + global `/audit` screen)
- Small cleanup items: `DbFixHandler` TODO stub, `ILogReader` removal,
  `monitored-jobs.service.ts` consolidation, analytics endpoint shape
  consistency, sort UI on failures-list, style budget warnings,
  CHECK constraints for composite invariants, `FixExecutionLog` retention
  worker

**Last completed (2026-06-11):**

- **Tier 2.5 frontend rework — dedicated per-job config screen (d2 a–d), SHIPPED.** New route `/config/monitored-jobs/:id` (`JobConfigComponent`) is now the full editing surface for a job; the Monitored Jobs **list** was slimmed to job-identity CRUD only and its "Configure" button **navigates** to the screen (the old inline-expand tabs + scan/class/fix drawers were deleted — list chunk 269 kB → 45 kB). Backend: one new endpoint `GET /api/config/monitored-jobs/{id}` → `MonitoredJobDto.From` with `Sources` (active sources + their active rules) for a single round-trip. The screen renders three row-panels:
  - **Scan Sources** — per-source CRUD (ScanType-immutable-on-edit, validation-matrix 400s surfaced) with **nested per-source scan-rule CRUD** (rule drawer branches on the *source's* ScanType: FS keyword+inputpath / DB checktype+table+field+range+watermark+sourceid+filepath / FileContent extractor fields; API = rule-free). Create routes through `createScanRuleForSource(sourceId, …)`.
  - **Classification Rules** — new/edit/link/delete of per-job rules + a read-only "{JobType} defaults" subsection (union semantics: `allClassRules` ∪ `allJobs` link-map, mirrors `GetEffectiveRulesAsync`).
  - **Fix Options** — the full fix-rule drawer ported intact (scope flip, classification-rule shortcut, ErrorType reachability + "covers N", client dup-conflict + server 409, composite step editor, `{sourceFilePath}` warning, token legend, auto-heal banner, shadowed-default badge), adapted to single-job context (`this.job()` + source rules).
  - **Case-B deep-link** retargeted: `/unconfigured` "Configure fix" now → `/config/monitored-jobs/:id?errorTypeId=`, which pops a pre-filled new-fix drawer on load and clears the param.
  - **Density polish (Change 1–3):** source/rule secondary actions (Edit/Delete) are hover/`:focus-within`-reveal (`+ Add Rule`/`+ Add Source`/`Edit Job` stay visible); the "Scan Rules N + Add Rule" subheader collapsed into an inline rule-count chip on the source header; +4px row padding on scan-rule tables only.
  - New config drawers use the shared `DrawerComponent`; the list's job-edit drawer was also converted to the shared `DrawerComponent` (see d2e below) so every drawer matches in size.

- **Tier 2.5 d2e/d3 — per-source worker-status breakdown + dashboard drill-down + UI polish, SHIPPED.**
  - **Backend:** `GET /api/data/worker-status` `jobs[]` gained a nested `sources[]` (`{ scanSourceId, name, scanTypeName, lastScan }`); per-source `lastScan` is the latest `ScanRunHistory` row filtered by `ScanSourceId` (the per-source grain the worker writes). The job-level `lastScan` rollup is unchanged.
  - **Dashboard drill-down:** expanding a Monitored Jobs row now shows a per-source scan breakdown (outcome badge · duration · age · failure/classified/rec counts; "No scans yet" for never-run sources) instead of one job-level summary. Frontend model: `LastScanSummary` + `SourceLastScanRow` + `JobLastScanRow.sources`; `sourcesFor(jobId)` joins worker-status by `monitoredJobId`. The "Scan sources N" subheader was dropped (kept as `aria-label`); the leaked job-description line was removed (descriptions are config-screen only).
  - **Compact-row scan-type label** is derived from the job's sources: one type → that type; N sources same type → "FileSystem · N sources"; mixed → "Mixed · N sources" with a per-source hover tooltip. Falls back to the legacy `scanTypeName` until the first worker-status poll.
  - **Config screen organization:** capped `.page` to `max-width: 1080px` (was the ultra-wide 1500); source headers laid out as identity-left / count+actions-right / truncating config-path-middle so edges align across sources; the scan-rule / classification / fix tables use `table-layout: fixed` with explicit per-column widths so columns don't shift with the data (long XPaths wrap).
  - **Drawer unification:** the Monitored Jobs list's job-edit drawer now uses the shared `DrawerComponent` (760px, same chrome as the config-screen source/rule/class/fix drawers) — all drawers open identically.
  - **Dashboard charts:** Errors Over Time + Failures by Job + Resolution Mix moved into a single row (`1.7fr 1fr 1fr`, all 200px tall); collapses to one column ≤1024px. Saves vertical space vs the prior two-row layout.

- **Case B (`/unconfigured` policy-gaps) status filter.** `GetPolicyGaps` now filters to `Status == JobStatus.Failed` (mirrors Case A) — resolved/acknowledged failures no longer count as open policy gaps. Fixes "marked it resolved but the gap stayed." Regression test `PolicyGapStatusTests` (first controller-level test — added `AIEngineApi` ProjectRef + `Microsoft.AspNetCore.App` FrameworkRef to the test project). Surfaced two by-design behaviors worth knowing: classification is a sticky one-time label, and `JobFailure.JobTypeId` is a creation-time snapshot.
- **FileContent locator validation + extraction visibility (the (a)+(b) follow-up).** (a) `IFileContentExtractor.ValidateLocator` + `ConfigController` save-time `400 InvalidLocator`; (b) `PredicateUnevaluableSkips` counter + all three FileContent counters exposed in `ScanRunDto`. Migration `AddPredicateUnevaluableSkips`. 11 new tests (179 total). Live-verified: malformed XPath rejected at save; the `\\`-vs-`//` typo can no longer be saved.

**Last completed (2026-06-07):**

- **`FileContentScanStrategy` + `XmlContentExtractor` (4th scan strategy).**
  Structured extraction from INPUT DATA files (not logs). Two operator modes,
  one rule shape: **(1) filename signals failure** — a file whose name matches
  the rule's pattern IS a failure (e.g. `*WARNING*.xml`); **(2) content
  predicate** — extract a value via XPath and test it (e.g. `/file/status/code`
  Equals `ERROR`). Either way an `IdentifierLocator` (XPath) can pull a natural
  key for `SourceId`. New `ScanType.FileContent=3`, `CheckType.FileContent=5`,
  `ScanTypes` row 4 (lease 300s), enums `FileFormat{Xml}` + `ScanPredicateType
  {Equals,NotEquals,Contains,NotContains}`. Migration `AddFileContentScan`.
- **`IFileContentExtractor` plug-in seam + `XmlContentExtractor`.** Interface in
  `Core/Interfaces` (Format + `ExtractAsync(filePath, locator) → string?`),
  registered as `IEnumerable<IFileContentExtractor>`, strategy dispatches by
  `ExtractorType` — same shape as `IFixActionExecutor`. XML impl uses
  `XPathEvaluate` (elements, attributes, text/CDATA, functions), **strips
  namespaces** before evaluation (operators write plain `/file/status/code`),
  hard 5MB cap (`FileContentTooLargeException`), null-on-miss/malformed. 21 unit
  tests. v2 extractors (CSV/JSON/Excel) plug in here.
- **`ScanCheckRule` +5 fields** (all nullable, FileContent-only): `ExtractorType`,
  `ExtractorLocator`, `IdentifierLocator`, `ExtractorPredicateType`,
  `ExtractorPredicateValue`. **`TargetField` is reused as the filename pattern**
  (same `*`-wildcard DSL as classification/FS) — no new column. **`MonitoredJob`
  +`IncludeSubfolders`** (reuses `LogFolder` as the scanned folder). **`ScanRunHistory`
  +`IdentifierExtractionFailures` +`OversizeFileSkips`** counters (flow
  `ScanResult` → worker → history row).
- **New `ScanContentWatermarks` table** (per-`(MonitoredJobId, FilePath)`, mtime-based).
  Methods folded into the existing `IScanWatermarkRepository` (it's already the
  multi-kind watermark repo) — no new interface. Dedup: skip a file when its
  current mtime ≤ recorded `LastModifiedAt`; re-scan when new or modified.
  Content-hash tamper detection deferred to v2.
- **File-outer / rule-inner walk ("walk-once-apply-many").** Forced by the
  per-file watermark grain: each file is examined once, every rule whose filename
  pattern matches is applied, watermark written once after. A single file
  produces 0–N `JobFailure`s depending on how many rules' predicates evaluate
  successfully on it.
- **ConfigController validation, FileContent-scoped only.** Three 400 codes:
  `ExtractorTypeRequired`, `PredicateIncomplete` (type/value must be both-or-
  neither), `PredicateRequiresLocator`. FS/DB/API rule types are untouched
  (their existing "validate at scan time" behavior stays). Verified live: all
  three codes fire. UI surfaces them as a footer save-error banner + an inline
  soft-warning when a predicate lacks a locator.
- **Frontend — Scan Rules drawer learns FileContent.** New `FileContent` scan
  type; the rule drawer renders Filename Pattern (relabeled `TargetField`),
  Format, Value Locator, Predicate (+ value), Identifier Locator with
  format-specific hints; `IncludeSubfolders` checkbox on the job config.
  (Also fixed a latent bug: `SqlMonitoredJobRepository.UpdateAsync` never
  persisted `InputFolder` — added alongside `IncludeSubfolders`.)
- **Smoke test (live, job 1005):** two rules (filename-only + content-predicate)
  over real XML → 2 failures (`ORD-88134` via `/order/@id`; `INV-2026-001` via
  `/file/header/invoiceId`, fired on `code=ERROR`); healthy `invoice-ok.xml`
  (code=OK) correctly didn't fire; all 3 files watermarked (incl. the non-firing
  one); `classify-pending` then classified the invoice failure against its
  constructed `ErrorMessage` → `ErrorTypeId=FileNotFound`, confirming the
  classify→suggest pipeline consumes FileContent failures unchanged.

**Last completed (2026-06-06):**

- **`/unconfigured` operator screen + coverage-gap analysis.** Two read-only
  sections over a 30-day (or all-time) window: **Case A** — unclassified
  failures (`ErrorTypeId IS NULL`) clustered into suggested ClassificationRule
  patterns; **Case B** — classified failures whose recommendation has no
  effective FixPolicyRule (override→default lookup null), aggregated by
  (ErrorType, JobType, MonitoredJob). Case A "Configure" opens a focused
  classification-rule drawer (pattern pre-filled) and on save runs
  classify-pending so the cluster clears; Case B "Configure fix" **deep-links**
  into the job's Fix Options drawer pre-filled (`?fixForJob=&errorTypeId=`).
  The dashboard "Unconfigured" tile now drills here (was `/failures?view=unconfigured`).
- **`IUnconfiguredClusterAnalyzer` (v2 seam) + `NgramClusterAnalyzer`.** v1 is
  n-gram frequency over normalized messages (`MessageNormalizer`: strip scan
  prefix → strip leading timestamp → collapse GUIDs → collapse 4+ digit runs;
  GUID-before-digits ordering is load-bearing). Greedy set-cover by
  `documentFrequency × n` (n=2..7) gives non-overlapping clusters; single-
  occurrence noise stays uncategorized. `AnalyzerVersion="ngram-v1"`,
  `ConfidenceScore=null`. Registered via DI like `IFixActionExecutor` — v2
  (embedding/LLM) swaps in without touching callers. 16 unit tests.
- **Rule suggestion provenance** — nullable `SuggestedBy` / `SuggestedFromHash`
  / `SuggestedConfidence` on both `ClassificationRules` and `FixPolicyRules`
  (migration `AddRuleSuggestionProvenance`). Recorded on CREATE when a rule is
  accepted from a cluster (Case A wired; Case B deferred — see follow-ups).
  `SuggestedFromHash` = SHA-256(sorted sample failure ids, comma-joined), first
  16 hex. The v2 ML/LLM training signal; null for manual creation.
- **New endpoints** `GET /api/unconfigured/clusters` + `/policy-gaps` (windowed).
- **Classifier UNION semantics (replaces "linked-only opts out of globals").**
  `GetEffectiveRulesAsync` now returns the job's linked rules (by Priority)
  **plus** the JobType-global rules not already linked (by Priority), deduped.
  A JobType-level ClassificationRule again applies to every job of that type;
  per-job links add on top. Precedence = list order → first-match-wins →
  **linked beats global** (mirrors FixPolicyRule override→default). The old
  "any link disables all globals" was a UI/system mismatch that silently broke
  `/unconfigured` Case A (operator creates a JobType-DTSX rule, it never fires
  on a job that has links). Verified live: classify-pending cleared 19 of 20
  unclassified after configuring two clusters.
- **Whitespace-tolerant matching — `Core/Classification/ClassificationMatcher`.**
  Extracted the ClassificationRule match logic (case-insensitive, `*`-only
  wildcard, regex metachars literal, 50ms timeout) out of `RuleBasedClassifier`
  into a public static helper (sibling to `Core/Scanning/FilenamePattern`), and
  made it collapse runs of whitespace to a single space on BOTH the line and
  the pattern. Logs have irregular spacing (`INFO␣␣Package`) while the n-gram
  analyzer emits single-spaced suggested patterns — without this a correct
  suggestion silently failed to match its own source. Strictly more permissive
  on whitespace only; existing single-space matches unchanged.
- **ClassificationRule 3-layer duplicate guard.** Filtered unique index
  `UX_ClassificationRules_ActiveKey` on `(JobTypeId, Pattern) WHERE IsActive=1`
  (migration `AddClassificationRuleActiveKeyIndex`); backend 409 pre-flight in
  Create/Update (`{ error:"DuplicateClassificationRule", conflictingRuleId }`);
  UI inline soft-warning + post-409 banner with "Open existing rule" on the
  Classification Rules screen (the `/unconfigured` drawer already surfaces the
  409 message). Natural key is `(JobTypeId, Pattern)` — ClassificationRule has
  no MonitoredJobId (per-job is the `MonitoredJobRules` link). Cleaned up 4+2
  duplicate rules that the retry-on-no-effect loop had created before the guard.
- **Classification Rules screen — Scope column.** Operators couldn't tell, from
  the global rules list, whether a rule was a JobType-wide default or scoped to
  specific jobs (the trigger: an operator added a `Warning` rule "for B2B DB
  Files Process" and had no way to confirm it was job-specific, not Exe-wide).
  `ConfigController.GetAllClassificationRules` now projects `LinkedJobNames` per
  rule via one batched query (active `MonitoredJobRules` ⋈ `MonitoredJobs.Name`,
  grouped by RuleId — no N+1). The frontend `ClassificationRule` model carries
  `linkedJobNames: string[]`; the table renders **Default · all {JobType}**
  (badge-muted) when the array is empty, else **Linked · {names}** (badge-resolved).
  Empty = JobType default (applies to every job of that type, per the UNION
  classifier); non-empty = scoped override. Mirrors the Monitored Jobs Class tab,
  which already shows effective = linked ∪ JobType-globals.
- **`badge-muted` recolored on two screens (was near-black `#2a2f3a`).** The
  per-component `.badge-muted` default fell back to a dark slate that read as an
  unclear black chip on light tables. Replaced with readable light fills: the
  Classification Rules **Default · all {JobType}** badge → soft indigo
  (`#e0e7ff` / `#3730a3` / `#c7d2fe` border, signalling "global/default"); the
  Recommendations **Auto-run "No"** badge → light slate (`#e2e8f0` / `#475569`).
  `.badge-muted` is defined per-component (not global), so each screen needs its
  own override — the dashboard's Monitored Jobs already had a light variant.
- **Responsive layout pass (14" laptop fit).** Two screens were clipping content
  on laptop-width viewports (~1280–1366px CSS):
  - **Classification Rules table** — wrapped in `.table-wrap { overflow-x:auto }`
    (scroll safety net, last column never clipped) + a `.compact` density class
    (tighter cells/badges, narrower confidence bar). Progressive column drop via
    media queries: **Confidence** hides ≤1500px, **Priority** hides ≤1280px —
    keeps Pattern · Job Type · Scope · Error Type · Status · Actions visible.
  - **Dashboard** — the KPI grid already wrapped at ≤1400px, but the analytics
    row (Errors Over Time + Failures by Job) only stacked at ≤1200px and the
    info panels (Recent Failures + Monitored Jobs) only at ≤900px, so on a 14"
    laptop the right column of both got clipped. Aligned all three to collapse to
    full-width at the **same ≤1400px** breakpoint. Also hardened the Monitored
    Jobs compact row (`min-width:0` on the flex row + name ellipsis) so a long
    job name truncates instead of overflowing its card even on wide screens.

**Last completed (2026-06-03):**

- **Shared `DrawerComponent`** (`shared/drawer/`) — extracted the failures-list
  drawer shell (backdrop, slide-in panel, smart back button, ✕, Esc/click-outside
  close) into a reusable component with `[drawer-title]` / `[drawer-controls]` /
  body projection slots. Three consumers now: failures list, **recommendations**
  (Phase 2 — in-place `?selected=` drawer, no longer routes to `/failures`), and
  **dashboard** Recent Failures (local `selectedFailureId`, not URL — live view).
- **Execution History** in the failure drawer — backend `/failures/{id}/status`
  returns an `Executions` array (per-step composite rows + summary); UI shows a
  collapsible, newest-attempt-first list grouped into attempts, a one-line ✓/✗
  per row, and a red banner counting only the **latest attempt**. Rec-card
  composite steps render ✓/✗/• per step (matched to execution results).
- **Retry Fix** — `POST /api/recommendations/{id}/retry` re-arms a failure stuck
  in `ManualRequired` (clears IsExecuted/claim, OperatorApproved=true, Status→
  Failed) and drains synchronously with the CURRENT policy; explicit operator
  override for "I fixed the root cause, run it again". UI button on the rec card
  when that rec failed to execute.
- **`{sourceFileName}` placeholder** — `Path.GetFileName({sourceFilePath})`;
  added to legend + CopyFile examples.
- **Fix Options ErrorType-join clarity** — classification-rule picker (pick a
  symptom → sets ErrorType), reachability warning (ErrorType with no
  classification rule on the job won't trigger), "Covers N rules" fan-in line,
  and Override·active / Default·shadowed badges. Mirrors `GetEffectiveRulesAsync`.
- **Bug fixes** — Job/Step filter on `/failures` now applies on keystroke (was a
  no-op via the query-param round-trip); single-action SqlScript example fixed to
  `WHERE Id = '{sourceId}'` (quoted) with a hint that `{failureId}` is MAIA's
  internal id, not a source-table column.

**Last completed (2026-06-01):**

- Composite fix policies + `CopyFile` executor + path-aware placeholders
  (`{sourceFilePath}` / `{jobFolder}` / `{inputFolder}`) — scan strategies
  capture input path per failure; composite engine runs ordered steps
  best-effort with per-step `FixExecutionLog`; UI step editor in Fix
  Options drawer + bulleted step list on the failure drawer's rec card.
- Recommendation atomic claim (`UPDATE TOP(N) ... OUTPUT ... WITH READPAST,
  UPDLOCK, ROWLOCK` on `AIRecommendations` + new `ClaimedBy`/`ClaimedAt`
  columns) closing the infinite-retry bug and concurrent-drain race.
- Per-step executor timeout standardisation (60s default, Script 120s);
  every executor honours `CancellationToken`; `CopyFileExecutor` switched
  to stream-based async copy so cancellation propagates per 81KB chunk.
- FS-scan filename DSL alignment (`Core/Scanning/FilenamePattern.cs`) —
  matches classification-rule wildcard semantics, replaces `Directory.GetFiles`
  glob in both `FileSystemScanStrategy` and `DirectoryPipelineUseCase`.
- "Fix Failures Today" dashboard KPI (5th tile, 🚨 alarm icon + pulse) +
  `view=fix-failed` drill-down + per-row "Failed to Execute" badge on the
  failures table.

# Known follow-ups (not blocking)

- **🏗️ ARCHITECTURE — heterogeneous scan rules under one MonitoredJob — DECIDED: Tier 2.5, IN PROGRESS.**
  **Decision (2026-06-09):** build a **`ScanSource`** entity (a typed observation point within a job: its own ScanType + scan config + scan rules), keep **leases 1:1 with MonitoredJob** (per-source leases deferred — operator confirmed no cadence-divergence need: B2B DB+log mix isn't a problem, no differing frequency requirements, no real-time recovery tension). Per-source `ScanRunHistory` rows (achievable under per-job leases). This delivers Option A's "roll up by operational concept, drill down to per-source detail" UX without the per-source lease machinery.
  **Phasing:** **(a)** schema + backfill, behavior-preserving — new `ScanSources` table + nullable `ScanSourceId` on `ScanCheckRules`/`ScanFileWatermarks`/`ScanContentWatermarks`/`JobFailures`/`ScanRunHistory`; backfill one ScanSource per existing job mirroring its config; old MonitoredJob columns + `MonitoredJobLeases` left untouched, no NOT NULL yet, no worker change. **(b)** worker + `IScanStrategy` contract change (`ScanAsync` reads config from the source; biggest correctness risk — its own focused round). **(c)** read-side (worker-status rollup, scan-runs `ScanSourceId`). **(d)** config UI/UX rework (below). Then a cleanup migration: enforce NOT NULL + drop the moved MonitoredJob columns. **Per-source leases remain cleanly deferrable** — re-key the lease FK + claim JOIN as an isolated later change if cadence divergence ever becomes real.
  **UI/UX direction (part of this change, phase d):** the Monitored Jobs config screen is getting too heavy. Keep the **job list** with **edit-job-details in a drawer** (as today). But **"Configure" opens a dedicated full job-configuration screen** (not the current tabbed drawer panel): each section (Scan Sources, Classification Rules, Fix Options) is its own **row panel** showing the relevant list, and **editing an item in a section opens a drawer** (as today). Sources nest their scan rules.
  *(Original three framings + the per-coupling analysis + Tier 1/2/3 trade-offs preserved below for context.)*

  **Root cause (verified in code):** `ScanType` lives at *both* levels and they conflict. `MonitoredJob.ScanTypeId` makes the worker pick exactly one strategy (`strategies.FirstOrDefault(s => s.ScanType == job.ScanType)`), but `ScanCheckRule.CheckType` is *already* a per-rule discriminator and every strategy already filters the job's rules by it (`FileSystem`→`ErrorKeyword`, `Database`→`ColumnRange`/`ValueEquals`, `ApiEndpoint`→`StatusCode`/`ResponseContains`, `FileContent`→`FileContent`). So the rule layer is already type-aware; the job-level `ScanType` is the artificial bottleneck duplicating what the rules know.

  **Concrete coupling points any fix must address:** (a) worker dispatch (`FirstOrDefault(==job.ScanType)` → run every strategy with ≥1 matching active rule); (b) per-type config fields (`LogFolder`/`SearchPatterns`, `ConnectionName`, `LogSourceUrl`) are job-level + singular — all DB rules share one connection, all FS rules one folder; (c) lease duration is per-`ScanType` and doubles as the per-job timeout → mixed job needs `max(involved)` or a job-level value; (d) `ScanRunHistory` is one-row-per-job-scan → aggregate vs per-strategy rows; (e) **FS has a non-rule-driven "full-pipeline over all lines" mode triggered purely by `ScanType=FS` with no keyword rules — that signal vanishes under CheckType dispatch and needs an explicit flag**; (f) UI rule editor branches its form on the *job's* ScanType today → would branch on the *rule's* CheckType.

  **Recommendation (Claude, 2026-06-07):** **Tier 1 now, Tier 3 as the north star; avoid Tier 2.**
    - **Tier 1 (≈ framing 1) — CheckType-driven dispatch.** Worker runs every strategy whose supported CheckTypes appear in the job's active rules; `CheckType` is the discriminator (no new field needed); the config columns already coexist (just fill more of them). Unlocks the operator's exact example *when both tables share one connection*. Smallest, lowest-risk change; matches the additive codebase style. Costs: UI rework (rule editor goes CheckType-first; job config shows the source-config sections actually used), and two decisions — fate of `ScanType`-at-job (remove vs keep-as-default) and how to re-signal FS full-pipeline mode.
    - **Tier 2 — push source config onto `ScanCheckRule` (connection/folder/url per rule).** Unlocks different connections per rule BUT adds yet more nullable columns to `ScanCheckRule`, already the widest table and already flagged at the "normalize?" threshold during FileContent. **A trap** — spends schema churn without giving the clean model.
    - **Tier 3 (≈ framing 2) — `ScanSource` / `MonitoredService` sub-entity.** `MonitoredJob (1) → (N) ScanSource [typed + its own config] → (N) ScanCheckRule`. Job = the logical operational concept; sources = typed observation points (this DB on this connection, that folder, that API); rules = checks within a source. Conceptually correct; resolves the `ScanCheckRule`-width problem (per-type config moves to the source); lease/watermark/history/dashboard all key naturally to a source. Heavy: new table, move `ScanCheckRule.MonitoredJobId → ScanSourceId`, worker rewrite, a Sources UI tab, watermark re-key, data backfill (each existing job → one source).
    - Framing 3 (naming conventions / N jobs per process) is the current workaround the operator wants to escape — fragments the mental model, splits leases/failures/recommendations/views.

  **Decisions that pick the tier:** Is the real "two tables" case same connection or different? (same → Tier 1 suffices; different → go straight to Tier 3, don't pass through Tier 2.) Does one job ever need two databases / two log folders? Is "one business process = one monitored job" the intended operator mental model? When ready, resolve these, then plan the chosen tier end-to-end.

  **Phase (b) decisions (confirmed 2026-06-09, implement in the worker round):** `IScanStrategy.ScanAsync(MonitoredJob job, ScanSource source)` — job carries identity (`JobTypeId`/`MonitoredJobId`/`Name`), source carries the 8 config fields + its rules. Worker runs sources **sequentially** under the per-job lease, **one `ScanRunHistory` row per source** (`MonitoredJobId` + `ScanSourceId`). Source **exception** → best-effort (that source's row = Failed, continue); **timeout** (shared `jobCts`) → ends the tick, remaining sources get **no row** (history = actual executions only). Rolled-up lease outcome = worst wins (`Timeout > Failed > Success`), **no `Partial`**. Lease-duration claim stays `job.ScanTypeId`-based through (b)/(c); MAX-vs-SUM-vs-per-source-CTS decided in (d) (MAX is likely *wrong* under sequential execution — sum risk). Interface change is **atomic across all 4 strategies** → one lockstep step (no bridge), since only FileContent has unit tests.

- **Three of four scan strategies (FS, DB, ApiEndpoint) have no direct unit test coverage.** The FileContent strategy inherited investigation-first discipline that produced 15 test cases; the older three have only integration coverage via live-scan verification. If meaningful changes land on any of these strategies in the future, building unit tests at that point is harder than building them now would have been. Worth a focused round to add baseline strategy unit tests once architectural work settles.
- **Four bespoke drawers in the monitored-jobs flow (job-edit, scan-rule, class-rule, fix-rule) don't use the shared `DrawerComponent`.** Functional today; convert opportunistically when one needs significant changes anyway (not a side-effect of the Tier 2.5 structural migration).
- **`CheckType.StatusCode` / `CheckType.ResponseContains` are defined in the enum but consumed by no strategy.** `ApiEndpointScanStrategy` uses a hardcoded heuristic (non-2xx OR body contains error/exception/failed) and iterates no `ScanCheckRules`. So ApiEndpoint sources are rule-free by design. If configurable API checks are ever wanted (status-equals, body-contains, JSON-path predicate), implement those CheckTypes in the strategy first — then the Sources UI can show nested rules for API like the other types. Until then the UI shows an API source as URL + "no rules needed".

- **Operator identity is hardcoded `'operator'`** in approve/reject calls. Replace with real authenticated user when auth lands.
- **No authn/authz on any controller.** All endpoints are currently open. Operators, admins, and auditors should have distinct permission sets. Needs role-based authorization before production. `AuditLog.Actor` (and `OperatorAction.OperatorId`) should be wired to the real identity.
- **Analytics endpoint response shapes are inconsistent.** `failures-over-time` wraps its rows in `{ range, bucketSize, rangeStart, rangeEnd, buckets[] }`; the newer `failures-by-job` and `resolution-mix` return plain arrays. Flatten `failures-over-time` to a plain array next time it's touched — don't refactor purely for consistency.
- **No audit-viewing UI yet.** Audit data with no way to read it is invisible. Two surfaces likely: (a) a contextual tab on each config entity's detail view showing its own history; (b) a global `/audit` screen with cross-entity timeline + filters for when compliance asks. Defer until operators or auditors actually request it.
- **`DbFixHandler.HandleAsync` is a TODO stub returning `false`.** Surfaced during the JobTypeId fix — when a `DbFix`-category recommendation has no matching `FixPolicyRule`, execution falls through to this handler and silently fails. Either implement it or flip those failures to `ManualRequired` explicitly with a clearer message.
  - **The `{sourceId}`-in-WHERE write-guard is now SHIPPED for SqlScript fix payloads (2026-06-11)** — see the SqlScript write-guard decision below. When `DbFixHandler` proper is implemented it should reuse the same `ISqlFixScopeValidator`. `SourceId` is the natural key into the operator's table (invoice/order id) — what a fix's WHERE must target; `FailureId` is MAIA's internal PK and meaningless as a fix target (and deliberately does NOT satisfy the guard).
- **Frontend `monitored-jobs.service.ts` is GET-only** while the backend has full CRUD on `/api/config/monitored-jobs`. The config UI uses `config.service.ts`, so this isn't broken — just inconsistent. Consolidate or remove.
- **`ILogReader` / `FileLogReader` are no longer consumed.** `ClassifyJobsUseCase` was the only caller; it now classifies against `JobFailure.ErrorMessage`. The interface, implementation, and DI registration in `AddMaiaAI` can be removed once we're sure nothing external relies on them.
- **Sort UI on failures-list.** None today. URL hygiene reserves the slot but no UI is wired up.
- **Style budget warnings.** `features/dashboard/dashboard.component.ts` (~4.79kB vs 4kB budget) and `features/config/monitored-jobs/monitored-jobs.component.ts` (~4.34kB) exceed the Angular per-component SCSS budget. Pre-existing pattern in this codebase, not regressions from current work; raise the budget or extract shared styles.
- **No DB-level CHECK constraints for composite invariants.** The controller's `ValidateCompositePayload` is the only enforcer of "Composite header has null payload, steps cannot be Manual/Composite, step payload non-empty." Direct SQL INSERTs could bypass. Add CHECK constraints in a follow-up migration (mirrors the defense-in-depth rationale of the filtered unique indexes on `FixPolicyRules`).
- **`FixExecutionLog` has no retention worker.** Table sits at ~12k rows today and grows monotonically. Composite policies write one row per step; high-frequency drains amplify it further. Mirror `ScanHistoryRetentionWorker` with a configurable retention window (default 90d) when this becomes a noise / size problem.
- **Composite scan rules (deferred from FileContent v1).** Operator requested multi-step scan checks for both file-content (multi-predicate within one file) and DB scans (cross-table value checks). **The cross-table DB case is now served by `CheckType.SqlQuery`** (see the SqlQuery decision below) — the operator writes the JOIN/aggregation directly, so explicit composite UI is only needed if a concrete multi-predicate file-content case appears. v1 supports single-predicate / single-check rules; operators express compound checks via XPath compound conditions (XML), a JOIN in `SourceTable` (DB ColumnRange/ValueEquals), or a full SqlQuery. When concrete cases demand explicit composite UI, mirror the `FixPolicyRule` composite pattern: a parent `ScanCheckRule` with `ActionType=Composite` + a child `ScanCheckRuleStep` table (`StepId`, `RuleId`, `Locator`/`TargetColumn`, `PredicateType`, `PredicateValue`, `StepOrder`). All steps evaluated, AND logic, fires a single `JobFailure` on full match. Architecture supports it additively — single-predicate v1 rules keep working unchanged.
- **FileContent v2 backlog.** CSV/JSON/Excel extractors (add a `FileFormat` value + an `IFileContentExtractor` impl); content-hash watermarking (mtime is the v1 dedup key); per-rule failure-mode config (malformed-file behavior is fixed at "log Warning, skip" in v1); regex predicate type; streaming extraction for >5MB files; walk-once-apply-many is already implemented so multi-rule scans don't re-read files. Also: the `Severity` enum has no `Critical` value but the frontend `SEVERITIES` list offers it — surfaced during FileContent smoke test; either add `Critical` to the enum or drop it from the UI list.
- **Invalid locator / extraction failure visibility — SHIPPED (2026-06-09).** Both halves done. **(a)** `IFileContentExtractor.ValidateLocator(locator)` (XML impl compiles the XPath via `XPathExpression.Compile`) — `ConfigController` injects `IEnumerable<IFileContentExtractor>` and validates `ExtractorLocator`/`IdentifierLocator` at save, returning `400 {error:"InvalidLocator"}` on a malformed XPath (the `\\`-vs-`//` typo from the 2026-06-08 incident is now rejected at save). **(b)** new `PredicateUnevaluableSkips` counter (mirrors `IdentifierExtractionFailures`/`OversizeFileSkips`) incremented when a predicate is set but the locator yields no value, flowed `ScanResult → worker → ScanRunHistory` (migration `AddPredicateUnevaluableSkips`) and — since the prior two counters were DB-only — **all three are now exposed in `ScanRunDto`** (`GET /api/data/scan-runs`). The extractor still returns null on bad XPath at scan time (don't crash a scan over one rule) — but a bad locator can no longer be *saved*, and a valid-but-unmatched locator is now counted. Remaining visibility nicety (not built): a dedicated scan-run-history UI table to show these counts (the `/scan-runs` endpoint exposes them, but no frontend view consumes it yet).
- **Unconfigured surface — SHIPPED (2026-06-06), with one deferral.** The `/unconfigured` screen + dashboard tile now surface both coverage gaps (Case A unclassified clusters, Case B missing-policy). **Deferred:** Case B fix-policy creation goes via a deep-link into the existing Fix Options drawer, which does **not** thread suggestion provenance — so `FixPolicyRule.SuggestedBy`/`SuggestedFromHash` stay null for Case-B-created policies in v1 (the columns exist; Case A captures provenance fully). Wire provenance through the deep-link (query params → `fixRuleForm` → create request) when Case B gets a real suggester. Case A creates a JobType-level ClassificationRule which, under the new UNION classifier semantics, correctly applies to all jobs of that type; a "link to just this job" option remains a possible future enhancement for narrower scope.
- **Failure drawer composite step list re-fetches `FixPolicyRule.Steps` on every 5s poll tick** (per composite rec). Cheap today (clustered PK lookup, typically 1-3 composite recs per drawer-open), but the cost scales with rec count. If a failure ever has many composite recs, switch to a single batched fetch (one query for all distinct ruleIds on the failure) and cache on the rec object.

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
- **`ExecuteFixesUseCase` uses atomic per-recommendation claim** — `IRecommendationRepository.ClaimPendingAsync` does `UPDATE TOP(@batch) ... OUTPUT inserted.* WITH (READPAST, UPDLOCK, ROWLOCK)` against `AIRecommendations`, matching the lease-repo pattern. Concurrent drains (worker tick + approve endpoint + manual `/execute-fixes`) see disjoint sets — no double-execution. Claim is cleared by `MarkExecutedAsync` (success) or `ReleaseClaimAsync` (failure → eligible for retry after `ClaimTimeout` of 5min). Claim eligibility also requires `Failure.Status = 'Failed'`, which closes a pre-existing infinite-retry bug where a failed executor left the rec `!IsExecuted` and the next drain re-ran it indefinitely.
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
- **Multi-column dashboard rows collapse at the KPI breakpoint (≤1400px), not lower.** The target device is a 14" laptop (~1280–1366px CSS width). Three rows were collapsing at three different widths (KPIs ≤1400, analytics ≤1200, info panels ≤900), so on a laptop the KPIs wrapped but the analytics + panel rows stayed side-by-side and clipped on the right. Rule going forward: any side-by-side dashboard row stacks to full-width at the **same ≤1400px** breakpoint the KPIs use — so "KPIs wrapped" and "everything is full-width" happen together, never in between. Wider monitors keep multi-column. Secondary defense: flex rows that hold nowrap content get `min-width:0` + an ellipsis on the most-shrinkable child so they never overflow their card even above the breakpoint.
- **Dense config tables degrade by hiding non-essential columns, not by horizontal scroll alone.** The Classification Rules table (8 columns) gets `overflow-x:auto` as a never-clip safety net, but the real fix is a `.compact` density class plus media queries that drop the two lowest-value columns as width shrinks (**Confidence** ≤1500px, **Priority** ≤1280px). The essential triage columns — Pattern · Scope · Error Type · Status · Actions — stay visible on a laptop without forcing the operator to scroll sideways to reach the action buttons.
- **`.badge-muted` is per-component and MUST be overridden to a light fill.** There is no global `.badge-muted`; each screen that uses the class defines its own. The historical fallback (`var(--badge-muted-bg, #2a2f3a)`) renders a near-black chip on the light tables, which reads as broken. Convention: a "default/neutral" badge uses a light tint with dark text (Classification Rules' "Default · all {JobType}" → indigo `#e0e7ff`/`#3730a3`; Recommendations' Auto-run "No" → slate `#e2e8f0`/`#475569`). New screens copying a badge must pick a light fill, not inherit the dark default.
- **Dashboard stats "today" is server-local midnight, not UTC.** `DateTime.Today` (== `CAST(GETDATE() AS DATE)`). Matches the rest of the schema's local-time convention; an operator on Israel time sees their workday-local numbers, not a 7-hour-shifted view.
- **`NavigationHistoryService` tracks distinct *paths*, not URLs.** Query-param-only navigation (drawer ↑/↓, filter changes, pagination) doesn't shift the previous-route pointer. Without this guard, pressing ↓ once would hide the back button forever — the referrer would become `/failures?selected=124`, no longer a different page.
- **`NavigationHistoryService` is eagerly instantiated in `ShellComponent`.** Service has to be alive before the first `NavigationEnd` fires, otherwise the initial referrer is lost. `providedIn: 'root'` alone isn't enough — Angular instantiates lazily on first inject, and the first inject might happen after the first navigation. `ShellComponent` has a `private _navHistory = inject(NavigationHistoryService);` field purely for the side effect.
- **Smart-back label map is hand-maintained, not derived from `app.routes.ts`.** Only top-level menu destinations get labels (Dashboard, Failures, Recommendations, Operator Actions, Scan Jobs, the three Config screens). Other paths (e.g. the legacy `/failures/:id` redirect target) return null → back button hides rather than rendering a confusing label. Auto-derivation from routes was rejected — route paths aren't always human-friendly and the menu's display strings live in `side-menu.component.ts` anyway.
- **One `AuditLog` table for all event types, not a sibling `ConfigAuditLog`.** A second table was considered and rejected. Keeping a single timeline of "who did what when" — config edits, operator decisions on recommendations, system fix attempts — makes cross-cutting queries (e.g. "everything operator X did today") cheap and means a single audit-viewing UI later. The discriminator (`EntityType` + `EntityId`) costs two nullable columns; the alternative would cost a second table, a second repo, and a duplicated UI.
- **Existing column names kept (`EventType` / `Actor` / `Detail`), spec's `Action` / `PerformedBy` / `Details` rejected.** Original task spec proposed renames; the existing names are actually better (`EventType` covers non-user events; `Actor` includes "system"; `Detail` singular matches the column). Renaming would churn `RecommendationsController` + repository for cosmetic gain. The choice mattered enough to record: future config writers must use the existing field names.
- **`EventType` convention is `{EntityType}{ActionVerb}` PascalCase**, e.g. `FixPolicyUpdated`, `MonitoredJobDeleted`, `ClassificationRuleLinked`. EntityType + EventType are mildly redundant by design — the redundancy lets an auditor read just one column and still understand the event. Existing values `OperatorApproved` / `OperatorRejected` are grandfathered (don't mutate audit history). New writers MUST follow the compound form.
- **`AwaitingManualAction` requires `MarkExecutedAsync` even though no fix ran.** Counterintuitive but load-bearing. `ExecuteFixesUseCase.GetPendingAsync` returns recs where `!IsExecuted && (AutoFixAvailable || OperatorApproved)`. Without flagging the rec as Executed when it transitions to AwaitingManualAction, every drain tick re-processes it — accumulating noise `ManualActionRequired` audit rows + `FixExecutionLog` rows (verified bug: 4 dupes in 4 ticks during smoke testing before the fix). Semantic reading: `IsExecuted` means "this recommendation has been actioned, stop offering it to the drain" — operator acknowledgement of a Manual rec IS the action. The badge in the rec card reads "Acknowledged" not "Executed" for `(FixCategory=Manual, IsExecuted=true)` so the operator-facing label stays honest.
- **Stage pipeline frontend order array must match the template's `stages` array.** `failure-detail.component.ts.isStageCompleted` does `order.indexOf(key) < order.indexOf(current)` — if a stage key is missing from the order, its `indexOf` is `-1` and the comparison falsely returns true for any non-Failed current stage, marking that stage as "completed/past". Hit this when adding `Acknowledged`: until both the rendered `stages` list AND the order array contained it, a freshly-Recommended failure showed `Acknowledged` as a done step in the pipeline.
- **Stage pipeline derives from `Status` first, falls back to `isExecuted`.** Order matters: `Status=AwaitingManualAction` must take precedence over the `isExecuted → "Fixed"` fallback, otherwise acknowledged-Manual failures would render as Stage=Fixed even while the operator's work is pending. The `isExecuted` fallback is retained for legacy data where `Status` might be `Failed` but the rec ran successfully — first-write wins, but the new ordering protects the AwaitingManualAction semantic.
- **`FixPolicyRule` active-key uniqueness enforced at three layers, not one.** DB index + backend pre-flight + UI soft warning. Single-layer (DB-only) would surface as `2601 duplicate key` to the operator — opaque. Backend-only would let direct SQL writes break the invariant. UI-only would be trivially bypassed by a curl. Three layers each catch a different operator/script path: DB is the floor (defense in depth), backend produces actionable 409 JSON with `conflictingPolicyId` so the UI can pivot to edit-existing, UI surfaces the conflict before submission (warning is soft so disabled drafts still work). Indexes are filtered to `WHERE Enabled = 1` — disabled rows can duplicate freely, which is what lets operators stage a replacement (create disabled, disable old, enable new) without temporarily violating the constraint.
- **`FixPolicyRule` has a per-`MonitoredJob` override layer on top of the JobType default.** Nullable `MonitoredJobId` on the row. NULL = JobType-level default (applies to every MonitoredJob of `JobTypeId`); non-NULL = override scoped to one MonitoredJob that wins over the default for that (MonitoredJob, ErrorType) pair. Rejected the framed-as-needed cross-JobType case — the real problem operators hit is *within* a JobType (two DTSX jobs, same ErrorType, but only one should auto-heal). UX already implied per-job scope: the Fix Options drawer is opened from a specific job's tab, so making the row job-scoped matches what the operator expects. Lookup priority is identical at execution time (`SqlFixPolicyRepository`) and suggestion-generation time (`SqlFixCatalogueRepository`) so the rec's frozen `AutoFixAvailable` snapshot reflects the policy that would actually execute — drift between the two would manifest as "Auto-run" badge lying about what happens on approval. The recommendations-list projection (`SqlRecommendationRepository.GetPagedAsync`) was migrated to the same two-layer join — verified EF translates cleanly without N+1 or in-memory fallback.
- **`FixPolicyRule.MonitoredJobId` FK uses `OnDelete(Restrict)` even though MonitoredJob deletes are soft today.** Defensive declaration: if a hard-delete is ever introduced, SQL surfaces a clean error before silently erasing operator overrides; the controller layer can then translate to a 409 with "this job has N active overrides — disable them first." With today's soft-delete (`IsActive=false` on MonitoredJob), the FK never fires — chose **sub-option (a)**: overrides stay dormant on the deactivated job, automatically reactivate if the job is reactivated. Mirrors how the codebase already treats `ErrorType` / `ScanCheckRule` soft-deletes (the related rows aren't touched). Operator-facing surface: `monitored-jobs.component.ts` `deleteJob` discloses the count of active overrides in the confirmation prompt, so the operator isn't surprised when reactivation restores fix behavior.
- **Override rows are keyed on `(MonitoredJobId, ErrorTypeId)` for the lookup — `JobTypeId` on an override row is informational.** The override unique index doesn't include `JobTypeId` (filtered on `MonitoredJobId IS NOT NULL`); the repo's override-layer query doesn't filter on it either. Practical consequence: the override's `JobTypeId` column always equals `MonitoredJob.JobTypeId` for the override's job (the controller fills it in), but lookup correctness doesn't depend on that. A test (`GetForAsync_OverrideIgnoresJobTypeId`) pins this — if the column ever gets out of sync due to a bug, the override still wins for its MonitoredJob.
- **Existing `AiRecommendation.AutoFixAvailable` snapshots stay frozen across the override change.** Snapshot integrity reaffirmed for the override case: toggling, creating, or deleting an override never mutates rows in `AiRecommendations`. The `RecommendationListItem` projection's `policyIsAutoHealEligible` reflects live state (override-then-default); `rec.AutoFixAvailable` reflects the snapshot at suggestion time. Two distinct concepts in the UI: read-only "Auto-run" badge (frozen snapshot) + interactive auto-heal toggle (live policy). The auto-heal toggle on the Recommendations screen now preserves `monitoredJobId` through the two-step fetch-then-PUT, so flipping the toggle on an override-backed rec edits the override (not the default).
- **Soft-deletes use the `Deleted` verb, not `Deactivated` or `SoftDeleted`.** `ErrorType`, `ScanCheckRule`, `FixPolicyRule` all flip `IsActive` / `Enabled` to `false` rather than removing the row (RESTRICT FKs make a hard DELETE unsafe). The `EventType` is still `ErrorTypeDeleted` etc. — the `Detail` text uses "Soft-deleted X" so an auditor reading the row knows the row is still in the DB; the EventType keeps the auditor's mental model simple (one verb per intent, regardless of implementation). `ClassificationRule` is the lone hard-delete; same `Deleted` verb.
- **Update audits emit `Detail = "No changes"` when nothing changed.** Auditor still sees evidence the PUT was attempted (operator clicked Save on an unchanged form, or sent an idempotent retry). Cheaper to surface than to elide — eliding would make the absence of an audit row mean either "no PUT happened" or "PUT happened but nothing changed", which is ambiguous in incident review.
- **`FailureId` is nullable so config audits fit the same table.** Config changes have no associated `JobFailure`. The FK cascade behavior is unchanged for rows that *do* set `FailureId` — it just doesn't apply to rows where it's null. EF Core infers an optional relationship automatically.
- **Audit-write failures log + swallow, never fail the request.** Wrapped in `try/catch` in `WriteAuditAsync`; on exception, `ILogger.LogError` surfaces the failure to ops monitoring and the method returns normally. The primary operation already succeeded by then — rolling back a successful config change because the audit row didn't write would be worse for users than degraded audit. If reliability ever becomes a concern, an outbox pattern fits cleanly on top.
- **Update audits use a manual before-snapshot, not EF Core's `PropertyEntry.OriginalValue`.** The existing PUT handlers do tracked reads (`FindAsync`) then mutate the entity in place. We capture each property into a local before mutation, save, then diff against the now-saved entity. `EntityEntry.OriginalValue` would also work but requires accessing the change tracker before/after `SaveChangesAsync` (state changes during save). Local snapshot is clearer to read and faster to maintain.
- **Link / unlink events log against the host entity, not the relationship.** `ClassificationRuleLinked` and `ClassificationRuleUnlinked` use `EntityType="MonitoredJob"` and `EntityId=<jobId>`, so an auditor querying "everything that happened to job X" sees the link history alongside the job's own edits. The detail string still names the rule id, so the join-target stays discoverable.
- **`operatorId` required on every write, hardcoded `'operator'` from the frontend constant.** Same pattern as `RecommendationsController`. Backend rejects missing/empty `operatorId` with 400. `ConfigService.actor = 'operator'` is the single point of change for when authn lands. Frontend gets ergonomics (callers don't think about operatorId), backend gets the audit contract (every row has an actor).
- **Audit-viewing UI deferred.** The schema can carry the data; until compliance or operators ask, no surface is wired up. Two likely paths when needed: (a) per-entity contextual tab on each config detail view; (b) global `/audit` screen with cross-entity timeline + filters. The new `EntityType` / `EntityId` columns are indexed-friendly for either approach.
- **Logging: Serilog over NReco / built-in / Microsoft.Extensions.Logging.File.** The literal Microsoft.Extensions.Logging.File package exists but never went stable (abandoned 2018-era preview). NReco.Logging.File is the lightweight equivalent. Serilog won because: (a) one NuGet (`Serilog.AspNetCore`) brings console + file + configuration as transitive deps; (b) sink expansion is later just an `appsettings.json` edit (Seq, OpenSearch, Slack, …) — no code change; (c) structured logging is available if/when needed without re-platforming.
- **Serilog config lives in `appsettings.json`, not code.** `Program.cs` only wires `UseSerilog(...)` and calls `ReadFrom.Configuration(...)`. Everything else — sinks, rolling policy, retention, level overrides, output template — is declarative. Ops can swap rolling policy, raise verbosity for an incident, or add a sink without a rebuild. Trade-off accepted: the config has the canonical knowledge, not a constructor.
- **Microsoft framework log levels filtered to `Warning`, not `Information`.** `Microsoft.AspNetCore` chats per-request at Information; that's pure noise in a file you'll want to grep for actual problems. Application code (`MaiaAI.*`, controllers, use cases, workers) stays at `Information`. Inverted defaults compared to most templates — deliberate.
- **Log file path is relative to the working directory** of `dotnet run`, not the binary location. In dev that means the AIEngineAPI folder; in production it'll need an absolute path or volume mount via env-var override (`Serilog__WriteTo__1__Args__path=/var/log/maia/maia-api-.log`). Not a problem today; documenting so the deployment shim is obvious later.
- **Composite is an `ActionType` value, not a separate flag on `FixPolicyRule`.** Considered `IsComposite bool` + `ActionType` on the header. Rejected — the executor dispatch table is already keyed on `ActionType`, so adding a sibling boolean would split the truth across two columns and force every consumer to check both. With `ActionType=Composite` as a sentinel, the header's `ActionPayload` MUST be null (controller validates) and steps live in `FixPolicyRuleSteps`. The dispatch table stays single-keyed; `DefaultFixEngine`'s switch already had one branch per `ActionType` value and gained one more.
- **Composite execution lives inline in `DefaultFixEngine`, not in a separate `CompositeExecutor`.** Considered making `CompositeExecutor` an `IFixActionExecutor` and dispatching from the engine. Rejected because `IFixActionExecutor.ExecuteAsync(string? payload, ...)` would have to reload the policy to access `Steps` — wasteful, since `DefaultFixEngine` just loaded it via `IFixPolicyRepository.GetForAsync`. Inlining the composite branch in the engine keeps the policy in scope and means both call paths (auto-heal drain via `ExecuteFixesUseCase` and synchronous approve via `RecommendationsController.Approve → ExecuteFixesUseCase`) get composite for free with zero ceremony.
- **Composite steps are best-effort, no abort-on-first-failure flag.** Considered per-step `ContinueOnFailure` boolean. Rejected because aborting partway doesn't undo what already ran — if step 1 updated a DB row and step 2 fails, step 1's mutation is still there. Aborting and continuing produce the same partial state; continuing additionally maximises the chance that subsequent independent steps succeed. Decision: every step runs, any step failure → `FixOutcome.Failed` → `ManualRequired`, per-step `FixExecutionLog` row tells the operator exactly what completed vs what needs manual cleanup. The only case where abort genuinely matters is "step N+1 reads what step N wrote" — defer until a real case demands it; the `RequiresPriorSuccess` flag is a one-column add.
- **Per-step `FixExecutionLog` rows alongside the existing summary row from `ExecuteFixesUseCase`.** Two layers of log: N per-step rows written from `DefaultFixEngine.ExecuteCompositeAsync` (each carries the step's `ActionType` and Description, `ExecutedBy = "DefaultFixEngine.Composite"`, `Success` per step) plus the existing 1 summary row written from `ExecuteFixesUseCase` (overall outcome). An auditor scanning logs sees the whole story; an operator reading the rec card's execution history sees both granular and rolled-up views.
- **Steps cannot be `Manual` or `Composite` — no nesting, no human steps inside an automated chain.** Manual-as-step makes no sense (the composite IS by definition automated; if any step needs human action, the policy itself should be Manual at the header level). Composite-as-step would create a tree of fixes with arbitrary depth — operationally ungovernable, hard to audit, and zero current use cases. Controller rejects both with specific error codes (`ManualStepForbidden`, `NestedCompositeForbidden`) so the UI can show targeted messages.
- **Step `(RuleId, StepOrder)` uniqueness is unfiltered, NOT scoped to `Enabled` like `FixPolicyRules`'s active-key.** Steps need a stable order regardless of whether their parent rule is enabled — operators editing a disabled draft policy still expect ordered steps. The unique index just guarantees every step within a rule has a distinct order. Gaps in operator input (step orders 1, 3, 7) are accepted; the controller renumbers to 1..N before persist. Spares the UI from having to repack after each delete.
- **Step update is replace-all, not diff-then-patch.** Considered diffing step lists by `StepId` to issue targeted INSERT/UPDATE/DELETE statements. Rejected — steps are small (typically 2-5 per policy), the FK cascade handles orphan deletes, and replace-all is far simpler to read + audit. The cost is a slightly noisier audit (`Steps: 1:SqlScript → 2:CopyFile  →  1:CopyFile → 2:SqlScript → 3:Script` instead of a per-step diff), accepted because step changes are rare and the operator just saw what they edited.
- **`JobFailure` has TWO file-path columns: `SourceLogPath` (existing) AND `SourceFilePath` (new).** Considered overloading `SourceLogPath` to mean either depending on scan type. Rejected — log path and input file path are different semantics that consumers must distinguish. FS scan: `SourceLogPath = "C:\logs\app.log"` (where the error appeared), `SourceFilePath = "C:\input\data.txt"` (what the process was reading when it failed). DB scan: `SourceLogPath = "db://Conn/Table"`, `SourceFilePath` from `ScanCheckRule.FilePathColumn`. Each has a dedicated `{sourceLogPath}` / `{sourceFilePath}` placeholder. `SourceFilePath IS NULL` cleanly means "no file path was captured — configure InputPathPattern (FS) or FilePathColumn (DB)" rather than ambiguity about which-is-which.
- **`SourceFilePath` is captured at scan time, not looked up at fix time.** Considered `{col:X}` runtime-lookup syntax inside the fix payload — would let `CopyFileExecutor` query the source DB row at execution time for the file path. Rejected because (a) gives `CopyFileExecutor` a DB dependency it shouldn't carry, (b) the source row may have changed by fix time (matches the snapshot model on the rec itself — frozen values from generation time, live policy values from the projection), (c) duplicates the capture work the scan strategy already does for `SourceId`. Capture-once at scan time gives a stable snapshot; fix executors are pure file/SQL/HTTP operations against captured data.
- **`InputPathPattern` is full regex with capture group, NOT the wildcard-substring DSL used for classification patterns.** Classification patterns use `*` as a wildcard with everything else literal (no capture groups by design — they just match). Input-path extraction needs capture groups, so it must be real regex. UX consequence: the two fields look similar but the operator must know they speak different DSLs. The drawer's field-hint copy spells this out explicitly — "Differs from classification patterns — full regex applies here, *not* the `*`-wildcard shorthand."
- **`InputPathPattern` regex match is hard-capped at 50ms** via `Regex` timeout. Same hardening `RuleBasedClassifier` already uses for classification patterns. A pathological pattern that runs away during a scan blocks the worker tick; 50ms is enough for any reasonable extraction and short enough that a runaway pattern fails fast. Invalid regexes (config bug) are swallowed at compile time and `SourceFilePath` simply stays null — logged at Info so operators can grep.
- **Stable placeholders only: no `{timestamp}` / `{guid}` / anything resolved at execution time.** Composite-step coordination relies on "step 2 writes `{sourceFilePath}|{sourceId}.txt`, step 3 reads `{sourceFilePath}|{sourceId}.txt`" — both steps resolve the placeholders to identical values because they're stable per failure. A `{timestamp}` token would resolve to different microseconds in each step, breaking the implicit handoff. Spec'd as a v2 problem to solve if operators demand it (would need a "resolve once per composite execution, share across steps" mechanism).
- **`CopyFileExecutor` is atomic via `.tmp` + rename, overwrites by default, no skip-if-exists option.** `File.Copy` to `<dest>.tmp`, then `File.Move` to final name. Prevents readers seeing a half-written file mid-copy. Overwrite is the default because composite fixes are idempotent by design — re-approving the same rec should produce the same final state, not an "already exists" failure. UNC supported natively; NTFS permissions are the failure mode (the executor surfaces them as Error-level logs with the specific path that failed).
- **`PlaceholderResolver` has two methods: `ResolveAsync` (non-strict) and `ResolveOrThrowAsync(template, required[])` (strict on named placeholders).** Considered a single `strict: bool` parameter. Rejected — different callers need different placeholders enforced. `CopyFileExecutor` requires `{sourceFilePath}` to be non-empty on the SOURCE half but doesn't care about the DEST half. The two-method signature lets each caller declare exactly which placeholders are non-negotiable, while the rest fall back to empty-string substitution. The thrown exception (`PlaceholderUnresolvedException`) carries the specific placeholder name AND a targeted error message (for `{sourceFilePath}`: "configure InputPathPattern on the FS scan rule, or FilePathColumn on the DB scan rule, for this job"). Cheap operator-actionable feedback at the place the misconfiguration hurts.
- **`ConfigController.ValidateCompositePayload` enforces 8 distinct rules with specific error codes.** Each rule returns a `400 {error: "<code>", message: "..."}`. Codes are stable for the UI to switch on later (today the UI just renders the message). The eight: `CompositeRequiresSteps`, `CompositePayloadConflict`, `NonCompositeWithSteps`, `NestedCompositeForbidden`, `ManualStepForbidden`, `UnknownStepActionType`, `DuplicateStepOrder`, `StepPayloadRequired`. Validation happens BEFORE the duplicate-active 409 check, so a malformed composite payload doesn't accidentally surface as a 409 with the wrong story.
- **Composite recs in the list view show a `Composite · N steps` badge, not an inline expandable step list.** Considered a click-to-expand sub-row with the full step list. Rejected for v1 — the rec table is dense and adding a sub-row is heavy CSS work (existing tbody rendering doesn't have a pattern for sub-rows). The badge tells the operator "this approval triggers N actions" so they're not surprised; full step details are one click away in the Fix Options config drawer (which already loads `Steps`). A future revisit could add inline expansion when the rec card pattern lands (per the deferred follow-up in this file).
- **`FixPolicyRuleSteps.RuleId` FK is `OnDelete(Cascade)`, NOT `Restrict`.** Different from `FixPolicyRule.MonitoredJobId` which uses `Restrict`. Defaults + overrides are independent records that can outlive each other (a default policy survives even if the override for one job is deleted). Steps cannot exist without their parent rule — they ARE the rule's body. Deleting the rule should obliterate its steps, not block on them. Cascade is the right cascade behaviour here.
- **`ScanCheckRule.FilePathColumn` supports a dotted `alias.Column` form for joined-table cases, but v1 does NOT auto-JOIN.** If the path lives on a related table, operators put the JOIN into `SourceTable` directly (it's already SQL-shaped) and reference the column as `j.FilePath`. The strategy brackets only the column portion in the SELECT (`j.[FilePath]`), so the alias resolves naturally. Auto-join detection would require parsing the column reference and inferring relationships — not worth the complexity when operators can author the JOIN themselves.
- **Switching Execution Type to/from `Composite` auto-cleans the inappropriate-for-the-new-type field on the form.** The Fix Options drawer's `setFixRuleActionType` handler nulls out `actionPayload` when switching TO Composite (header must have null payload) and empties the `steps` array when switching FROM Composite (single-action rules cannot carry steps). Without this, editing an existing `SqlScript` policy and flipping it to Composite kept the stale SQL string on the header, tripping the backend's `CompositePayloadConflict` 400. Same logic prevents the inverse (`NonCompositeWithSteps`).
- **Non-409 save errors surface as a red banner in the Fix Options drawer footer**, not silent fall-throughs. The original error handler only branched on `409 DuplicateFixPolicy`; every other status (including all 400 validation codes from `ValidateCompositePayload`) silently reset the Save button with no operator feedback. New `fixRuleSaveError` signal renders the server's `message` text in a `.dup-warn.save-error` styled block above the footer. Cleared on drawer reopen and on every save attempt.
- **`/api/data/failures/{id}/status` does its own override-then-default policy lookup**, separate from `SqlRecommendationRepository.GetPagedAsync`. Reason: the failure-detail endpoint builds recommendation DTOs inline (not via `RecommendationDto.From`) because it carries failure-specific fields the paged DTO doesn't. New `BuildPolicyInfoAsync` does ONE batched query (all distinct ErrorTypeIds for the failure's recs at once), picks the winner per ErrorTypeId in memory using identical priority (`MonitoredJobId != null` then `ActionTimestamp DESC`), and projects `FixPolicyRuleId` + `PolicyIsAutoHealEligible` + `PolicyStepCount` onto each rec. The duplication of lookup logic is acceptable — both implementations must change in lockstep if priority semantics ever evolve.
- **Composite step list on the rec card shows description-only bullets, NOT the raw payloads.** Operators read the human summary; the SQL / scripts / URLs live in the Fix Options config drawer where they're edited. Fall-back text `"Step N (ActionType)"` when description is empty so the bullet is never blank. The choice is symmetric with the Recommendations table's rollback to compact (no inline composite badge) — operators get rich info in the drawer (where they decide) and lean info in the table (where they scan).
- **Recommendations TABLE row stays single-line; no composite badge.** Briefly added a `Composite · N steps` badge next to the `suggestedAction` cell; reverted because it expanded composite rows to two lines and made the table inconsistent. The drawer (where the operator actually reviews) carries the badge + step list; the table is for scanning and stays compact.
- **Failure drawer composite step list lazy-fetches policy on drawer load + every poll tick**, cached per `ruleId` in a `Map` signal. Failed fetches are silent (badge renders without the list) — surfacing a banner for an unimportant background fetch would be noise. Re-fetching on every 5s poll keeps the step list in sync if the operator edits the policy in another tab; cache key is `ruleId` so already-fetched policies don't re-query within a single drawer session.
- **Recommendation atomic claim modelled on the lease-repo pattern, not a transaction-wrapped exec.** Considered serializing executor work inside a `BEGIN TRAN` with `WITH (UPDLOCK, HOLDLOCK)` on the rec row. Rejected — the DB connection sits idle through the executor work (could be 60s for a SQL step, 120s for a script), so concurrent operators contend on DB connections, not just on the row. Atomic claim + release-on-failure decouples the claim from the work duration; the claim is a 1ms `UPDATE TOP(N)`, the work runs without holding any DB lock. Same shape as `SqlMonitoredJobLeaseRepository`'s claim — operators reading either repo see one consistent idiom.
- **Claim timeout is 5 minutes, hard-coded in `ExecuteFixesUseCase`.** Tradeoff: long enough that any reasonable executor completes within it (the longest is `Script` at 120s); short enough that a crashed worker doesn't strand a rec for hours. Could be config later but no operator demand yet. The claim row carries `ClaimedBy` (`host;pid;runId`, matching lease shape) so an auditor scanning stuck claims can see whose process owns it.
- **`ReleaseClaimAsync` called on failure path is technically redundant with claim-expiry, but explicit.** A crashed worker would eventually have its claim stolen after 5min; the explicit release just lets the next drain pick up the rec immediately without waiting. The eligibility filter on `Failure.Status = 'Failed'` excludes failures already moved to `ManualRequired` (by the same use case, on the failed-fix path) so the released rec doesn't infinite-loop — exactly the bug the status filter closes.
- **`MarkExecutedAsync` switched from EF tracked-load to `ExecuteUpdateAsync`** so the IsExecuted set + claim clear happen in one SQL roundtrip. Same change for `ReleaseClaimAsync`. The old `Find + SaveChanges` pattern was two roundtrips per rec; with the new batched drain (up to 50 recs), the savings matter.
- **Per-executor `CancellationToken` honoured uniformly via `ExecutorTimeouts.LinkedWithTimeout`.** Centralised constants (Default = 60s, Script = 120s) and a helper that builds the linked-CTS-with-timeout in one line. Every executor wraps its call site in `using var cts = ExecutorTimeouts.LinkedWithTimeout(ct, ExecutorTimeouts.Default);` and the inner Async call gets `cts.Token`. Catches `OperationCanceledException` with a guard `when (cts.IsCancellationRequested && !ct.IsCancellationRequested)` to distinguish "step timeout fired" from "outer cancellation requested" — different log levels (Warning vs the swallow-and-rethrow pattern).
- **`CopyFileExecutor` uses async stream copy, not `File.Copy`, so cancellation propagates per-chunk.** `File.Copy` is synchronous and ignores `CancellationToken` — a 5GB copy runs to completion regardless of timeout. `FileStream.CopyToAsync(stream, ct)` cancels at the next 81KB buffer boundary, so a per-step timeout actually interrupts the copy. `.tmp` cleanup happens in both the timeout-catch and the general-exception-catch via a small `TryCleanupTmp` helper.
- **`ApiCallExecutor` finally moved to `IPlaceholderResolver`.** Was the last executor still doing inline `string.Replace("{failureId}", ...)`. Now gets the full `{sourceId}` / `{sourceFilePath}` / `{jobFolder}` / `{inputFolder}` token set for free. Composite chains can now mix CopyFile + ApiCall + SqlScript steps that all reference the same `{sourceFilePath}` consistently.
- **"Failed to Execute" marker is Status-agnostic, but the "Fix Failures Today" KPI + drill-down are NOT.** Two surfaces, slightly different predicates by design. The KPI counts (and the `view=fix-failed` filter returns) only failures currently in `ManualRequired` — these are the rows the operator should triage right now. The per-row marker on the failures list fires for ANY row with a `Success=false` `FixExecutionLog` since today-midnight, regardless of current status. Why the asymmetry: a row may be `Failed` momentarily between an executor failing and `UpdateStatusAsync(ManualRequired)` committing (or just after, before the polled UI refreshes). Surfacing the marker on those rows tells the operator "this row had a fix failure" even when the status transition hasn't propagated. The KPI stays on `ManualRequired` only because operators want a stable, actionable count — not a momentarily-double-counting one. Tests pin both behaviours (`GetPagedAsync_FixFailedView_OnlyReturnsManualRequiredWithRecentFailedLog` + `GetIdsWithRecentFixFailureAsync_ReturnsOnlyIdsWithFailedLogSinceCutoff`).
- **FS-scan filename matching uses the same wildcard DSL as classification rules**, NOT `Directory.GetFiles`'s native glob. Lives in `Core/Scanning/FilenamePattern.cs`; reused by `FileSystemScanStrategy` (keyword-mode file enumeration) and `Application/Pipeline/DirectoryPipelineUseCase` (full-pipeline-mode). DSL: `*` is the ONLY wildcard, every other character is literal (`.`, `?`, `[`, `+` all match themselves), no-`*` patterns are case-insensitive SUBSTRING match, matching is case-insensitive cross-platform, empty/whitespace pattern returns false. Closes four bugs the previous `Directory.GetFiles(folder, pattern)` had: (a) no-`*` patterns were exact-filename match instead of substring, so `WARNING` matched only a file literally named "WARNING" not "log_WARNING.txt"; (b) `?` was accepted as single-char wildcard, diverging from classification-rule semantics; (c) `*` alone hit the Win32 legacy quirk where it matched only files with no extension; (d) case-sensitivity was OS-dependent (Windows insensitive, Linux/macOS sensitive). `Path.GetFileName(...)` is matched (never full path). 50ms regex timeout matches the classification-pattern hardening. Deliberately NOT supported (v2 conversation): `?`, `[abc]`, `**`, regex-as-mode. `FilenamePattern` lives in `Core/` (not `Infrastructure/`) so the Application-layer `DirectoryPipelineUseCase` can reach it without an upward dependency.
- **Drawer shell extracted to a shared `DrawerComponent` (`shared/drawer/`), consumed by three screens.** The failures-list drawer's generic chrome (backdrop + click-outside, 760px slide-in, smart back button, ✕, Esc-to-close) moved into one component with `[drawer-title]` / `[drawer-controls]` / body projection slots. Host-specific concerns stay in each consumer: failures keeps ↑/↓ row nav + page-boundary auto-load + nav toast + `?selected` URL; recommendations adds within-page ↑/↓ + `?selected`; dashboard uses a plain local signal. Esc-to-close lives in the drawer (hosts only handle arrows). The earlier "drawer lives only on /failures" decision was reversed once a reusable shell made in-place drawers cheap on every screen.
- **Recommendations + dashboard open the failure detail in-place, not by routing to `/failures`.** Recommendations uses `?selected=` on its own route (refresh-safe/shareable, mirrors failures). Dashboard uses a **local** `selectedFailureId` signal, NOT a query param — it's a live-polling overview, not a deep-link surface, so URL plumbing would be overkill. Both reuse `<app-failure-detail>`, so execution history / per-step ✓✗ / Retry Fix come along for free.
- **Fix-execution history is grouped into attempts; the failure banner counts only the LATEST attempt.** The flat `FixExecutionLog` rows split into cycles (composite step rows terminated by the `ExecuteFixesUseCase` summary row; a Retry opens a new cycle). The drawer banner reports "N of M actions in the latest attempt", not the lifetime total across retries (which read as a misleading "22 of 28"). History is collapsed by default (the rec card already shows the latest per-step ✓/✗) and ordered newest-attempt-first.
- **Retry re-arms from `ManualRequired` rather than relaxing the drain guard.** `POST /api/recommendations/{id}/retry` flips the failure back to `Failed`, clears `IsExecuted`/claim, sets `OperatorApproved=true`, then drains synchronously — so the fix re-runs with whatever policy is configured NOW. The drain's `Failure.Status='Failed'` claim guard (the infinite-retry fix) is left intact; Retry is the explicit, audited (`FixRetried`) operator override that re-qualifies the rec.
- **Fix↔classification connection is surfaced as UI clarity, not a new constraint.** Fixes are keyed to ErrorType (never the classification rule); multiple rules → one ErrorType → one fix, and the only blocking constraint that matters (one enabled policy per layer per ErrorType) already exists at three layers. So instead of new blocks: a classification-rule picker (pick a symptom → sets ErrorType), a reachability warning (fix for an ErrorType no rule produces for the job won't fire), a "Covers N rules" fan-in line, and Override·active / Default·shadowed badges. All computed from the job's effective classifier rules (linked rules, else JobType globals — mirrors `GetEffectiveRulesAsync`). To get *different* fixes per symptom, split into distinct ErrorTypes.
- **`{sourceFileName}` is derived, not stored.** `Path.GetFileName({sourceFilePath})` — empty when no path was captured. No new column; it's a pure projection of the existing `{sourceFilePath}` so a CopyFile dest can reuse the original name.
- **Classification is UNION(linked, JobType-global), linked-beats-global — NOT linked-only.** The prior `GetEffectiveRulesAsync` returned linked rules *exclusively* when a job had any, silently disabling JobType globals for that job. That was a misunderstood implementation detail, not a feature: it broke the intuitive "a JobType rule applies to all jobs of that type" expectation (and `/unconfigured` Case A, which creates exactly such rules). Now: linked rules (by Priority) ++ JobType globals not already linked (by Priority), deduped; the classifier's first-match-wins over that order yields linked-beats-global (same precedence shape as FixPolicyRule override→default). Sweep before committing showed this newly applies ~15-17 globals to the JT1 jobs — accepted as additive coverage (globals only fire where no linked rule matched; substring matching means they only hit where the text actually contains the pattern).
- **ClassificationRule matching lives in `Core/Classification/ClassificationMatcher` and is whitespace-tolerant.** Extracted from `RuleBasedClassifier` (mirrors how `FilenamePattern` factors out the FS DSL) so it's unit-testable + reusable. Collapses whitespace runs to a single space on both line and pattern before matching — logs carry irregular spacing while the n-gram analyzer emits single-spaced patterns, so without this a correct suggestion fails to match its own source. The fix is the matcher (not the analyzer) because that makes the whole class of whitespace mismatch go away, for hand-written rules too. Strictly more permissive on whitespace; no existing match regresses.
- **ClassificationRule uniqueness key is `(JobTypeId, Pattern)`, not `(…, MonitoredJobId)`.** The entity has no `MonitoredJobId` — JobType-scoped, with per-job association via the `MonitoredJobRules` M:N link. So the 3-layer dup guard (filtered unique index + backend 409 + UI) keys on `(JobTypeId, Pattern) WHERE IsActive=1`. Case-insensitive collation matches the case-insensitive matcher. Mandatory because the retry-on-no-effect pattern (network blip, double-click, UI lag, or — as actually happened — a rule that silently doesn't fire) otherwise piles up invisible duplicates that obscure classification state.
- **Unconfigured cluster analysis is normalize-then-grep, behind an interface seam.** N-gram frequency over `MessageNormalizer`-cleaned text — not ML. Normalization is a precondition, not optional: raw `ErrorMessage` carries the scan prefix `[kw] file:`, an embedded timestamp, and varying ids/GUIDs that otherwise dominate frequency counts and bury the signal. Stages are individually public/testable; GUID collapse MUST precede digit-run collapse (a GUID contains 4+ digit runs — collapsing digits first shreds it). Greedy set-cover by `df × n` yields non-overlapping clusters and leaves single-occurrence noise uncategorized. `IUnconfiguredClusterAnalyzer` is the v2 swap-point (embedding/LLM) — same DI idiom as `IFixActionExecutor`; v1 leaves `ConfidenceScore` null rather than faking a number.
- **The "Unconfigured" KPI counts raw failures, not clusters; the screen clusters on-demand.** Running the analyzer on every 5s dashboard poll would be wasteful and the honest operator number is "27 failures unhandled," not "5 patterns." So the tile stays a cheap COUNT (failure-based) and only the `/unconfigured` screen runs the analyzer (on load / window-toggle). The tile's drill-down was repointed from `/failures?view=unconfigured` to the new screen (the `view=unconfigured` failures filter still exists as a valid direct link).
- **Case B "Configure" deep-links into the existing Fix Options drawer, not a rebuilt one.** The FixPolicyRule drawer is the complex one (scope radio, composite step editor); Case B is low-volume. So `/unconfigured` navigates `/config/monitored-jobs?fixForJob=&errorTypeId=`, and `MonitoredJobsComponent` reads those params once on load to expand the job, open its Fix tab, and pop a pre-filled new-fix drawer (then clears the params). Trade-off: provenance isn't captured on Case-B-created policies in v1 (documented in follow-ups).
- **Suggestion provenance is CREATE-only and reproducible.** `SuggestedBy`/`SuggestedFromHash`/`SuggestedConfidence` are set only when a rule is accepted from a suggestion; update paths never touch them. The hash (SHA-256 of sorted sample failure ids, first 16 hex) is computed by a shared `ClusterHash.Of(...)` so the same cluster membership yields the same hash across code paths and v2 analyzers — the seam that lets v2 ML/LLM learn from "operator accepted/modified this suggestion."
- **Case B (`/unconfigured` policy-gaps) counts OPEN failures only — `Status == Failed`.** `GetPolicyGaps` originally had no status filter (unlike Case A's `Status == Failed && ErrorTypeId == null`), so a *resolved* classified failure with no fix policy still showed as a gap — and marking it resolved didn't clear it (2026-06-08 incident: a leftover smoke-test failure 1622 `invoice-error.xml`/FileNotFound lingered after the job was repurposed DTSX→Exe). A policy gap is by definition something still needing configuration; Resolved / AwaitingManualAction / ManualRequired failures have already been actioned. Fix = add `x.f.Status == JobStatus.Failed` to the join filter, mirroring Case A. Two adjacent gotchas this surfaced (both by-design, not fixed): **classification is a sticky one-time label** (deleting a ClassificationRule never reclassifies existing failures), and **`JobFailure.JobTypeId` is a creation-time snapshot** (so Case B can show a stale JobType, e.g. DTSX, for a job later switched to Exe). Regression test `PolicyGapStatusTests` pins "resolved failures don't appear as gaps"; testing the controller directly required adding an `AIEngineApi` ProjectReference + `Microsoft.AspNetCore.App` FrameworkReference to the test project (first controller-level test).

## FileContent scan (2026-06-07)

- **`FileContentScanStrategy` is the 4th `IScanStrategy`, added purely additively.** No orchestrator change: `MonitoringWorker` resolves `strategies.FirstOrDefault(s => s.ScanType == job.ScanType)` over `GetServices<IScanStrategy>()`, with zero hard-coded scan-type lists. New strategy = new enum value + `ScanTypes` seed row + one DI line. **Load-bearing detail:** `MonitoredJob.ScanType` is `Enum.Parse<ScanType>(ScanTypeDefinition.Name)`, so the enum member name must equal the seed `Name` **exactly** (`"FileContent"`); the enum int (`3`) and the `ScanTypeId` PK (`4`) intentionally differ (same offset the existing types already have).
- **Extractor plug-in, not a switch.** `IFileContentExtractor` (Core/Interfaces) mirrors `IFixActionExecutor`: `Format` + `ExtractAsync(filePath, locator) → string?`, registered as `IEnumerable<>`, dispatched by `ExtractorType` via a `Dictionary<FileFormat,…>` built at construction. v2 formats (CSV/JSON/Excel) add an enum value + an impl, nothing else.
- **Dual single-string locators on `ScanCheckRule`, not a JSON blob.** `ExtractorLocator` (value to test) and `IdentifierLocator` (natural key → SourceId) are plain `nvarchar(500)` columns whose grammar the chosen extractor owns (XPath for XML). Rejected a `nvarchar(max)` JSON config blob — it'd force a schema on every extractor and be opaque in ad-hoc SQL.
- **`TargetField` is reused as the FileContent filename pattern** (same `*`-wildcard DSL as classification/FS) — no `FileNamePattern` column. `TargetField` is already the polymorphic per-rule target (keyword for ErrorKeyword, column for ColumnRange). `LogFolder` is reused as the scanned folder; `MonitoredJob.IncludeSubfolders` toggles recursion. Net new columns kept to the 5 FileContent fields + 1 job flag + 2 history counters. The wide-table threshold: if a *5th* scan type needs another batch of columns, normalize then (child config table / TPH) — not now.
- **`ScanContentWatermarks` (new table), mtime-based, NOT byte-offset.** Content scans track WHOLE files: skip when current mtime ≤ recorded `LastModifiedAt`; re-scan when new or modified. Methods folded into the existing `IScanWatermarkRepository` (already multi-kind: file-offset + db + now content) rather than a new interface. **Watermark is written once per examined (new/changed) file regardless of outcome** — failure, predicate-not-satisfied, oversize, or malformed-null. Contract = "process each file *version* once"; the transient-IO edge (a briefly-locked file gets watermarked and won't retry until its mtime changes) is accepted as rare. Content-hash tamper detection deferred to v2.
- **File-outer / rule-inner walk.** Forced by the per-file watermark grain (a per-rule walk would let rule A's watermark write make rule B skip the file). Each file is examined once, every rule whose filename pattern matches is applied, watermark written once after. **Contract note: a single file produces 0–N `JobFailure`s** depending on how many rules' predicates evaluate successfully on it. A predicate that can't be evaluated (locator returns null) **skips that rule cleanly with a Warning** rather than firing a false-positive failure with a meaningless message — so rules over the same file don't interfere, and the "N rules legitimately match → N failures" case still works.
- **Oversize cap (5MB) is enforced by the extractor (throws), only when extraction is attempted.** The strategy catches `FileContentTooLargeException` → `OversizeFileSkips++` + skip. A pure filename-match rule with **no** locators never opens the file, so a 6MB `*WARNING*.xml` still fires (if we don't read it, its size is moot). `XmlContentExtractor` checks `FileInfo.Length` before any parse.
- **XPath is namespace-blind (v1).** `XmlContentExtractor` strips xmlns declarations + prefixes from the loaded `XDocument` before evaluation, so operator XPaths like `/file/status/code` match namespaced XML without `local-name()` or per-rule namespace config. Trade-off: explicit namespace-prefixed XPath won't match, and same-local-name-different-namespace elements merge. Real business XML rarely needs the disambiguation; a per-rule `NamespaceManager` is the v2 escape hatch.
- **No XPath timeout — the 5MB cap is the real bound.** Unlike `Regex`, XPath evaluation isn't cancellable mid-run; a `Task.Run`+token wrapper wouldn't actually stop the work. On a doc already capped at 5MB a reasonable expression completes in microseconds. Malformed XML / invalid XPath are caught → null (logged Warning), never throw past the extractor.
- **`JobFailure` field assignment for FileContent.** `SourceLogPath` = full file path (the field is `required` / non-null — it can't be null; the data file *is* where detection happened), `SourceFilePath` = same path (for `{sourceFilePath}`), `StepName` = filename (matches FS convention — FS uses the filename, not the rule name), `SourceId` = identifier-locator value else `Path.GetFileNameWithoutExtension`. `ErrorMessage` = `"{Description|'FileContent match'}: {primaryValue} (file: {filename})"` — predictable so classification patterns can match it; empty primary value is acceptable for pattern-only rules.
- **Identifier fallback is counted, not silent.** `IdentifierLocator` set but extraction yields null → use filename, increment `ScanRunHistory.IdentifierExtractionFailures` (+ an Info log when an examined file produces no failure at all). Surfaces a misconfigured locator as "N fell back" in scan history instead of hiding in the log file.
- **FileContent validation is reject-at-save (400), scoped to `CheckType=FileContent` only.** Three codes — `ExtractorTypeRequired`, `PredicateIncomplete` (type/value both-or-neither), `PredicateRequiresLocator` — mirror the FixPolicy composite validation precedent. Deliberately NOT retrofitted onto FS/DB/API rules, which keep their existing "accept, validate at scan time" behavior (no back-compat risk since FileContent is new). The 5 fields are nulled for non-FileContent rules on save.

## SqlQuery scan (CheckType.SqlQuery=6, 2026-06-11) — IN PROGRESS

The cross-table / aggregation DB-scan case that the single-table ColumnRange/ValueEquals shape couldn't express. Operator writes SQL (or `EXEC sp_Name`), the strategy runs it and turns the result set into failures. Mirrors FileContent's "operator writes in the source's native language; strategy executes + scans the result." **Status: schema migration applied, strategy + seam + 9 unit tests green, ConfigController validation + UI built; CLAUDE decisions recorded; live smoke test pending.**

- **New `CheckType.SqlQuery` under the existing `Database` ScanType — NOT a new ScanType.** Connection management, result-set→JobFailure flow, classify/suggest pipeline all stay shared with the existing DB scan. Only query construction branches. The worker dispatches by ScanType, so no worker change.
- **`SqlQuery=6` is string-stored (`HasConversion<string>` on the existing `nvarchar(50)` column) — no enum migration.** The **only** schema change is widening `ScanCheckRules.SourceTable` `nvarchar(200)` → `nvarchar(max)` (migration `WidenSourceTableForSqlQuery`) so it can hold a multi-line query.
- **Field reuse, zero new columns.** `SourceTable` is repurposed to hold the operator query / `EXEC` statement (CheckType discriminates, same polymorphism as `TargetField` holding a column-vs-filename-pattern). `TargetField` = the result-set column surfaced on each failure. **`SourceIdColumn` is reused** as the result-set column → `JobFailure.SourceId` (a proposed separate `IdentifierColumn` was rejected — identical semantic, already wired end-to-end; null → falls back to the row index via the existing `srcValue ?? rowKey`). `MinValue`/`MaxValue`/`ExpectedValue`/`FilePathColumn`/`InputPathPattern` are unused and **nulled on save** for SqlQuery rules. **`WatermarkColumn` is also reused/kept** (see the watermark + per-SourceId dedup bullet below) — both it and `SourceIdColumn` name result-set columns and drive the two in-memory dedup layers.
- **Option A — every returned row is a failure; no separate predicate.** The operator's `WHERE`/`JOIN`/`HAVING` *is* the filter. Rejected Option B (a post-query ColumnRange/ValueEquals predicate on TargetField) because it splits filter logic across two places and is mutually exclusive with SqlQuery being its own CheckType. (B's "opaque stored proc returns a status column you must test" case is a clean v2 add — reuse `ScanPredicateType` + `ExpectedValue`, still no new schema.)
- **All SqlQuery runs as `CommandType.Text` — no EXEC heuristic, no `IsStoredProcedure` flag.** `EXEC sp_Foo @p=1` and `SELECT …` are both valid T-SQL text returning a result set, so the operator writes either and we run it verbatim. (A bare `sp_Foo` without `EXEC` isn't valid standalone T-SQL → operators must write `EXEC`.) Eliminates a fragile detection step.
- **Result columns read BY NAME (`row[TargetField]`), not by ordinal** — the result shape is operator-defined. Missing `TargetField` column → `InvalidOperationException` listing the returned columns (fails the scan visibly as a config error). Missing/absent `SourceIdColumn` → row-index fallback. The existing ColumnRange/ValueEquals path stays ordinal-based (safe — it builds its own SELECT).
- **Testability seam `ISqlQueryRunner` (Core/Interfaces) + `SqlQueryRunner` (Infrastructure/Scanning).** Narrow on purpose: `ExecuteAsync(connStr, commandText, maxRows, ct)` → rows as **case-insensitive column-name→value maps**. This is *testability only* (the DB strategy had zero unit tests and talks to `new SqlConnection` directly) — NOT a DB-engine/NoSQL abstraction. Only the SqlQuery branch uses it; ColumnRange/ValueEquals keep their direct `SqlConnection` calls and **remain untested in v1** (known, scoped gap). 9 unit tests cover the branch via a fake runner.
- **Row cap (500) enforced in code, not via `TOP`** (can't inject TOP into an arbitrary query/proc) — the runner stops reading after `maxRows`; the strategy logs a truncation Warning at the cap. `CommandTimeout=60s`.
- **Short, stable label for StepName + dedup key.** `SourceTable` (the query) is too big for `JobFailure.StepName` (`nvarchar(200)`), so SqlQuery failures use `Description ?? "SqlQuery #{ruleId}"` for **both** `StepName` and the dedup key; `SourceLogPath = "db://{conn}/query"`. ColumnRange/ValueEquals behavior is byte-for-byte unchanged (their key stays the table name). `ErrorMessage = "{Description|'SqlQuery match'}: [{TargetField}] = {value} ({rowId})"` — predictable so classification patterns can match it.
- **Validation reject-at-save (400), scoped to `CheckType=SqlQuery`** — `ApplyAndValidateSqlQuery` mirrors `ApplyAndValidateFileContent`: `SourceQueryRequired` (empty SourceTable) + `TargetFieldRequired`. No SQL syntactic/semantic check — same trust model as today's `SourceTable` (a wrong query fails clearly at scan time). Following the FileContent precedent, the 400 codes are **live-verified at smoke test**, not covered by a ConfigController integration test in v1.
- **Watermark + per-SourceId dedup — BOTH, in-memory, composable (SHIPPED 2026-06-14).** The original v1 had no watermark and only a *coarse per-rule* dedup (`HasOpenFailureAsync(job, StepName)`): while ANY failure with that label was open, the whole rule was skipped — so a genuinely-new problem row was **invisible until the prior batch resolved** (found in testing: a `FileStatusCode=2` row never fired because 26 unrelated status-2 failures sat unresolved). The fix adds two independent layers in `DatabaseScanStrategy.ScanSqlRuleAsync`, both applied IN-MEMORY on the returned rows (the operator's SQL/EXEC can't be safely rewritten to push a filter into the query — wrapping arbitrary SQL/EXEC in a subquery breaks):
  - **Per-SourceId dedup** (when `SourceIdColumn` set) — the *correctness* layer. New repo method `IJobRepository.GetOpenFailureSourceIdsAsync(jobId, stepName)` returns the set of SourceIds with a non-resolved failure (one batched query, **case-insensitive** — failures store lowercased GUIDs while the source row's key may be uppercase, so ordinal would miss and duplicate). Candidate rows whose SourceId is already open are dropped; a NEW id fires even while an unrelated row's failure is still open. Fixes the original gap, needs no watermark.
  - **Watermark** (when `WatermarkColumn` set) — the *efficiency/incremental* layer, parity with ValueEquals. Keeps only rows whose watermark value exceeds the stored mark; advances the mark to the highest value seen across ALL returned rows (so handled rows aren't re-examined). Comparison is **typed** (`IsAfter`/`ValueGreater`/`Canonical` helpers): DateTime and numerics compare by value, not lexically (`"9"` vs `"10"`); DateTime stored in the same ISO `yyyy-MM-dd HH:mm:ss.fffffff` shape as the table path's `CONVERT(...,121)`. The watermark column **must be in the operator's `SELECT`** — absent → `InvalidOperationException` naming the column (clear config error, fails the scan).
  - **Composition + fallback matrix:** SourceId+Watermark → incremental **+** per-id safety net (best); SourceId only → per-id dedup (new ids fire, no dup, no watermark needed); Watermark only → watermark-only (exactly like ValueEquals); neither → falls back to the original coarse per-rule `HasOpenFailureAsync` dedup (unchanged degenerate behavior).
  - **500-row cap caveat under watermark:** the cap reads an arbitrary 500 unless the operator adds `ORDER BY <watermarkCol> ASC` to the query (then it reads oldest-first and advance-to-max is safe, like ValueEquals' server-side `ORDER BY`). The cap warning names the column and says so. Documented, not blocked.
  - Table rules (ColumnRange/ValueEquals) are **untouched** — still push the watermark into the generated SQL server-side; only SqlQuery does the in-memory variant. `ConfigController.ApplyAndValidateSqlQuery` now KEEPS `WatermarkColumn` + `SourceIdColumn` (can't validate column presence at save — only the result shape at scan time knows). UI: the SqlQuery rule drawer gained a Watermark Column field (+ "must be in your SELECT" / `ORDER BY` hints) alongside Source ID Column.
- **DatabaseScanStrategy is now per-rule resilient (SHIPPED 2026-06-14, same change).** Surfaced immediately by the watermark feature: a SqlQuery rule with `WatermarkColumn` set but the column missing from its `SELECT` throws — and the throw, propagating out of the rule loop, **aborted the entire source scan**. Since `classify`/`suggest` run *after* the whole loop, failures already created by earlier rules (e.g. the status-5/7 ValueEquals rules) were saved but left **unclassified**, and the source's scan failed every tick (all detection wedged). Fix: each rule's body runs in a `try/catch` (skips `OperationCanceledException`); a throwing rule is logged at Error, remembered, and **skipped** so the remaining rules still scan. After the loop, `classify`+`suggest` run on whatever was created, and *then* the first rule error is rethrown so the scan-run is still recorded `Failed` (visible) — but no earlier rule's failures are orphaned. Test `OneRuleThrows_LaterRuleStillCreatesAndClassifies_ScanSurfacesError` pins it (bad rule first, good rule still creates + classifies, scan throws). 7 new unit tests total (226).
- **Security posture — DOCUMENTED, not blocked.** SqlQuery is a **read** path: worst case from an operator mistake is noise failures + query load, no data mutation. Connection strings are admin-configured (appsettings.json); rules are operator-configured — so SqlQuery does elevate an operator from "single-table SELECT" to "arbitrary T-SQL under the connection login." The meaningful guard is operational: **the monitoring connection should use a least-privilege read-only login** (deployment guardrail) + the row cap + command timeout (built). Parser-level DML-blocking is a v2 nicety, not a boundary (a stored proc can do anything regardless of how its `EXEC` reads). Consistent with the existing "no authn/authz; operator is a trusted role" model. The *write*-path WHERE-scoping guard (`{sourceId}` AST check) belongs on DbFix/SqlScript, not here — see the DbFixHandler follow-up.
- **No-WHERE SqlQuery is allowed, with a SOFT client-side warning only.** Under Option A a WHERE-less query (`SELECT * FROM dbo.Files`) isn't invalid — it means "flag every row," which is occasionally intended (small reference table, pre-filtered view, aggregation). It's bounded noise (500-row cap + no-watermark dedup + read-only login), not danger, so it is NOT blocked. The rule drawer shows a non-blocking hint (`sqlQueryNeedsWhereWarning()`: query is non-empty, doesn't start with `EXEC`, and matches neither `\bWHERE\b` nor `\bHAVING\b`) telling the operator it will flag every row. Deliberately no server-side parser check — hard-blocking would reject legitimate view/proc/aggregation cases and re-introduce the SQL-parsing fragility we avoided.

## Validation enforcement framework (2026-06-11)

Operator-configurable executable content (scan queries, fix payloads) is guarded by **three layers**; a feature picks the layer matching its operational risk, not "always the strictest." The SqlQuery-vs-DbFix split is the canonical example — same surface (operator-written SQL), opposite risk, different enforcement:

1. **Hard server-side validation (parser-level, blocks save)** — for **irreversible blast radius**. The future **DbFix/SqlScript** write path: `UPDATE/DELETE/EXEC` only, must contain `{sourceId}` in the `WHERE`/`ExecuteParameter` (AST check via `Microsoft.SqlServer.TransactSql.ScriptDom`). A missing WHERE on a write can wipe a table — worth blocking even at the cost of rejecting some valid SQL. Catches accidents; deliberate bypass (tautologies) is out of scope (trust model + audit cover it).
2. **Soft client-side warning (string check, non-blocking)** — for **bounded annoyance**. The **SqlQuery** scan path's no-WHERE hint: a regex check in the drawer, operator can still save. A missing WHERE on a read just makes noise (capped, deduped, read-only), so warn — don't block — and don't pay for a real parser.
3. **Runtime guardrails (always on, defense in depth)** — row cap (500), command timeout (60s), no-watermark open-failure dedup, and the deployment-level least-privilege read-only login. These apply regardless of layers 1–2 and bound the blast radius of anything that slips through.

Rule of thumb for future operator-executable features: **irreversible → layer 1; reversible/bounded → layer 2; always add layer 3.** Don't hard-block a read path (fragile, rejects valid cases); don't rely on a soft warning for a write path (trivially bypassed).

## SqlScript fix write-guard (layer 1, SHIPPED 2026-06-11)

The first concrete layer-1 enforcement. A `SqlScript` fix (single-action policy OR a composite step) runs `UPDATE/DELETE/EXEC` against the source DB under a **write-capable** login (read-only logins can't run fixes, so the layer-3 read-only-login guardrail does NOT protect this path — layer 1 is the only real guard). An unscoped statement like `UPDATE dbo.Files SET FileStatusCode = 1` (no WHERE) would mutate the whole table; `SqlScriptExecutor` would even report success (rows affected > 0). This was a **live hole** — `SqlScriptExecutor` is used today as both a single-action fix and a composite step, and `ConfigController.ValidateCompositePayload` only checked composite *shape*, never SQL content.

- **`ISqlFixScopeValidator` (Core/Interfaces) + `SqlFixScopeValidator` (Infrastructure/Fix), ScriptDom-backed.** Injected into `ConfigController` (Core-interface dependency, mirroring `IFileContentExtractor` — keeps the controller off Infrastructure types). `Validate(payload)` returns null if OK, else a reason string.
- **Algorithm:** strip the optional `ConnectionName|SQL` prefix → substitute `{sourceId}` (case-insensitive) → sentinel `__MAIA_SRC__` → parse with `TSql150Parser` → for **every** statement: UPDATE/DELETE must have the sentinel **inside its `WhereClause`** (token-span scan), EXEC must have it **inside the EXEC fragment** (a parameter); any other statement type (SELECT, etc.), an unparseable script, or a missing/sentinel-free WHERE → reject. Multi-statement batches must have *every* statement scoped. `{failureId}` deliberately does NOT count (MAIA's PK, not a key into the operator's table).
- **Wired in `ValidateCompositePayload`** at two points: the single-action branch (when `ActionType=SqlScript`) and per composite step (when `stepActionType=SqlScript`). 400 `DbFixRequiresSourceIdInWhere` with a per-step order in the message. Both `CreateFixPolicyRule` and `UpdateFixPolicyRule` go through it.
- **Honest limit:** catches accidental missing-WHERE / bulk writes (the incident); does NOT catch deliberate tautology bypasses (`OR '{sourceId}'='{sourceId}'`) — covered by trust model + audit, not the parser. 15 unit tests on the validator (no DB needed — ScriptDom is in-process). **Save-time only** — a *pre-existing* bad row in the DB still fires (the live test policy `RuleId 1016` step 1 was disabled out-of-band to neutralize it; re-enabling now requires fixing the WHERE so the guard passes).
- **UI affordance (Fix drawer):** a "+ scope to the failing row" link appears under a SqlScript fix field (single-action + each composite SqlScript step) **only** when the payload lacks `{sourceId}` and isn't an `EXEC`. Clicking appends ` WHERE [KeyColumn] = '{sourceId}'` (or ` AND …` when a WHERE already exists; trailing `;` stripped) — supplying the boilerplate the guard checks. The column is the **literal placeholder `[KeyColumn]`, deliberately NOT derived** from the scan rule's `SourceIdColumn`: a fix may write a *different* table where `{sourceId}` is an FK under another name, so any derived guess would mislead. `[KeyColumn]` is valid T-SQL (passes the guard, saves) and fails *safe* at runtime if left unedited (no such column → fix fails → `ManualRequired`, no data touched) rather than silently updating the wrong column.

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
GET  /api/unconfigured/clusters?window=30d|all                → Case A: unclassified-failure clusters
                                                                 (IUnconfiguredClusterAnalyzer / ngram-v1):
                                                                 { totalUnclassified, clusteredCount,
                                                                   uncategorizedCount, clusters[] }
GET  /api/unconfigured/policy-gaps?window=30d|all             → Case B: classified failures with no
                                                                 effective FixPolicyRule, grouped by
                                                                 (ErrorType, JobType, MonitoredJob)
POST /api/classification/classify                              → IClassifyJobsUseCase
POST /api/fix/execute-fixes                                    → IExecuteFixesUseCase
POST /api/pipeline/run-pipeline                                → IDirectoryPipelineUseCase
POST /api/process/process                                      → full pipeline
POST /api/logparser/parse                                      → ILogParser

GET/POST/PUT/DELETE /api/config/monitored-jobs[/{id}]          → MonitoredJob CRUD
POST/PUT/DELETE     /api/config/scan-rules[/{id}]              → ScanCheckRule CRUD
POST/DELETE         /api/config/monitored-jobs/{id}/classification-rules[/{ruleId}]
GET/POST/PUT/DELETE /api/config/classification-rules[/{id}]    → global ClassificationRule CRUD
GET/POST/PUT/DELETE /api/config/fix-policy-rules[/{id}]        → FixPolicyRule CRUD
                                                                 GET ?jobTypeId= filters to one JobType's rules.
                                                                 GET ?monitoredJobId= additionally surfaces overrides
                                                                 scoped to that job (effective-config view for the
                                                                 per-job Fix Options tab).
                                                                 POST/PUT body accepts optional monitoredJobId — null
                                                                 = JobType default, set = per-MonitoredJob override.
                                                                 GET /{id} returns a single rule
                                                                 (used by the auto-heal toggle's two-step PUT flow).
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
POST     /api/recommendations/{id}/retry                       → re-run a fix that failed to execute.
                                                                 Only valid while the failure is
                                                                 ManualRequired: re-arms it (IsExecuted=
                                                                 false, OperatorApproved=true, claim
                                                                 cleared, Status→Failed) + SYNCHRONOUS
                                                                 drain with the CURRENT policy. Writes
                                                                 OperatorAction + FixRetried audit.
                                                                 body: { "operatorId": "<name>" }

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

`ExecuteFixesUseCase` claims recs atomically via `IRecommendationRepository.ClaimPendingAsync` (`UPDATE TOP(N) ... OUTPUT ... WITH (READPAST, UPDLOCK, ROWLOCK)`); concurrent drains see disjoint sets. Claim is held for 5min; cleared by `MarkExecutedAsync` on success, `ReleaseClaimAsync` on failure.

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
               ├── ErrorTypeId (FK)
               └── MonitoredJobId (FK, nullable)   NULL = JobType default
                                                   set  = per-MonitoredJob override
                                                          (wins over default for that job)
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
