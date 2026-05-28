import { Component, OnDestroy, effect, inject, input, signal } from '@angular/core';
import { DatePipe, PercentPipe } from '@angular/common';
import { FailuresService } from '../../core/services/failures.service';
import { RecommendationsService } from '../../core/services/recommendations.service';
import { FailureStatus, Recommendation } from '../../core/models';
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
            @for (s of stages; track s.key) {
              <div class="stage" [class.active]="s.key === failure()!.stage" [class.done]="isStageCompleted(s.key)">
                <div class="stage-dot">{{ s.icon }}</div>
                <span class="stage-label">{{ s.label }}</span>
              </div>
              @if (!$last) { <div class="stage-connector" [class.done]="isStageCompleted(s.key)"></div> }
            }
          </div>
        </div>

        <div class="detail-grid">
          <!-- Failure info -->
          <div class="card">
            <div class="card-header">
              <h3>Failure Details</h3>
              <span class="badge" [class]="'badge-' + failure()!.status.toLowerCase()">{{ failure()!.status }}</span>
            </div>
            <dl class="detail-list">
              <dt>Job</dt>        <dd dir="auto">{{ failure()!.monitoredJobName ?? '—' }}</dd>
              <dt>Step / File</dt><dd dir="auto">{{ failure()!.stepName ?? '—' }}</dd>
              <dt>Source ID</dt>  <dd class="font-mono">{{ failure()!.sourceId ?? '—' }}</dd>
              <dt>Error Type</dt> <dd>
                @if (failure()!.errorTypeCode) {
                  <span class="badge badge-medium">{{ failure()!.errorTypeCode }}</span>
                } @else { <span class="text-muted">Not yet classified</span> }
              </dd>
              <dt>Detected</dt>   <dd>{{ failure()!.detectedAt | date:'medium' }}</dd>
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
                      @if (rec.isExecuted) { <span class="badge badge-fixed">Executed</span> }
                      @if (rec.operatorApproved === true)  { <span class="badge badge-resolved">Approved</span> }
                      @if (rec.operatorApproved === false) { <span class="badge badge-failed">Rejected</span> }
                    </div>

                    <p class="rec-action" dir="auto">{{ rec.suggestedAction }}</p>

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

                      @if (!rec.isExecuted && rec.operatorApproved === null) {
                        <div class="rec-actions">
                          <button class="btn btn-success btn-sm" (click)="approve(rec)">✓ Approve</button>
                          <button class="btn btn-danger btn-sm"  (click)="reject(rec)">✕ Reject</button>
                        </div>
                      } @else {
                        <!-- Graceful disabled-state. If the rec resolved mid-review (via
                             background drain), we keep the action buttons present-but-
                             disabled and label the new state, so the operator doesn't
                             have controls vanish under their cursor. -->
                        <div class="rec-actions rec-actions-done">
                          <button class="btn btn-success btn-sm" disabled>✓ Approve</button>
                          <button class="btn btn-danger btn-sm"  disabled>✕ Reject</button>
                          <span class="text-sm text-muted action-note">
                            @if (rec.isExecuted) { Already executed }
                            @else if (rec.operatorApproved === true)  { Already approved }
                            @else { Already rejected }
                          </span>
                        </div>
                      }
                    </div>
                  </div>
                }
              </div>
            }
          </div>
        </div>
      }
    </div>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; align-items: center; gap: 16px; }
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

  stages = [
    { key: 'Failed',      label: 'Detected',    icon: '⚠' },
    { key: 'Classified',  label: 'Classified',  icon: '🔍' },
    { key: 'Recommended', label: 'Recommended', icon: '💡' },
    { key: 'Fixed',       label: 'Fixed',       icon: '✓' },
  ];

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
      next: f => { this.failure.set(f); if (!opts.silent) this.loading.set(false); },
      error: () => { if (!opts.silent) this.loading.set(false); }
    });
  }

  /** Public so the host can force a refresh after an operator action. */
  reload() {
    this.loadDetail(this.failureId(), { silent: true });
  }

  isStageCompleted(key: string): boolean {
    const order = ['Failed', 'Classified', 'Recommended', 'Fixed'];
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
    this.recSvc.approveRecommendation(rec.recommendationId, 'operator').subscribe({
      next: () => this.reload(),
      error: () => rec.operatorApproved = true
    });
    rec.operatorApproved = true;
  }

  reject(rec: Recommendation) {
    this.recSvc.rejectRecommendation(rec.recommendationId, 'operator').subscribe({
      next: () => this.reload(),
      error: () => rec.operatorApproved = false
    });
    rec.operatorApproved = false;
  }
}
