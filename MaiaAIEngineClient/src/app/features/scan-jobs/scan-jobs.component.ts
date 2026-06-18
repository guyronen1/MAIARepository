import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop';
import { ScanService } from '../../core/services/scan.service';
import { MonitoredJobsService } from '../../core/services/monitored-jobs.service';
import { PolledData, WorkerStatusService } from '../../core/services/worker-status.service';
import { MonitoredJob, ScanResult, WorkerStatus } from '../../core/models';
import { scanIconForSources } from '../../core/util/scan-type-label.util';

interface ScanRun { job: MonitoredJob; result: ScanResult | null; running: boolean; error: string | null; }

type JobIconState = 'spinner' | 'success' | 'failed' | 'gray';
const FAIL_OUTCOMES = new Set(['Failed', 'Timeout', 'Stolen']);

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
                <!-- Worker-state icon (spinner / check / triangle / dash) -->
                <span class="status-icon" [class]="'state-' + iconState(run.job)"
                      [title]="iconTooltip(run.job)">
                  @switch (iconState(run.job)) {
                    @case ('spinner') { <span class="spinner-mini"></span> }
                    @case ('success') { ✓ }
                    @case ('failed')  { ▲ }
                    @default          { — }
                  }
                </span>
                <div class="job-icon">{{ jobIcon(run.job) }}</div>
                <div class="job-title">
                  <strong>{{ run.job.displayName ?? run.job.name }}</strong>
                  <span class="text-muted text-sm">{{ run.job.jobTypeName }}</span>
                </div>
                <button class="btn btn-primary btn-sm" (click)="scan(run)" [disabled]="run.running">
                  @if (run.running) { <span class="spinner"></span> }
                  @else { ▶ }
                  Scan
                </button>
              </div>

              <div class="job-meta">
                @for (s of run.job.sources; track s.scanSourceId) {
                  @if (s.logFolder) {
                    <span class="meta-item">📁 {{ s.logFolder }}</span>
                  }
                  @if (s.connectionName) {
                    <span class="meta-item">🗄 {{ s.connectionName }}</span>
                  }
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

    /* Per-job worker-state icon */
    .status-icon {
      width: 16px; height: 16px; flex-shrink: 0;
      display: inline-flex; align-items: center; justify-content: center;
      font-size: 12px; font-weight: 700;
    }
    .status-icon.state-spinner { /* spinner-mini styled below */ }
    .status-icon.state-success { color: var(--success); }
    .status-icon.state-failed  { color: var(--danger); }
    .status-icon.state-gray    { color: var(--text-muted); }

    .spinner-mini {
      width: 12px; height: 12px;
      border: 2px solid var(--border);
      border-top-color: var(--primary);
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }
    @keyframes spin { to { transform: rotate(360deg); } }
  `]
})
export class ScanJobsComponent implements OnInit, OnDestroy {
  private jobsSvc   = inject(MonitoredJobsService);
  private scanSvc   = inject(ScanService);
  private statusSvc = inject(WorkerStatusService);

  loading    = signal(false);
  allRunning = signal(false);
  runs       = signal<ScanRun[]>([]);

  // Live worker-status snapshot — used to render real-time spinners on
  // jobs currently being claimed (the per-job lease on each card is the
  // snapshot from page load and would otherwise go stale). The service
  // emits PolledData<WorkerStatus>; unwrap `.value` so downstream sees the
  // raw shape.
  private static readonly EMPTY: PolledData<WorkerStatus> = {
    value: null, isStale: false, lastUpdatedAt: null,
  };
  private statusPolled = toSignal(this.statusSvc.status$, { initialValue: ScanJobsComponent.EMPTY });
  private status       = computed(() => this.statusPolled().value);
  private activeIds    = computed(() =>
    new Set((this.status()?.activeScans ?? []).map(a => a.monitoredJobId)));

  ngOnInit() {
    this.statusSvc.start();  // refcounted — also OK if dashboard already started it

    this.loading.set(true);
    this.jobsSvc.getAll().subscribe({
      next: jobs => {
        this.runs.set(jobs.map(j => ({ job: j, result: null, running: false, error: null })));
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }

  ngOnDestroy() {
    this.statusSvc.stop();
  }

  iconState(job: MonitoredJob): JobIconState {
    if (!job.isActive) return 'gray';
    if (this.activeIds().has(job.monitoredJobId)) return 'spinner';
    const lease = job.lease;
    const claimedNow = lease?.leasedBy && lease.leasedUntil
      && new Date(lease.leasedUntil).getTime() > Date.now();
    if (claimedNow) return 'spinner';
    if (lease?.lastRunOutcome === 'Success') return 'success';
    if (lease?.lastRunOutcome && FAIL_OUTCOMES.has(lease.lastRunOutcome)) return 'failed';
    return 'gray';
  }

  iconTooltip(job: MonitoredJob): string {
    if (!job.isActive) return 'Job inactive';
    if (this.iconState(job) === 'spinner') return 'Scanning now…';
    const l = job.lease;
    if (!l?.lastRunOutcome || !l.lastRunCompletedAt) return 'No run recorded yet';
    const when    = new Date(l.lastRunCompletedAt).toLocaleString();
    const durSec  = l.lastRunDurationMs != null ? (l.lastRunDurationMs / 1000).toFixed(1) + 's' : '—';
    const err     = l.lastRunError ? ` — ${l.lastRunError}` : '';
    return `Last run: ${l.lastRunOutcome} at ${when}, duration ${durSec}${err}`;
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

  jobIcon(job: MonitoredJob): string {
    return job.sources.length ? scanIconForSources(job.sources) : '📋';
  }
}
