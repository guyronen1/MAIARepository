import {
  AfterViewInit, Component, ElementRef, OnDestroy, ViewChild,
  computed, effect, inject, signal, untracked,
} from '@angular/core';
import { ArcElement, Chart, DoughnutController, Tooltip } from 'chart.js';
import { FailuresService, ResolutionMixItem } from '../../core/services/failures.service';
import { ThemeService } from '../../core/services/theme.service';

Chart.register(ArcElement, DoughnutController, Tooltip);

// Ring colours match the operator's reading model: green = "MAIA handled it
// autonomously", blue = "operator decided + MAIA executed", amber = "needs you".
// rgba @ 0.85 for the muted ops-dashboard tone. "Still Active" is deliberately
// NOT in the ring — it's unresolved, not a resolution outcome — so it shows as
// a muted line below.
const COLORS = {
  autoHealed:       'rgba(22, 163, 74, 0.85)',  // --success @ 0.85
  operatorApproved: 'rgba(37, 99, 235, 0.85)',  // --info @ 0.85
  manualRequired:   'rgba(217, 119, 6, 0.85)',  // --warning @ 0.85
};

@Component({
  selector: 'app-resolution-mix-chart',
  standalone: true,
  template: `
    <div class="card chart-card">
      <div class="chart-header">
        <h3>Resolution Mix</h3>
        <span class="text-muted text-sm">Last 7 days</span>
      </div>

      @if (loading()) {
        <div class="chart-skeleton" aria-hidden="true"></div>
      } @else if (isEmpty()) {
        <div class="chart-empty">
          <p class="text-muted">No failures detected in the last 7 days</p>
        </div>
      } @else {
        <div class="donut-block">
          @if (resolvedTotal() > 0) {
            <!-- Donut + hero metric: what share of resolved failures MAIA
                 auto-healed vs needed a human. Center reads "% auto-healed". -->
            <div class="donut-wrap">
              <canvas #canvas></canvas>
              <div class="donut-center" aria-hidden="true">
                <div class="donut-pct">{{ autoHealPct() }}%</div>
                <div class="donut-cap">auto-healed</div>
              </div>
            </div>
            <div class="donut-sub text-muted text-sm">of {{ resolvedTotal() }} resolved</div>
            <div class="donut-legend">
              <span class="lg"><span class="lg-dot dot-auto"></span>Auto-Healed <b>{{ totals().autoHealed }}</b></span>
              <span class="lg"><span class="lg-dot dot-op"></span>Operator-Approved <b>{{ totals().operatorApproved }}</b></span>
              <span class="lg"><span class="lg-dot dot-man"></span>Manual-Required <b>{{ totals().manualRequired }}</b></span>
            </div>
          } @else {
            <div class="chart-empty"><p class="text-muted">No resolved failures yet in this window</p></div>
          }

          <!-- Still Active is unresolved — kept off the ring, shown muted here. -->
          @if (totals().stillActive > 0) {
            <div class="still-active text-muted text-sm">
              <span class="lg-dot dot-still"></span>Still Active <b>{{ totals().stillActive }}</b>
              <span class="still-note">— unresolved, not part of the mix</span>
            </div>
          }
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
      height: 200px;
      border-radius: var(--radius-sm);
      background: linear-gradient(90deg,
        var(--surface-2) 0%, var(--surface-3) 50%, var(--surface-2) 100%);
      background-size: 200% 100%;
      animation: shimmer 1.8s ease-in-out infinite;
    }
    @keyframes shimmer { 0% { background-position: 200% 0; } 100% { background-position: -200% 0; } }
    .chart-empty {
      height: 150px;
      display: flex; align-items: center; justify-content: center;
      color: var(--text-muted); text-align: center;
    }

    .donut-block { display: flex; flex-direction: column; align-items: center; gap: 6px; }
    .donut-wrap  { position: relative; height: 150px; width: 100%; }
    .donut-center {
      position: absolute; inset: 0;
      display: flex; flex-direction: column; align-items: center; justify-content: center;
      pointer-events: none;
    }
    .donut-pct { font-size: 28px; font-weight: 800; color: var(--text); line-height: 1; }
    .donut-cap { font-size: 11px; color: var(--text-muted); margin-top: 2px; }
    .donut-sub { margin-top: -2px; }

    .donut-legend {
      display: flex; flex-wrap: wrap; justify-content: center; gap: 6px 14px;
      margin-top: 2px;
    }
    .lg { display: inline-flex; align-items: center; gap: 5px; font-size: 11px; color: var(--text-muted);
          b { color: var(--text); font-weight: 700; } }
    .lg-dot { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
    .dot-auto  { background: var(--success); }
    .dot-op    { background: var(--info);    }
    .dot-man   { background: var(--warning); }
    .dot-still { background: var(--text-dim); }

    .still-active {
      display: flex; align-items: center; gap: 5px;
      margin-top: 4px; padding-top: 6px;
      border-top: 1px solid var(--border-light); width: 100%; justify-content: center;
      b { color: var(--text-muted); font-weight: 700; }
    }
    .still-note { color: var(--text-dim); }
  `]
})
export class ResolutionMixChartComponent implements AfterViewInit, OnDestroy {
  private svc = inject(FailuresService);
  private theme = inject(ThemeService);

  @ViewChild('canvas') canvasRef?: ElementRef<HTMLCanvasElement>;

  constructor() {
    // Rebuild with the theme-matched segment border on theme flip (data read
    // untracked so this fires on theme change only).
    effect(() => {
      this.theme.resolved();
      if (!this.chart) return;
      const canvas = this.canvasRef?.nativeElement;
      if (canvas && untracked(() => this.resolvedTotal()) > 0) {
        this.chart.destroy();
        this.buildChart(canvas);
      }
    });
  }

  loading = signal(true);
  data    = signal<ResolutionMixItem[]>([]);

  /** Window totals across the gap-filled day rows. */
  totals = computed(() => this.data().reduce((a, r) => ({
    autoHealed:       a.autoHealed       + r.autoHealed,
    operatorApproved: a.operatorApproved + r.operatorApproved,
    manualRequired:   a.manualRequired   + r.manualRequired,
    stillActive:      a.stillActive      + r.stillActive,
  }), { autoHealed: 0, operatorApproved: 0, manualRequired: 0, stillActive: 0 }));

  /** Resolved = the three ring outcomes (Still Active excluded). */
  resolvedTotal = computed(() => {
    const t = this.totals();
    return t.autoHealed + t.operatorApproved + t.manualRequired;
  });
  autoHealPct = computed(() => {
    const n = this.resolvedTotal();
    return n ? Math.round((this.totals().autoHealed / n) * 100) : 0;
  });

  /** Empty only when there's literally nothing — no resolutions AND nothing
   *  still active — across the whole window. */
  isEmpty = () => {
    const t = this.totals();
    return t.autoHealed + t.operatorApproved + t.manualRequired + t.stillActive === 0;
  };

  private chart?: Chart;
  private pendingRender = false;

  ngAfterViewInit(): void { this.fetch(); }
  ngOnDestroy(): void { this.chart?.destroy(); }

  private fetch(): void {
    this.loading.set(true);
    this.chart?.destroy();
    this.chart = undefined;

    this.svc.getResolutionMix().subscribe({
      next: rows => {
        this.data.set(rows);
        this.loading.set(false);
        this.pendingRender = true;
        queueMicrotask(() => this.renderIfReady());
      },
      error: () => {
        this.data.set([]);
        this.loading.set(false);
      }
    });
  }

  private renderIfReady(): void {
    if (!this.pendingRender) return;
    // The canvas only exists when resolvedTotal > 0 (template @if). If the view
    // hasn't caught up, retry once next frame; if there's nothing to plot
    // (only still-active, or empty), just clear the pending flag.
    const canvas = this.canvasRef?.nativeElement;
    if (canvas && this.resolvedTotal() > 0) {
      this.buildChart(canvas);
      this.pendingRender = false;
      return;
    }
    requestAnimationFrame(() => {
      if (!this.pendingRender) return;
      const c2 = this.canvasRef?.nativeElement;
      if (c2 && this.resolvedTotal() > 0) this.buildChart(c2);
      this.pendingRender = false;
    });
  }

  private buildChart(canvas: HTMLCanvasElement): void {
    const t = this.totals();
    // Segment separators match the card background in both themes.
    const surface = (typeof document !== 'undefined'
      ? getComputedStyle(document.documentElement).getPropertyValue('--surface').trim()
      : '') || '#ffffff';

    this.chart = new Chart(canvas, {
      type: 'doughnut',
      data: {
        labels: ['Auto-Healed', 'Operator-Approved', 'Manual-Required'],
        datasets: [{
          data: [t.autoHealed, t.operatorApproved, t.manualRequired],
          backgroundColor: [COLORS.autoHealed, COLORS.operatorApproved, COLORS.manualRequired],
          borderColor: surface,
          borderWidth: 2,
          hoverOffset: 4,
        }],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        cutout: '68%',
        animation: { duration: 250, easing: 'easeOutQuad' },
        plugins: {
          legend: { display: false },   // custom HTML legend below (carries counts)
          tooltip: {
            callbacks: {
              label: ctx => {
                const val = (ctx.parsed as number) ?? 0;
                const n = t.autoHealed + t.operatorApproved + t.manualRequired;
                const pct = n ? Math.round((val / n) * 100) : 0;
                return ` ${ctx.label}: ${val} (${pct}%)`;
              },
            },
          },
        },
      },
    });
  }
}
