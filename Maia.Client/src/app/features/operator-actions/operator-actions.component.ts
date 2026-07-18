import { Component, DestroyRef, HostListener, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RecommendationsService } from '../../core/services/recommendations.service';
import { NotificationService } from '../../core/services/notification.service';
import { OperatorActionEntry, PagedResult } from '../../core/models';
import { FailureDetailComponent } from '../failures/failure-detail.component';
import { DrawerComponent } from '../../shared/drawer/drawer.component';

/**
 * Operator Actions — the DECISION HISTORY view: one row per Approve / Reject /
 * Retry an operator took on a recommendation, newest first, with where the
 * failure ended up. Distinct from /recommendations (the pending-action queue):
 * that screen answers "what needs me now?", this one answers "what did we
 * decide, and how did it turn out?".
 */
@Component({
  selector: 'app-operator-actions',
  standalone: true,
  imports: [DatePipe, FormsModule, FailureDetailComponent, DrawerComponent],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1><span class="op-chip">OA</span> Operator Actions</h1>
          <p class="text-muted text-sm">{{ paged()?.totalCount ?? 0 }} decisions recorded — approvals, rejections and retries, newest first</p>
        </div>
      </div>

      <div class="page-filters">
        <input [(ngModel)]="filterText" placeholder="Filter by action, error type, job…"
               (input)="onFilterInput()" style="min-width:220px" />
        <select [(ngModel)]="filterDecision" (change)="reload()">
          <option value="">All decisions</option>
          <option value="Approve">Approved</option>
          <option value="Reject">Rejected</option>
          <option value="Retry">Retried</option>
        </select>
      </div>

      <div class="card" style="padding:0;overflow:hidden">
        @if (loading()) {
          <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
        } @else if ((paged()?.items ?? []).length === 0) {
          <div class="empty-state">
            <span class="empty-icon">📋</span>
            <p>No operator decisions yet</p>
            <p class="text-sm text-muted">Approve or reject a recommendation and it will show up here</p>
          </div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>When</th>
                <th>Operator</th>
                <th>Decision</th>
                <th>Failure</th>
                <th>Job</th>
                <th>Recommendation</th>
                <th title="Whether the fix executed, and the failure's current status">Outcome</th>
              </tr>
            </thead>
            <tbody>
              @for (a of paged()!.items; track a.actionId) {
                <tr [class.clickable]="a.failureId !== null"
                    (click)="a.failureId !== null && openFailure(a.failureId)">
                  <td class="text-muted" style="white-space:nowrap">{{ a.actionTimestamp | date:'dd/MM/yy HH:mm' }}</td>
                  <td>{{ a.operatorId }}</td>
                  <td><span class="badge" [class]="decisionBadge(a.actionTaken)">{{ decisionLabel(a.actionTaken) }}</span></td>
                  <td>
                    @if (a.failureId !== null) {
                      <div class="failure-ref">
                        <span class="failure-id">#{{ a.failureId }}</span>
                        <span class="text-muted text-sm">{{ a.errorTypeCode ?? '—' }}</span>
                      </div>
                    } @else { <span class="text-muted">—</span> }
                  </td>
                  <td dir="auto">{{ a.monitoredJobName ?? '—' }}</td>
                  <td class="action-cell" dir="auto" [title]="a.suggestedAction ?? ''">{{ a.suggestedAction ?? '—' }}</td>
                  <td>
                    <div class="outcome-cell">
                      @if (a.actionTaken !== 'Reject' && a.isExecuted) {
                        <span class="badge badge-fixed">Executed</span>
                      }
                      @if (a.failureStatus; as st) {
                        <span class="badge" [class]="'badge-' + st.toLowerCase()">{{ st }}</span>
                      }
                    </div>
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

      <!-- Failure detail drawer — same in-place ?selected contract as the
           recommendations + failures screens. -->
      <app-drawer
          [open]="selectedFailureId() !== null"
          [width]="'760px'"
          [ariaLabel]="'Failure ' + selectedFailureId() + ' detail'"
          (close)="closeDrawer()">
        <ng-container drawer-title>
          <span class="text-muted text-sm">Failure</span>
          <strong>#{{ selectedFailureId() }}</strong>
        </ng-container>
        <ng-container drawer-controls>
          <button class="btn btn-ghost btn-sm nav-arrow" (click)="navigate(-1)"
                  [disabled]="!canNav(-1)" title="Previous (↑)">↑</button>
          <button class="btn btn-ghost btn-sm nav-arrow" (click)="navigate(1)"
                  [disabled]="!canNav(1)" title="Next (↓)">↓</button>
        </ng-container>
        @if (selectedFailureId() !== null) {
          <app-failure-detail [failureId]="selectedFailureId()!"></app-failure-detail>
        }
      </app-drawer>
    </div>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; display: flex; align-items: center; gap: 10px; }
    .op-chip { background: linear-gradient(135deg, #f59e0b, #d97706); color: #fff; font-size: 10px; font-weight: 800; padding: 3px 8px; border-radius: 6px; letter-spacing: 0.06em; flex-shrink: 0; }
    .page-filters { display: flex; gap: 8px; align-items: center; }
    .failure-ref { display: flex; flex-direction: column; gap: 1px; }
    .failure-id { font-size: 13px; font-weight: 600; }
    tr.clickable:hover .failure-id { color: var(--primary); }
    .action-cell { max-width: 300px; font-size: 12px; line-height: 1.5; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .outcome-cell { display: flex; gap: 4px; flex-wrap: wrap; }
    .pagination { display: flex; align-items: center; justify-content: center; gap: 16px; padding: 12px; border-top: 1px solid var(--border); }
    .nav-arrow:disabled { opacity: 0.35; cursor: not-allowed; }
  `]
})
export class OperatorActionsComponent implements OnInit {
  private svc        = inject(RecommendationsService);
  private notify     = inject(NotificationService);
  private route      = inject(ActivatedRoute);
  private router     = inject(Router);
  private destroyRef = inject(DestroyRef);

  loading        = signal(false);
  paged          = signal<PagedResult<OperatorActionEntry> | null>(null);
  page           = signal(1);
  filterText     = '';
  filterDecision = '';
  private filterDebounce: ReturnType<typeof setTimeout> | null = null;

  /** Drawer state — ?selected query param, refresh-safe + shareable. */
  selectedFailureId = signal<number | null>(null);

  /** Distinct failureIds of the current page, in row order — drives ↑/↓. */
  private navIds = computed(() => {
    const seen = new Set<number>();
    for (const a of this.paged()?.items ?? []) {
      if (a.failureId !== null) seen.add(a.failureId);
    }
    return [...seen];
  });

  ngOnInit() {
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
    this.svc.getOperatorActions({
      page: this.page(), pageSize: 50,
      actionTaken: this.filterDecision || undefined,
      q: this.filterText.trim() || undefined,
    }).subscribe({
      next: r => { this.paged.set(r); this.loading.set(false); },
      error: () => { this.loading.set(false); this.notify.error('Could not load operator actions.'); }
    });
  }

  /** Text filter is server-side (paging is server-side) — debounce 300ms. */
  onFilterInput() {
    if (this.filterDebounce) clearTimeout(this.filterDebounce);
    this.filterDebounce = setTimeout(() => this.reload(), 300);
  }

  reload() { this.page.set(1); this.load(); }

  decisionBadge(action: string): string {
    switch (action) {
      case 'Approve': return 'badge-resolved';
      case 'Reject':  return 'badge-failed';
      case 'Retry':   return 'badge-classified';
      default:        return 'badge-info';
    }
  }

  decisionLabel(action: string): string {
    switch (action) {
      case 'Approve': return '✓ Approved';
      case 'Reject':  return '✕ Rejected';
      case 'Retry':   return '↻ Retried';
      default:        return action;
    }
  }

  // ── Drawer open/close + keyboard navigation ────────────────────────────
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

  canNav(dir: -1 | 1): boolean {
    const ids = this.navIds();
    const i = ids.indexOf(this.selectedFailureId() ?? -1);
    return i !== -1 && i + dir >= 0 && i + dir < ids.length;
  }

  navigate(dir: -1 | 1) {
    const ids = this.navIds();
    const i = ids.indexOf(this.selectedFailureId() ?? -1);
    if (i !== -1 && i + dir >= 0 && i + dir < ids.length) this.patchSelected(ids[i + dir]);
  }

  @HostListener('document:keydown', ['$event'])
  onKey(ev: KeyboardEvent) {
    const tag = (ev.target as HTMLElement | null)?.tagName;
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
    if (this.selectedFailureId() === null) return;   // Esc handled by <app-drawer>
    if (ev.key === 'ArrowDown')    { ev.preventDefault(); this.navigate(1); }
    else if (ev.key === 'ArrowUp') { ev.preventDefault(); this.navigate(-1); }
  }

  prevPage() { if (this.page() > 1) { this.page.update(p => p - 1); this.patchSelected(null); this.load(); } }
  nextPage() { this.page.update(p => p + 1); this.patchSelected(null); this.load(); }
}
