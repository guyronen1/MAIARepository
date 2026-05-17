import { Component, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ScanService } from '../../core/services/scan.service';
import { MonitoredJobsService } from '../../core/services/monitored-jobs.service';
import { MonitoredJob, ScanResult } from '../../core/models';

interface ScanRun { job: MonitoredJob; result: ScanResult | null; running: boolean; error: string | null; }

@Component({
  selector: 'app-scan-jobs',
  standalone: true,
  imports: [],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Scan Jobs</h1>
          <p class="text-muted text-sm">Manually trigger scans on monitored jobs</p>
        </div>
        <button class="btn btn-primary" (click)="scanAll()" [disabled]="allRunning()">
          @if (allRunning()) { <span class="spinner"></span> }
          Run All Scans
        </button>
      </div>

      @if (loading()) {
        <div class="loading-overlay"><span class="spinner"></span> Loading jobs…</div>
      } @else {
        <div class="jobs-grid">
          @for (run of runs(); track run.job.monitoredJobId) {
            <div class="job-card" [class.running]="run.running" [class.has-result]="run.result">
              <div class="job-card-header">
                <div class="job-icon">{{ scanTypeIcon(run.job.scanTypeId) }}</div>
                <div class="job-title">
                  <strong>{{ run.job.displayName ?? run.job.name }}</strong>
                  <span class="text-muted text-sm">{{ run.job.jobTypeName }} · {{ run.job.scanTypeName }}</span>
                </div>
                <button class="btn btn-primary btn-sm" (click)="scan(run)" [disabled]="run.running">
                  @if (run.running) { <span class="spinner"></span> }
                  @else { ▶ }
                  Scan
                </button>
              </div>

              <div class="job-meta">
                @if (run.job.logFolder) {
                  <span class="meta-item">📁 {{ run.job.logFolder }} / {{ run.job.searchPatterns }}</span>
                }
                @if (run.job.connectionName) {
                  <span class="meta-item">🗄 {{ run.job.connectionName }}</span>
                }
                <span class="meta-item">{{ run.job.scanCheckRules.length }} check rules</span>
              </div>

              @if (run.result) {
                <div class="scan-result">
                  <div class="result-row">
                    <div class="result-stat" [class.highlight]="run.result.failuresDetected > 0">
                      <span class="stat-value">{{ run.result.failuresDetected }}</span>
                      <span class="stat-label">Failures</span>
                    </div>
                    <div class="result-stat">
                      <span class="stat-value">{{ run.result.classifications }}</span>
                      <span class="stat-label">Classified</span>
                    </div>
                    <div class="result-stat">
                      <span class="stat-value">{{ run.result.recommendations }}</span>
                      <span class="stat-label">Recommended</span>
                    </div>
                    <div class="result-stat">
                      <span class="stat-value">{{ run.result.fixesExecuted }}</span>
                      <span class="stat-label">Fixed</span>
                    </div>
                  </div>
                  <p class="result-detail text-muted text-sm">{{ run.result.detail }}</p>
                </div>
              }

              @if (run.error) {
                <div class="scan-error">⚠ {{ run.error }}</div>
              }
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }
    .jobs-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(380px, 1fr)); gap: 16px; }
    .job-card {
      background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); padding: 18px;
      display: flex; flex-direction: column; gap: 12px;
      transition: border-color var(--transition);
      &.running { border-color: var(--primary); }
      &.has-result { border-color: var(--success); }
    }
    .job-card-header { display: flex; align-items: center; gap: 12px; }
    .job-icon { font-size: 24px; }
    .job-title { flex: 1; display: flex; flex-direction: column; gap: 2px; strong { font-size: 14px; } }
    .job-meta { display: flex; flex-wrap: wrap; gap: 8px; }
    .meta-item { font-size: 11px; color: var(--text-muted); background: var(--surface-2); padding: 3px 8px; border-radius: 4px; }
    .scan-result { background: var(--surface-2); border-radius: var(--radius-sm); padding: 12px; display: flex; flex-direction: column; gap: 8px; }
    .result-row { display: flex; gap: 16px; }
    .result-stat { display: flex; flex-direction: column; align-items: center; gap: 2px; flex: 1;
      .stat-value { font-size: 22px; font-weight: 700; color: var(--text); }
      .stat-label { font-size: 10px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; }
      &.highlight .stat-value { color: var(--warning); }
    }
    .result-detail { border-top: 1px solid var(--border); padding-top: 8px; font-size: 11px; }
    .scan-error { background: var(--danger-bg); color: var(--danger); border-radius: var(--radius-sm); padding: 8px 12px; font-size: 12px; }
  `]
})
export class ScanJobsComponent implements OnInit {
  private jobsSvc = inject(MonitoredJobsService);
  private scanSvc = inject(ScanService);

  loading    = signal(false);
  allRunning = signal(false);
  runs       = signal<ScanRun[]>([]);

  ngOnInit() {
    this.loading.set(true);
    this.jobsSvc.getAll().subscribe({
      next: jobs => {
        this.runs.set(jobs.map(j => ({ job: j, result: null, running: false, error: null })));
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }

  scan(run: ScanRun) {
    run.running = true; run.error = null;
    this.scanSvc.scanById(run.job.monitoredJobId).subscribe({
      next: r => { run.result = r; run.running = false; this.runs.update(v => [...v]); },
      error: e => { run.error = e?.error?.message ?? 'Scan failed'; run.running = false; this.runs.update(v => [...v]); }
    });
  }

  scanAll() {
    this.allRunning.set(true);
    this.scanSvc.scanAll().subscribe({
      next: results => {
        const map = new Map(results.map(r => [r.jobName, r]));
        this.runs.update(runs => runs.map(run => ({
          ...run,
          result: map.get(run.job.name) ?? null,
          running: false
        })));
        this.allRunning.set(false);
      },
      error: () => this.allRunning.set(false)
    });
  }

  scanTypeIcon(id: number): string {
    const icons: Record<number, string> = { 1: '📁', 2: '🗄', 3: '🌐' };
    return icons[id] ?? '📋';
  }
}
