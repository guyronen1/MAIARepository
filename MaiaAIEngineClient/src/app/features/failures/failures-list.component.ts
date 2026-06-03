import {
  AfterViewChecked, Component, ElementRef, HostListener, OnDestroy, OnInit,
  QueryList, ViewChild, ViewChildren, computed, inject, signal,
} from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Subject } from 'rxjs';
import { debounceTime, takeUntil } from 'rxjs/operators';
import { FailuresService } from '../../core/services/failures.service';
import { JobFailure, PagedResult } from '../../core/models';
import { FailureDetailComponent } from './failure-detail.component';
import { DrawerComponent } from '../../shared/drawer/drawer.component';

const VIEW_LABELS: Record<string, string> = {
  'active':          'Active Failures',
  'unclassified':    'Unclassified',
  'awaiting-action': 'Awaiting Operator Action',
  'auto-fixed':      'Auto-Fixed',
  'operator-fixed':  'Operator-Fixed',
  'resolved':        'Resolved',
  'manual-required': 'Manual Required',
};

const PAGE_SIZE = 50;

/**
 * Failures list + drawer host.
 *
 * URL is the single source of truth for view-related state: ?view, ?status,
 * ?q (free-text search), ?page, ?selected (id of the failure currently
 * showing in the drawer). Filter inputs / pagination / drawer open-state all
 * round-trip through the URL so refreshing or sharing the link reproduces
 * exactly what the operator was looking at.
 *
 * Drawer slides in from the right at a fixed 760px width. The list itself
 * gets a min-width so narrow viewports horizontally scroll the page rather
 * than crushing either side. Keyboard navigation: Esc closes, ↑/↓ moves
 * between failures (auto-loads previous/next page at the boundary), Enter
 * on a focused row opens the drawer.
 */
@Component({
  selector: 'app-failures-list',
  standalone: true,
  imports: [DatePipe, FormsModule, FailureDetailComponent, DrawerComponent],
  template: `
    <div class="page failures-page" [class.drawer-open]="selectedId() !== null">
      <div class="page-header">
        <div>
          <h1>Failures</h1>
          <p class="text-muted text-sm">{{ paged()?.totalCount ?? 0 }} total failures</p>
        </div>
        <button class="btn btn-primary btn-sm" (click)="reload()">
          <span [class.spinner]="loading()"></span> Refresh
        </button>
      </div>

      @if (view()) {
        <div class="view-filter-banner">
          <span class="view-filter-label">Filtered:</span>
          <span class="badge badge-info">{{ viewLabel() }}</span>
          <button class="btn btn-ghost btn-sm" (click)="clearView()">✕ Clear filter</button>
        </div>
      }

      <div class="filter-bar">
        <div class="form-group">
          <label>Job / Step</label>
          <input [ngModel]="filterText()" (ngModelChange)="onFilterTextChange($event)"
                 placeholder="Search…" style="min-width:180px" />
        </div>
        <div class="form-group">
          <label>Status</label>
          <select [ngModel]="filterStatus()" (ngModelChange)="setFilterStatus($event)">
            <option value="">All</option>
            <option value="Failed">Failed</option>
            <option value="Resolved">Resolved</option>
            <option value="ManualRequired">Manual Required</option>
          </select>
        </div>
        <div style="display:flex;gap:6px;align-items:flex-end">
          <button class="btn btn-ghost" (click)="clearFilters()">Clear</button>
        </div>
      </div>

      <div class="card list-card">
        @if (loading()) {
          <div class="loading-overlay"><span class="spinner"></span> Loading failures…</div>
        } @else if (filtered().length === 0) {
          <div class="empty-state"><span class="empty-icon">✓</span><p>No failures match your filter</p></div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>#</th>
                <th>Job</th>
                <th>Step / File</th>
                <th>Error Type</th>
                <th>Message</th>
                <th>Detected</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              @for (f of filtered(); track f.failureId; let i = $index) {
                <tr #row class="clickable"
                    tabindex="0"
                    [class.row-selected]="f.failureId === selectedId()"
                    [attr.data-failure-id]="f.failureId"
                    (click)="openDrawer(f.failureId)"
                    (keydown.enter)="openDrawer(f.failureId); $event.preventDefault()">
                  <td class="text-muted">{{ f.failureId }}</td>
                  <td><span class="job-pill" dir="auto">{{ f.monitoredJobName ?? f.jobTypeName }}</span></td>
                  <td class="truncate" style="max-width:160px" dir="auto">{{ f.stepName ?? '—' }}</td>
                  <td>
                    @if (f.errorTypeCode) {
                      <span class="badge badge-medium">{{ f.errorTypeCode }}</span>
                    } @else {
                      <span class="badge badge-failed">Unclassified</span>
                    }
                  </td>
                  <td class="truncate text-muted" style="max-width:260px; font-size:12px" dir="auto">
                    {{ f.errorMessage ?? '—' }}
                  </td>
                  <td class="text-muted text-sm">{{ f.detectedAt | date:'MM/dd/yy HH:mm' }}</td>
                  <td>
                    <span class="badge" [class]="'badge-' + f.status.toLowerCase()">{{ f.status }}</span>
                    @if (f.hasRecentFixFailure) {
                      <!-- Auto-fix or operator approval tried + failed today.
                           Distinct from a plain ManualRequired (operator hasn't
                           taken any action yet) — this is "system tried, failed". -->
                      <span class="badge badge-failed fix-failed-badge"
                            title="A fix attempt failed today — operator review needed">
                        Failed to Execute
                      </span>
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

      <!-- Drawer shell is the shared <app-drawer>; this component supplies the
           title, the ↑/↓ nav controls, and the body (failure detail). -->
      <app-drawer
          [open]="selectedId() !== null"
          [ariaLabel]="'Failure ' + selectedId() + ' detail'"
          (close)="closeDrawer()">
        <ng-container drawer-title>
          <span class="text-muted text-sm">Failure</span>
          <strong>#{{ selectedId() }}</strong>
          @if (selectedIndex() !== -1 && filtered().length > 0) {
            <span class="text-muted text-sm">· {{ selectedIndex() + 1 }} of {{ filtered().length }} on this page</span>
          } @else if (filtered().length > 0) {
            <span class="filter-drift-hint" title="This failure no longer matches the current filter, but the drawer stays open until you close it.">
              no longer in filter
            </span>
          }
        </ng-container>
        <ng-container drawer-controls>
          <button class="btn btn-ghost btn-sm nav-arrow" (click)="navigatePrev()"
                  [disabled]="!canNavPrev()" title="Previous (↑)">↑</button>
          <button class="btn btn-ghost btn-sm nav-arrow" (click)="navigateNext()"
                  [disabled]="!canNavNext()" title="Next (↓)">↓</button>
        </ng-container>
        @if (selectedId() !== null) {
          <app-failure-detail [failureId]="selectedId()!"></app-failure-detail>
        }
      </app-drawer>

      <!-- Transient spatial-awareness toast — fired by arrow-key navigation
           that crosses a page boundary or hits the end of the list. Auto-
           dismissed; no operator action required. -->
      @if (navToast()) {
        <div class="nav-toast" [class.toast-end]="navToast()!.kind === 'end'" role="status" aria-live="polite">
          {{ navToast()!.text }}
        </div>
      }
    </div>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }

    /* Secondary marker shown alongside the status badge when a fix attempt
       failed today. Tighter and slightly smaller than the status badge so
       it reads as supplementary info, not a parallel status. */
    .fix-failed-badge { margin-left: 4px; font-size: 10px; padding: 2px 6px; }

    /* List min-width so narrow viewports scroll horizontally rather than crushing
       either side. With the 760px drawer, total fitting width is ~720 (table) +
       760 (drawer) + gutter — anything narrower scrolls the page. */
    .list-card { min-width: 720px; padding: 0; overflow: hidden; }

    .job-pill {
      display: inline-block;
      padding: 2px 8px;
      background: var(--surface-2);
      border: 1px solid var(--border);
      border-radius: 4px;
      font-size: 11px;
      font-weight: 500;
      color: var(--text);
    }
    .pagination {
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 16px;
      padding: 12px;
      border-top: 1px solid var(--border);
    }
    .view-filter-banner {
      display: flex; align-items: center; gap: 10px;
      margin: 0 0 12px;
      padding: 10px 14px;
      background: var(--info-bg, rgba(56,189,248,0.08));
      border: 1px solid rgba(56,189,248,0.3);
      border-radius: var(--radius);
      font-size: 13px;
      button { margin-left: auto; }
    }
    .view-filter-label { color: var(--text-muted); font-size: 12px; }

    /* Drawer shell styles now live in DrawerComponent. The ↑/↓ buttons are
       projected into its controls slot, so their disabled state is styled
       here (they belong to this component's view). */
    .nav-arrow:disabled { opacity: 0.35; cursor: not-allowed; }
    .drawer-title strong { font-weight: 700; color: var(--text); }

    .row-selected {
      background: var(--primary-glow, rgba(99, 102, 241, 0.08)) !important;
      box-shadow: inset 3px 0 0 var(--primary, #6366f1);
    }
    tr.clickable:focus { outline: 2px solid var(--primary, #6366f1); outline-offset: -2px; }
    tr.clickable:focus-visible { outline: 2px solid var(--primary, #6366f1); outline-offset: -2px; }

    .filter-drift-hint {
      font-size: 11px; color: var(--warning, #d97706);
      background: var(--warning-bg, rgba(245, 158, 11, 0.08));
      border: 1px solid rgba(245, 158, 11, 0.3);
      border-radius: 4px;
      padding: 1px 6px;
      cursor: help;
    }

    /* Spatial-awareness toast for arrow-key page traversal. Positioned over
       the page footer area; fades out via the animation's last keyframe so
       it leaves no residue when removed by the @if. */
    .nav-toast {
      position: fixed;
      bottom: 24px; left: 50%; transform: translateX(-50%);
      background: var(--surface-2);
      border: 1px solid var(--border);
      border-radius: 20px;
      padding: 6px 14px;
      font-size: 12px; font-weight: 500;
      color: var(--text);
      box-shadow: 0 4px 12px rgba(15, 23, 42, 0.12);
      z-index: 60;
      animation: toast-fade 1500ms ease-in-out forwards;
      pointer-events: none;
    }
    .nav-toast.toast-end {
      color: var(--warning, #d97706);
      border-color: rgba(245, 158, 11, 0.4);
      animation-duration: 1800ms;
    }
    @keyframes toast-fade {
      0%   { opacity: 0; transform: translateX(-50%) translateY(4px); }
      15%  { opacity: 1; transform: translateX(-50%) translateY(0); }
      75%  { opacity: 1; }
      100% { opacity: 0; }
    }
  `]
})
export class FailuresListComponent implements OnInit, OnDestroy, AfterViewChecked {
  private svc   = inject(FailuresService);
  private route = inject(ActivatedRoute);
  router        = inject(Router);

  @ViewChildren('row', { read: ElementRef }) rowRefs!: QueryList<ElementRef<HTMLTableRowElement>>;

  loading      = signal(false);
  paged        = signal<PagedResult<JobFailure> | null>(null);
  filtered     = signal<JobFailure[]>([]);
  page         = signal(1);
  view         = signal<string | null>(null);
  filterText   = signal('');
  filterStatus = signal('');
  selectedId   = signal<number | null>(null);

  /** Transient spatial-awareness toast: "Page 3 of 5" on cross-boundary
   *  arrow nav, "End of list" when ↓ hits the last failure of the last page.
   *  Auto-cleared after 1.5–1.8s by the helper that sets it. */
  navToast = signal<{ kind: 'page' | 'end'; text: string } | null>(null);
  private toastTimerId: ReturnType<typeof setTimeout> | null = null;

  viewLabel = () => VIEW_LABELS[this.view() ?? ''] ?? this.view();

  selectedIndex = computed(() => {
    const id = this.selectedId();
    if (id === null) return -1;
    return this.filtered().findIndex(f => f.failureId === id);
  });

  canNavPrev = computed(() => this.selectedIndex() > 0 || this.page() > 1);
  canNavNext = computed(() => {
    const i = this.selectedIndex();
    return (i !== -1 && i < this.filtered().length - 1)
        || this.page() < (this.paged()?.totalPages ?? 1);
  });

  private destroy$ = new Subject<void>();
  private searchInput$ = new Subject<string>();
  private scrollIntoViewPending = false;
  private hasFetched = false;

  ngOnInit() {
    // URL → state (one direction of the round-trip). Re-runs whenever the
    // operator clicks a KPI tile, edits the URL, or we patch ?selected /
    // ?page via router.navigate below.
    this.route.queryParamMap.pipe(takeUntil(this.destroy$)).subscribe(params => {
      const view     = params.get('view');
      const status   = params.get('status')   ?? '';
      const q        = params.get('q')        ?? '';
      const pageStr  = params.get('page');
      const selStr   = params.get('selected');
      const pageNum  = pageStr ? Math.max(1, parseInt(pageStr, 10) || 1) : 1;
      const selId    = selStr  ? parseInt(selStr, 10) || null         : null;

      const viewChanged   = (this.view() ?? '') !== (view && view !== 'all' ? view : '');
      const statusChanged = this.filterStatus() !== status;
      const qChanged      = this.filterText()   !== q;
      const pageChanged   = this.page()         !== pageNum;

      this.view.set(view && view !== 'all' ? view : null);
      this.filterStatus.set(status);
      this.filterText.set(q);
      this.page.set(pageNum);
      this.selectedId.set(selId);

      // Re-fetch when something that affects the server query changed, OR
      // on the very first emission (component just mounted and has no data
      // yet — handles the dashboard → /failures?selected=N case where the
      // URL carries only `selected` and nothing else triggers a change flag).
      if (!this.hasFetched || viewChanged || pageChanged) {
        this.hasFetched = true;
        this.fetchPage();
      } else if (statusChanged || qChanged) {
        this.applyFilter();
      }
    });

    // Debounce free-text search so the URL/query doesn't churn on every
    // keystroke. 250ms is short enough to feel responsive without spamming
    // navigation entries.
    this.searchInput$.pipe(debounceTime(250), takeUntil(this.destroy$)).subscribe(q => {
      this.patchUrl({ q: q || null, page: 1, selected: null });
    });
  }

  ngOnDestroy() {
    this.destroy$.next();
    this.destroy$.complete();
    if (this.toastTimerId !== null) clearTimeout(this.toastTimerId);
  }

  ngAfterViewChecked() {
    // If a keyboard navigation moved selection to a row that wasn't visible,
    // bring it into view once the DOM reflects the new selection.
    if (!this.scrollIntoViewPending) return;
    const id = this.selectedId();
    if (id === null) { this.scrollIntoViewPending = false; return; }
    const el = this.rowRefs.find(r => +r.nativeElement.dataset['failureId']! === id);
    if (el) {
      el.nativeElement.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
      el.nativeElement.focus({ preventScroll: true });
      this.scrollIntoViewPending = false;
    }
  }

  // ── Data fetch ─────────────────────────────────────────────────────────
  private fetchPage() {
    this.loading.set(true);
    this.svc.getFailures(this.page(), PAGE_SIZE, this.view() ?? undefined).subscribe({
      next: r => { this.paged.set(r); this.applyFilter(); this.loading.set(false); },
      error: () => this.loading.set(false)
    });
  }

  /** Manual refresh button — re-pulls the current page with current view filter. */
  reload() { this.fetchPage(); }

  private applyFilter() {
    const items  = this.paged()?.items ?? [];
    const text   = this.filterText().toLowerCase();
    const status = this.filterStatus();
    this.filtered.set(items.filter(f =>
      (!text || (f.monitoredJobName ?? '').toLowerCase().includes(text)
              || (f.stepName ?? '').toLowerCase().includes(text)
              || (f.errorTypeCode ?? '').toLowerCase().includes(text)
              || (f.errorMessage ?? '').toLowerCase().includes(text)) &&
      (!status || f.status === status)
    ));
  }

  // ── URL writes (the other direction) ───────────────────────────────────
  // Default-value sentinels — when a patched key equals its default we emit
  // null so Angular Router strips it from the URL. Keeps shareable links
  // clean (e.g. /failures?status=Failed, not /failures?q=&status=Failed&page=1).
  private static readonly URL_DEFAULTS: Record<string, string | number | null> = {
    page: 1, q: '', status: '', view: '', selected: null,
  };

  private patchUrl(patch: Record<string, string | number | null>) {
    const params: Record<string, string | null> = {};
    for (const [k, v] of Object.entries(patch)) {
      const isEmpty   = v === null || v === '' || v === undefined;
      const isDefault = !isEmpty && FailuresListComponent.URL_DEFAULTS[k] !== undefined
                        && String(v) === String(FailuresListComponent.URL_DEFAULTS[k]);
      params[k] = (isEmpty || isDefault) ? null : String(v);
    }
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: params,
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  /** Show a transient nav toast and auto-dismiss after `ms`. */
  private showNavToast(kind: 'page' | 'end', text: string, ms = 1500) {
    if (this.toastTimerId !== null) clearTimeout(this.toastTimerId);
    this.navToast.set({ kind, text });
    this.toastTimerId = setTimeout(() => {
      this.navToast.set(null);
      this.toastTimerId = null;
    }, ms);
  }

  onFilterTextChange(v: string) {
    this.filterText.set(v);   // optimistic local update so the input doesn't lag
    // Filter NOW (client-side) — don't wait for the debounced URL round-trip.
    // The queryParamMap handler can't drive this: it compares q against the
    // already-updated filterText signal, sees no change, and skips applyFilter.
    this.applyFilter();
    this.searchInput$.next(v);   // debounced ?q= push, purely for shareable links
  }

  setFilterStatus(v: string) {
    this.patchUrl({ status: v || null, page: 1, selected: null });
  }

  clearFilters() {
    this.patchUrl({ status: null, q: null, page: 1, selected: null });
  }

  clearView() {
    this.patchUrl({ view: null, page: 1, selected: null });
  }

  prevPage() { if (this.page() > 1) this.patchUrl({ page: this.page() - 1, selected: null }); }
  nextPage() {
    if (this.page() < (this.paged()?.totalPages ?? 1))
      this.patchUrl({ page: this.page() + 1, selected: null });
  }

  // ── Drawer open/close + keyboard navigation ────────────────────────────
  openDrawer(id: number) { this.patchUrl({ selected: id }); }
  closeDrawer()          { this.patchUrl({ selected: null }); }

  navigatePrev() {
    if (!this.canNavPrev()) {
      // ↑ at the first row of page 1 — not a queue-end case (operator is at
      // the top), no toast needed. Silent ignore.
      return;
    }
    const i = this.selectedIndex();
    if (i > 0) {
      const next = this.filtered()[i - 1];
      this.scrollIntoViewPending = true;
      this.patchUrl({ selected: next.failureId });
    } else if (this.page() > 1) {
      this.loadAdjacentPage(this.page() - 1, 'last');
    }
  }

  navigateNext() {
    if (!this.canNavNext()) {
      // ↓ at the last row of the last page — end of queue. Show a 1.8s
      // "End of list" toast so the operator knows the keypress registered
      // and they're not stuck on a frozen UI. Never wrap to the first page
      // (operator could re-process the same failure unknowingly).
      this.showNavToast('end', 'End of list', 1800);
      return;
    }
    const i = this.selectedIndex();
    if (i !== -1 && i < this.filtered().length - 1) {
      const next = this.filtered()[i + 1];
      this.scrollIntoViewPending = true;
      this.patchUrl({ selected: next.failureId });
    } else if (this.page() < (this.paged()?.totalPages ?? 1)) {
      this.loadAdjacentPage(this.page() + 1, 'first');
    }
  }

  /** Cross-page navigation: fetch the target page, then point ?selected at
   *  the first or last failure in that page. One-shot subscribe; the main
   *  URL-effect picks up the page change for normal display. */
  private loadAdjacentPage(targetPage: number, edge: 'first' | 'last') {
    this.loading.set(true);
    this.svc.getFailures(targetPage, PAGE_SIZE, this.view() ?? undefined).subscribe({
      next: r => {
        if (r.items.length === 0) { this.loading.set(false); return; }
        const target = edge === 'first' ? r.items[0] : r.items[r.items.length - 1];
        this.loading.set(false);
        this.scrollIntoViewPending = true;
        // Page-boundary toast — brief spatial-awareness cue. Total pages comes
        // from the response we just received; falls back to the cached signal
        // if the new payload arrives ordered (it always does, but defensive).
        this.showNavToast('page', `Page ${targetPage} of ${r.totalPages}`, 1300);
        this.patchUrl({ page: targetPage, selected: target.failureId });
      },
      error: () => this.loading.set(false)
    });
  }

  // Global keyboard shortcuts — only when the drawer is open OR the focused
  // element is a table row. Skips when focus is inside a form input so typing
  // a search query doesn't trigger row-navigation.
  @HostListener('document:keydown', ['$event'])
  onKey(ev: KeyboardEvent) {
    const tag = (ev.target as HTMLElement | null)?.tagName;
    const inField = tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA';
    if (inField) return;

    // Esc-to-close is owned by the shared <app-drawer>. Here we only handle
    // ↑/↓ row navigation, and only while the drawer is open (the triage flow).
    if (this.selectedId() === null) return;

    if (ev.key === 'ArrowDown') {
      ev.preventDefault();
      this.navigateNext();
    } else if (ev.key === 'ArrowUp') {
      ev.preventDefault();
      this.navigatePrev();
    }
  }
}
