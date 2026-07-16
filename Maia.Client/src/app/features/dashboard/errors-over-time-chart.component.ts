import {
  AfterViewInit, Component, ElementRef, OnDestroy, ViewChild,
  computed, effect, inject, input, output, signal, untracked,
} from '@angular/core';
import {
  Chart, Filler, Legend, LineController, LineElement, LinearScale,
  PointElement, CategoryScale, Tooltip,
} from 'chart.js';
import { FailuresService, FailuresOverTimeResponse } from '../../core/services/failures.service';
import { ThemeService } from '../../core/services/theme.service';
import { readChartTheme } from '../../core/util/chart-theme.util';

// Register exactly the components we use — keeps the bundle lean.
Chart.register(
  LineController, LineElement, PointElement, LinearScale,
  CategoryScale, Filler, Legend, Tooltip,
);

type Range = '24h' | '7d' | '30d';

// Stable palette aligned with existing dashboard tones (CSS custom-prop hex values).
// errorTypeId 0 = unclassified → muted gray. Other ids hash modulo palette length so
// "DbConnection" gets the same color on every page load regardless of arrival order.
const PALETTE: readonly string[] = [
  '#dc2626', // danger
  '#d97706', // warning
  '#2563eb', // info
  '#7c3aed', // purple
  '#16a34a', // success
  '#0891b2', // cyan
  '#db2777', // pink
  '#65a30d', // lime
];
const UNCLASSIFIED_COLOR = '#9ca3af';

function colorFor(errorTypeId: number): string {
  if (!errorTypeId) return UNCLASSIFIED_COLOR;
  return PALETTE[Math.abs(errorTypeId) % PALETTE.length];
}

function withAlpha(hex: string, alpha: number): string {
  // #rrggbb → rgba()
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

@Component({
  selector: 'app-errors-over-time-chart',
  standalone: true,
  template: `
    <div class="card chart-card">
      <div class="chart-header">
        <h3>Errors Over Time</h3>
        <!-- Toggle stays visually attached here but emits to the dashboard
             parent via rangeChange, so Failures by Job re-fetches in lockstep. -->
        <div class="range-toggle" role="tablist">
          @for (r of ranges; track r) {
            <button type="button"
                    [class.active]="range() === r"
                    (click)="setRange(r)">{{ r }}</button>
          }
        </div>
      </div>

      <!-- Loading skeleton (not a spinner) — matches chart height exactly -->
      @if (loading()) {
        <div class="chart-skeleton" aria-hidden="true"></div>
      } @else if (!hasData()) {
        <div class="chart-empty">
          <p class="text-muted">No failures in this time range</p>
        </div>
      } @else {
        <div class="chart-wrap">
          <canvas #canvas></canvas>
        </div>
      }
    </div>
  `,
  styles: [`
    .chart-card { padding: 8px 10px; }
    .chart-header {
      display: flex; justify-content: space-between; align-items: center;
      margin-bottom: 4px;
      h3 { font-size: 13px; font-weight: 600; color: var(--text); margin: 0; }
    }
    .range-toggle {
      display: inline-flex; gap: 0;
      background: var(--surface-2); border: 1px solid var(--border);
      border-radius: var(--radius-sm); overflow: hidden;
      button {
        background: transparent; border: none; cursor: pointer;
        padding: 2px 8px; font-size: 11px; font-weight: 500;
        color: var(--text-muted); font-family: inherit;
        transition: all var(--transition);
        &:hover { color: var(--text); background: var(--surface-3); }
        &.active { background: var(--primary); color: #fff; }
      }
    }
    .chart-wrap { position: relative; height: 200px; }
    .chart-skeleton {
      height: 200px;
      border-radius: var(--radius-sm);
      background: linear-gradient(90deg,
        var(--surface-2) 0%,
        var(--surface-3) 50%,
        var(--surface-2) 100%);
      background-size: 200% 100%;
      animation: shimmer 1.8s ease-in-out infinite;
    }
    @keyframes shimmer { 0% { background-position: 200% 0; } 100% { background-position: -200% 0; } }
    .chart-empty {
      height: 200px;
      display: flex; align-items: center; justify-content: center;
      color: var(--text-muted);
    }
  `]
})
export class ErrorsOverTimeChartComponent implements AfterViewInit, OnDestroy {
  private svc = inject(FailuresService);
  private theme = inject(ThemeService);

  @ViewChild('canvas') canvasRef?: ElementRef<HTMLCanvasElement>;

  /** Time range is now owned by the dashboard parent. The toggle button row
   *  in this component's template emits rangeChange when an operator picks
   *  a different window; the parent updates its signal which flows back as
   *  an updated input, and any sibling charts bound to the same parent
   *  signal re-fetch in lockstep. */
  rangeInput   = input<Range>('24h', { alias: 'range' });
  rangeChange  = output<Range>();

  ranges: Range[] = ['24h', '7d', '30d'];
  /** Local mirror of the input — used by the template for active-state
   *  highlighting and by the fetch effect for the API call. Kept as a
   *  signal so the active-button styling reacts immediately. */
  range = computed<Range>(() => this.rangeInput());

  loading  = signal(true);
  payload  = signal<FailuresOverTimeResponse | null>(null);
  hasData  = computed(() => (this.payload()?.buckets.length ?? 0) > 0);

  private chart?: Chart;
  private pendingRender = false;

  constructor() {
    // Re-fetch on every range change driven from the parent. First emit
    // happens at construction time before AfterViewInit; the renderIfReady
    // retry covers the case where the canvas isn't in the DOM yet.
    effect(() => {
      const r = this.range();
      this.fetch(r);
    });

    // Rebuild with theme-matched grid/tick/legend colours when the theme flips.
    // Reads payload untracked so this only fires on theme change, not on data.
    effect(() => {
      this.theme.resolved();
      if (!this.chart) return;
      const canvas = this.canvasRef?.nativeElement;
      const p = untracked(() => this.payload());
      if (canvas && p && p.buckets.length) { this.chart.destroy(); this.buildChart(canvas, p); }
    });
  }

  ngAfterViewInit(): void {
    // No-op: fetch is driven by the effect above.
  }

  ngOnDestroy(): void {
    this.chart?.destroy();
  }

  setRange(r: Range): void {
    if (r === this.range()) return;
    // Emit upward — the parent will update its signal and the input will
    // round-trip back, triggering the effect that calls fetch().
    this.rangeChange.emit(r);
  }

  private fetch(r: Range): void {
    this.loading.set(true);
    // Destroy any existing chart so the skeleton can show in its place
    this.chart?.destroy();
    this.chart = undefined;

    this.svc.getFailuresOverTime(r).subscribe({
      next: res => {
        this.payload.set(res);
        this.loading.set(false);
        // Canvas only exists in the *next* change-detection pass after hasData becomes
        // true; defer one tick so @ViewChild resolves before we build the chart.
        this.pendingRender = true;
        queueMicrotask(() => this.renderIfReady());
      },
      error: () => {
        this.payload.set(null);
        this.loading.set(false);
      }
    });
  }

  private renderIfReady(): void {
    if (!this.pendingRender) return;
    const canvas = this.canvasRef?.nativeElement;
    const payload = this.payload();
    if (!canvas || !payload || payload.buckets.length === 0) {
      // Retry once after another tick — the @if branch may not have rendered yet
      requestAnimationFrame(() => {
        if (!this.pendingRender) return;
        const c2 = this.canvasRef?.nativeElement;
        if (c2 && this.payload() && this.payload()!.buckets.length > 0) {
          this.buildChart(c2, this.payload()!);
          this.pendingRender = false;
        }
      });
      return;
    }
    this.buildChart(canvas, payload);
    this.pendingRender = false;
  }

  private buildChart(canvas: HTMLCanvasElement, res: FailuresOverTimeResponse): void {
    // Pivot the flat bucket list → datasets keyed by errorTypeId.
    // X labels are unique bucketStart values sorted ascending.
    const labels = Array.from(new Set(res.buckets.map(b => b.bucketStart))).sort();
    const idx = new Map<string, number>(labels.map((l, i) => [l, i]));

    const byType = new Map<number, { display: string; counts: number[] }>();
    for (const b of res.buckets) {
      if (!byType.has(b.errorTypeId)) {
        byType.set(b.errorTypeId, { display: b.errorTypeDisplay, counts: new Array(labels.length).fill(0) });
      }
      byType.get(b.errorTypeId)!.counts[idx.get(b.bucketStart)!] += b.count;
    }

    const datasets = Array.from(byType.entries())
      .sort(([a], [b]) => a - b)
      .map(([errorTypeId, { display, counts }]) => {
        const color = colorFor(errorTypeId);
        return {
          label:           display,
          data:            counts,
          backgroundColor: withAlpha(color, 0.35),
          borderColor:     color,
          borderWidth:     1.5,
          fill:            true,
          tension:         0.25,
          pointRadius:     2,
          pointHoverRadius:4,
        };
      });

    // Format X labels for display: hourly → HH:mm, daily → DD/MM.
    const displayLabels = labels.map(iso => {
      const d = new Date(iso);
      return res.bucketSize === 'hour'
        ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
        : `${d.getDate().toString().padStart(2, '0')}/${(d.getMonth() + 1).toString().padStart(2, '0')}`;
    });

    const t = readChartTheme();
    this.chart = new Chart(canvas, {
      type: 'line',
      data: { labels: displayLabels, datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'index', intersect: false },
        animation: { duration: 250, easing: 'easeOutQuad' },
        layout: { padding: { top: 4, right: 4, bottom: 0, left: 0 } },
        scales: {
          x: { grid: { display: false }, ticks: { color: t.tick, font: { size: 10 }, maxRotation: 0, autoSkipPadding: 12 } },
          y: {
            stacked: true,
            beginAtZero: true,
            ticks: { precision: 0, maxTicksLimit: 4, color: t.tick, font: { size: 10 } },
            grid:  { color: t.grid },
          },
        },
        plugins: {
          // Legend at bottom — works at any panel width (Phase 2 will shrink this
          // chart to ~60% width to share a row with another chart). Items wrap to
          // multiple rows when needed. Click-to-toggle is on by default.
          legend: {
            display: true,
            position: 'bottom',
            align: 'center',
            labels: {
              boxWidth: 10,
              boxHeight: 10,
              padding: 10,
              font: { size: 11 },
              color: t.tick,
              usePointStyle: false,
            },
          },
          tooltip: {
            callbacks: {
              footer: (items) => {
                const total = items.reduce((s, i) => s + (i.parsed.y ?? 0), 0);
                return `Total: ${total}`;
              },
            },
          },
        },
      },
    });
  }
}
