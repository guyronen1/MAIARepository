import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { FailuresService } from '../../core/services/failures.service';
import { JobFailure, PagedResult } from '../../core/models';

@Component({
  selector: 'app-failures-list',
  standalone: true,
  imports: [DatePipe, FormsModule],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Failures</h1>
          <p class="text-muted text-sm">{{ paged()?.totalCount ?? 0 }} total failures</p>
        </div>
        <button class="btn btn-primary btn-sm" (click)="load()">
          <span [class.spinner]="loading()"></span> Refresh
        </button>
      </div>

      <div class="filter-bar">
        <div class="form-group">
          <label>Job / Step</label>
          <input [(ngModel)]="filterText" placeholder="Search…" (input)="applyFilter()" style="min-width:180px" />
        </div>
        <div class="form-group">
          <label>Status</label>
          <select [(ngModel)]="filterStatus" (change)="applyFilter()">
            <option value="">All</option>
            <option value="Failed">Failed</option>
            <option value="Resolved">Resolved</option>
            <option value="ManualRequired">Manual Required</option>
          </select>
        </div>
        <div style="display:flex;gap:6px;align-items:flex-end">
          <button class="btn btn-primary" (click)="load()">Search</button>
          <button class="btn btn-ghost" (click)="filterText='';filterStatus='';applyFilter()">Clear</button>
        </div>
      </div>

      <div class="card" style="padding:0;overflow:hidden">
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
              @for (f of filtered(); track f.failureId) {
                <tr class="clickable" (click)="openDetail(f.failureId)">
                  <td class="text-muted">{{ f.failureId }}</td>
                  <td><span class="job-pill">{{ f.monitoredJobName ?? f.jobTypeName }}</span></td>
                  <td class="truncate" style="max-width:160px">{{ f.stepName ?? '—' }}</td>
                  <td>
                    @if (f.errorTypeCode) {
                      <span class="badge badge-medium">{{ f.errorTypeCode }}</span>
                    } @else {
                      <span class="badge badge-failed">Unclassified</span>
                    }
                  </td>
                  <td class="truncate text-muted" style="max-width:260px; font-size:12px">
                    {{ f.errorMessage ?? '—' }}
                  </td>
                  <td class="text-muted text-sm">{{ f.detectedAt | date:'MM/dd/yy HH:mm' }}</td>
                  <td><span class="badge" [class]="'badge-' + f.status.toLowerCase()">{{ f.status }}</span></td>
                </tr>
              }
            </tbody>
          </table>
          <!-- Pagination -->
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
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }
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
  `]
})
export class FailuresListComponent implements OnInit {
  private svc = inject(FailuresService);
  router      = inject(Router);

  loading      = signal(false);
  paged        = signal<PagedResult<JobFailure> | null>(null);
  filtered     = signal<JobFailure[]>([]);
  page         = signal(1);
  filterText   = '';
  filterStatus = '';

  ngOnInit() { this.load(); }

  load() {
    this.loading.set(true);
    this.svc.getFailures(this.page(), 50).subscribe({
      next: r => { this.paged.set(r); this.applyFilter(); this.loading.set(false); },
      error: () => this.loading.set(false)
    });
  }

  applyFilter() {
    const items = this.paged()?.items ?? [];
    const text  = this.filterText.toLowerCase();
    this.filtered.set(items.filter(f =>
      (!text || (f.monitoredJobName ?? '').toLowerCase().includes(text)
              || (f.stepName ?? '').toLowerCase().includes(text)
              || (f.errorTypeCode ?? '').toLowerCase().includes(text)
              || (f.errorMessage ?? '').toLowerCase().includes(text)) &&
      (!this.filterStatus || f.status === this.filterStatus)
    ));
  }

  openDetail(id: number) { this.router.navigate(['/failures', id]); }
  prevPage() { if (this.page() > 1) { this.page.update(p => p - 1); this.load(); } }
  nextPage() { this.page.update(p => p + 1); this.load(); }
}
