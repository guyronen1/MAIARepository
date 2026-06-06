import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import {
  UnconfiguredService, UnconfiguredWindow,
  ClustersResponse, UnclassifiedCluster, PolicyGapsResponse, PolicyGap,
} from '../../core/services/unconfigured.service';
import { ScanService } from '../../core/services/scan.service';
import {
  ConfigService, JobType, ErrorType, UpsertClassificationRuleRequest,
} from '../../core/services/config.service';
import { DrawerComponent } from '../../shared/drawer/drawer.component';
import { PluralizePipe } from '../../core/pipes/pluralize.pipe';

/**
 * Coverage-gap surface. Two read-only sections over a 30-day (or all-time)
 * window:
 *   • Case A — unclassified failures clustered into suggested ClassificationRule
 *     patterns (n-gram analyzer). "Configure" opens a focused drawer that
 *     pre-fills the pattern and records provenance on save.
 *   • Case B — classified failures with no effective FixPolicyRule, aggregated
 *     by (ErrorType, Job). "Configure fix" deep-links into the job's Fix
 *     Options drawer pre-filled (we don't rebuild the composite editor here).
 */
@Component({
  selector: 'app-unconfigured',
  standalone: true,
  imports: [FormsModule, DrawerComponent, PluralizePipe],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Unconfigured</h1>
          <p class="text-muted text-sm">Failures MAIA detected but can't fully act on — gaps in classification or fix policy.</p>
        </div>
        <div class="window-toggle">
          <button class="btn btn-sm" [class.btn-primary]="window() === '30d'" [class.btn-ghost]="window() !== '30d'"
                  (click)="setWindow('30d')">30 days</button>
          <button class="btn btn-sm" [class.btn-primary]="window() === 'all'" [class.btn-ghost]="window() !== 'all'"
                  (click)="setWindow('all')">All time</button>
        </div>
      </div>

      @if (banner()) {
        <div class="info-banner">{{ banner() }}<button class="btn btn-ghost btn-sm" (click)="banner.set(null)">✕</button></div>
      }

      <!-- ── Case A: unclassified clusters ──────────────────────────────────── -->
      <div class="card section">
        <div class="card-header">
          <h3><span class="ai-chip">A</span> Unclassified failures</h3>
          @if (clusters(); as r) {
            <span class="text-muted text-sm">
              {{ r.totalUnclassified | pluralize:'failure' }} → {{ r.clusters.length | pluralize:'pattern' }}
              · {{ r.uncategorizedCount }} uncategorized
            </span>
          }
        </div>

        @if (loadingA()) {
          <div class="loading-overlay"><span class="spinner"></span> Analyzing…</div>
        } @else if (!clusters() || clusters()!.clusters.length === 0) {
          <div class="empty-state"><span class="empty-icon">✓</span><p>No recurring unclassified patterns in this window</p></div>
        } @else {
          <ul class="gap-list">
            @for (c of clusters()!.clusters; track c.suggestedFromHash) {
              <li class="gap-row">
                <div class="gap-main">
                  <div class="gap-line">
                    <span class="count-pill">{{ c.failureCount }}×</span>
                    <code class="pattern">{{ c.suggestedPattern }}</code>
                  </div>
                  <div class="normalized">normalized from <code>{{ c.normalizedSample }}</code></div>
                  <div class="samples text-muted text-sm">
                    Sample failures: {{ sampleLabel(c) }}
                  </div>
                  @if (showSamples().has(c.suggestedFromHash)) {
                    <ul class="sample-msgs">
                      @for (m of c.sampleMessages; track $index) { <li dir="auto">{{ m }}</li> }
                    </ul>
                  }
                </div>
                <div class="gap-actions">
                  <button class="btn btn-primary btn-sm" (click)="openConfigure(c)">Configure as ClassificationRule</button>
                  <button class="btn btn-ghost btn-sm" (click)="toggleSamples(c)">
                    {{ showSamples().has(c.suggestedFromHash) ? 'Hide samples' : 'View sample messages' }}
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      </div>

      <!-- ── Case B: classified, no fix policy ──────────────────────────────── -->
      <div class="card section">
        <div class="card-header">
          <h3><span class="ai-chip b">B</span> Classified, no fix policy</h3>
          @if (gaps(); as g) { <span class="text-muted text-sm">{{ g.totalGaps | pluralize:'recommendation' }} across {{ g.gaps.length | pluralize:'gap' }}</span> }
        </div>

        @if (loadingB()) {
          <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
        } @else if (!gaps() || gaps()!.gaps.length === 0) {
          <div class="empty-state"><span class="empty-icon">✓</span><p>Every classified failure has a fix policy in this window</p></div>
        } @else {
          <table class="data-table">
            <thead><tr><th>Error Type</th><th>Job</th><th>Job Type</th><th>Recs</th><th></th></tr></thead>
            <tbody>
              @for (g of gaps()!.gaps; track g.errorTypeId + '-' + g.monitoredJobId) {
                <tr>
                  <td><span class="badge badge-classified">{{ g.errorTypeCode }}</span></td>
                  <td dir="auto">{{ g.monitoredJobName ?? '—' }}</td>
                  <td><span class="badge badge-info">{{ g.jobTypeName }}</span></td>
                  <td>{{ g.count }}</td>
                  <td><button class="btn btn-primary btn-sm" (click)="configureFix(g)">Configure fix →</button></td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    </div>

    <!-- ── Case A configure drawer (focused classification-rule form) ───────── -->
    <app-drawer [open]="configuring() !== null" [ariaLabel]="'Configure classification rule'" (close)="closeConfigure()">
      <ng-container drawer-title>
        <span class="text-muted text-sm">New</span><strong>&nbsp;Classification Rule</strong>
      </ng-container>
      @if (configuring(); as c) {
        <div class="cfg-form">
          <p class="text-sm text-muted">Suggested from <strong>{{ c.failureCount }}</strong> unclassified
            {{ c.failureCount === 1 ? 'failure' : 'failures' }} (analyzer: {{ c.analyzerVersion }}).</p>

          <div class="form-group">
            <label>Match Pattern *</label>
            <input [(ngModel)]="form.pattern" placeholder="substring or *-wildcard" />
            <span class="field-hint">Case-insensitive substring; <code>*</code> = any text. Edit freely before saving.</span>
          </div>
          <div class="form-row">
            <div class="form-group">
              <label>Job Type *</label>
              <select [(ngModel)]="form.jobTypeId">
                <option [ngValue]="0" disabled>Select…</option>
                @for (t of jobTypes(); track t.jobTypeId) { <option [ngValue]="t.jobTypeId">{{ t.name }}</option> }
              </select>
            </div>
            <div class="form-group">
              <label>Error Type *</label>
              <select [(ngModel)]="form.errorTypeId">
                <option [ngValue]="0" disabled>Select…</option>
                @for (e of errorTypes(); track e.errorTypeId) { <option [ngValue]="e.errorTypeId">{{ e.code }}</option> }
              </select>
            </div>
          </div>
          <div class="form-row">
            <div class="form-group">
              <label>Confidence (0–1)</label>
              <input type="number" [(ngModel)]="form.confidence" min="0" max="1" step="0.05" />
            </div>
            <div class="form-group">
              <label>Priority</label>
              <input type="number" [(ngModel)]="form.priority" min="1" />
            </div>
          </div>

          @if (saveError()) { <div class="dup-warn">⚠ {{ saveError() }}</div> }

          <div class="cfg-footer">
            <button class="btn btn-ghost" (click)="closeConfigure()">Cancel</button>
            <button class="btn btn-primary" (click)="saveClassRule()" [disabled]="saving() || !form.pattern || !form.jobTypeId || !form.errorTypeId">
              @if (saving()) { <span class="spinner"></span> }
              Create &amp; re-classify
            </button>
          </div>
        </div>
      }
    </app-drawer>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }
    .window-toggle { display: flex; gap: 6px; }
    .ai-chip { background: linear-gradient(135deg, var(--primary), var(--accent)); color:#fff; font-size:10px; font-weight:800; padding:2px 7px; border-radius:6px; margin-right:6px; }
    .ai-chip.b { background: linear-gradient(135deg, #f59e0b, #d97706); }
    .section { margin-bottom: 16px; }
    .card-header { display:flex; align-items:center; justify-content:space-between; }
    .info-banner { display:flex; align-items:center; gap:12px; background:var(--info-bg); border:1px solid rgba(56,189,248,0.3); border-radius:var(--radius); padding:10px 16px; font-size:13px; margin-bottom:12px; button{margin-left:auto;} }

    .gap-list { list-style:none; margin:8px 0 0; padding:0; display:flex; flex-direction:column; gap:8px; }
    .gap-row { display:flex; gap:12px; align-items:flex-start; justify-content:space-between;
      padding:10px 12px; border:1px solid var(--border-light); border-radius:var(--radius-sm); background:var(--surface-2); }
    .gap-main { min-width:0; flex:1; }
    .gap-line { display:flex; align-items:center; gap:8px; }
    .count-pill { background:var(--primary-glow,rgba(99,102,241,0.12)); color:var(--primary,#6366f1); font-weight:700; font-size:12px; padding:2px 8px; border-radius:10px; flex-shrink:0; }
    .pattern { font-size:13px; font-weight:600; word-break:break-word; }
    .normalized { font-size:11px; color:var(--text-dim); margin-top:3px; word-break:break-word; }
    .normalized code { font-size:11px; }
    .samples { margin-top:3px; }
    .sample-msgs { margin:6px 0 0; padding-left:18px; font-size:11px; color:var(--text-muted); }
    .sample-msgs li { word-break:break-word; }
    .gap-actions { display:flex; flex-direction:column; gap:6px; flex-shrink:0; }

    .cfg-form { display:flex; flex-direction:column; gap:14px; }
    .form-row { display:grid; grid-template-columns:1fr 1fr; gap:12px; }
    .form-group { display:flex; flex-direction:column; gap:4px; }
    .field-hint { font-size:11px; color:var(--text-dim); }
    .cfg-footer { display:flex; justify-content:flex-end; gap:8px; margin-top:6px; }
    .dup-warn { background:#fef3c7; border:1px solid #fde68a; color:#78350f; font-size:12px; padding:8px 10px; border-radius:var(--radius-sm); }
  `]
})
export class UnconfiguredComponent implements OnInit {
  private svc     = inject(UnconfiguredService);
  private cfgSvc  = inject(ConfigService);
  private scanSvc = inject(ScanService);
  private router  = inject(Router);

  window     = signal<UnconfiguredWindow>('30d');
  loadingA   = signal(false);
  loadingB   = signal(false);
  clusters   = signal<ClustersResponse | null>(null);
  gaps       = signal<PolicyGapsResponse | null>(null);
  banner     = signal<string | null>(null);
  showSamples = signal<Set<string>>(new Set());

  jobTypes   = signal<JobType[]>([]);
  errorTypes = signal<ErrorType[]>([]);

  // Case A configure drawer
  configuring = signal<UnclassifiedCluster | null>(null);
  saving      = signal(false);
  saveError   = signal<string | null>(null);
  form: UpsertClassificationRuleRequest = this.blankForm();

  ngOnInit() {
    this.cfgSvc.getJobTypes().subscribe({ next: t => this.jobTypes.set(t) });
    this.cfgSvc.getErrorTypes().subscribe({ next: t => this.errorTypes.set(t) });
    this.reload();
  }

  setWindow(w: UnconfiguredWindow) { if (this.window() !== w) { this.window.set(w); this.reload(); } }

  reload() {
    const w = this.window();
    this.loadingA.set(true);
    this.svc.getClusters(w).subscribe({
      next: r => { this.clusters.set(r); this.loadingA.set(false); },
      error: () => this.loadingA.set(false),
    });
    this.loadingB.set(true);
    this.svc.getPolicyGaps(w).subscribe({
      next: g => { this.gaps.set(g); this.loadingB.set(false); },
      error: () => this.loadingB.set(false),
    });
  }

  sampleLabel(c: UnclassifiedCluster): string {
    const shown = c.sampleFailureIds.join(', ');
    const more  = c.failureCount - c.sampleFailureIds.length;
    return more > 0 ? `${shown} (+${more} more)` : shown;
  }

  toggleSamples(c: UnclassifiedCluster) {
    const next = new Set(this.showSamples());
    if (next.has(c.suggestedFromHash)) next.delete(c.suggestedFromHash);
    else next.add(c.suggestedFromHash);
    this.showSamples.set(next);
  }

  // ── Case A: configure a ClassificationRule ───────────────────────────────
  openConfigure(c: UnclassifiedCluster) {
    this.saveError.set(null);
    this.form = { ...this.blankForm(), pattern: c.suggestedPattern };
    this.configuring.set(c);
  }
  closeConfigure() { this.configuring.set(null); this.saving.set(false); }

  saveClassRule() {
    const c = this.configuring();
    if (!c || !this.form.pattern || !this.form.jobTypeId || !this.form.errorTypeId) return;
    this.saving.set(true);
    this.saveError.set(null);
    const req: UpsertClassificationRuleRequest = {
      ...this.form,
      // Provenance — the v2 training signal.
      suggestedBy:         c.analyzerVersion,
      suggestedFromHash:   c.suggestedFromHash,
      suggestedConfidence: c.confidenceScore,
    };
    this.cfgSvc.createClassificationRule(req).subscribe({
      next: () => {
        // Apply the new rule to existing unclassified failures right away so
        // the cluster actually clears, then refresh both sections.
        this.scanSvc.classifyPending().subscribe({
          next: r => {
            this.banner.set(`Rule created — re-classified ${r.classified}, ${r.suggestions} suggestion(s) generated.`);
            this.closeConfigure();
            this.reload();
          },
          error: () => { this.banner.set('Rule created. Re-classification will run on the next scan.'); this.closeConfigure(); this.reload(); },
        });
      },
      error: (err) => { this.saving.set(false); this.saveError.set(err?.error?.message || err?.message || 'Save failed.'); },
    });
  }

  // ── Case B: deep-link into the job's Fix Options drawer, pre-filled ───────
  configureFix(g: PolicyGap) {
    this.router.navigate(['/config/monitored-jobs'], {
      queryParams: { fixForJob: g.monitoredJobId, errorTypeId: g.errorTypeId },
    });
  }

  private blankForm(): UpsertClassificationRuleRequest {
    return { jobTypeId: 0, errorTypeId: 0, pattern: '', confidence: 0.9, priority: 1, isActive: true };
  }
}
