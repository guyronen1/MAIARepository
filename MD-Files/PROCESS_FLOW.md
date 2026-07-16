> Part of MAIA CLAUDE.md, split out for size. Root index: ../CLAUDE.md

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
                        &sort=&dir=                              view ∈ active | unclassified |
                                                                       awaiting-action | auto-fixed |
                                                                       operator-fixed | resolved |
                                                                       manual-required
                                                                 sort ∈ id | job | errortype |
                                                                       detected(default) | status;
                                                                 dir ∈ asc | desc(default).
                                                                 Whitelisted server-side sort +
                                                                 FailureId tiebreaker.
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
GET  /api/data/operator-actions?operatorId=&actionTaken=       → OperatorActionDto[] (paged, max 200)
                          &fromDate=&toDate=&q=&page=&pageSize=   Decision history (Approve/Reject/Retry,
                                                                  newest first) with joined rec + failure
                                                                  + job context. Backs /operator-actions.
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
├── AddMaiaAI(connectionString)          Maia.Infrastructure/Extensions
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
├── AddApplicationServices()             Maia.API/Extensions
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
