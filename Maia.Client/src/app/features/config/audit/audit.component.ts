import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { AuditService } from '../../../core/services/audit.service';
import { AuditLogEntry } from '../../../core/models/audit.model';

const ENTITY_TYPES = [
  'AiRecommendation', 'ClassificationRule', 'ErrorType', 'FixPolicyRule',
  'JobFailure', 'MonitoredJob', 'ScanCheckRule', 'ScanSource', 'User',
];

@Component({
  selector: 'app-audit',
  standalone: true,
  imports: [FormsModule, DatePipe, DecimalPipe],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Audit Log</h1>
          <p class="text-muted text-sm">
            Immutable trail of all config changes, operator actions, and system fix events.
            Most-recent entries first.
          </p>
        </div>
      </div>

      <!-- Filters -->
      <div class="filter-bar">
        <select [(ngModel)]="f.entityType" style="min-width:160px">
          <option value="">Entity type (all)</option>
          @for (t of entityTypes; track t) { <option [value]="t">{{ t }}</option> }
        </select>
        <input [(ngModel)]="f.entityId" placeholder="Entity ID" style="width:100px" />
        <input [(ngModel)]="f.actor" placeholder="Actor" style="width:130px" />
        <input [(ngModel)]="f.eventType" placeholder="Event type" style="width:160px" />
        <input [(ngModel)]="f.fromDate" type="date" title="From date" />
        <input [(ngModel)]="f.toDate"   type="date" title="To date" />
        <button class="btn btn-primary btn-sm" (click)="search()">Search</button>
        <button class="btn btn-ghost btn-sm" (click)="clearFilters()">Clear</button>
        @if (totalCount() !== null) {
          <span class="text-muted text-sm" style="margin-left:4px">{{ totalCount() | number }} records</span>
        }
      </div>

      <!-- Table -->
      <div class="card" style="padding:0;overflow:hidden">
        @if (loading()) {
          <div class="loading-overlay"><span class="spinner"></span> Loading&hellip;</div>
        } @else if (error()) {
          <div class="empty-state"><p class="text-danger">{{ error() }}</p></div>
        } @else if (rows().length === 0) {
          <div class="empty-state">
            <span class="empty-icon">📋</span>
            <p>No audit entries match the current filters.</p>
          </div>
        } @else {
          <div style="overflow-x:auto">
            <table class="data-table audit-table">
              <colgroup>
                <col style="width:130px" />
                <col style="width:110px" />
                <col style="width:200px" />
                <col style="width:180px" />
                <col />
              </colgroup>
              <thead>
                <tr>
                  <th>Timestamp</th>
                  <th>Actor</th>
                  <th>Event</th>
                  <th>Entity</th>
                  <th>Detail</th>
                </tr>
              </thead>
              <tbody>
                @for (row of rows(); track row.auditId) {
                  <tr>
                    <td class="text-sm text-muted text-nowrap">
                      {{ row.timestamp | date:'dd/MM/yy HH:mm:ss' }}
                    </td>
                    <td class="text-sm font-mono" dir="auto" [title]="row.actor">{{ row.actor }}</td>
                    <td>
                      <span [class]="eventClass(row.eventType)">
                        {{ row.eventType }}
                      </span>
                    </td>
                    <td class="text-sm text-muted">
                      @if (row.entityType) {
                        <span>{{ row.entityType }}</span>
                        @if (row.entityId) { <span class="text-dim"> #{{ row.entityId }}</span> }
                      } @else if (row.failureId) {
                        <span>JobFailure #{{ row.failureId }}</span>
                      } @else {
                        <span class="text-dim">&ndash;</span>
                      }
                    </td>
                    <td>
                      <span class="detail-text" [innerHTML]="renderDetail(row.detail)"></span>
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>

          <!-- Pagination -->
          <div class="pagination-bar">
            <button class="btn btn-ghost btn-sm" [disabled]="page() <= 1" (click)="goPage(page() - 1)">&larr; Prev</button>
            <span class="text-sm text-muted">Page {{ page() }} of {{ totalPages() }}</span>
            <button class="btn btn-ghost btn-sm" [disabled]="page() >= totalPages()" (click)="goPage(page() + 1)">Next &rarr;</button>
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; margin: 0 0 4px; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; margin-bottom: 12px; }
    .filter-bar { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-bottom: 14px; }

    .audit-table { table-layout: fixed; width: 100%; }

    .event-badge {
      display: inline-block; font-size: 11px; font-weight: 600;
      padding: 2px 8px; border-radius: 10px; white-space: nowrap;
    }
    .ev-config  { background: #e0e7ff; color: #3730a3; }
    .ev-op      { background: #dcfce7; color: #166534; }
    .ev-system  { background: #fef3c7; color: #92400e; }
    .ev-user    { background: #f3e8ff; color: #7c3aed; }
    .ev-delete  { background: #fee2e2; color: #b91c1c; }
    .ev-default { background: #f1f5f9; color: #475569; }

    .detail-text {
      font-size: 12px; font-family: 'Consolas', 'Menlo', monospace;
      word-break: break-word; line-height: 1.5;
    }

    .pagination-bar {
      display: flex; align-items: center; gap: 12px;
      padding: 10px 16px; border-top: 1px solid var(--border);
    }

    .text-nowrap { white-space: nowrap; }
    .text-dim    { color: var(--text-dim, #9ca3af); }
    .font-mono   { font-family: 'Consolas', 'Menlo', monospace; }

    .loading-overlay {
      display: flex; align-items: center; justify-content: center;
      gap: 8px; padding: 48px 16px; color: var(--text-muted);
    }
    .empty-state {
      text-align: center; padding: 48px 16px;
      color: var(--text-muted);
    }
    .empty-icon { font-size: 32px; display: block; margin-bottom: 8px; }
  `]
})
export class AuditComponent implements OnInit {
  private svc = inject(AuditService);

  entityTypes = ENTITY_TYPES;

  loading    = signal(false);
  error      = signal<string | null>(null);
  rows       = signal<AuditLogEntry[]>([]);
  page       = signal(1);
  totalPages = signal(1);
  totalCount = signal<number | null>(null);

  f = this.blank();

  ngOnInit() { this.load(); }

  search() { this.page.set(1); this.load(); }

  clearFilters() {
    this.f = this.blank();
    this.page.set(1);
    this.load();
  }

  goPage(n: number) { this.page.set(n); this.load(); }

  private load() {
    this.loading.set(true);
    this.error.set(null);
    this.svc.query({
      entityType: this.f.entityType || undefined,
      entityId:   this.f.entityId   || undefined,
      actor:      this.f.actor      || undefined,
      eventType:  this.f.eventType  || undefined,
      fromDate:   this.f.fromDate   || undefined,
      toDate:     this.f.toDate     || undefined,
      page:       this.page(),
      pageSize:   50,
    }).subscribe({
      next: result => {
        this.rows.set(result.items);
        this.totalPages.set(result.totalPages || 1);
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Failed to load audit log.');
        this.loading.set(false);
      },
    });
  }

  eventClass(eventType: string): string {
    if (eventType.endsWith('Deleted') || eventType === 'FixFailed') return 'event-badge ev-delete';
    if (eventType === 'OperatorApproved' || eventType === 'OperatorRejected' ||
        eventType === 'ManuallyResolved'  || eventType === 'FixRetried') return 'event-badge ev-op';
    if (eventType === 'FixExecuted' || eventType === 'ManualActionRequired') return 'event-badge ev-system';
    if (eventType.startsWith('User')) return 'event-badge ev-user';
    if (eventType.endsWith('Created') || eventType.endsWith('Updated') ||
        eventType.endsWith('Linked')  || eventType.endsWith('Unlinked')) return 'event-badge ev-config';
    return 'event-badge ev-default';
  }

  renderDetail(detail: string | null): string {
    if (!detail) return '<span style="color:var(--text-dim,#9ca3af)">&ndash;</span>';
    // Escape HTML first, then replace → with a styled span
    const escaped = detail
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');
    return escaped.replace(/ → /g, ' <span style="color:var(--primary,#6366f1);font-weight:700">→</span> ');
  }

  private blank() {
    return { entityType: '', entityId: '', actor: '', eventType: '', fromDate: '', toDate: '' };
  }
}
