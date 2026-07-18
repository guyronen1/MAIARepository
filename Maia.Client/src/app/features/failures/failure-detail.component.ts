import { Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { DatePipe, PercentPipe } from '@angular/common';
import { FailuresService } from '../../core/services/failures.service';
import { RecommendationsService } from '../../core/services/recommendations.service';
import { ConfigService, FixPolicyRuleStep } from '../../core/services/config.service';
import { AuthService } from '../../core/services/auth.service';
import { NotificationService } from '../../core/services/notification.service';
import { FailureStatus, FixExecution, Recommendation } from '../../core/models';
import { PluralizePipe } from '../../core/pipes/pluralize.pipe';

/**
 * Pure detail-content for a single failure. Input-driven — the host (currently
 * the drawer in FailuresListComponent) sets `failureId` and the component
 * re-fetches whenever it changes. No router dependency; the host owns
 * URL/state. Re-render-without-remount keeps the drawer transition smooth
 * when navigating via ↑/↓ between adjacent failures.
 */
@Component({
  selector: 'app-failure-detail',
  standalone: true,
  imports: [DatePipe, PercentPipe, PluralizePipe],
  template: `
    <div class="detail-root">
      @if (loading()) {
        <div class="loading-overlay" style="height:300px"><span class="spinner"></span> Loading…</div>
      } @else if (failure()) {
        <!-- Stage pipeline -->
        <div class="card">
          <div class="stage-pipeline">
            @for (s of stages(); track s.key) {
              <div class="stage" [class.active]="s.key === failure()!.stage" [class.done]="isStageCompleted(s.key)">
                <div class="stage-dot">{{ s.icon }}</div>
                <span class="stage-label">{{ s.label }}</span>
              </div>
              @if (!$last) { <div class="stage-connector" [class.done]="isStageCompleted(s.key)"></div> }
            }
          </div>
        </div>

        <!-- Prominent banner when any fix attempt failed to execute. The
             per-step ✓/✗ breakdown lives in the Execution History card below. -->
        @if (hasFixFailure()) {
          <div class="fix-failure-alert" role="alert">
            <span class="alert-icon">✕</span>
            <span>
              A fix <strong>failed to execute</strong> for this failure —
              {{ lastCycleFailedCount() }} of {{ lastCycleTotal() }}
              {{ lastCycleTotal() === 1 ? 'action' : 'actions' }} in the latest attempt did not complete.
              The step breakdown is on the recommendation; expand <strong>Execution History</strong> for earlier attempts.
            </span>
          </div>
        }

        <div class="detail-grid">
          <!-- Failure info -->
          <div class="card">
            <div class="card-header">
              <h3>Failure Details</h3>
              <div class="header-actions">
                <span class="badge" [class]="'badge-' + failure()!.status.toLowerCase()">{{ statusLabel(failure()!.status) }}</span>
                @if (canAct() && (failure()!.status === 'AwaitingManualAction' || failure()!.status === 'ManualRequired')) {
                  <!-- Exit ramp for the off-system manual flow. Visible when:
                         • Status = AwaitingManualAction → operator approved a
                           Manual rec; clicking confirms the off-system work done
                         • Status = ManualRequired       → operator rejected or
                           auto-heal failed; clicking confirms operator handled
                           it manually some other way
                       Same backend endpoint (idempotent: already-Resolved → 204). -->
                  <button class="btn btn-success btn-sm"
                          [disabled]="markingResolved()"
                          (click)="markResolved()">
                    @if (markingResolved()) { <span class="spinner"></span> }
                    ✓ Mark Resolved
                  </button>
                }
              </div>
            </div>
            <dl class="detail-list">
              <dt>Job</dt>        <dd dir="auto">{{ failure()!.monitoredJobName ?? '—' }}</dd>
              <dt>Step / File</dt><dd dir="auto">{{ failure()!.stepName ?? '—' }}</dd>
              <dt>Source ID</dt>  <dd class="font-mono">{{ failure()!.sourceId ?? '—' }}</dd>
              @if (failure()!.sourceFilePath) {
                <dt>Source File</dt>
                <dd class="font-mono" dir="auto" title="Captured input file path — what {sourceFilePath} resolves to">{{ failure()!.sourceFilePath }}</dd>
              }
              <dt>Error Type</dt> <dd>
                @if (failure()!.errorTypeCode) {
                  <span class="badge badge-medium">{{ failure()!.errorTypeCode }}</span>
                } @else { <span class="text-muted">Not yet classified</span> }
              </dd>
              <dt>Detected</dt>   <dd>{{ failure()!.detectedAt | date:'dd/MM/yy HH:mm:ss' }}</dd>
              <dt>Stage</dt>      <dd><span class="badge badge-info">{{ failure()!.stage }}</span></dd>
            </dl>
            @if (failure()!.errorMessage) {
              <div class="error-message-box">
                <label>Error Message</label>
                <pre dir="auto">{{ failure()!.errorMessage }}</pre>
              </div>
            }
          </div>

          <!-- Recommendations (AI panel) -->
          <div class="card ai-panel">
            <div class="card-header">
              <h3>
                <span class="ai-chip">AI</span>
                Recommendations
              </h3>
              <span class="text-muted text-sm">{{ failure()!.recommendations.length | pluralize:'suggestion' }}</span>
            </div>

            @if (failure()!.recommendations.length === 0) {
              <div class="empty-state" style="padding:30px">
                <span class="empty-icon">💡</span>
                <p>No recommendations yet</p>
                <p class="text-sm text-muted">Run classify-pending to generate suggestions</p>
              </div>
            } @else {
              <div class="rec-list">
                @for (rec of failure()!.recommendations; track rec.recommendationId) {
                  <div class="rec-card" [class.executed]="rec.isExecuted">
                    <div class="rec-header">
                      <span class="badge" [class]="categoryBadge(rec.fixCategory)">{{ rec.fixCategory }}</span>
                      @if (rec.isExecuted) {
                        @if (rec.policyActionType === 'Manual' || rec.policyActionType === null) {
                          <!-- IsExecuted=true on a Manual or no-policy rec means the
                               operator acknowledged it. Don't claim "Executed" — it
                               would read as "the system ran the fix" which is false. -->
                          <span class="badge badge-awaitingmanualaction">Acknowledged</span>
                        } @else {
                          <span class="badge badge-fixed">Executed</span>
                        }
                      }
                      <!-- "Approved" only shown in the narrow window between
                           operator click and drain execution. Once executed,
                           the terminal badge (Acknowledged / Executed) tells
                           the full story — "Approved" on top would be noise. -->
                      @if (rec.operatorApproved === true && !rec.isExecuted)  { <span class="badge badge-resolved">Approved</span> }
                      @if (rec.operatorApproved === false) { <span class="badge badge-failed">Rejected</span> }
                    </div>

                    <p class="rec-action" dir="auto">
                      {{ rec.suggestedAction }}
                      @if (rec.policyStepCount > 0) {
                        <span class="badge badge-info composite-badge"
                              title="Composite policy: this approval triggers multiple ordered actions. Best-effort: any step failure routes the failure to ManualRequired, remaining steps still run.">
                          Composite · {{ rec.policyStepCount }} steps
                        </span>
                      }
                    </p>

                    @if (rec.policyStepCount > 0) {
                      <!-- Inline composite step list — operator reads BEFORE
                           clicking Approve so they know what they're committing
                           to. Lazy-fetched once per ruleId on load (and on
                           every poll-refresh, to pick up edits operators
                           made elsewhere); cached locally. -->
                      <div class="rec-steps">
                        @if (stepsFor(rec).length === 0) {
                          <div class="rec-steps-loading text-sm text-muted">
                            Loading steps…
                          </div>
                        } @else {
                          <!-- Description-only view. Operators read the human
                               summary, not the raw payloads — the scripts /
                               SQL / URLs are for the config screen. Falls
                               back to "Step N (ActionType)" when description
                               is empty so the bullet is never blank. -->
                          <ul class="rec-steps-list">
                            @for (step of stepsFor(rec); track step.stepId; let idx = $index) {
                              <li class="rec-step" dir="auto" [class]="'step-' + stepStatus(rec, step.stepOrder)">
                                <span class="step-icon">{{ stepIcon(stepStatus(rec, step.stepOrder)) }}</span>
                                <span class="step-text">{{ step.description?.trim()
                                    || 'Step ' + (idx + 1) + ' (' + step.actionType + ')' }}</span>
                              </li>
                            }
                          </ul>
                        }
                      </div>
                    }

                    <div class="confidence-bar">
                      <div class="bar-track">
                        <div class="bar-fill" [style.width.%]="rec.confidenceScore * 100"></div>
                      </div>
                      <span class="bar-value">{{ rec.confidenceScore | percent }}</span>
                    </div>

                    <!-- Auto-run snapshot (read-only — toggle the policy from the Recommendations screen) -->
                    <div class="rec-footer">
                      <span class="text-sm text-muted">
                        Auto-run on next drain:
                        @if (rec.autoFixAvailable) {
                          <span class="badge badge-info">Yes</span>
                        } @else {
                          <span class="badge badge-muted">No</span>
                        }
                      </span>

                      @if (canAct()) {
                      @if (!rec.isExecuted && rec.operatorApproved === null) {
                        <div class="rec-actions">
                          <!-- policyActionType=Manual or null → no automation runs.
                               null = no policy configured; drain will find nothing
                               and transition to ManualRequired. Same outcome as
                               Manual: operator is on the hook. Honest verb: Acknowledge.
                               Any non-null, non-Manual actionType → system executes
                               something on approval. Verb: Approve.
                               Using policyActionType (mechanism), NOT fixCategory
                               (intent) — they can differ (e.g. DbFix+Manual,
                               Retry+SqlScript). -->
                          @if (rec.policyActionType === 'Manual' || rec.policyActionType === null) {
                            <button class="btn btn-success btn-sm" (click)="approve(rec)">✓ Acknowledge</button>
                          } @else {
                            <button class="btn btn-success btn-sm" (click)="approve(rec)">✓ Approve</button>
                          }
                          <button class="btn btn-danger btn-sm"  (click)="reject(rec)">✕ Reject</button>
                        </div>
                      } @else {
                        <!-- Graceful disabled-state. If the rec resolved mid-review (via
                             background drain), we keep the action buttons present-but-
                             disabled and label the new state, so the operator doesn't
                             have controls vanish under their cursor. -->
                        <div class="rec-actions rec-actions-done">
                          <!-- Retry: only when THIS rec failed to execute and
                               the failure is in ManualRequired. After fixing
                               the root cause (e.g. a bad policy), re-run the
                               same failure. Re-arms + drains synchronously. -->
                          @if (recFailedToExecute(rec) && failure()!.status === 'ManualRequired') {
                            <button class="btn btn-primary btn-sm" (click)="retry(rec)"
                                    [disabled]="retrying() === rec.recommendationId"
                                    title="Re-run this fix now (use after fixing the root cause)">
                              @if (retrying() === rec.recommendationId) { <span class="spinner"></span> }
                              ↻ Retry Fix
                            </button>
                          }
                          @if (rec.policyActionType === 'Manual' || rec.policyActionType === null) {
                            <button class="btn btn-success btn-sm" disabled>✓ Acknowledge</button>
                          } @else {
                            <button class="btn btn-success btn-sm" disabled>✓ Approve</button>
                          }
                          <button class="btn btn-danger btn-sm"  disabled>✕ Reject</button>
                          <span class="text-sm text-muted action-note">
                            @if (rec.isExecuted && (rec.policyActionType === 'Manual' || rec.policyActionType === null)) { Already acknowledged }
                            @else if (rec.isExecuted)                              { Already executed }
                            @else if (rec.operatorApproved === true)               { Already approved }
                            @else                                                  { Already rejected }
                          </span>
                        </div>
                      }
                      }
                    </div>
                  </div>
                }
              </div>
            }
          </div>
        </div>

        <!-- Execution History — chronological fix-attempt log. For composite
             policies each step is its own ✓/✗ row (indented), followed by the
             overall summary row. Failed rows are highlighted red so the
             operator sees exactly which action needs manual cleanup. -->
        @if (executions().length > 0) {
          <div class="card exec-history">
            <div class="card-header">
              <h3>Execution History</h3>
              <button type="button" class="history-toggle"
                      (click)="historyExpanded.set(!historyExpanded())">
                {{ historyExpanded() ? 'Hide' : 'Show' }} · {{ cyclesNewestFirst().length | pluralize:'attempt' }}
              </button>
            </div>
            <!-- Collapsed by default; the rec card carries the latest per-step
                 ✓/✗. Expanded shows every attempt newest-first, steps in order
                 within each. Result detail + trigger are on the row's hover. -->
            @if (historyExpanded()) {
              @for (cycle of cyclesNewestFirst(); track cycle.attempt) {
                <div class="exec-cycle">
                  <div class="exec-cycle-head">
                    <span class="exec-attempt">Attempt {{ cycle.attempt }}</span>
                    <span class="exec-cycle-time">{{ cycle.at | date:'dd/MM/yy HH:mm' }} · {{ cycle.trigger }}</span>
                  </div>
                  <ul class="exec-list">
                    @for (e of cycle.rows; track e.fixId) {
                      <li class="exec-row" [class.failed]="!e.success" [class.step]="isStepRow(e)"
                          [attr.title]="(e.resultDetail || '') + (e.resultDetail ? '  —  ' : '') + e.triggerType">
                        <span class="exec-status" [class.ok]="e.success" [class.bad]="!e.success">{{ e.success ? '✓' : '✗' }}</span>
                        <span class="exec-action" dir="auto">{{ e.executedAction }}</span>
                        <span class="exec-time">{{ e.executedAt | date:'dd/MM/yy HH:mm' }}</span>
                      </li>
                    }
                  </ul>
                </div>
              }
            }
          </div>
        }
      }
    </div>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; align-items: center; gap: 16px; }
    /* Stack the detail sections (stage card, fix-failure banner, detail grid,
       execution history) with consistent spacing. .card carries no margin. */
    .detail-root { display: flex; flex-direction: column; gap: 16px; }
    .breadcrumb { display: flex; align-items: center; gap: 4px; }

    .stage-pipeline { display: flex; align-items: center; justify-content: center; padding: 8px 0; }
    .stage { display: flex; flex-direction: column; align-items: center; gap: 6px; opacity: 0.4;
      &.active { opacity: 1; .stage-dot { background: var(--primary); border-color: var(--primary); color: #fff; box-shadow: 0 0 0 4px var(--primary-glow); } }
      &.done   { opacity: 1; .stage-dot { background: var(--success); border-color: var(--success); color: #fff; } }
    }
    .stage-dot { width: 36px; height: 36px; border-radius: 50%; border: 2px solid var(--border); background: var(--surface-2); display: flex; align-items: center; justify-content: center; font-size: 16px; }
    .stage-label { font-size: 11px; font-weight: 600; color: var(--text-muted); white-space: nowrap; }
    .stage-connector { flex: 1; height: 2px; background: var(--border); margin: 0 8px; margin-bottom: 18px; &.done { background: var(--success); } }

    .detail-grid { display: grid; grid-template-columns: 1fr 1.2fr; gap: 16px; }
    .detail-list { display: grid; grid-template-columns: auto 1fr; gap: 8px 16px; align-items: start;
      dt { font-size: 11px; font-weight: 600; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; white-space: nowrap; padding-top: 2px; }
      dd { font-size: 13px; }
      /* Source ID value reads as an identifier — monospace + slightly smaller,
         same body color so it doesn't look "demoted". The label stays as-is. */
      dd.font-mono {
        font-family: 'Fira Code', ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 0.875em;
        color: var(--text);
        word-break: break-all;
      }
    }
    .error-message-box {
      margin-top: 16px; padding-top: 16px; border-top: 1px solid var(--border);
      label { font-size: 11px; font-weight: 600; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; display: block; margin-bottom: 8px; }
      pre { font-family: 'Fira Code', monospace; font-size: 11px; color: var(--danger); background: var(--danger-bg); border-radius: var(--radius-sm); padding: 12px; overflow-x: auto; white-space: pre-wrap; word-break: break-word; }
    }

    /* Card-header right side: status badge + optional Mark Resolved button.
       Inline-flex keeps them on one row aligned right; gap matches the
       drawer's rest-of-app spacing. */
    .header-actions { display: inline-flex; align-items: center; gap: 8px; }

    /* Prominent banner when a fix failed to execute — sits above the detail
       grid so the operator can't miss it. Red family matches the error box. */
    .fix-failure-alert {
      display: flex; align-items: center; gap: 10px;
      background: var(--danger-bg); border: 1px solid var(--danger);
      color: var(--danger); border-radius: var(--radius-sm);
      padding: 10px 14px; font-size: 13px; line-height: 1.4;
      .alert-icon { font-weight: 700; font-size: 15px; flex-shrink: 0; }
      strong { font-weight: 700; }
    }

    /* Execution History — chronological ✓/✗ list. Composite step rows are
       indented under the summary; failed rows are tinted red. */
    .history-toggle {
      background: none; border: none; cursor: pointer; padding: 0;
      color: var(--primary, #6366f1); font-size: 12px; font-weight: 600;
      text-decoration: underline;
    }
    .exec-cycle { margin-top: 10px; }
    .exec-cycle-head { display: flex; align-items: baseline; gap: 8px; margin-bottom: 4px; }
    .exec-attempt { font-size: 12px; font-weight: 700; color: var(--text); }
    .exec-cycle-time { font-size: 11px; color: var(--text-dim); }
    .exec-list { display: flex; flex-direction: column; gap: 4px; }
    .exec-row {
      display: flex; gap: 8px; align-items: center;
      padding: 5px 10px; border-radius: var(--radius-sm);
      background: var(--surface-2); border: 1px solid var(--border-light);
      &.failed { background: var(--danger-bg); border-color: var(--danger); }
      &.step   { margin-left: 20px; }
    }
    .exec-status {
      flex-shrink: 0; width: 14px; text-align: center;
      font-weight: 700; font-size: 13px;
      &.ok  { color: var(--success); }
      &.bad { color: var(--danger); }
    }
    .exec-action {
      flex: 1; min-width: 0; font-size: 12px; font-weight: 600;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .exec-time { flex-shrink: 0; font-size: 11px; color: var(--text-dim); white-space: nowrap; }

    .ai-panel .card-header h3 { display: flex; align-items: center; gap: 8px; }
    .ai-chip { background: linear-gradient(135deg, var(--primary), var(--accent)); color: #fff; font-size: 10px; font-weight: 800; padding: 2px 6px; border-radius: 4px; letter-spacing: 0.06em; }

    .rec-list { display: flex; flex-direction: column; gap: 12px; }
    .rec-card {
      background: var(--surface-2); border: 1px solid var(--border); border-radius: var(--radius);
      padding: 14px; display: flex; flex-direction: column; gap: 10px;
      transition: border-color var(--transition);
      &.executed { border-color: rgba(34,197,94,0.3); background: var(--success-bg); }
    }
    .rec-header { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
    .rec-action { font-size: 13px; font-weight: 500; color: var(--text); line-height: 1.5; }
    .composite-badge { margin-left: 6px; font-size: 10px; vertical-align: middle; cursor: help; }

    /* Inline composite step list — description-only bullets so the operator
       reads the human summary, not the raw payload. The full action details
       (SQL / scripts / URLs) live in the Fix Options config drawer. */
    .rec-steps         { margin: 4px 0 8px; }
    .rec-steps-loading { padding: 4px 0; }
    .rec-steps-list    {
      margin: 0; padding-left: 0;
      display: flex; flex-direction: column; gap: 6px;
      list-style: none;
    }
    .rec-step {
      font-size: 12px; line-height: 1.5; color: var(--text);
      display: flex; gap: 6px; align-items: baseline;
      .step-icon { flex-shrink: 0; width: 14px; text-align: center; font-weight: 700; }
      .step-text { flex: 1; min-width: 0; }
      &.step-ok      .step-icon { color: var(--success); }
      &.step-fail    .step-icon { color: var(--danger); }
      &.step-pending .step-icon { color: var(--text-dim); }
    }
    .rec-footer { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; }
    .autoheal-label { display: flex; align-items: center; gap: 4px; }
    .rec-actions { display: flex; gap: 6px; align-items: center; }
    .rec-actions-done .action-note { font-style: italic; }

    @media (max-width:900px) { .detail-grid { grid-template-columns: 1fr; } }
  `]
})
export class FailureDetailComponent implements OnDestroy {
  private failureSvc = inject(FailuresService);
  private recSvc    = inject(RecommendationsService);
  private configSvc = inject(ConfigService);
  private auth      = inject(AuthService);
  private notify    = inject(NotificationService);

  /** Approve/reject/retry/mark-resolved are Operator+. A read-only User sees the
   *  failure detail but no action controls (cosmetic; the API enforces too). */
  canAct = computed(() => this.auth.hasAtLeast('Operator'));

  /** Cache of composite policy step lists, keyed by ruleId. Lazy-fetched on
   *  failure load and on every silent refresh — but cached per ruleId so a
   *  poll tick doesn't re-fetch what we already have. Steps are static once
   *  the policy is loaded (editing the policy doesn't mutate already-issued
   *  recs' snapshot, but the live policy projection used here CAN change if
   *  the operator edits steps between polls — fetching on every load keeps
   *  the displayed step list in sync). */
  policySteps = signal<Map<number, FixPolicyRuleStep[]>>(new Map());

  /** Host (drawer) sets this; component re-fetches on every change via the
   *  effect below — so navigating ↑/↓ between failures inside the drawer
   *  swaps the data without unmounting the component. */
  failureId = input.required<number>();

  loading = signal(true);
  failure = signal<FailureStatus | null>(null);

  // Live polling cadence while the drawer is open. Matches the dashboard
  // service's interval so the operator sees status updates from background
  // drains (auto-heal resolving a failure mid-review, etc.) without ever
  // having to refresh. Re-fetches are SILENT — no loading flag, no template
  // remount — so scroll position and focus stay put.
  private static readonly POLL_MS = 5000;
  private pollTimerId: ReturnType<typeof setInterval> | null = null;

  // After "Recommended" the failure branches: either Acknowledged (operator
  // approved a Manual rec, off-system action pending) OR Manual (operator
  // rejected, or auto-heal failed → operator must handle manually). Both
  // converge at Fixed. Render whichever middle stage the failure is actually
  // in — never both — so the pipeline doesn't pretend the operator did
  // contradictory things.
  private static readonly STAGE_ACK    = { key: 'Acknowledged', label: 'Acknowledged', icon: '👤' };
  private static readonly STAGE_MANUAL = { key: 'Manual',       label: 'Manual',       icon: '⚙' };
  stages = computed(() => {
    const middle = this.failure()?.status === 'ManualRequired'
      ? FailureDetailComponent.STAGE_MANUAL
      : FailureDetailComponent.STAGE_ACK;
    return [
      { key: 'Failed',      label: 'Detected',    icon: '⚠' },
      { key: 'Classified',  label: 'Classified',  icon: '🔍' },
      { key: 'Recommended', label: 'Recommended', icon: '💡' },
      middle,
      { key: 'Fixed',       label: 'Fixed',       icon: '✓' },
    ];
  });

  /** Fix-execution history from the status payload, chronological. */
  executions = computed<FixExecution[]>(() => this.failure()?.executions ?? []);

  /** Composite step rows are written by DefaultFixEngine.Composite; the single
   *  overall summary row is written by ExecuteFixesUseCase and CLOSES a cycle.
   *  Used to indent per-step rows and to split history into attempts. */
  isStepRow(e: FixExecution): boolean { return e.executedBy.endsWith('.Composite'); }

  /** Group the flat execution log into attempts (cycles), newest first. Each
   *  cycle = the run of composite-step rows terminated by the summary row a
   *  drain writes; a Retry produces a fresh cycle. */
  cyclesNewestFirst = computed(() => {
    const groups: FixExecution[][] = [];
    let cur: FixExecution[] = [];
    for (const e of this.executions()) {
      cur.push(e);
      if (!this.isStepRow(e)) { groups.push(cur); cur = []; }
    }
    if (cur.length) groups.push(cur);   // trailing steps with no summary (rare/mid-flight)
    return groups
      .map((rows, i) => ({
        attempt: i + 1,
        rows,
        at:      rows[rows.length - 1].executedAt,
        trigger: rows[rows.length - 1].triggerType,
      }))
      .reverse();
  });

  /** Banner reflects only the LATEST attempt, not the lifetime total across
   *  every retry — "3 of 4 actions failed" should mean this rerun, not 22/28. */
  private lastCyclePool = computed<FixExecution[]>(() => {
    const rows  = this.cyclesNewestFirst()[0]?.rows ?? [];
    const steps = rows.filter(e => this.isStepRow(e));
    return steps.length > 0 ? steps : rows;   // composite → its steps; single-action → the summary row
  });
  lastCycleFailedCount = computed(() => this.lastCyclePool().filter(e => !e.success).length);
  lastCycleTotal       = computed(() => this.lastCyclePool().length);
  hasFixFailure        = computed(() => this.lastCycleFailedCount() > 0);

  /** History list is collapsed by default — the rec card already shows the
   *  latest per-step ✓/✗, so the full audit trail of past attempts is opt-in. */
  historyExpanded = signal(false);

  /** Execution outcome of a composite step, matched by recommendation + step
   *  order against the per-step execution rows (ExecutedAction = "Step N: …").
   *  'pending' = no execution row yet (not run, or single-action). Latest
   *  matching attempt wins. Drives the ✓/✗/• icon on the rec-card step list. */
  stepStatus(rec: Recommendation, stepOrder: number): 'ok' | 'fail' | 'pending' {
    const prefix = `Step ${stepOrder}:`;
    const matches = this.executions().filter(e =>
      e.recommendationId === rec.recommendationId
      && this.isStepRow(e)
      && e.executedAction.startsWith(prefix));
    if (matches.length === 0) return 'pending';
    return matches[matches.length - 1].success ? 'ok' : 'fail';
  }
  stepIcon(s: 'ok' | 'fail' | 'pending'): string {
    return s === 'ok' ? '✓' : s === 'fail' ? '✗' : '•';
  }

  /** Inflight flag for the Mark Resolved button so we can disable + spinner. */
  markingResolved = signal(false);

  /** Human-friendly status label — adds a space to the camel-cased enum
   *  values so "AwaitingManualAction" reads as "Awaiting Manual Action". */
  statusLabel(status: string): string {
    return status.replace(/([A-Z])/g, ' $1').trim();
  }

  /** Operator confirms the off-system work is done. Backend is idempotent
   *  (re-marking an already-Resolved failure returns 204), so a double-click
   *  doesn't error. Reload the detail to pull the new Status + Stage. */
  markResolved() {
    this.markingResolved.set(true);
    this.failureSvc.markResolved(this.failureId()).subscribe({
      next:  () => { this.markingResolved.set(false); this.reload(); },
      error: () => { this.markingResolved.set(false); },
    });
  }

  constructor() {
    effect((onCleanup) => {
      const id = this.failureId();
      this.clearPoll();
      if (!id) return;
      // Initial load surfaces a spinner; subsequent polled refreshes are silent.
      this.loadDetail(id, { silent: false });
      this.pollTimerId = setInterval(
        () => this.loadDetail(this.failureId(), { silent: true }),
        FailureDetailComponent.POLL_MS,
      );
      onCleanup(() => this.clearPoll());
    });
  }

  ngOnDestroy() { this.clearPoll(); }

  private clearPoll() {
    if (this.pollTimerId !== null) {
      clearInterval(this.pollTimerId);
      this.pollTimerId = null;
    }
  }

  private loadDetail(id: number, opts: { silent: boolean } = { silent: false }) {
    if (!opts.silent) this.loading.set(true);
    this.failureSvc.getFailureStatus(id).subscribe({
      next: f => {
        this.failure.set(f);
        if (!opts.silent) this.loading.set(false);
        this.loadStepsForComposites(f.recommendations);
      },
      // Silent poll refreshes stay quiet (they'd spam a toast every 5s during an
      // outage); only the initial, operator-visible load surfaces an error.
      error: () => {
        if (!opts.silent) { this.loading.set(false); this.notify.error('Could not load failure details.'); }
      }
    });
  }

  /** For every recommendation backed by a Composite policy, fetch the policy
   *  once and cache its steps so the rec card can render them inline. Skips
   *  recs already in the cache (poll ticks won't re-fetch). Per-rec fire-and-
   *  forget; one slow policy fetch doesn't block another. */
  private loadStepsForComposites(recs: Recommendation[]) {
    const cache = this.policySteps();
    for (const rec of recs) {
      if (rec.policyStepCount <= 0 || rec.fixPolicyRuleId == null) continue;
      if (cache.has(rec.fixPolicyRuleId)) continue;
      const ruleId = rec.fixPolicyRuleId;
      this.configSvc.getFixPolicyRuleById(ruleId).subscribe({
        next: policy => {
          const next = new Map(this.policySteps());
          next.set(ruleId, policy.steps ?? []);
          this.policySteps.set(next);
        },
        // Swallow — a failed fetch just means the badge shows without the
        // expanded step list. Not worth a UI banner.
        error: () => {},
      });
    }
  }

  /** Template helper: returns the cached step list for a rec, or empty. */
  stepsFor(rec: Recommendation): FixPolicyRuleStep[] {
    if (rec.fixPolicyRuleId == null) return [];
    return this.policySteps().get(rec.fixPolicyRuleId) ?? [];
  }

  /** Public so the host can force a refresh after an operator action. */
  reload() {
    this.loadDetail(this.failureId(), { silent: true });
  }

  isStageCompleted(key: string): boolean {
    // Acknowledged and Manual are alternative middle stages at the same
    // position — only one is in the rendered `stages()` array for any given
    // failure. Use the actual rendered keys (via stages()) to drive the
    // ordering so this stays in sync with whichever middle stage was
    // picked. Without this, indexOf for the missing alternative would
    // return -1 and falsely mark it as a completed/past step (see the
    // earlier Acknowledged bug — same trap, two alternatives now).
    const order = this.stages().map(s => s.key);
    const current = this.failure()?.stage ?? 'Failed';
    return order.indexOf(key) < order.indexOf(current);
  }

  categoryBadge(cat: string): string {
    const map: Record<string, string> = {
      AutoFix: 'badge-success', Manual: 'badge-warning', Notify: 'badge-info', Escalate: 'badge-failed'
    };
    return map[cat] ?? 'badge-info';
  }

  approve(rec: Recommendation) {
    this.recSvc.approveRecommendation(rec.recommendationId).subscribe({
      next: () => this.reload(),
      error: () => rec.operatorApproved = true
    });
    rec.operatorApproved = true;
  }

  reject(rec: Recommendation) {
    this.recSvc.rejectRecommendation(rec.recommendationId).subscribe({
      next: () => this.reload(),
      error: () => rec.operatorApproved = false
    });
    rec.operatorApproved = false;
  }

  /** True when THIS rec has a failed execution row — i.e. its fix tried and
   *  failed. Drives the "Retry Fix" button (only meaningful in ManualRequired). */
  recFailedToExecute(rec: Recommendation): boolean {
    return this.executions().some(e => e.recommendationId === rec.recommendationId && !e.success);
  }

  /** Inflight guard for the retry button so a double-click can't double-fire. */
  retrying = signal<number | null>(null);

  /** Re-run a fix that failed to execute (after the operator fixed the root
   *  cause). Backend re-arms the failure + rec and drains synchronously, then
   *  we reload to show the new outcome. */
  retry(rec: Recommendation) {
    this.retrying.set(rec.recommendationId);
    this.recSvc.retryRecommendation(rec.recommendationId).subscribe({
      next:  () => { this.retrying.set(null); this.reload(); },
      error: () => { this.retrying.set(null); },
    });
  }
}
