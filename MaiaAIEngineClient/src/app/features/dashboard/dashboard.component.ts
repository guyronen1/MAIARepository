import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { FailuresService } from '../../core/services/failures.service';
import { ScanService } from '../../core/services/scan.service';
import { MonitoredJobsService } from '../../core/services/monitored-jobs.service';
import { JobFailure, MonitoredJob, ScanResult } from '../../core/models';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Dashboard</h1>
          <p class="text-muted text-sm">Real-time monitoring overview</p>
        </div>
        <div class="page-actions">
          <button class="btn btn-ghost btn-sm" (click)="refresh()" [disabled]="loading()">
            <span [class.spinner]="loading()"></span>
            Refresh
          </button>
          <button class="btn btn-primary btn-sm" (click)="runScanAll()" [disabled]="scanning()">
            @if (scanning()) { <span class="spinner"></span> }
            Run All Scans
          </button>
        </div>
      </div>

      <!-- KPI Cards -->
      <div class="kpi-grid">
        <div class="kpi-card danger">
          <div class="kpi-icon">⚠</div>
          <div class="kpi-body">
            <div class="kpi-value">{{ stats().failed }}</div>
            <div class="kpi-label">Active Failures</div>
          </div>
        </div>
        <div class="kpi-card warning">
          <div class="kpi-icon">🔍</div>
          <div class="kpi-body">
            <div class="kpi-value">{{ stats().unclassified }}</div>
            <div class="kpi-label">Unclassified</div>
          </div>
        </div>
        <div class="kpi-card info">
          <div class="kpi-icon">💡</div>
          <div class="kpi-body">
            <div class="kpi-value">{{ stats().recommended }}</div>
            <div class="kpi-label">Awaiting Action</div>
          </div>
        </div>
        <div class="kpi-card success">
          <div class="kpi-icon">✓</div>
          <div class="kpi-body">
            <div class="kpi-value">{{ stats().total }}</div>
            <div class="kpi-label">Total Logged</div>
          </div>
        </div>
      </div>

      <div class="row-2col">
        <!-- Recent Failures -->
        <div class="card">
          <div class="card-header">
            <h3>Recent Failures</h3>
            <button class="btn btn-ghost btn-sm" (click)="router.navigate(['/failures'])">View All</button>
          </div>
          @if (loadingFailures()) {
            <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
          } @else if (recentFailures().length === 0) {
            <div class="empty-state"><span class="empty-icon">✓</span><p>No failures found</p></div>
          } @else {
            <table class="data-table">
              <thead>
                <tr><th>Job</th><th>Step</th><th>Error Type</th><th>Detected</th><th>Status</th></tr>
              </thead>
              <tbody>
                @for (f of recentFailures(); track f.failureId) {
                  <tr class="clickable" (click)="router.navigate(['/failures', f.failureId])">
                    <td>{{ f.monitoredJobName ?? '—' }}</td>
                    <td class="truncate" style="max-width:120px">{{ f.stepName ?? '—' }}</td>
                    <td><span class="badge badge-medium">{{ f.errorTypeCode ?? 'Unknown' }}</span></td>
                    <td class="text-muted">{{ f.detectedAt | date:'MM/dd HH:mm' }}</td>
                    <td><span class="badge" [class]="'badge-' + f.status.toLowerCase()">{{ f.status }}</span></td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </div>

        <!-- Monitored Jobs -->
        <div class="card">
          <div class="card-header">
            <h3>Monitored Jobs</h3>
            <button class="btn btn-ghost btn-sm" (click)="router.navigate(['/scan-jobs'])">Scan Now</button>
          </div>
          @if (jobs().length === 0) {
            <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
          } @else {
            <div class="job-list">
              @for (j of jobs(); track j.monitoredJobId) {
                <div class="job-row">
                  <div class="job-info">
                    <span class="job-name">{{ j.displayName ?? j.name }}</span>
                    <span class="job-meta">{{ j.scanTypeName }} · {{ j.jobTypeName }}</span>
                  </div>
                  <div class="job-actions">
                    <span class="badge badge-info">{{ j.scanCheckRules.length }} rules</span>
                    <button class="btn btn-ghost btn-sm btn-icon" title="Trigger scan"
                            (click)="triggerScan(j)">▶</button>
                  </div>
                </div>
              }
            </div>
          }
        </div>
      </div>

      <!-- Last scan result toast -->
      @if (lastScan()) {
        <div class="scan-result-banner">
          <strong>{{ lastScan()!.jobName }}</strong> scan complete —
          {{ lastScan()!.failuresDetected }} failures · {{ lastScan()!.classifications }} classified · {{ lastScan()!.recommendations }} recommended
          <button class="btn btn-ghost btn-sm" (click)="lastScan.set(null)">✕</button>
        </div>
      }
    </div>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }
    .page-actions { display: flex; gap: 8px; }

    .kpi-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 16px; }
    .kpi-card {
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 20px;
      display: flex;
      align-items: center;
      gap: 16px;
      transition: transform var(--transition);
      &:hover { transform: translateY(-2px); }
      &.danger  { border-left: 3px solid var(--danger);  }
      &.warning { border-left: 3px solid var(--warning); }
      &.info    { border-left: 3px solid var(--info);    }
      &.success { border-left: 3px solid var(--success); }
    }
    .kpi-icon { font-size: 28px; opacity: 0.7; }
    .kpi-value { font-size: 32px; font-weight: 700; line-height: 1; }
    .kpi-label { font-size: 12px; color: var(--text-muted); margin-top: 4px; }

    .row-2col { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }

    .job-list { display: flex; flex-direction: column; gap: 2px; }
    .job-row {
      display: flex; align-items: center; justify-content: space-between;
      padding: 10px 8px;
      border-radius: var(--radius-sm);
      transition: background var(--transition);
      &:hover { background: var(--surface-2); }
    }
    .job-info { display: flex; flex-direction: column; gap: 2px; }
    .job-name { font-size: 13px; font-weight: 500; }
    .job-meta { font-size: 11px; color: var(--text-muted); }
    .job-actions { display: flex; align-items: center; gap: 8px; }

    .scan-result-banner {
      display: flex; align-items: center; gap: 12px;
      background: var(--success-bg); border: 1px solid rgba(34,197,94,0.3);
      border-radius: var(--radius); padding: 12px 16px;
      font-size: 13px; color: var(--success);
      button { margin-left: auto; }
    }

    @media (max-width: 900px) {
      .kpi-grid { grid-template-columns: repeat(2,1fr); }
      .row-2col { grid-template-columns: 1fr; }
    }
  `]
})
export class DashboardComponent implements OnInit {
  router = inject(Router);
  private failuresSvc = inject(FailuresService);
  private scanSvc     = inject(ScanService);
  private jobsSvc     = inject(MonitoredJobsService);

  loading         = signal(false);
  loadingFailures = signal(false);
  scanning        = signal(false);
  recentFailures  = signal<JobFailure[]>([]);
  jobs            = signal<MonitoredJob[]>([]);
  lastScan        = signal<ScanResult | null>(null);
  stats           = signal({ failed: 0, unclassified: 0, recommended: 0, total: 0 });

  ngOnInit() { this.refresh(); }

  refresh() {
    this.loadingFailures.set(true);
    this.failuresSvc.getFailures(1, 10).subscribe({
      next: r => {
        this.recentFailures.set(r.items);
        this.stats.set({
          total:        r.totalCount,
          failed:       r.items.filter(f => f.status === 'Failed').length,
          unclassified: r.items.filter(f => !f.errorTypeCode).length,
          recommended:  r.items.filter(f => f.status === 'Failed' && f.errorTypeCode).length,
        });
        this.loadingFailures.set(false);
      },
      error: () => this.loadingFailures.set(false)
    });

    this.jobsSvc.getAll().subscribe({ next: j => this.jobs.set(j) });
  }

  runScanAll() {
    this.scanning.set(true);
    this.scanSvc.scanAll().subscribe({
      next: results => { this.scanning.set(false); this.refresh(); },
      error: () => this.scanning.set(false)
    });
  }

  triggerScan(job: MonitoredJob) {
    this.scanSvc.scanById(job.monitoredJobId).subscribe({
      next: r => { this.lastScan.set(r); this.refresh(); }
    });
  }
}
