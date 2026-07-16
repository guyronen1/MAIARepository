> Part of MAIA CLAUDE.md, split out for size. Root index: ../CLAUDE.md

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

- ~~Authn / authz on controllers + real operator identity replacing hardcoded
  `'operator'`~~ **SHIPPED 2026-06-19** (Auth v1 Phases 0–4 — see entry below).
  Remaining: AD authn front-end (v2).
- ~~Audit-viewing UI (per-entity tab + global `/audit` screen)~~ **SHIPPED 2026-06-20** — global `/config/audit` screen (Admin-only). See "Last completed (2026-06-20)" entry.
- Small cleanup items: `DbFixHandler` TODO stub, `ILogReader` removal,
  `monitored-jobs.service.ts` consolidation, analytics endpoint shape
  consistency, sort UI on failures-list, style budget warnings,
  CHECK constraints for composite invariants, `FixExecutionLog` retention
  worker

**Last completed (2026-07-16) — UI/UX improvement batch:**

A round of frontend polish (details + rationale in FOLLOWUPS items 1–10 and the dated DECISIONS entries). Highlights:
- **Recommendations vs Operator Actions split (item 1):** `/operator-actions` is now a dedicated `OperatorActionsComponent` (decision HISTORY — Approve/Reject/Retry log) backed by new `GET /api/data/operator-actions` + `OperatorActionDto`; `/recommendations` stays the pending queue.
- **Data-staleness surfaced (item 2):** top-bar "⚠ Reconnecting… · Xs ago" chip off the `worker-status` heartbeat; the pill dims/stops pulsing while stale. (Also fixed: the always-visible badge showed gray on non-dashboard pages because only the dashboard/scan-jobs started polling — the top bar now owns the poll.)
- **Status consolidation (item 3):** the top-bar pill is the single worker status + pause/resume control (Admin), optimistic instant flip; the duplicate dashboard pill was removed.
- **Dashboard KPI hierarchy (item 4):** Resolved Today de-emphasized (narrower, muted) so the five action tiles dominate.
- **Dark theme (item 5):** OS default + manual toggle; `maia-dark` token mixin in `styles.scss`; `ThemeService`; anti-FOUC inline script. Follow-on dark-mode fixes: table-row hover (`#fafbfc`→token), the amber warning-panel family (new `--warn-*` tokens), and the Chart.js grid/tick/legend colours (`chart-theme.util` + rebuild-on-theme-change).
- **Command palette (item 6):** `Ctrl/Cmd+K` + top-bar Search; `SearchService.query()` (nav + failure-by-id + jobs, role-filtered), built to host a future LLM "Ask" mode.
- **Server-side sortable failures list (item 7):** whitelisted `sort`/`dir` on `GetPagedAsync` + clickable headers.
- **Dead-code cleanup (item 9)** and **simplified activity banner (item 10).**
- **Language-switcher scaffold** (`LanguageService`, English active / Hebrew "soon") + **top-bar declutter** (avatar-only account dropdown holding Theme/Language/Sign out; subtitle + Search collapse on narrow widths).
- **Deferred by operator:** item 8 (guided scan-config wizard) and item 11 (full RTL/Hebrew translations).

**Last completed (2026-06-20):**

- **Audit-viewing UI — global `/config/audit` screen, Admin-only.** Paged, filtered read of the `AuditLog` table. Backend: `IAuditRepository.QueryAsync(AuditLogFilter, ct)` + `SqlAuditRepository` EF implementation (`ORDER BY AuditId DESC`, conditional WHERE on EntityType/EntityId/Actor/EventType/date range, `CountAsync` + `Skip/Take` paging); `AuditLogDto.From(AuditLog)` contract record; `GET /api/admin/audit-log` endpoint on `AdminController` (RequireAdmin, pageSize clamped 1–200). Frontend: `AuditLogEntry`/`AuditLogPage` models; `AuditService.query()` with `HttpParams`; `AuditComponent` — filter bar (EntityType select, Actor/EventType text inputs, from/to date pickers, Search + Clear + record count), fixed-column table (Timestamp · Actor · Event badge · Entity · Detail), server-side pagination, monospace Detail with `→` wrapped in a primary-color `<span>` via HTML-escaped `renderDetail()`; event-badge colour classes (red=Deleted/FixFailed, green=operator decisions, amber=system, purple=User*, indigo=Created/Updated/Linked, slate=default); lazy route `/config/audit` with `adminGuard`; "Audit Log" nav item in the Administration sidebar section. **Per-entity contextual tab deferred** (no concrete operator ask; global screen covers the compliance-audit use case).

**Last completed (2026-06-19):**

- **Authentication & Authorization v1 — Phases 0–4 SHIPPED.** MAIA-local username/password login, server-side sessions in an httpOnly cookie, role-tiered API enforcement, forced password rotation (non-skippable in prod), and cosmetic frontend role-gating (Phase 4). Backend tests green (incl. an exhaustive authorization matrix + the forced-rotation gate, both directions); Angular builds clean; cookie flow + actor stamping live-verified.
  - **Schema (Phase 0):** new `Roles` (seed: 1=User, 2=Operator, 3=Administrator; `RoleId == (int)MaiaRole`), `Users` (unique-CI Username, PBKDF2 `PasswordHash`, RoleId FK, IsActive, MustChangePassword, CreatedAt, LastLoginAt), `UserSessions` (unique Token, UserId FK cascade, LastActivityAt). Migration `AddAuthTables` seeds the 3 roles via `HasData` and the bootstrap admin via raw `INSERT` (random PBKDF2 salt → non-deterministic, can't use `HasData`). **Default admin: `admin` / `admin`, RoleId=3, MustChangePassword=1** (forced rotation on first login — non-skippable in prod; see the MustChangePassword bullet). `Microsoft.Extensions.Identity.Core` added to Infrastructure for `PasswordHasher<T>`.
  - **Auth backend (Phase 1):** `IAuthService`/`AuthService` (Application) — login (with rehash-on-login), logout, change-password, `DismissPasswordChangeAsync`, `ValidateSessionAsync` (live user+role lookup, lazy idle-expiry, throttled slide). `MaiaSessionAuthenticationHandler` (scheme `"MaiaSession"`) reads the cookie → validates → builds the principal (NameIdentifier/Name/Role + `maia:must_change_password` claim) and re-issues the sliding cookie; missing/invalid token → `NoResult` (anonymous). `ICurrentUserAccessor` (`HttpContextCurrentUserAccessor`) exposes the principal. `AuthController`: `login` `[AllowAnonymous]`, `logout`, `change-password`, `dismiss-password-change`, `me` `[AllowAnonymous]`.
  - **Enforcement (Phase 3):** three policies `RequireUser`/`RequireOperator`/`RequireAdmin` + `FallbackPolicy = RequireAuthenticatedUser` (default-CLOSED for authn; NOT default-admin). Every controller tier-attributed: Data/Unconfigured = User; Recommendations/Failures/JobScan/Fix/Classification/Process/Pipeline/LogParser = Operator; ConfigController = class `RequireOperator` (reads) + per-write `RequireAdmin` (AND-combined); Admin/Users = Admin. `[AllowAnonymous]` on login/me + `/health/live`+`/health/ready` (K8s probes). New `UsersController` (Admin): list/create/update/reset-password, AuditLog per write, last-admin lockout guard.
  - **Server-authoritative actor:** the `operatorId` field/query param is **removed from every contract** (ConfigController records + DELETE/link query params + MissingOperator; RecommendationsController `DecisionRequest`; FailuresController `MarkResolvedRequest`). Audit `Actor` / `OperatorAction.OperatorId` now come from `currentUser.UserName` (the cookie principal). Frontend stopped sending `operatorId` (ConfigService helpers gutted to pass-throughs; rec/failure service params dropped).
  - **Frontend (Phase 2):** `AuthService` (currentUser signal, `hasAtLeast`), `credentialsInterceptor` (`withCredentials` so the cross-origin cookie rides), `authErrorInterceptor` — **3 distinct cases: 401→`/login`, 403 `PasswordChangeRequired`→`/change-password`, plain 403→toast + stay put** (never collapse them). Login + change-password components, `authGuard`, top-bar real user + Sign-out, shell toasts (`NotificationService`). CORS gained `.AllowCredentials()`.
  - **MustChangePassword is FORCED + non-skippable in any real deployment; skip is a fail-closed Development-only convenience (2026-06-19, after a security review caught a regression).** `MustChangePasswordMiddleware` runs unconditionally (after authn, before authz): an authenticated user who still owes a rotation is 403'd (`PasswordChangeRequired`) on every `/api/*` call except the allow-list (change-password, dismiss-password-change, logout, me) until they change it. This forced rotation is the **entire mitigation** for the seed credential living in source control (known-compromised by design — the seed is unusable for anything but its own rotation). The **only** escape hatch is `POST /api/auth/dismiss-password-change`, which is gated `IWebHostEnvironment.IsDevelopment()` and **fails closed** — non-Development (incl. unset → Production) returns `403 SkipNotAllowed`, so the rotation cannot be skipped in prod. `/me`+`/login` expose `canSkipPasswordChange` (= IsDevelopment); the frontend shows "Skip for now" only when true, and `authGuard` routes must-change users to `/change-password`. **`admin`/`admin` seed is acceptable specifically because forced rotation is restored** — the seed value (admin/admin vs any other) carries the same residual risk under forced rotation: known-from-source, valid only until first rotation. Tests pin both halves: the matrix factory runs in "Testing" (non-dev) and asserts BLOCK-until-rotation + `dismiss → 403`; a second test runs a `"Development"` factory and asserts the dev skip works. **History:** an interim commit (`8407a5d`) had removed the middleware entirely + made the seed admin/admin + softened the matrix test to assert non-blocking — a live privilege-escalation hole (skippable known-admin = full Administrator). That was reverted to this secure-by-default + dev-gated design.
  - **Session:** sliding idle, default **3h** (`Auth:IdleTimeoutSeconds`, now set to 3600=1h in appsettings); `ActivitySlideThrottleSeconds=60` throttles the `LastActivityAt` DB write. Cookie: HttpOnly, SameSite=Strict, `Secure` env-driven (`Auth:CookieSecure`, false in dev).
  - **The gate:** `AuthorizationMatrixTests` (`WebApplicationFactory<Program>` over InMemory, workers stripped, 3 role users seeded) drives the **full route inventory × every role** — Admin write→403 for Operator+User, Operator action→403 for User, anonymous→401 except login/me/health. This is the cutover go/no-go because the fallback is default-*authenticated* (a forgotten `[Authorize]` would silently fall through, not lock out). Runbook: `docs/AUTH-CUTOVER-RUNBOOK.md`. Password hashing is **PBKDF2-HMAC-SHA512 @ 100k iterations** (.NET 8 Identity v3 default), random per-password salt — NOT reproducible via T-SQL `HASHBYTES`.

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

- **Case B (`/unconfigured` policy-gaps) status filter.** `GetPolicyGaps` now filters to `Status == JobStatus.Failed` (mirrors Case A) — resolved/acknowledged failures no longer count as open policy gaps. Fixes "marked it resolved but the gap stayed." Regression test `PolicyGapStatusTests` (first controller-level test — added `Maia.API` ProjectRef + `Microsoft.AspNetCore.App` FrameworkRef to the test project). Surfaced two by-design behaviors worth knowing: classification is a sticky one-time label, and `JobFailure.JobTypeId` is a creation-time snapshot.
- **FileContent locator validation + extraction visibility (the (a)+(b) follow-up).** (a) `IFileContentExtractor.ValidateLocator` + `ConfigController` save-time `400 InvalidLocator`; (b) `PredicateUnevaluableSkips` counter + all three FileContent counters exposed in `ScanRunDto`. Migration `AddPredicateUnevaluableSkips`. 11 new tests (179 total). Live-verified: malformed XPath rejected at save; the `\\`-vs-`//` typo can no longer be saved.

**Last completed (2026-06-18, second batch):**

- **Worker pause/resume toggle — "Live"/"Paused" pill on the dashboard.** Operators can now stop the `MonitoringWorker` scan loop at runtime without restarting the process. Backend: `IWorkerControlService` / `WorkerControlService` (singleton, thread-safe `volatile int` flag); `MonitoringWorker` checks `control.IsPaused` at the top of each tick and sleeps 5s instead of claiming jobs. Two new endpoints: `POST /api/admin/worker/pause` and `POST /api/admin/worker/resume`. `GET /api/data/worker-status` now includes `isPaused: bool`. Frontend: `WorkerStatus` model gains `isPaused`; `ScanService` gains `pauseWorker()` / `resumeWorker()`; the dashboard page-title row shows a persistent pill — green pulsing dot "Live" or amber "Paused" — that toggles on click. In-flight scans complete normally when paused; no claims are made until resumed.
- **"Link existing rule" → "Swap to existing rule" in edit mode (classification rule drawer).** When editing an existing classification rule (e.g. changing pattern from `FileStatusCode=7` to `FileStatusCode=5`) and the new pattern conflicts with rule 18, clicking the action button previously just linked rule 18 while leaving the old rule still associated — both ended up linked. Fix: `linkConflictingClassRule()` in `JobConfigComponent` now detects edit mode via `editingClassRule()` and, after linking the new rule, also calls `deleteJobClassificationRule(jobId, oldRule.ruleId)` to unlink the rule that was being edited. Button label changes to "Swap to existing rule" in edit mode vs. "Link the existing rule instead" in create mode.

**Last completed (2026-06-18):**

- **`scanRuleNeedsClassification` heuristic fix — ValueEquals false-positive ⚠ eliminated.** The ⚠ "No class rule" badge on DB scan-rule rows was comparing `rule.targetField` (e.g. `"FileStatusCode"`) against class-rule patterns (e.g. `"FileStatusCode=5"`). `"FileStatusCode".includes("FileStatusCode=5")` is always false → badge fired even when the matching rule existed. Fix: use `classPatternForScanRule(rule)` as the keyword — for ValueEquals this produces `"Field=Value"` (same string the classification drawer pre-fills), making the overlap check apples-to-apples. FileContent rules now also use the stripped filename keyword consistently.
- **`jobTypeGlobalRules` / `effectiveClassRules` stale-`allJobs` bug fixed.** After linking a classification rule to a specific job, the rule persisted in the "Exe defaults" subsection because `allJobs` is loaded once at `ngOnInit` and never refreshed. Both computeds now also exclude rules present in `job().rules` (always fresh after `reload()`) via a `linkedToThisJob` set — independent of the stale `allJobs`. `effectiveClassRules` gains the same guard to prevent a just-linked rule appearing twice (once in `linked`, once still in `defaults`).
- **"Re-classify & generate suggestions" button added to Unconfigured screen** — calls `GET /api/jobscan/classify-pending` (same as the Recommendations page "Classify Pending" button); reloads both Case A and Case B sections after completion. Third entry point for the classify-pending action alongside the auto-trigger in Case A rule creation and the manual button on the Recommendations page.
- **"Run Full Pipeline" button removed from Recommendations page.** `POST /api/process/run-pipeline` → `DirectoryPipelineUseCase` is the pre-worker legacy directory scanner, not a substitute for "Run All Scans" (`POST /api/jobscan/scan-all` → modern per-job `IScanStrategy`). Removed button, `runPipeline()` component method, and `runPipeline()` from `RecommendationsService`.

**Last completed (2026-06-17, second batch):**

- **Tier 2.5 cleanup migration — `ScanSourceId` enforced NOT NULL, orphan rows deleted, 7 legacy `MonitoredJob` scan-config columns dropped.** Migration `20260617185954_Tier25CleanupColumns` applied: DROP INDEX → ALTER COLUMN INT NOT NULL → CREATE INDEX for the 5 tables that carry `ScanSourceId` (`ScanCheckRules`, `ScanContentWatermarks`, `ScanFileWatermarks`, `JobFailures`, `ScanRunHistory`). Entity nullable declarations (`int?`) changed to `int` in `ScanFileWatermark`, `ScanContentWatermark`, `ScanRunHistory`. Worker + controller method signatures updated (`int? scanSourceId` → `int scanSourceId`). Test fixtures that lacked `ScanSourceId` now supply the correct constant to avoid EF InMemory navigation-property breakage. 230 tests green.
- **Angular frontend purged of 8 legacy `MonitoredJob` scan-config fields** — `scanTypeId`, `scanTypeName`, `logFolder`, `searchPatterns`, `inputFolder`, `includeSubfolders`, `connectionName`, `logSourceUrl` removed from `MonitoredJob` model. `UpsertJobRequest` simplified to 6 identity fields. All 5 affected components (`monitored-jobs`, `job-config`, `scan-jobs`, `dashboard`, and the shared util) updated to derive icon/label/config chips from `job.sources` instead. No frontend regressions.
- **`CreateJobClassificationRule` 409 pre-flight — silent 500 on "Add Classification Rule" fixed.** Root cause: the unique index `UX_ClassificationRules_ActiveKey` (JobTypeId, Pattern) already had a rule with pattern `FileStatusCode=5` for JobTypeId=5; `CreateJobClassificationRule` had no duplicate pre-flight, so a new INSERT hit the index → unhandled `DbUpdateException` → 500. Fix: added the same two-pronged `FindActiveClassificationDuplicateAsync` pre-flight as `CreateClassificationRule` — returns `409 { error:"DuplicateClassificationRule", conflictingRuleId }`. Frontend (`job-config.component.ts`) updated: `saveClassRule` error handler now surfaces the 409 message as a banner + "Link the existing rule instead" button that calls `linkJobClassificationRule(jobId, conflictingRuleId)`. New signals `classRuleSaveError` / `classRuleConflictId` cleared on drawer reopen.
- **`IScanWatermarkRepository.UpdateFileOffsetAsync` + `UpsertContentWatermarkAsync` now require `int scanSourceId`** — previously these methods created new watermark rows without `ScanSourceId`, which silently used the default value `0` and failed the FK constraint `FK_ScanFileWatermarks_ScanSources_ScanSourceId` after the NOT NULL migration. All callers (`FileSystemScanStrategy`, `FileContentScanStrategy`, `DirectoryPipelineUseCase`) updated to pass `source.ScanSourceId`. `DirectoryPipelineUseCase.ResolveMonitoredJob` refactored to return `(MonitoredJob?, ScanSource?)` so the caller gets the matched source's ID. Test mocks / stubs updated to match the new signatures. FK violation confirmed gone from logs.

**Last completed (2026-06-17):**

- **Config screen bug fixes and UI polish.**
  - **`ClassificationRule` delete is now a true hard-delete** — `SqlClassificationRuleRepository.DeleteAsync` was soft-deleting (`IsActive=false`) despite the intent documented in decisions. Fixed to load the rule with `MonitoredJobRules`, remove those links first (RESTRICT FK), then `Remove(rule)` + `SaveChangesAsync` in one call. Added an error handler to the component's `deleteRule` subscribe so backend errors surface as a red banner instead of silently swallowing.
  - **`Severity.Critical` added to enum** — `Maia.Core/Enums/Severity.cs` was missing `Critical`; the controller already referenced it in the 400 message and the frontend `SEVERITIES` list already offered it. No migration needed (stored as `nvarchar`).
  - **Severity badge colors fixed** — `sevBadge()` in `ErrorTypesComponent` mapped High → `badge-warning` and Low → `badge-muted`, neither of which is defined globally. Changed to use the globally-defined `badge-high` / `badge-medium` / `badge-low` / `badge-failed` (Critical) classes.
  - **Shared `DrawerComponent` improvements** — added `width = input<string>('600px')` so each consumer can set its own width; failure-detail drawers stay at 760px, config drawers narrower; height changed to `100vh` (full viewport); drawer titles bolded (`font-weight:600`, `font-size:15px`).
  - **Fix Option drawer — Fix Category is read-only / derived** — replaced the editable `<select>` with a `<input readonly>` that auto-derives from Execution Type; `setFixRuleActionType` always re-derives on type change; `orderedActionTypes` no longer traps at Manual.

- **Job-config screen — Round 1 UX polish + bug fixes (frontend only, `JobConfigComponent`).**
  - **Coverage gap indicators (⚠)** — three inline amber pill badges that connect the scan→class→fix pipeline gaps. Each badge is clickable and opens the pre-filled drawer for the missing downstream piece:
    - Scan-rule row: ⚠ **No class rule** → opens Classification drawer with pattern pre-filled (ErrorKeyword → keyword; ValueEquals → `"Field=Value"`; ColumnRange/FileContent → field name). SqlQuery rules excluded (output unpredictable).
    - Classification-rule row: ⚠ **No fix option** → opens Fix Option drawer with ErrorType pre-selected.
    - Fix-options row: ⚠ **No class rule** → opens Classification drawer with ErrorType pre-selected (upstream coverage gap).
    - Badge style: amber pill (`#fef3c7` / `#f59e0b` border, bold 12px) — visible at a glance, same tone as the collapsed-source rollup chip. Hover darkens to `#fde68a`.
  - **Hover-reveal action buttons** — Edit/Delete on scan-rule, classification-rule, and fix-options rows are `:focus-within`-reveal (always visible on keyboard focus, hover-only otherwise). Collapse button bug fixed: `colBtn.blur()` called after `toggleSource()` so `.source-head:focus-within` releases after click.
  - **Per-source collapse** — multi-source jobs show a ▼/▶ chevron per source; single-source jobs always expand (no pointless click). Collapsed header shows a rollup chip counting uncovered rules. Signal uses immutable `Set` replacement for reactivity.
  - **Scan-rule ⚠ heuristic** — `scanRuleNeedsClassification()` is type-aware. `ErrorKeyword` and `FileContent` rules: fires only when `effectiveClassRules().length === 0` (the operator has set up no classification at all). Substring overlap is NOT checked — a broad keyword like `*Error*` captures lines of any shape, so partial class-rule coverage is expected and the `/unconfigured` screen handles the gap analysis. `ValueEquals` / `ColumnRange` rules: the original substring check is kept (`keyword.includes(literal)` where keyword = `Field=Value` and literal is the stripped class-rule pattern) because those patterns are precise and the overlap is meaningful. `SqlQuery` always returns false (output shape is unpredictable).
  - **Bug fixes:** (1) FileContent predicate type → None now calls `onPredicateTypeChange()` which clears `extractorPredicateValue`, preventing `PredicateIncomplete 400`. (2) Collapse button blur fix (see above). (3) Gap heuristic upgraded (see above).
  - **Gap badge placement** — badges sit inline in the actions column (last `<td>`), before the hover-reveal Edit/Delete span. Actions column widened: scan-rules 12%→24%, classification 18%→26% (redistributed from adjacent columns). `.rule-actions` changed to `display:flex` with `.row-actions { margin-left:auto }` so Edit/Delete always pin to the right edge regardless of badge presence — no alignment shift between rows.
  - **Fix option drawer — no-default execution type.** Fix type genuinely varies (SqlScript, ApiCall, CopyFile, Composite, Manual); no single type dominates. New fix option forms now start with Execution Type = empty ("Select execution type…") and Fix Category = empty ("Derived from execution type…"). Save button disabled until execution type chosen. When execution type is picked, Fix Category auto-derives via `defaultCategoryFor()` (SqlScript→DbFix, CopyFile→FileRepair, ApiCall/Script/Composite→Retry, Manual→Manual). Unlocking from Manual resets actionType to `''` (operator must re-choose). `orderedActionTypes()` handles empty fixCategory with a neutral list. Action Payload + token legend hidden until execution type is chosen.
  - **Null `policyActionType` fix** — rec with no matching FixPolicyRule (`policyActionType=null`) now treated same as `'Manual'` in the failure drawer: button reads "Acknowledge" (not "Approve"), executed state shows "Acknowledged" badge. Fixed at three sites in `failure-detail.component.ts`.

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
  `Maia.Core/Interfaces` (Format + `ExtractAsync(filePath, locator) → string?`),
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
- **Whitespace-tolerant matching — `Maia.Core/Classification/ClassificationMatcher`.**
  Extracted the ClassificationRule match logic (case-insensitive, `*`-only
  wildcard, regex metachars literal, 50ms timeout) out of `RuleBasedClassifier`
  into a public static helper (sibling to `Maia.Core/Scanning/FilenamePattern`), and
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
- FS-scan filename DSL alignment (`Maia.Core/Scanning/FilenamePattern.cs`) —
  matches classification-rule wildcard semantics, replaces `Directory.GetFiles`
  glob in both `FileSystemScanStrategy` and `DirectoryPipelineUseCase`.
- "Fix Failures Today" dashboard KPI (5th tile, 🚨 alarm icon + pulse) +
  `view=fix-failed` drill-down + per-row "Failed to Execute" badge on the
  failures table.

