import { Component, OnDestroy, OnInit, computed, effect, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { DashboardStats } from '../../core/services/failures.service';
import { ScanService } from '../../core/services/scan.service';
import { PolledData, WorkerStatusService } from '../../core/services/worker-status.service';
import { JobFailure, JobLastScanRow, MonitoredJob, ScanResult, SourceLastScanRow, WorkerStatus } from '../../core/models';
import { ErrorsOverTimeChartComponent } from './errors-over-time-chart.component';
import { FailuresByJobChartComponent } from './failures-by-job-chart.component';
import { ResolutionMixChartComponent } from './resolution-mix-chart.component';
import { FailureDetailComponent } from '../failures/failure-detail.component';
import { DrawerComponent } from '../../shared/drawer/drawer.component';
import { scanTypeLabelFromNames, scanTypeTitleFromSources } from '../../core/util/scan-type-label.util';
import { StatusBadgeComponent } from '../../shared/status-badge/status-badge.component';

type ChartRange = '24h' | '7d' | '30d';

type LastScanRecord = NonNullable<JobLastScanRow['lastScan']>;
type JobIconState   = 'spinner' | 'success' | 'failed' | 'gray';
const FAIL_OUTCOMES = new Set(['Failed', 'Timeout', 'Stolen']);

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    StatusBadgeComponent,
    ErrorsOverTimeChartComponent,
    FailuresByJobChartComponent,
    ResolutionMixChartComponent,
    FailureDetailComponent,
    DrawerComponent,
  ],
  template: `
    <div class="page">
      <div class="page-header">
        <div class="page-title">
          <h1>Dashboard</h1>
          <!-- Worker Live/Paused status + pause control now live in the top-bar
               pill (single global status signal) — no duplicate here. -->
          <p class="text-muted text-sm">Real-time monitoring overview</p>
        </div>

        <!-- Inline activity strip. In-flight scans show a live spinner strip
             (bound directly to worker state — appears/disappears on its own).
             A completed scan shows a plain self-dismissing "✓ …" beat for a few
             seconds. No hover-pause / manual dismiss (simplified, item 10). -->
        <div class="activity-slot">
          @if (activityInFlight()) {
            <div class="activity-inline in-flight">
              <span class="spinner-mini"></span>
              <span class="activity-text" [title]="activityLabel()">{{ activityLabel() }}</span>
            </div>
          } @else if (completedBeat(); as beat) {
            <div class="activity-inline completed">
              <span class="check-mark">✓</span>
              <span class="activity-text" [title]="beat">{{ beat }}</span>
            </div>
          }
        </div>

      </div>

      <!-- Action-first KPI row (P0.3): two hero tiles + a compact 2×2 secondary
           block, one row. Awaiting Action (amber — a decision is pending) and
           Manual Required (red — no automation will help, the loudest bucket)
           dominate; the four context metrics recede into the nested block.
           Every tile routerLinks to its pre-filtered queue. Zero counts dim
           (label + number) so an empty queue recedes. Accents use
           border-inline-start (RTL-safe). -->
      <div class="kpi-row">
        <a class="kpi-hero amber" [class.zero]="stats().awaitingAction === 0"
           routerLink="/failures" [queryParams]="{ view: 'awaiting-action' }"
           title="Failures awaiting an operator decision">
          <div class="hero-eyebrow"><span class="hero-ico">⏳</span> Awaiting Action</div>
          <div class="hero-body">
            <div class="hero-main">
              <span class="hero-value">{{ stats().awaitingAction }}</span>
              <span class="hero-verb">Review →</span>
            </div>
            <div class="hero-sub">needs a decision</div>
          </div>
        </a>

        <a class="kpi-hero red" [class.zero]="stats().manualRequired === 0"
           routerLink="/failures" [queryParams]="{ view: 'manual-required' }"
           title="Failures that need operator intervention — no automation applies">
          <div class="hero-eyebrow"><span class="hero-ico">⚠</span> Manual Required</div>
          <div class="hero-body">
            <div class="hero-main">
              <span class="hero-value">{{ stats().manualRequired }}</span>
              <span class="hero-verb">Handle →</span>
            </div>
            <div class="hero-sub">operator must act</div>
          </div>
        </a>

        <!-- Secondary 2×2 — recessed surface so it reads as context. Titles
             carry the breakdowns the old tiles showed inline (kept compact). -->
        <div class="kpi-secondary">
          <a class="kpi-chip danger" [class.zero]="stats().active === 0"
             [attr.tabindex]="stats().active === 0 ? -1 : null"
             [attr.aria-disabled]="stats().active === 0 || null"
             routerLink="/failures" [queryParams]="{ view: 'active' }"
             title="All active failures">
            <span class="chip-label">Active</span>
            <span class="chip-value">{{ stats().active }}</span>
          </a>
          <a class="kpi-chip neutral" [class.zero]="stats().unconfigured === 0"
             [attr.tabindex]="stats().unconfigured === 0 ? -1 : null"
             [attr.aria-disabled]="stats().unconfigured === 0 || null"
             routerLink="/unconfigured"
             [title]="'Unconfigured — no class: ' + stats().unclassified + ' · no policy: ' + stats().unconfiguredNoPolicy + '. Opens the Unconfigured screen.'">
            <span class="chip-label">Unconfigured</span>
            <span class="chip-value">{{ stats().unconfigured }}</span>
          </a>
          <a class="kpi-chip alarm danger" [class.zero]="stats().fixFailedToday === 0"
             [attr.tabindex]="stats().fixFailedToday === 0 ? -1 : null"
             [attr.aria-disabled]="stats().fixFailedToday === 0 || null"
             routerLink="/failures" [queryParams]="{ view: 'fix-failed' }"
             title="Failures whose automated fix attempts failed today — operator review needed">
            <span class="chip-label">Fix Failures</span>
            <span class="chip-value">{{ stats().fixFailedToday }}</span>
          </a>
          <a class="kpi-chip success" [class.zero]="stats().resolvedToday === 0"
             [attr.tabindex]="stats().resolvedToday === 0 ? -1 : null"
             [attr.aria-disabled]="stats().resolvedToday === 0 || null"
             routerLink="/failures" [queryParams]="{ view: 'resolved' }"
             [title]="'Resolved today — auto ' + stats().autoFixedToday + ' · manual ' + stats().manuallyFixedToday">
            <span class="chip-label">Resolved</span>
            <span class="chip-value">{{ stats().resolvedToday }}</span>
          </a>
        </div>
      </div>

      <!-- Analytics: all three charts in one row. Errors Over Time is widest
           (carries the range toggle + multi-series legend) and its toggle
           drives Failures by Job too via chartRange; Resolution Mix keeps its
           own fixed 7-day window. Collapses to a single column on narrow
           viewports. -->
      <div class="analytics-row">
        <app-errors-over-time-chart
            [range]="chartRange()"
            (rangeChange)="chartRange.set($event)"></app-errors-over-time-chart>
        <app-failures-by-job-chart
            [range]="chartRange()"></app-failures-by-job-chart>
        <app-resolution-mix-chart></app-resolution-mix-chart>
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
                      (click)="selectedFailureId.set(f.failureId)"
                      [title]="f.errorMessage ?? ''">
                    <td dir="auto">{{ f.monitoredJobName ?? '—' }}</td>
                    <td class="truncate" style="max-width:120px" dir="auto">{{ f.stepName ?? '—' }}</td>
                    <td><span class="badge badge-type">{{ f.errorTypeCode ?? 'Unknown' }}</span></td>
                    <td class="text-muted">{{ f.detectedAt | date:'dd/MM HH:mm' }}</td>
                    <td><app-status-badge [status]="f.status" /></td>
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
                    <span class="row-meta" [title]="scanTypeTitle(j.monitoredJobId)">{{ scanTypeLabel(j.monitoredJobId, '') }} · {{ j.jobTypeName }}</span>

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

                  <!-- Expanded detail panel — per-source scan breakdown (Tier 2.5) -->
                  <div class="job-row-detail">
                    @if (sourcesFor(j.monitoredJobId); as sources) {
                      @if (sources.length > 0) {
                        <div class="source-scans" [attr.aria-label]="'Scan sources: ' + sources.length">
                          @for (s of sources; track s.scanSourceId) {
                            <div class="source-scan-row">
                              <span class="src-name" dir="auto">{{ s.name }}</span>
                              <span class="badge badge-info">{{ s.scanTypeName }}</span>
                              @if (s.lastScan; as sl) {
                                <span class="badge" [class]="sl.outcome === 'Success' ? 'badge-resolved' : 'badge-failed'">{{ sl.outcome }}</span>
                                <span class="text-muted text-sm">{{ sl.durationMs }}ms · {{ relativeAge(sl.completedAt) }}</span>
                                <span class="src-counts text-sm">
                                  @if (sl.failuresDetected > 0) { <span class="count-warn">{{ sl.failuresDetected }} failures</span> }
                                  @if (sl.classifications > 0) { · {{ sl.classifications }} classified }
                                  @if (sl.recommendations > 0) { · {{ sl.recommendations }} recs }
                                </span>
                              } @else {
                                <span class="badge badge-muted">No scans yet</span>
                              }
                            </div>
                          }
                        </div>
                      } @else if (lastScanFor(j.monitoredJobId); as ls) {
                        <!-- Fallback for jobs with no active sources (pre-backfill / all inactive). -->
                        <dl class="detail-grid">
                          <dt>Last outcome</dt>
                          <dd><span class="badge" [class]="lastScanBadgeClass(j.monitoredJobId)">{{ ls.lastScan.outcome }}</span></dd>
                          <dt>Completed</dt>
                          <dd>{{ ls.lastScan.completedAt | date:'dd/MM HH:mm:ss' }} ({{ relativeAge(ls.lastScan.completedAt) }})</dd>
                          <dt>Duration</dt>
                          <dd>{{ ls.lastScan.durationMs }} ms</dd>
                        </dl>
                      } @else {
                        <p class="text-muted text-sm">This job has not been scanned yet.</p>
                      }
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

      <!-- Recent-failure detail opens in-place via the shared drawer — stays
           on the dashboard instead of routing to /failures. -->
      <app-drawer
          [open]="selectedFailureId() !== null"
          [width]="'760px'"
          [ariaLabel]="'Failure ' + selectedFailureId() + ' detail'"
          (close)="selectedFailureId.set(null)">
        <ng-container drawer-title>
          <span class="text-muted text-sm">Failure</span>
          <strong>#{{ selectedFailureId() }}</strong>
        </ng-container>
        @if (selectedFailureId() !== null) {
          <app-failure-detail [failureId]="selectedFailureId()!"></app-failure-detail>
        }
      </app-drawer>
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

    /* ── Action-first KPI row (P0.3) ─────────────────────────────────────
       Two hero tiles + a compact 2×2 secondary block, one row. Accents are
       border-inline-start (RTL-safe). Heroes stay the height of the 2×2
       block (not tall). */
    .kpi-row { display: grid; grid-template-columns: 1fr 1fr 1.15fr; gap: 12px; align-items: stretch; }

    .kpi-hero {
      text-decoration: none; color: inherit; cursor: pointer;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      padding: 12px 16px;
      display: flex; flex-direction: column; justify-content: space-between; gap: 8px;
      transition: transform var(--transition), box-shadow var(--transition);
      &:hover { transform: translateY(-1px); box-shadow: var(--shadow); }
      &:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }
      &.amber { border-inline-start: 5px solid var(--warning); background: var(--hero-amber-grad); }
      &.red   { border-inline-start: 5px solid var(--danger);  background: var(--hero-red-grad); }
    }
    .hero-eyebrow {
      font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em;
      color: var(--text-muted); display: flex; align-items: center; gap: 5px;
    }
    .hero-ico { font-size: 13px; }
    .hero-body { display: flex; flex-direction: column; gap: 2px; }
    .hero-main { display: flex; align-items: flex-end; justify-content: space-between; gap: 8px; }
    .hero-value { font-size: 34px; font-weight: 800; line-height: 1; color: var(--text); }
    .hero-verb { font-size: 12px; font-weight: 600; white-space: nowrap; }
    .kpi-hero.amber .hero-verb { color: var(--warning); }
    .kpi-hero.red   .hero-verb { color: var(--danger);  }
    .hero-sub { font-size: 11px; color: var(--text-muted); }

    /* Zero → the whole tile recedes: mute the accent + fill, dim label+number. */
    .kpi-hero.zero { background: var(--surface); border-inline-start-color: var(--border); }
    .kpi-hero.zero .hero-value,
    .kpi-hero.zero .hero-eyebrow,
    .kpi-hero.zero .hero-verb,
    .kpi-hero.zero .hero-sub { color: var(--text-dim); }

    /* Secondary 2×2 — recessed surface, label-left / number-right chips. */
    .kpi-secondary { display: grid; grid-template-columns: 1fr 1fr; grid-template-rows: 1fr 1fr; gap: 8px; }
    .kpi-chip {
      text-decoration: none; color: inherit; cursor: pointer;
      background: var(--surface-2);
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      padding: 8px 10px;
      display: flex; align-items: center; justify-content: space-between; gap: 8px;
      transition: background var(--transition), transform var(--transition);
      &:hover { transform: translateY(-1px); background: var(--surface-3); }
      &:focus-visible { outline: 2px solid var(--primary); outline-offset: 2px; }
    }
    /* Category accent — colored inline-start border (RTL-safe), restoring the
       per-metric colours the old tiles carried. */
    .kpi-chip.danger  { border-inline-start: 3px solid var(--danger);  }
    .kpi-chip.success { border-inline-start: 3px solid var(--success); }
    .kpi-chip.neutral { border-inline-start: 3px solid var(--text-muted); }
    .chip-label { font-size: 11px; color: var(--text-muted); }
    .chip-value { font-size: 18px; font-weight: 700; line-height: 1; }
    /* Fix Failures is a system-problem signal — number reads red when > 0. */
    .kpi-chip.alarm:not(.zero) .chip-value { color: var(--danger); }
    /* Zero → recede fully AND go inert (nothing to drill into): neutralise
       the accent (comes after the colour rules so it wins on equal
       specificity), dim label + number, and disable interaction. tabindex=-1
       + aria-disabled on the anchors keep keyboard/AT out too. */
    .kpi-chip.zero { border-inline-start-color: var(--border); pointer-events: none; cursor: default; }
    .kpi-chip.zero .chip-label,
    .kpi-chip.zero .chip-value { color: var(--text-dim); }

    /* Stack on narrow viewports so nothing crushes. */
    @media (max-width: 900px) {
      .kpi-row { grid-template-columns: 1fr; }
    }

    .row-2col { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }

    /* Phase 2 analytics rows. Row 1 splits at 60/40 (Errors Over Time wider
       since it carries the range toggle + multi-series legend). Row 2 hosts
       the full-width Resolution Mix.
       3fr / 2fr is mathematically the same as 1.5fr / 1fr but the explicit
       form is easier to reason about. Items get min-width: 0 so the right-
       column's Chart.js canvas can shrink with the grid instead of forcing
       its parent wider via intrinsic content size (which was causing the
       visual to look 55/45 instead of the intended 60/40). */
    /* All three charts in one row. Errors Over Time widest (toggle + legend),
       the bar + pie narrower. min-width:0 lets each Chart.js canvas shrink with
       the grid instead of forcing the column wider. */
    .analytics-row {
      display: grid; grid-template-columns: 1.7fr 1fr 1fr; gap: 16px;
      > * { min-width: 0; }
    }

    .job-list { display: flex; flex-direction: column; gap: 1px; }

    /* Compact single-line row (~48px) — click to expand */
    .job-row-wrap { border-radius: var(--radius-sm); transition: background var(--transition); }
    .job-row-wrap:hover { background: var(--surface-2); }

    .job-row-compact {
      display: flex; align-items: center; gap: 10px;
      min-height: 44px; padding: 8px 10px;
      cursor: pointer;
      min-width: 0;  /* let children ellipsis instead of overflowing the card */
    }
    .row-status-icon {
      width: 16px; height: 16px; flex-shrink: 0;
      display: inline-flex; align-items: center; justify-content: center;
      font-size: 11px; font-weight: 700;
      &.state-success { color: var(--success); }
      &.state-failed  { color: var(--danger); }
      &.state-gray    { color: var(--text-muted); }
    }
    .row-job-name  {
      font-size: 13px; font-weight: 600; color: var(--text);
      white-space: nowrap; min-width: 0; flex-shrink: 1;
      overflow: hidden; text-overflow: ellipsis;
    }
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
      max-height: 600px; opacity: 1; padding: 4px 10px 12px 32px; overflow-y: auto;
    }
    .detail-grid {
      display: grid; grid-template-columns: 140px 1fr; row-gap: 4px; column-gap: 12px;
      font-size: 12px; color: var(--text);
      dt { color: var(--text-muted); font-weight: 500; }
      dd { color: var(--text); }
    }
    /* Tier 2.5 per-source scan breakdown in the expand panel. */
    .source-scan-row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
      padding: 5px 0; border-bottom: 1px solid var(--border-light); }
    .source-scan-row:last-child { border-bottom: none; }
    .src-name { font-weight: 600; font-size: 12px; }
    .src-counts { color: var(--text-muted); }
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
    .spinner-mini {
      width: 12px; height: 12px;
      border: 2px solid rgba(99, 102, 241, 0.3);
      border-top-color: var(--primary, #6366f1);
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }
    @keyframes spin { to { transform: rotate(360deg); } }

    /* At ≤1400px (typical 14" laptop CSS width) the KPI grid wraps to 3 cols.
       The side-by-side analytics row and the two info panels get too narrow at
       that width and clip on the right — so stack them full-width at the same
       breakpoint. Keeps every panel readable and edge-to-edge on a laptop;
       wide monitors keep the multi-column layout. */
    @media (max-width: 1400px) {
      .kpi-grid { grid-template-columns: repeat(3, 1fr); }
      .row-2col { grid-template-columns: 1fr; }
    }
    /* Charts shrink more gracefully than the info panels, so keep all three in
       one row down to ~1024px; stack only below that. */
    @media (max-width: 1024px) {
      .analytics-row { grid-template-columns: 1fr; }
    }
    @media (max-width: 900px) {
      .kpi-grid { grid-template-columns: repeat(2,1fr); }
    }
  `]
})
export class DashboardComponent implements OnInit, OnDestroy {
  router = inject(Router);
  private scanSvc   = inject(ScanService);
  private statusSvc = inject(WorkerStatusService);

  lastScan = signal<ScanResult | null>(null);

  /** Shared time-range for the row-1 analytics charts (Errors Over Time and
   *  Failures by Job). Owned here so a single toggle in the Errors Over Time
   *  card updates both. Resolution Mix on row 2 has its own fixed 7-day
   *  window and ignores this signal. */
  chartRange = signal<ChartRange>('24h');

  // Raw PolledData<T> from the service — each slice is independent and tracks
  // its own isStale / lastUpdatedAt. The component-facing getters below unwrap
  // `.value` so templates stay simple. Global data-staleness is surfaced in the
  // top bar (the worker-status heartbeat drives a "Reconnecting…" chip); a
  // finer per-panel indicator off these flags remains a possible future polish.
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

  /** Failure currently open in the in-place detail drawer (local, not URL —
   *  the dashboard is a live view, not a deep-link surface). Clicking a recent
   *  failure opens it here instead of routing to /failures. */
  selectedFailureId = signal<number | null>(null);

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
    fixFailedToday: 0, unconfigured: 0, unconfiguredNoPolicy: 0,
  };
  stats = computed<DashboardStats>(() => this.statsPolled().value ?? DashboardComponent.EMPTY_STATS);

  // Live worker-status — refcounted polling keeps this alive while either the
  // dashboard or the scan-jobs screen is mounted.
  private workerStatus = computed(() => this.statusPolled().value);

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

  // ── Completed-scan beat (plain self-dismissing toast) ────────────────────
  // The "✓ N scans completed — M new failures" beat. Set when a new completed
  // scan lands (while not in-flight), snapshotting the label, and cleared by a
  // single timer after BEAT_MS. No hover-pause / restart / manual dismiss.
  completedBeat = signal<string | null>(null);
  private lastSeenScanRunId = signal<number | null>(null);
  private beatTimerId?: ReturnType<typeof setTimeout>;
  private readonly BEAT_MS = 8_000;

  private bannerLifecycleEffect = effect(() => {
    const s = this.workerStatus();
    const latestId = s?.recentScansLast30s?.[0]?.scanRunId ?? null;

    // In-flight strip takes over — no completed beat while scanning.
    if (this.activityInFlight()) {
      this.clearBeatTimer();
      this.completedBeat.set(null);
      return;
    }
    // A newly-completed scan → show its beat for BEAT_MS, then it's gone.
    if (latestId !== null && latestId !== this.lastSeenScanRunId()) {
      this.lastSeenScanRunId.set(latestId);
      this.completedBeat.set(this.activityLabel());
      this.clearBeatTimer();
      this.beatTimerId = setTimeout(() => this.completedBeat.set(null), this.BEAT_MS);
    }
  });

  private clearBeatTimer(): void {
    if (this.beatTimerId !== undefined) {
      clearTimeout(this.beatTimerId);
      this.beatTimerId = undefined;
    }
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

  /** Per-source last-scan rows for a job (Tier 2.5 drill-down). Empty when the
   *  worker-status payload hasn't arrived or the job has no active sources. */
  sourcesFor(monitoredJobId: number): SourceLastScanRow[] {
    return this.workerStatus()?.jobs.find(j => j.monitoredJobId === monitoredJobId)?.sources ?? [];
  }

  /** Compact-row scan-type label derived from the job's active sources (Tier 2.5):
   *  one type → that type; several sources of one type → "FileSystem · N sources";
   *  mixed types → "Mixed · N sources". Falls back to the job's legacy scanType
   *  string until the first worker-status poll arrives (sources empty). */
  scanTypeLabel(monitoredJobId: number, fallback: string): string {
    return scanTypeLabelFromNames(this.sourcesFor(monitoredJobId).map(s => s.scanTypeName), fallback);
  }

  /** Tooltip for the scan-type label — lists every source with its type so a
   *  rolled-up "Mixed · 3 sources" stays inspectable on hover. */
  scanTypeTitle(monitoredJobId: number): string {
    return scanTypeTitleFromSources(this.sourcesFor(monitoredJobId));
  }

  relativeAge(iso: string): string {
    const sec = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
    if (sec < 5)    return 'just now';
    if (sec < 60)   return `${sec}s ago`;
    if (sec < 3600) return `${Math.floor(sec / 60)}m ago`;
    if (sec < 86400)return `${Math.floor(sec / 3600)}h ago`;
    return `${Math.floor(sec / 86400)}d ago`;
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
    return `Last run: ${ls.outcome} at ${new Date(ls.completedAt).toLocaleString('en-GB')}, duration ${ls.durationMs}ms`;
  }

  anyCount(monitoredJobId: number): boolean {
    const ls = this.jobLastScans().get(monitoredJobId);
    return !!ls && (ls.failuresDetected > 0 || ls.classifications > 0 || ls.recommendations > 0);
  }

  rulesLabel(n: number | null | undefined): string {
    const c = n ?? 0;
    return c === 1 ? '1 rule' : `${c} rules`;
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
    this.clearBeatTimer();
  }

  triggerScan(job: MonitoredJob) {
    // Manual scan — toast on completion. Post-scan reload is unnecessary: the
    // 5s snapshot poll will pick up any new failures / lease state on its own.
    this.scanSvc.scanById(job.monitoredJobId).subscribe({
      next: r => this.lastScan.set(r)
    });
  }
}
