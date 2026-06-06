import { Component, DestroyRef, HostListener, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, PercentPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RecommendationsService } from '../../core/services/recommendations.service';
import { ScanService } from '../../core/services/scan.service';
import { ConfigService, FixPolicyRule, UpsertFixPolicyRuleRequest } from '../../core/services/config.service';
import { Recommendation, PagedResult } from '../../core/models';
import { PluralizePipe, pluralize } from '../../core/pipes/pluralize.pipe';
import { FailureDetailComponent } from '../failures/failure-detail.component';
import { DrawerComponent } from '../../shared/drawer/drawer.component';

@Component({
  selector: 'app-recommendations',
  standalone: true,
  imports: [DatePipe, PercentPipe, FormsModule, PluralizePipe, FailureDetailComponent, DrawerComponent],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>
            @if (isOperatorMode()) {
              <span class="op-chip">OA</span>
              Operator Actions
            } @else {
              <span class="ai-chip">AI</span>
              Recommendations
            }
          </h1>
          <p class="text-muted text-sm">
            @if (isOperatorMode()) {
              {{ filtered().length | pluralize:'pending item' }} awaiting your review
            } @else {
              {{ paged()?.totalCount ?? 0 }} total recommendations
            }
          </p>
        </div>
        @if (!isOperatorMode()) {
          <div class="page-actions">
            <button class="btn btn-ghost btn-sm" (click)="classifyPending()" [disabled]="running()">
              @if (running()) { <span class="spinner"></span> }
              Classify Pending
            </button>
            <button class="btn btn-primary btn-sm" (click)="runPipeline()" [disabled]="running()">
              @if (running()) { <span class="spinner"></span> }
              Run Full Pipeline
            </button>
          </div>
        }
      </div>

      @if (banner()) {
        <div class="info-banner">{{ banner() }}
          <button class="btn btn-ghost btn-sm" (click)="banner.set(null)">✕</button>
        </div>
      }

      <div class="page-filters">
        <input [(ngModel)]="filterText" placeholder="Filter by action, error type…" (input)="applyFilter()" style="min-width:220px" />
        @if (!isOperatorMode()) {
          <select [(ngModel)]="filterCategory" (change)="applyFilter()">
            <option value="">All categories</option>
            <option value="Retry">Retry</option>
            <option value="FileRepair">FileRepair</option>
            <option value="DbFix">DbFix</option>
            <option value="Manual">Manual</option>
          </select>
          <select [(ngModel)]="filterApproved" (change)="applyFilter()">
            <option value="">All states</option>
            <option value="pending">Pending Review</option>
            <option value="approved">Approved</option>
            <option value="rejected">Rejected</option>
            <option value="executed">Executed</option>
          </select>
        }
      </div>

      <div class="card" style="padding:0;overflow:hidden">
        @if (loading()) {
          <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
        } @else if (filtered().length === 0) {
          <div class="empty-state">
            <span class="empty-icon">💡</span>
            <p>No recommendations yet</p>
            <p class="text-sm text-muted">Run a scan and classify pending failures first</p>
          </div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Failure</th>
                <th>Category</th>
                <th>Suggested Action</th>
                <th>Confidence</th>
                <th title="Toggles FixPolicyRule.IsAutoHealEligible — affects future recommendations for this ErrorType">Auto-heal policy</th>
                <th title="Whether this specific recommendation will run on the next drain (frozen at generation time)">Auto-run</th>
                <th>State</th>
                <th>Date</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (r of filtered(); track r.recommendationId) {
                <tr [class.row-executed]="r.isExecuted">
                  <td>
                    <div class="failure-ref" (click)="openFailure(r.failureId)">
                      <span class="failure-id">#{{ r.failureId }}</span>
                      <span class="failure-type text-muted text-sm">{{ r.errorTypeCode }}</span>
                    </div>
                  </td>
                  <td><span class="badge" [class]="categoryBadge(r.fixCategory)">{{ r.fixCategory }}</span></td>
                  <td class="rec-action-cell" dir="auto">{{ r.suggestedAction }}</td>
                  <td>
                    <div class="confidence-bar" style="min-width:120px">
                      <div class="bar-track">
                        <div class="bar-fill" [style.width.%]="r.confidenceScore * 100"></div>
                      </div>
                      <span class="bar-value">{{ r.confidenceScore | percent }}</span>
                    </div>
                  </td>
                  <td>
                    @if (policyMissing(r)) {
                      <div class="policy-missing">
                        <label class="toggle disabled" title="No enabled FixPolicyRule for this error">
                          <input type="checkbox" disabled [checked]="false" />
                          <span class="slider"></span>
                        </label>
                        <button class="btn-link btn-sm" (click)="configurePolicy(r)">Configure policy</button>
                      </div>
                    } @else {
                      <label class="toggle">
                        <input type="checkbox"
                               [checked]="!!r.policyIsAutoHealEligible"
                               [disabled]="togglingRule() === r.fixPolicyRuleId"
                               (change)="toggleAutoHeal(r, $any($event.target).checked)" />
                        <span class="slider"></span>
                      </label>
                    }
                  </td>
                  <td>
                    @if (r.autoFixAvailable) {
                      <span class="badge badge-info">Yes</span>
                    } @else {
                      <span class="badge badge-muted">No</span>
                    }
                  </td>
                  <td>
                    @if (r.isExecuted) {
                      <span class="badge badge-fixed">Executed</span>
                    } @else if (r.operatorApproved === true) {
                      <span class="badge badge-resolved">Approved</span>
                    } @else if (r.operatorApproved === false) {
                      <span class="badge badge-failed">Rejected</span>
                    } @else {
                      <span class="badge badge-classified">Pending</span>
                    }
                  </td>
                  <td class="text-muted text-sm">{{ r.recommendedAt | date:'MM/dd HH:mm' }}</td>
                  <td>
                    @if (!r.isExecuted && r.operatorApproved === null) {
                      <div style="display:flex;gap:4px">
                        <button class="btn btn-success btn-sm" (click)="approve(r)">✓</button>
                        <button class="btn btn-danger btn-sm"  (click)="reject(r)">✕</button>
                      </div>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
          <div class="pagination">
            <button class="btn btn-ghost btn-sm" (click)="prevPage()" [disabled]="page() === 1">← Prev</button>
            <span class="text-muted text-sm">Page {{ page() }} of {{ paged()?.totalPages ?? 1 }}</span>
            <button class="btn btn-ghost btn-sm" (click)="nextPage()" [disabled]="page() >= (paged()?.totalPages ?? 1)">Next →</button>
          </div>
        }
      </div>

      <!-- Shared drawer hosting the failure detail — opens in-place via
           ?selected= instead of bouncing to /failures. Same component the
           failures list uses, so execution history / retry / per-step ✓✗
           all come along for free. -->
      <app-drawer
          [open]="selectedFailureId() !== null"
          [ariaLabel]="'Failure ' + selectedFailureId() + ' detail'"
          (close)="closeDrawer()">
        <ng-container drawer-title>
          <span class="text-muted text-sm">Failure</span>
          <strong>#{{ selectedFailureId() }}</strong>
          @if (selectedRecIndex() !== -1 && filtered().length > 0) {
            <span class="text-muted text-sm">· {{ selectedRecIndex() + 1 }} of {{ filtered().length }} on this page</span>
          }
        </ng-container>
        <ng-container drawer-controls>
          <button class="btn btn-ghost btn-sm nav-arrow" (click)="navigatePrev()"
                  [disabled]="!canNavPrev()" title="Previous (↑)">↑</button>
          <button class="btn btn-ghost btn-sm nav-arrow" (click)="navigateNext()"
                  [disabled]="!canNavNext()" title="Next (↓)">↓</button>
        </ng-container>
        @if (selectedFailureId() !== null) {
          <app-failure-detail [failureId]="selectedFailureId()!"></app-failure-detail>
        }
      </app-drawer>
    </div>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; display: flex; align-items: center; gap: 10px; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }
    .page-actions { display: flex; gap: 8px; }
    .ai-chip { background: linear-gradient(135deg, var(--primary), var(--accent)); color: #fff; font-size: 10px; font-weight: 800; padding: 3px 8px; border-radius: 6px; letter-spacing: 0.06em; flex-shrink: 0; }
    .op-chip { background: linear-gradient(135deg, #f59e0b, #d97706); color: #fff; font-size: 10px; font-weight: 800; padding: 3px 8px; border-radius: 6px; letter-spacing: 0.06em; flex-shrink: 0; }
    .info-banner { display:flex; align-items:center; gap:12px; background:var(--info-bg); border:1px solid rgba(56,189,248,0.3); border-radius:var(--radius); padding:10px 16px; font-size:13px; color:var(--info); button { margin-left:auto; } }
    .failure-ref { display:flex; flex-direction:column; gap:1px; cursor:pointer; &:hover .failure-id { color:var(--primary); } }
    .failure-id { font-size:13px; font-weight:600; }
    .failure-type { font-size:11px; }
    .rec-action-cell { max-width: 280px; font-size: 12px; line-height: 1.5; }
    .row-executed td { opacity: 0.65; }
    .pagination { display:flex; align-items:center; justify-content:center; gap:16px; padding:12px; border-top:1px solid var(--border); }
    .policy-missing { display:flex; align-items:center; gap:6px; }
    .toggle.disabled { opacity: 0.4; cursor: not-allowed; }
    .btn-link { background:none; border:none; color:var(--primary); cursor:pointer; padding:0; font-size:11px; text-decoration:underline; }
    .btn-link:hover { color:var(--accent); }
    .badge-muted { background: #e2e8f0; color: #475569; border: 1px solid #cbd5e1; }
    /* ↑/↓ buttons projected into the shared drawer's controls slot. */
    .nav-arrow:disabled { opacity: 0.35; cursor: not-allowed; }
    .drawer-title strong { font-weight: 700; color: var(--text); }
  `]
})
export class RecommendationsComponent implements OnInit {
  private svc     = inject(RecommendationsService);
  private scanSvc = inject(ScanService);
  private cfgSvc  = inject(ConfigService);
  private route   = inject(ActivatedRoute);
  private destroyRef = inject(DestroyRef);
  router          = inject(Router);

  loading        = signal(false);
  running        = signal(false);
  paged          = signal<PagedResult<Recommendation> | null>(null);
  filtered       = signal<Recommendation[]>([]);
  page           = signal(1);
  banner         = signal<string | null>(null);
  togglingRule   = signal<number | null>(null); // ruleId currently being toggled
  filterText     = '';
  filterCategory = '';
  filterApproved = '';

  isOperatorMode = signal(false);

  /** Drawer state — failureId of the rec currently open in the detail drawer.
   *  Driven by the ?selected query param so it's refresh-safe + shareable,
   *  same contract as the failures list. */
  selectedFailureId = signal<number | null>(null);

  /** Index of the open failure within the current filtered page (for the
   *  "N of M" header hint + ↑/↓ bounds). -1 when not in the current view. */
  selectedRecIndex = computed(() => {
    const id = this.selectedFailureId();
    if (id === null) return -1;
    return this.filtered().findIndex(r => r.failureId === id);
  });
  canNavPrev = computed(() => this.selectedRecIndex() > 0);
  canNavNext = computed(() => {
    const i = this.selectedRecIndex();
    return i !== -1 && i < this.filtered().length - 1;
  });

  ngOnInit() {
    const url = this.router.url;
    const opMode = url.includes('operator-actions');
    this.isOperatorMode.set(opMode);
    if (opMode) this.filterApproved = 'pending';

    // ?selected drives the drawer. Separate from the (locally-paged) list
    // query, so changing it never re-fetches — it just opens/closes the drawer.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(params => {
        const sel = params.get('selected');
        this.selectedFailureId.set(sel ? (parseInt(sel, 10) || null) : null);
      });

    this.load();
  }

  load() {
    this.loading.set(true);
    this.svc.getRecommendations(this.page(), 50).subscribe({
      next: r => { this.paged.set(r); this.applyFilter(); this.loading.set(false); },
      error: () => this.loading.set(false)
    });
  }

  applyFilter() {
    const items = this.paged()?.items ?? [];
    const text  = this.filterText.toLowerCase();
    this.filtered.set(items.filter(r => {
      const textMatch = !text || r.suggestedAction.toLowerCase().includes(text) || (r.errorTypeCode?.toLowerCase().includes(text) ?? false);
      const catMatch  = !this.filterCategory || r.fixCategory === this.filterCategory;
      const stateMatch = !this.filterApproved ||
        (this.filterApproved === 'pending'   && r.operatorApproved === null && !r.isExecuted) ||
        (this.filterApproved === 'approved'  && r.operatorApproved === true)  ||
        (this.filterApproved === 'rejected'  && r.operatorApproved === false) ||
        (this.filterApproved === 'executed'  && r.isExecuted);
      return textMatch && catMatch && stateMatch;
    }));
  }

  approve(rec: Recommendation) {
    rec.operatorApproved = true;
    this.svc.approveRecommendation(rec.recommendationId, 'operator').subscribe();
  }

  reject(rec: Recommendation) {
    rec.operatorApproved = false;
    this.svc.rejectRecommendation(rec.recommendationId, 'operator').subscribe();
  }

  /**
   * Toggle FixPolicyRule.IsAutoHealEligible — affects FUTURE recommendations
   * generated for this ErrorType. Does NOT mutate AutoFixAvailable on existing
   * recs (frozen snapshot — see /api/recommendations/{id}/approve to execute
   * this specific recommendation now).
   *
   * Two-step: GET the full policy → mutate one flag → PUT full body.
   */
  toggleAutoHeal(rec: Recommendation, enabled: boolean) {
    if (rec.fixPolicyRuleId == null) return; // disabled state — guard against rogue clicks

    const ruleId   = rec.fixPolicyRuleId;
    const previous = rec.policyIsAutoHealEligible;

    // Optimistic: flip every visible rec that shares this policy
    this.applyPolicyChange(ruleId, enabled);
    this.togglingRule.set(ruleId);

    this.cfgSvc.getFixPolicyRuleById(ruleId).subscribe({
      next: policy => {
        const body: UpsertFixPolicyRuleRequest = {
          jobTypeId:          policy.jobTypeId,
          errorTypeId:        policy.errorTypeId,
          monitoredJobId:     policy.monitoredJobId,  // preserve scope on auto-heal toggle
          actionToApply:      policy.actionToApply,
          fixCategory:        policy.fixCategory,
          actionType:         policy.actionType,
          actionPayload:      policy.actionPayload,
          isAutoHealEligible: enabled,
          enabled:            policy.enabled,
        };
        this.cfgSvc.updateFixPolicyRule(ruleId, body).subscribe({
          next: () => this.togglingRule.set(null),
          error: () => {
            this.applyPolicyChange(ruleId, previous);
            this.togglingRule.set(null);
            this.banner.set('Failed to update auto-heal policy. Reverted.');
          }
        });
      },
      error: () => {
        this.applyPolicyChange(ruleId, previous);
        this.togglingRule.set(null);
        this.banner.set('Failed to load policy. Reverted.');
      }
    });
  }

  private applyPolicyChange(ruleId: number, value: boolean | null) {
    const all = this.paged()?.items ?? [];
    for (const r of all) if (r.fixPolicyRuleId === ruleId) r.policyIsAutoHealEligible = value;
    this.applyFilter();
  }

  policyMissing(rec: Recommendation): boolean {
    return rec.fixPolicyRuleId == null;
  }

  configurePolicy(rec: Recommendation) {
    // No fix-policy-rules config screen yet — surface guidance for now.
    const where = rec.jobTypeId != null
      ? `JobTypeId=${rec.jobTypeId}, ErrorTypeId=${rec.errorTypeId}`
      : `ErrorTypeId=${rec.errorTypeId}`;
    this.banner.set(`No enabled FixPolicyRule for this error (${where}). Add one in Config → Fix Policy Rules.`);
  }

  classifyPending() {
    this.running.set(true);
    this.scanSvc.classifyPending().subscribe({
      next: r => {
        this.banner.set(`Classified ${pluralize(r.classified, 'failure')}, ${pluralize(r.suggestions, 'suggestion')} generated.`);
        this.running.set(false);
        this.load();
      },
      error: () => this.running.set(false)
    });
  }

  runPipeline() {
    this.running.set(true);
    this.svc.runPipeline().subscribe({
      next: r => {
        this.banner.set(`Pipeline complete — ${pluralize(r.classifications, 'classification')}. ${r.message}`);
        this.running.set(false);
        this.load();
      },
      error: () => this.running.set(false)
    });
  }

  categoryBadge(cat: string): string {
    const map: Record<string, string> = {
      AutoFix: 'badge-success', Manual: 'badge-warning', Notify: 'badge-info', Escalate: 'badge-failed'
    };
    return map[cat] ?? 'badge-info';
  }

  // ── Drawer open/close + keyboard navigation ────────────────────────────
  /** Opens the failure detail in-place via ?selected on the CURRENT route
   *  (/recommendations or /operator-actions) — no longer bounces to /failures. */
  openFailure(id: number) { this.patchSelected(id); }
  closeDrawer()           { this.patchSelected(null); }

  private patchSelected(id: number | null) {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { selected: id },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  navigatePrev() {
    const i = this.selectedRecIndex();
    if (i > 0) this.patchSelected(this.filtered()[i - 1].failureId);
  }
  navigateNext() {
    const i = this.selectedRecIndex();
    if (i !== -1 && i < this.filtered().length - 1)
      this.patchSelected(this.filtered()[i + 1].failureId);
  }

  // ↑/↓ navigate between recs of the current page while the drawer is open.
  // (No cross-page auto-load here — recommendations paging is manual.)
  @HostListener('document:keydown', ['$event'])
  onKey(ev: KeyboardEvent) {
    const tag = (ev.target as HTMLElement | null)?.tagName;
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
    if (this.selectedFailureId() === null) return;   // Esc is handled by <app-drawer>
    if (ev.key === 'ArrowDown')    { ev.preventDefault(); this.navigateNext(); }
    else if (ev.key === 'ArrowUp') { ev.preventDefault(); this.navigatePrev(); }
  }

  prevPage() { if (this.page() > 1) { this.page.update(p => p - 1); this.patchSelected(null); this.load(); } }
  nextPage() { this.page.update(p => p + 1); this.patchSelected(null); this.load(); }
}
