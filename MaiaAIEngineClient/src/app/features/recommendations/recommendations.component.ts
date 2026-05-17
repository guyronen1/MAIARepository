import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { DatePipe, PercentPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, ActivatedRoute } from '@angular/router';
import { RecommendationsService } from '../../core/services/recommendations.service';
import { ScanService } from '../../core/services/scan.service';
import { Recommendation, PagedResult } from '../../core/models';

@Component({
  selector: 'app-recommendations',
  standalone: true,
  imports: [DatePipe, PercentPipe, FormsModule],
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
              {{ filtered().length }} pending item(s) awaiting your review
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
            <option value="AutoFix">AutoFix</option>
            <option value="Manual">Manual</option>
            <option value="Notify">Notify</option>
            <option value="Escalate">Escalate</option>
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
                <th>Auto-heal</th>
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
                  <td class="rec-action-cell">{{ r.suggestedAction }}</td>
                  <td>
                    <div class="confidence-bar" style="min-width:120px">
                      <div class="bar-track">
                        <div class="bar-fill" [style.width.%]="r.confidenceScore * 100"></div>
                      </div>
                      <span class="bar-value">{{ r.confidenceScore | percent }}</span>
                    </div>
                  </td>
                  <td>
                    <label class="toggle">
                      <input type="checkbox" [checked]="r.autoFixAvailable"
                             (change)="toggleAutoHeal(r, $any($event.target).checked)" />
                      <span class="slider"></span>
                    </label>
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
  `]
})
export class RecommendationsComponent implements OnInit {
  private svc     = inject(RecommendationsService);
  private scanSvc = inject(ScanService);
  private route   = inject(ActivatedRoute);
  router          = inject(Router);

  loading        = signal(false);
  running        = signal(false);
  paged          = signal<PagedResult<Recommendation> | null>(null);
  filtered       = signal<Recommendation[]>([]);
  page           = signal(1);
  banner         = signal<string | null>(null);
  filterText     = '';
  filterCategory = '';
  filterApproved = '';

  isOperatorMode = signal(false);

  ngOnInit() {
    const url = this.router.url;
    const opMode = url.includes('operator-actions');
    this.isOperatorMode.set(opMode);
    if (opMode) this.filterApproved = 'pending';
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
      const textMatch = !text || r.suggestedAction.toLowerCase().includes(text) || r.errorTypeCode.toLowerCase().includes(text);
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
    this.svc.approveRecommendation(rec.recommendationId).subscribe();
  }

  reject(rec: Recommendation) {
    rec.operatorApproved = false;
    this.svc.rejectRecommendation(rec.recommendationId).subscribe();
  }

  toggleAutoHeal(rec: Recommendation, enabled: boolean) {
    rec.autoFixAvailable = enabled;
    this.svc.setAutoHeal(rec.recommendationId, enabled).subscribe();
  }

  classifyPending() {
    this.running.set(true);
    this.scanSvc.classifyPending().subscribe({
      next: r => {
        this.banner.set(`Classified ${r.classified} failure(s), ${r.suggestions} suggestion(s) generated.`);
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
        this.banner.set(`Pipeline complete — ${r.classifications} classification(s). ${r.message}`);
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

  openFailure(id: number) { this.router.navigate(['/failures', id]); }
  prevPage() { if (this.page() > 1) { this.page.update(p => p - 1); this.load(); } }
  nextPage() { this.page.update(p => p + 1); this.load(); }
}
