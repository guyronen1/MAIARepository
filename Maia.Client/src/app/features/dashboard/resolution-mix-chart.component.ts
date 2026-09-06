import {
  AfterViewInit, Component, ElementRef, OnDestroy, ViewChild,
  computed, effect, inject, input, signal, untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { ArcElement, Chart, DoughnutController, Tooltip } from 'chart.js';
import { FailuresService, FailureOutcomes } from '../../core/services/failures.service';
import { ThemeService } from '../../core/services/theme.service';

Chart.register(ArcElement, DoughnutController, Tooltip);

type Range = '24h' | '7d' | '30d';

// Ring palette — CLOSED group in cool/neutral tones, NEEDS-YOU in amber/red.
// "Fixed by hand" teal is deliberately distinct from the "Auto-healed" green
// so the two don't blur together in the ring.
const COLORS = {
  autoHealed:       'rgba(22, 163, 74, 0.9)',   // --success green
  operatorApproved: 'rgba(37, 99, 235, 0.9)',   // --info blue
  fixedByHand:      'rgba(20, 184, 166, 0.9)',   // teal
  operatorRejected: 'rgba(148, 163, 184, 0.9)',  // slate
  awaitingDecision: 'rgba(217, 119, 6, 0.9)',    // --warning amber
  manualRequired:   'rgba(220, 38, 38, 0.9)',    // --danger red
};

@Component({
  selector: 'app-resolution-mix-chart',
  standalone: true,
  imports: [RouterLink],
  template: `
    <div class="card chart-card">
      <div class="chart-header">
        <h3>Failure Outcomes</h3>
        @if (data(); as d) {
          <span class="text-muted text-sm">{{ d.totalDetected }} detected in {{ rangeLabel() }} · state now</span>
        } @else {
          <span class="text-muted text-sm">{{ rangeLabel() }}</span>
        }
      </div>

      @if (loading()) {
        <div class="chart-skeleton" aria-hidden="true"></div>
      } @else if (!data() || data()!.totalDetected === 0) {
        <div class="chart-empty"><p class="text-muted">No failures detected in this range</p></div>
      } @else {
        <div class="outcomes-body">
          <!-- Donut + hero: what share of everything detected MAIA auto-healed,
               and how much still needs a human. -->
          <div class="donut-wrap">
            <canvas #canvas></canvas>
            <div class="donut-center" aria-hidden="true">
              <div class="donut-pct">{{ data()!.autoHealPct }}%</div>
              <div class="donut-cap">auto-healed</div>
              <div class="donut-of">{{ data()!.closed.autoHealed }} of {{ data()!.totalDetected }}</div>
              @if (data()!.needsYou.total > 0) {
                <div class="donut-need">{{ data()!.needsYou.total }} need you</div>
              }
            </div>
          </div>

          <div class="outcomes-legend">
            <div class="lg-group">
              <div class="lg-head closed">
                <span>✓ CLOSED</span>
                <span class="lg-sub">{{ data()!.closed.total }} · {{ pct(data()!.closed.total) }}%</span>
              </div>
              <div class="lg-row"><span class="lg-dot dot-auto"></span><span class="lg-label">Auto-healed</span><span class="lg-num">{{ data()!.closed.autoHealed }}</span></div>
              <div class="lg-row"><span class="lg-dot dot-op"></span><span class="lg-label">Operator-approved</span><span class="lg-num">{{ data()!.closed.operatorApproved }}</span></div>
              <div class="lg-row"><span class="lg-dot dot-hand"></span><span class="lg-label">Fixed by hand</span><span class="lg-num">{{ data()!.closed.fixedByHand }}</span></div>
              <div class="lg-row"><span class="lg-dot dot-rej"></span><span class="lg-label">Operator-rejected</span><span class="lg-num">{{ data()!.closed.operatorRejected }}</span></div>
            </div>

            <div class="lg-group">
              <div class="lg-head needs">
                <span>⚠ STILL NEEDS YOU</span>
                <span class="lg-sub">{{ data()!.needsYou.total }} · {{ pct(data()!.needsYou.total) }}%</span>
              </div>
              <a class="lg-row link" routerLink="/failures" [queryParams]="{ view: 'active' }" title="Detected in window, still awaiting an operator decision">
                <span class="lg-dot dot-await"></span><span class="lg-label">Awaiting decision</span>
                <span class="lg-num">{{ data()!.needsYou.awaitingDecision }}</span><span class="lg-arrow">→</span>
              </a>
              <a class="lg-row link" routerLink="/failures" [queryParams]="{ view: 'manual-required' }" title="Needs off-system / manual intervention">
                <span class="lg-dot dot-manual"></span><span class="lg-label">Manual required</span>
                <span class="lg-num">{{ data()!.needsYou.manualRequired }}</span><span class="lg-arrow">→</span>
              </a>
            </div>
          </div>
        </div>

        <!-- Reconciles the windowed cohort with the live queue: failures from
             before this window that are still open aren't in the ring above. -->
        <div class="outcomes-foot">
          <a class="foot-link" routerLink="/failures" title="All failures — including older still-open ones not in this window">
            Older than this window and still open: <b>{{ data()!.olderOpen }}</b> →
          </a>
          <span class="foot-note">not counted above</span>
        </div>
      }
    </div>
  `,
  styles: [`
    .chart-card { padding: 10px 12px; }
    .chart-header {
      display: flex; justify-content: space-between; align-items: center;
      margin-bottom: 6px;
      h3 { font-size: 13px; font-weight: 600; color: var(--text); margin: 0; }
    }
    .chart-skeleton {
      height: 182px;
      border-radius: var(--radius-sm);
      background: linear-gradient(90deg,
        var(--surface-2) 0%, var(--surface-3) 50%, var(--surface-2) 100%);
      background-size: 200% 100%;
      animation: shimmer 1.8s ease-in-out infinite;
    }
    @keyframes shimmer { 0% { background-position: 200% 0; } 100% { background-position: -200% 0; } }
    .chart-empty {
      height: 182px;
      display: flex; align-items: center; justify-content: center;
      color: var(--text-muted); text-align: center;
    }

    /* Donut left, grouped legend right. Fixed body height so toggling the range
       never changes the card height (no grid-row jolt). */
    .outcomes-body { display: flex; align-items: center; gap: 12px; height: 156px; }
    .donut-wrap  { position: relative; width: 122px; height: 122px; flex-shrink: 0; }
    .donut-center {
      position: absolute; inset: 0;
      display: flex; flex-direction: column; align-items: center; justify-content: center;
      pointer-events: none;
    }
    .donut-pct  { font-size: 24px; font-weight: 800; color: var(--success); line-height: 1; }
    .donut-cap  { font-size: 10px; color: var(--text-muted); margin-top: 1px; }
    .donut-of   { font-size: 10px; color: var(--text-dim); }
    .donut-need { font-size: 10px; font-weight: 700; color: var(--warning); margin-top: 3px; }

    .outcomes-legend { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 8px; }
    .lg-group { display: flex; flex-direction: column; gap: 2px; }
    .lg-head {
      display: flex; justify-content: space-between; align-items: baseline;
      font-size: 10px; font-weight: 700; letter-spacing: 0.04em; margin-bottom: 1px;
      &.closed { color: var(--success); }
      &.needs  { color: var(--warning); }
      .lg-sub { color: var(--text-muted); font-weight: 600; }
    }
    .lg-row {
      display: flex; align-items: center; gap: 7px; font-size: 11px;
      color: var(--text); text-decoration: none;
    }
    .lg-row.link { cursor: pointer; border-radius: 4px; padding: 1px 4px; margin: 0 -4px;
      &:hover { background: var(--surface-2); } }
    .lg-dot { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
    .lg-label { flex: 1; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .lg-num { font-weight: 700; color: var(--text); }
    .lg-arrow { color: var(--text-dim); font-size: 10px; }
    .dot-auto   { background: rgba(22, 163, 74, 0.9); }
    .dot-op     { background: rgba(37, 99, 235, 0.9); }
    .dot-hand   { background: rgba(20, 184, 166, 0.9); }
    .dot-rej    { background: rgba(148, 163, 184, 0.9); }
    .dot-await  { background: rgba(217, 119, 6, 0.9); }
    .dot-manual { background: rgba(220, 38, 38, 0.9); }

    .outcomes-foot {
      display: flex; align-items: center; flex-wrap: wrap; gap: 2px 10px;
      margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--border-light);
      font-size: 11px;
    }
    .foot-link { color: var(--text-muted); text-decoration: none; b { color: var(--text); }
      &:hover { color: var(--text); } }
    /* margin-inline-start:auto pushes the note right; it wraps to its own line
       when the card is too narrow (no collision with the link). */
    .foot-note { color: var(--text-dim); margin-inline-start: auto; }
  `]
})
export class ResolutionMixChartComponent implements AfterViewInit, OnDestroy {
  private svc = inject(FailuresService);
  private theme = inject(ThemeService);

  @ViewChild('canvas') canvasRef?: ElementRef<HTMLCanvasElement>;

  /** 24h/7d/30d toggle, driven by the dashboard parent. */
  range = input.required<Range>();

  constructor() {
    // Re-fetch on range change (also fires once on init, before AfterViewInit).
    effect(() => {
      const r = this.range();
      this.fetch(r);
    });

    // Rebuild with the theme-matched segment border on theme flip.
    effect(() => {
      this.theme.resolved();
      if (!this.chart) return;
      const canvas = this.canvasRef?.nativeElement;
      if (canvas && untracked(() => this.data())?.totalDetected) {
        this.chart.destroy();
        this.buildChart(canvas);
      }
    });
  }

  loading = signal(true);
  data    = signal<FailureOutcomes | null>(null);

  rangeLabel = computed(() => {
    switch (this.range()) {
      case '7d':  return 'last 7 days';
      case '30d': return 'last 30 days';
      default:    return 'last 24 hours';
    }
  });

  /** Share of the total as an integer percent. */
  pct(n: number): number {
    const total = this.data()?.totalDetected ?? 0;
    return total > 0 ? Math.round((n / total) * 100) : 0;
  }

  private chart?: Chart;
  private pendingRender = false;

  ngAfterViewInit(): void { /* fetch runs from the range effect */ }
  ngOnDestroy(): void { this.chart?.destroy(); }

  private fetch(range: Range): void {
    // Skeleton only on the very first load; a range change keeps the current
    // donut on screen and swaps in the rebuilt chart when new data lands — no
    // flash, no layout shift.
    const firstLoad = this.data() === null;
    if (firstLoad) {
      this.loading.set(true);
      this.chart?.destroy();
      this.chart = undefined;
    }

    this.svc.getFailureOutcomes(range).subscribe({
      next: res => {
        this.data.set(res);
        this.loading.set(false);
        this.chart?.destroy();
        this.chart = undefined;
        this.pendingRender = true;
        queueMicrotask(() => this.renderIfReady());
      },
      error: () => {
        this.data.set(null);
        this.loading.set(false);
        this.chart?.destroy();
        this.chart = undefined;
      }
    });
  }

  private renderIfReady(): void {
    if (!this.pendingRender) return;
    const canvas = this.canvasRef?.nativeElement;
    if (canvas && (this.data()?.totalDetected ?? 0) > 0) {
      this.buildChart(canvas);
      this.pendingRender = false;
      return;
    }
    requestAnimationFrame(() => {
      if (!this.pendingRender) return;
      const c2 = this.canvasRef?.nativeElement;
      if (c2 && (this.data()?.totalDetected ?? 0) > 0) this.buildChart(c2);
      this.pendingRender = false;
    });
  }

  private buildChart(canvas: HTMLCanvasElement): void {
    const d = this.data();
    if (!d) return;
    const surface = (typeof document !== 'undefined'
      ? getComputedStyle(document.documentElement).getPropertyValue('--surface').trim()
      : '') || '#ffffff';

    this.chart = new Chart(canvas, {
      type: 'doughnut',
      data: {
        labels: ['Auto-healed', 'Operator-approved', 'Fixed by hand', 'Operator-rejected',
                 'Awaiting decision', 'Manual required'],
        datasets: [{
          data: [
            d.closed.autoHealed, d.closed.operatorApproved, d.closed.fixedByHand, d.closed.operatorRejected,
            d.needsYou.awaitingDecision, d.needsYou.manualRequired,
          ],
          backgroundColor: [
            COLORS.autoHealed, COLORS.operatorApproved, COLORS.fixedByHand, COLORS.operatorRejected,
            COLORS.awaitingDecision, COLORS.manualRequired,
          ],
          borderColor: surface,
          borderWidth: 2,
          hoverOffset: 4,
        }],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        cutout: '70%',
        animation: { duration: 250, easing: 'easeOutQuad' },
        plugins: {
          legend: { display: false },
          tooltip: {
            callbacks: {
              label: ctx => {
                const val = (ctx.parsed as number) ?? 0;
                return ` ${ctx.label}: ${val} (${this.pct(val)}%)`;
              },
            },
          },
        },
      },
    });
  }
}
