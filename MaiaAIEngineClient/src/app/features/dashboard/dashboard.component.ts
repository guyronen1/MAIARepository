import { Component, OnDestroy, OnInit, computed, effect, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { DashboardStats } from '../../core/services/failures.service';
import { ScanService } from '../../core/services/scan.service';
import { PolledData, WorkerStatusService } from '../../core/services/worker-status.service';
import { JobFailure, JobLastScanRow, MonitoredJob, ScanResult, WorkerStatus } from '../../core/models';
import { ErrorsOverTimeChartComponent } from './errors-over-time-chart.component';

type LastScanRecord = NonNullable<JobLastScanRow['lastScan']>;
type JobIconState   = 'spinner' | 'success' | 'failed' | 'gray';
const FAIL_OUTCOMES = new Set(['Failed', 'Timeout', 'Stolen']);

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [DatePipe, ErrorsOverTimeChartComponent],
  template: `
    <div class="page">
      <div class="page-header">
        <div class="page-title">
          <h1>Dashboard</h1>
          <p class="text-muted text-sm">Real-time monitoring overview</p>
        </div>

        <!-- Inline activity strip — slot sits between title and actions, vertically
             centered with the H1. Empty when nothing to show (no reserved space).
             Completed variant auto-dismisses after 30s; hover pauses, mouseleave
             restarts the FULL 30s (deliberate UX). -->
        <div class="activity-slot">
          @if (!bannerCollapsed()) {
            <div class="activity-inline"
                 [class.completed]="!activityInFlight() && showActivity()"
                 [class.in-flight]="activityInFlight()"
                 (mouseenter)="onBannerMouseEnter()"
                 (mouseleave)="onBannerMouseLeave()">
              @if (activityInFlight()) {
                <span class="spinner-mini"></span>
              } @else {
                <span class="check-mark">✓</span>
              }
              <span class="activity-text" [title]="activityLabel()">{{ activityLabel() }}</span>
              @if (!activityInFlight() && showActivity()) {
                <button class="banner-dismiss" type="button"
                        (click)="dismissBanner()" title="Dismiss">✕</button>
              }
            </div>
          }
        </div>

      </div>

      <!-- KPI Cards — 4 focused tiles, click drills into /failures with the matching view -->
      <div class="kpi-grid">
        <button class="kpi-card danger" (click)="drill('active')" title="Show all active failures">
          <div class="kpi-icon">⚠</div>
          <div class="kpi-body">
            <div class="kpi-value">{{ stats().active }}</div>
            <div class="kpi-label">Active Failures</div>
          </div>
        </button>
        <button class="kpi-card info" (click)="drill('awaiting-action')" title="Show failures awaiting operator action">
          <div class="kpi-icon">💡</div>
          <div class="kpi-body">
            <div class="kpi-value">{{ stats().awaitingAction }}</div>
            <div class="kpi-label">Awaiting Action</div>
          </div>
        </button>
        <button class="kpi-card success" (click)="drill('resolved')" title="Failures resolved today (auto-heal + operator approval)">
          <div class="kpi-icon">✓</div>
          <div class="kpi-body">
            <div class="kpi-value">{{ stats().resolvedToday }}</div>
            <div class="kpi-label">Resolved Today</div>
            <div class="kpi-breakdown">Auto: {{ stats().autoFixedToday }} · Manual: {{ stats().manuallyFixedToday }}</div>
          </div>
        </button>
        <button class="kpi-card warning" (click)="drill('manual-required')" title="Failures that need operator intervention">
          <div class="kpi-icon">!</div>
          <div class="kpi-body">
            <div class="kpi-value">{{ stats().manualRequired }}</div>
            <div class="kpi-label">Manual Required</div>
          </div>
        </button>
      </div>

      <!-- Errors Over Time — full-width analytics chart -->
      <app-errors-over-time-chart></app-errors-over-time-chart>

      <div class="row-2col">
        <!-- Recent Failures -->
        <div class="card">
          <div class="card-header">
            <h3>Recent Failures</h3>
            <button class="btn btn-ghost btn-sm" (click)="router.navigate(['/failures'])">View All</button>
          </div>
          @if (loadingFailures()) {
            <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
          } @else if (recentFailures()!.length === 0) {
            <div class="empty-state"><span class="empty-icon">✓</span><p>No failures found</p></div>
          } @else {
            <table class="data-table">
              <thead>
                <tr><th>Job</th><th>Step</th><th>Error Type</th><th>Detected</th><th>Status</th></tr>
              </thead>
              <tbody>
                @for (f of recentFailures()!; track f.failureId) {
                  <tr class="clickable"
                      (click)="router.navigate(['/failures'], { queryParams: { selected: f.failureId } })"
                      [title]="f.errorMessage ?? ''">
                    <td dir="auto">{{ f.monitoredJobName ?? '—' }}</td>
                    <td class="truncate" style="max-width:120px" dir="auto">{{ f.stepName ?? '—' }}</td>
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
          @if (loadingJobs()) {
            <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
          } @else if (jobs()!.length === 0) {
            <div class="empty-state"><span class="empty-icon">—</span><p>No monitored jobs configured</p></div>
          } @else {
            <div class="job-list">
              @for (j of jobs()!; track j.monitoredJobId) {
                <div class="job-row-wrap" [class.expanded]="isExpanded(j.monitoredJobId)">
                  <!-- Compact single-line row (~48px). Click to expand. -->
                  <div class="job-row-compact" (click)="toggleExpanded(j.monitoredJobId)">
                    <span class="row-status-icon" [class]="'state-' + jobIconState(j.monitoredJobId)"
                          [title]="jobIconTooltip(j.monitoredJobId)">
                      @switch (jobIconState(j.monitoredJobId)) {
                        @case ('spinner') { <span class="spinner-mini"></span> }
                        @case ('success') { ✓ }
                        @case ('failed')  { ▲ }
                        @default          { — }
                      }
                    </span>
                    <span class="row-job-name" dir="auto">{{ j.displayName ?? j.name }}</span>
                    <span class="row-meta">{{ j.scanTypeName }} · {{ j.jobTypeName }}</span>

                    @if (lastScanFor(j.monitoredJobId); as ls) {
                      <span class="row-badge badge" [class]="lastScanBadgeClass(j.monitoredJobId)">
                        {{ ls.lastScan.outcome }}
                      </span>
                      <span class="row-stat text-muted text-sm">
                        {{ ls.lastScan.durationMs }}ms · {{ relativeAge(ls.lastScan.completedAt) }}
                        @if (anyCount(j.monitoredJobId)) {
                          @if (ls.lastScan.failuresDetected > 0) {
                            · <span class="count-warn">{{ ls.lastScan.failuresDetected }} failures</span>
                          }
                          @if (ls.lastScan.classifications > 0) {
                            · {{ ls.lastScan.classifications }} classified
                          }
                          @if (ls.lastScan.recommendations > 0) {
                            · {{ ls.lastScan.recommendations }} recs
                          }
                        }
                      </span>
                    } @else {
                      <span class="row-badge badge badge-muted">No scans</span>
                      <span class="row-stat text-muted text-sm">—</span>
                    }

                    <span class="row-rules badge badge-info">{{ rulesLabel(j.scanCheckRules.length) }}</span>
                    <button class="btn btn-ghost btn-sm btn-icon" title="Trigger scan"
                            (click)="triggerScan(j); $event.stopPropagation()">▶</button>
                  </div>

                  <!-- Expanded detail panel — animated max-height + opacity -->
                  <div class="job-row-detail">
                    @if (lastScanFor(j.monitoredJobId); as ls) {
                      <dl class="detail-grid">
                        <dt>Last outcome</dt>
                        <dd>
                          <span class="badge" [class]="lastScanBadgeClass(j.monitoredJobId)">
                            {{ ls.lastScan.outcome }}
                          </span>
                        </dd>
                        <dt>Completed</dt>
                        <dd>{{ ls.lastScan.completedAt | date:'MM/dd HH:mm:ss' }} ({{ relativeAge(ls.lastScan.completedAt) }})</dd>
                        <dt>Duration</dt>
                        <dd>{{ ls.lastScan.durationMs }} ms</dd>
                        <dt>Failures detected</dt>
                        <dd [class.text-warn]="ls.lastScan.failuresDetected > 0">{{ ls.lastScan.failuresDetected }}</dd>
                        <dt>Classifications</dt>
                        <dd>{{ ls.lastScan.classifications }}</dd>
                        <dt>Recommendations</dt>
                        <dd>{{ ls.lastScan.recommendations }}</dd>
                      </dl>
                    } @else {
                      <p class="text-muted text-sm">This job has not been scanned yet.</p>
                    }
                    @if (j.description) {
                      <p class="detail-desc text-sm" dir="auto">{{ j.description }}</p>
                    }
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
    /* Dashboard-only override of the global .page rhythm — tighter vertical gap
       and top padding so KPIs + chart + the two-panel section fit on a 1080p
       screen without scrolling. Other pages keep the global 14px gap. */
    :host .page { gap: 10px; padding-top: 14px; }
    h1 { font-size: 20px; font-weight: 700; line-height: 1.15; }
    /* 2-column header: title | inline status slot. Refresh button removed —
       polling keeps every panel live, so a manual refresh trigger is vestigial.
       The slot is centered vertically against the H1 (not the subtitle) via
       align-self on .activity-slot. */
    .page-header {
      display: grid;
      grid-template-columns: auto 1fr;
      align-items: start;
      gap: 16px;
    }
    .page-title { display: flex; flex-direction: column; }
    .activity-slot {
      align-self: start;
      min-width: 0;
      padding-top: 4px;
      justify-self: end;
      max-width: 100%;
    }

    .kpi-grid { display: grid; grid-template-columns: repeat(6, 1fr); gap: 10px; }
    .kpi-card {
      /* button reset — these are <button> elements so they're keyboard-accessible */
      font: inherit; color: inherit; text-align: left;
      cursor: pointer;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 10px 12px;
      display: flex;
      align-items: center;
      gap: 10px;
      transition: transform var(--transition), box-shadow var(--transition);
      &:hover { transform: translateY(-1px); box-shadow: 0 2px 8px rgba(0,0,0,0.08); }
      &:focus-visible { outline: 2px solid var(--primary, #6366f1); outline-offset: 2px; }
      &.danger  { border-left: 3px solid var(--danger);  }
      &.warning { border-left: 3px solid var(--warning); }
      &.info    { border-left: 3px solid var(--info);    }
      &.success { border-left: 3px solid var(--success); }
      &.auto    { border-left: 3px solid var(--primary, #6366f1); }
      &.muted   { border-left: 3px solid var(--border); }
    }
    .kpi-icon { font-size: 20px; opacity: 0.7; flex-shrink: 0; }
    .kpi-value { font-size: 22px; font-weight: 700; line-height: 1; }
    .kpi-label { font-size: 11px; color: var(--text-muted); margin-top: 2px; }
    .kpi-breakdown { font-size: 10px; color: var(--text-muted); margin-top: 2px; }

    .row-2col { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }

    .job-list { display: flex; flex-direction: column; gap: 1px; }

    /* Compact single-line row (~48px) — click to expand */
    .job-row-wrap { border-radius: var(--radius-sm); transition: background var(--transition); }
    .job-row-wrap:hover { background: var(--surface-2); }

    .job-row-compact {
      display: flex; align-items: center; gap: 10px;
      min-height: 44px; padding: 8px 10px;
      cursor: pointer;
    }
    .row-status-icon {
      width: 16px; height: 16px; flex-shrink: 0;
      display: inline-flex; align-items: center; justify-content: center;
      font-size: 11px; font-weight: 700;
      &.state-success { color: var(--success); }
      &.state-failed  { color: var(--danger); }
      &.state-gray    { color: var(--text-muted); }
    }
    .row-job-name  { font-size: 13px; font-weight: 600; color: var(--text); white-space: nowrap; }
    .row-meta      { font-size: 11px; color: var(--text-muted); white-space: nowrap; }
    .row-badge     { flex-shrink: 0; }
    .row-stat      { flex: 1; min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .row-rules     { flex-shrink: 0; }
    .count-warn    { color: var(--warning); font-weight: 600; }
    .badge-muted   {
      background: var(--surface-3); color: var(--text-muted);
      border: 1px solid var(--border);
    }

    /* Expand panel — animated max-height + opacity */
    .job-row-detail {
      max-height: 0; opacity: 0; padding: 0 10px;
      overflow: hidden;
      transition: max-height 250ms ease, opacity 250ms ease, padding 250ms ease;
    }
    .job-row-wrap.expanded .job-row-detail {
      max-height: 240px; opacity: 1; padding: 4px 10px 12px 32px;
    }
    .detail-grid {
      display: grid; grid-template-columns: 140px 1fr; row-gap: 4px; column-gap: 12px;
      font-size: 12px; color: var(--text);
      dt { color: var(--text-muted); font-weight: 500; }
      dd { color: var(--text); }
    }
    .detail-desc { margin-top: 8px; color: var(--text-muted); font-style: italic; }
    .text-warn { color: var(--warning); font-weight: 600; }
    .spinner-mini {
      width: 12px; height: 12px;
      border: 2px solid var(--border);
      border-top-color: var(--primary);
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }

    .scan-result-banner {
      display: flex; align-items: center; gap: 12px;
      background: var(--success-bg); border: 1px solid rgba(34,197,94,0.3);
      border-radius: var(--radius); padding: 12px 16px;
      font-size: 13px; color: var(--success);
      button { margin-left: auto; }
    }

    /* Inline activity status — subtle left-border accent + matching text color,
       no filled background. Lives inside the header's middle column. Fades in
       when mounted (200ms); fade-out is achieved by the @if removing the node. */
    .activity-inline {
      display: inline-flex; align-items: center; gap: 8px;
      max-width: 100%;
      padding: 4px 10px 4px 10px;
      border-left: 3px solid var(--primary, #6366f1);
      background: transparent;
      color: var(--text);
      font-size: 12px;
      animation: status-fade-in 220ms ease-out;
      /* Variant colors are applied via the left border + text color, not a fill. */
      &.in-flight { color: var(--primary, #6366f1); border-left-color: var(--primary, #6366f1); }
      &.completed { color: var(--success, #22c55e); border-left-color: var(--success, #22c55e); }
    }
    @keyframes status-fade-in {
      from { opacity: 0; transform: translateY(-2px); }
      to   { opacity: 1; transform: none; }
    }
    .check-mark { font-weight: 700; color: var(--success, #22c55e); }
    .activity-text {
      font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
      max-width: 480px;
    }
    .banner-dismiss {
      background: transparent; border: none; cursor: pointer;
      color: inherit; opacity: 0.55;
      font-size: 12px; padding: 2px 6px; border-radius: 3px;
      transition: opacity var(--transition), background var(--transition);
      &:hover { opacity: 1; background: rgba(0,0,0,0.05); }
    }
    .spinner-mini {
      width: 12px; height: 12px;
      border: 2px solid rgba(99, 102, 241, 0.3);
      border-top-color: var(--primary, #6366f1);
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }
    @keyframes spin { to { transform: rotate(360deg); } }

    @media (max-width: 1400px) { .kpi-grid { grid-template-columns: repeat(3, 1fr); } }
    @media (max-width: 900px) {
      .kpi-grid { grid-template-columns: repeat(2,1fr); }
      .row-2col { grid-template-columns: 1fr; }
    }
  `]
})
export class DashboardComponent implements OnInit, OnDestroy {
  router = inject(Router);
  private scanSvc   = inject(ScanService);
  private statusSvc = inject(WorkerStatusService);

  lastScan = signal<ScanResult | null>(null);

  // Raw PolledData<T> from the service — each slice is independent and tracks
  // its own isStale / lastUpdatedAt. The component-facing getters below unwrap
  // `.value` so templates stay simple; isStale is available for a future
  // per-panel "couldn't refresh" indicator (not rendered yet).
  private static readonly EMPTY_POLLED: PolledData<unknown> = {
    value: null, isStale: false, lastUpdatedAt: null,
  };
  private statusPolled = toSignal(
    this.statusSvc.status$,
    { initialValue: DashboardComponent.EMPTY_POLLED as PolledData<WorkerStatus> });
  private statsPolled = toSignal(
    this.statusSvc.stats$,
    { initialValue: DashboardComponent.EMPTY_POLLED as PolledData<DashboardStats> });
  private recentFailuresPolled = toSignal(
    this.statusSvc.recentFailures$,
    { initialValue: DashboardComponent.EMPTY_POLLED as PolledData<JobFailure[]> });
  private monitoredJobsPolled = toSignal(
    this.statusSvc.monitoredJobs$,
    { initialValue: DashboardComponent.EMPTY_POLLED as PolledData<MonitoredJob[]> });

  /** Top-10 recent failures; null while the first response is in flight. */
  recentFailures  = computed(() => this.recentFailuresPolled().value);
  /** Active monitored jobs; null while the first response is in flight. */
  jobs            = computed(() => this.monitoredJobsPolled().value);
  loadingFailures = computed(() => this.recentFailures() === null);
  loadingJobs     = computed(() => this.jobs() === null);

  // Live KPI stats — driven by the polling service so the cards update without
  // operator action whenever a scan lands. The polled wrapper is nullable on
  // first emit; collapse to defaults so the template doesn't null-check every KPI.
  private static readonly EMPTY_STATS: DashboardStats = {
    totalFailures: 0, active: 0, resolved: 0, manualRequired: 0,
    unclassified: 0, awaitingAction: 0, autoFixed: 0, manuallyFixed: 0,
    resolvedToday: 0, autoFixedToday: 0, manuallyFixedToday: 0,
  };
  stats = computed<DashboardStats>(() => this.statsPolled().value ?? DashboardComponent.EMPTY_STATS);

  // Live worker-status — refcounted polling keeps this alive while either the
  // dashboard or the scan-jobs screen is mounted.
  private workerStatus = computed(() => this.statusPolled().value);

  // Activity-strip visibility: either a scan is in-flight, OR a scan completed
  // recently enough that the operator should still see the "found N errors" beat.
  // Quick file scans finish in <1s and the 5s poll usually misses them in-flight;
  // the recent-completion window catches that.
  showActivity = computed(() => {
    const s = this.workerStatus();
    return !!s && (s.activeScans.length > 0 || s.recentScansLast30s.length > 0);
  });

  activityLabel = computed(() => {
    const s = this.workerStatus();
    if (!s) return '';
    if (s.activeScans.length > 0) {
      const names = s.activeScans.map(a => a.jobName);
      const visible = names.slice(0, 3).join(', ');
      const more = names.length > 3 ? `, … and ${names.length - 3} more` : '';
      return `Active scans: ${s.activeScans.length} of ${s.jobSummary.total} jobs (${visible}${more})`;
    }
    if (s.recentScansLast30s.length > 0) {
      const totalNew = s.recentScansLast30s.reduce((acc, r) => acc + r.failuresDetected, 0);
      const last    = s.recentScansLast30s[0];
      const ageSec  = Math.max(0, Math.floor((Date.now() - new Date(last.completedAt).getTime()) / 1000));
      const when    = ageSec < 5 ? 'just now' : `${ageSec}s ago`;
      const n = s.recentScansLast30s.length;
      const scope = n === 1 ? `${last.jobName}` : `${n} scans`;
      if (totalNew > 0)
        return `✓ ${scope} completed ${when} — ${totalNew} new failure${totalNew === 1 ? '' : 's'} detected`;
      return `✓ ${scope} completed ${when} — clean`;
    }
    return '';
  });

  /** True when there's an in-flight scan (use spinner); false when only recent-completed (use ✓) */
  activityInFlight = computed(() => (this.workerStatus()?.activeScans.length ?? 0) > 0);

  // ── Banner auto-dismiss (completed variant only — in-flight stays sticky) ─────
  /** Has the operator (or the timer) dismissed the current completed banner? */
  private bannerDismissed     = signal(false);
  private lastSeenScanRunId   = signal<number | null>(null);
  private dismissTimerId?:    ReturnType<typeof setTimeout>;
  private readonly DISMISS_MS = 30_000;

  /** The strip is collapsed when (a) there's nothing to show, OR (b) the completed
   *  variant has been dismissed and no new in-flight scan has arrived since. */
  bannerCollapsed = computed(() => {
    if (!this.showActivity()) return true;
    if (this.activityInFlight()) return false;          // never auto-dismiss in-flight
    return this.bannerDismissed();
  });

  // Watch the most-recent recent-scan id; when it changes, treat that as a new
  // "completed" event → reset dismissed flag and (re)start the auto-dismiss timer.
  // When the strip flips to in-flight, the timer is irrelevant (cleared).
  // Effect must run in the injection context — declared as a class field initialiser
  // so it picks up the component's injector.
  private bannerLifecycleEffect = effect(() => {
    const s = this.workerStatus();
    const latestId = s?.recentScansLast30s?.[0]?.scanRunId ?? null;

    if (this.activityInFlight()) {
      // In-flight overrides — kill any pending dismissal timer
      this.clearDismissTimer();
      return;
    }

    if (latestId !== null && latestId !== this.lastSeenScanRunId()) {
      // New completed scan — un-dismiss, mark as seen, restart the 30s clock
      this.lastSeenScanRunId.set(latestId);
      this.bannerDismissed.set(false);
      this.scheduleDismiss();
    }
  });

  private scheduleDismiss(): void {
    this.clearDismissTimer();
    this.dismissTimerId = setTimeout(() => this.bannerDismissed.set(true), this.DISMISS_MS);
  }

  private clearDismissTimer(): void {
    if (this.dismissTimerId !== undefined) {
      clearTimeout(this.dismissTimerId);
      this.dismissTimerId = undefined;
    }
  }

  onBannerMouseEnter(): void {
    // Pause auto-dismiss while operator is reading
    this.clearDismissTimer();
  }

  onBannerMouseLeave(): void {
    // Deliberate UX: restart the FULL 30s (not resume) — operator who hovered at
    // second 28 gets a fresh re-read window. Skip if the banner is already dismissed
    // or in-flight (in-flight ignores the timer anyway).
    if (this.bannerDismissed() || this.activityInFlight()) return;
    this.scheduleDismiss();
  }

  dismissBanner(): void {
    this.clearDismissTimer();
    this.bannerDismissed.set(true);
  }

  /** monitoredJobId → latest-scan summary, refreshed every poll. */
  private jobLastScans = computed<Map<number, LastScanRecord>>(() => {
    const map = new Map<number, LastScanRecord>();
    for (const row of this.workerStatus()?.jobs ?? [])
      if (row.lastScan) map.set(row.monitoredJobId, row.lastScan);
    return map;
  });

  lastScanFor(monitoredJobId: number): { lastScan: LastScanRecord } | null {
    const ls = this.jobLastScans().get(monitoredJobId);
    return ls ? { lastScan: ls } : null;
  }

  relativeAge(iso: string): string {
    const sec = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
    if (sec < 5)    return 'just now';
    if (sec < 60)   return `${sec}s ago`;
    if (sec < 3600) return `${Math.floor(sec / 60)}m ago`;
    if (sec < 86400)return `${Math.floor(sec / 3600)}h ago`;
    return `${Math.floor(sec / 86400)}d ago`;
  }

  lastScanLabel(monitoredJobId: number): string {
    const ls = this.jobLastScans().get(monitoredJobId);
    if (!ls) return 'No scans yet';
    return `${ls.outcome} · ${ls.durationMs}ms · ${this.relativeAge(ls.completedAt)}`;
  }

  lastScanBadgeClass(monitoredJobId: number): string {
    const ls = this.jobLastScans().get(monitoredJobId);
    if (!ls) return 'badge-muted';
    return ls.outcome === 'Success' ? 'badge-resolved' : 'badge-failed';
  }

  /** Cross-reference active-scans list (live spinner) with the per-job lease snapshot. */
  jobIconState(monitoredJobId: number): JobIconState {
    if ((this.workerStatus()?.activeScans ?? []).some(a => a.monitoredJobId === monitoredJobId))
      return 'spinner';
    const ls = this.jobLastScans().get(monitoredJobId);
    if (!ls) return 'gray';
    if (ls.outcome === 'Success') return 'success';
    if (FAIL_OUTCOMES.has(ls.outcome)) return 'failed';
    return 'gray';
  }

  jobIconTooltip(monitoredJobId: number): string {
    if (this.jobIconState(monitoredJobId) === 'spinner') return 'Scanning now…';
    const ls = this.jobLastScans().get(monitoredJobId);
    if (!ls) return 'No run recorded yet';
    return `Last run: ${ls.outcome} at ${new Date(ls.completedAt).toLocaleString()}, duration ${ls.durationMs}ms`;
  }

  anyCount(monitoredJobId: number): boolean {
    const ls = this.jobLastScans().get(monitoredJobId);
    return !!ls && (ls.failuresDetected > 0 || ls.classifications > 0 || ls.recommendations > 0);
  }

  rulesLabel(n: number): string {
    return n === 1 ? '1 rule' : `${n} rules`;
  }

  // ── Expand / collapse per-row detail ─────────────────────────────────
  private expandedIds = signal<Set<number>>(new Set<number>());
  isExpanded(monitoredJobId: number): boolean {
    return this.expandedIds().has(monitoredJobId);
  }
  toggleExpanded(monitoredJobId: number): void {
    const next = new Set(this.expandedIds());
    next.has(monitoredJobId) ? next.delete(monitoredJobId) : next.add(monitoredJobId);
    this.expandedIds.set(next);
  }

  ngOnInit() {
    // Refcounted polling — start() spins up the timer if no other consumer is
    // already polling. The dashboard-snapshot endpoint feeds all four panels
    // (stats / worker-status / recent failures / monitored jobs) atomically.
    this.statusSvc.start();
  }

  ngOnDestroy() {
    this.statusSvc.stop();
    this.clearDismissTimer();
  }

  /** Navigate to the failures list with the matching server-side filter. */
  drill(view:
    | 'active' | 'unclassified' | 'awaiting-action'
    | 'resolved' | 'manual-required'
    | 'auto-fixed' | 'operator-fixed' | 'all') {
    this.router.navigate(['/failures'], { queryParams: view === 'all' ? {} : { view } });
  }

  triggerScan(job: MonitoredJob) {
    // Manual scan — toast on completion. Post-scan reload is unnecessary: the
    // 5s snapshot poll will pick up any new failures / lease state on its own.
    this.scanSvc.scanById(job.monitoredJobId).subscribe({
      next: r => this.lastScan.set(r)
    });
  }
}
