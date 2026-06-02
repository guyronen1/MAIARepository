import {
  AfterViewInit, Component, ElementRef, OnDestroy, ViewChild,
  computed, effect, inject, input, signal,
} from '@angular/core';
import {
  BarController, BarElement, CategoryScale, Chart, LinearScale, Legend, Tooltip,
} from 'chart.js';
import { FailuresByJobItem, FailuresService } from '../../core/services/failures.service';

// Register only the components this chart needs. Idempotent — Errors Over
// Time and Resolution Mix can register overlapping components without harm.
Chart.register(BarController, BarElement, CategoryScale, LinearScale, Legend, Tooltip);

type Range = '24h' | '7d' | '30d';

// Truncate long job names so the y-axis doesn't get crushed at 40% width.
// Full name still shown via the tooltip's title callback.
const MAX_LABEL_LEN = 24;
function truncate(s: string): string {
  return s.length <= MAX_LABEL_LEN ? s : s.slice(0, MAX_LABEL_LEN - 1) + '…';
}

@Component({
  selector: 'app-failures-by-job-chart',
  standalone: true,
  template: `
    <div class="card chart-card">
      <div class="chart-header">
        <h3>Failures by Job</h3>
        <!-- Header just labels the time window. Earlier "Top N" framing made
             a 2-row chart read like paginated data — operators don't need that
             reminder. -->
        <span class="text-muted text-sm">{{ rangeLabel() }}</span>
      </div>

      @if (loading()) {
        <div class="chart-skeleton" aria-hidden="true"></div>
      } @else if (data().length === 0) {
        <div class="chart-empty">
          <p class="text-muted">No failures in this time range</p>
        </div>
      } @else {
        <!-- Two-layer container:
             • .chart-scroll is the FIXED visible viewport (matches Errors Over
               Time at 200px) so row 1 height never grows when more scan jobs
               accumulate failures
             • .chart-wrap has the canvas's NATURAL height (28px per bar + ax
               padding). When bar count fits, no scroll; when it overflows the
               viewport, the operator scrolls within the chart panel.
             Top bars (highest counts) are always at the top of the y-axis, so
             they're visible without scrolling — the long-tail bars are what
             gets the scroll affordance, which is the right priority. -->
        <div class="chart-scroll">
          <div class="chart-wrap" [style.height.px]="canvasHeight()">
            <canvas #canvas></canvas>
          </div>
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
    /* Fixed viewport — bounds the panel height regardless of bar count.
       Matches Errors Over Time's canvas height so row 1 stays visually
       aligned. Browser shows a scrollbar automatically when content
       overflows (standard convention — no custom hint needed). */
    .chart-scroll {
      height: 200px;
      overflow-y: auto;
      /* Thin scrollbar styling so the bar doesn't dominate the panel.
         Falls back gracefully on browsers that don't support these props. */
      scrollbar-width: thin;
      scrollbar-color: var(--border) transparent;
    }
    .chart-scroll::-webkit-scrollbar       { width: 6px; }
    .chart-scroll::-webkit-scrollbar-thumb { background: var(--border); border-radius: 3px; }
    /* Inner wrap takes the canvas's natural height — bound inline from
       canvasHeight(). When < 200, viewport shows whitespace below (accepted
       trade-off for sparse data); when > 200, viewport scrolls. */
    .chart-wrap { position: relative; }
    .chart-skeleton {
      /* Skeleton uses a middle-of-range height so it doesn't jump on data arrival */
      height: 200px;
      border-radius: var(--radius-sm);
      background: linear-gradient(90deg,
        var(--surface-2) 0%, var(--surface-3) 50%, var(--surface-2) 100%);
      background-size: 200% 100%;
      animation: shimmer 1.8s ease-in-out infinite;
    }
    @keyframes shimmer { 0% { background-position: 200% 0; } 100% { background-position: -200% 0; } }
    .chart-empty {
      height: 160px;
      display: flex; align-items: center; justify-content: center;
      color: var(--text-muted);
    }
  `]
})
export class FailuresByJobChartComponent implements AfterViewInit, OnDestroy {
  private svc = inject(FailuresService);

  /** Time range driven by the dashboard parent — same toggle as Errors Over Time. */
  range = input.required<Range>();

  @ViewChild('canvas') canvasRef?: ElementRef<HTMLCanvasElement>;

  loading = signal(true);
  data    = signal<FailuresByJobItem[]>([]);

  rangeLabel = computed(() => {
    switch (this.range()) {
      case '7d':  return 'Last 7 days';
      case '30d': return 'Last 30 days';
      default:    return 'Last 24 hours';
    }
  });

  /** Canvas's *natural* height — n × 28px per bar + 40px for x-axis + top
   *  padding. Floor at 120 so the chart looks proportionate when there's
   *  only 1-2 bars; NO ceiling — the outer .chart-scroll viewport (200px,
   *  matching Errors Over Time) caps the visible area and the browser
   *  scrollbar engages when the inner canvas exceeds it.
   *
   *  Result: row 1 panel height stays constant. Top bars (highest counts)
   *  are at the top of the y-axis so they're always visible; long-tail
   *  bars get the scroll affordance.
   *
   *  Trade-off when sparse: when n is small the inner canvas is shorter
   *  than the 200px viewport, leaving whitespace below inside the scroll
   *  area. Accepted — it's the right kind of asymmetry (compact chart
   *  with intentional whitespace) vs. the wrong kind (row growing every
   *  time a new scan job accumulates failures). */
  canvasHeight = computed(() => {
    const n = this.data().length;
    return Math.max(120, n * 28 + 40);
  });

  private chart?: Chart;
  private pendingRender = false;

  constructor() {
    // Re-fetch whenever the parent changes the range. First effect run fires
    // once after the component is constructed, which is before AfterViewInit;
    // ngAfterViewInit triggers the initial render in case the canvas hadn't
    // existed yet on the first effect tick.
    effect(() => {
      const r = this.range();
      this.fetch(r);
    });
  }

  ngAfterViewInit(): void {
    // No-op — fetch happened in the effect. Canvas rendering is gated by
    // renderIfReady() which waits for the @if branch to render the <canvas>.
  }

  ngOnDestroy(): void {
    this.chart?.destroy();
  }

  private fetch(range: Range): void {
    this.loading.set(true);
    this.chart?.destroy();
    this.chart = undefined;

    this.svc.getFailuresByJob(range).subscribe({
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
    const canvas = this.canvasRef?.nativeElement;
    const rows = this.data();
    if (!canvas || rows.length === 0) {
      // Canvas exists only after the @if (data > 0) branch renders. Retry
      // once on the next animation frame.
      requestAnimationFrame(() => {
        if (!this.pendingRender) return;
        const c2 = this.canvasRef?.nativeElement;
        if (c2 && this.data().length > 0) {
          this.buildChart(c2, this.data());
          this.pendingRender = false;
        }
      });
      return;
    }
    this.buildChart(canvas, rows);
    this.pendingRender = false;
  }

  private buildChart(canvas: HTMLCanvasElement, rows: FailuresByJobItem[]): void {
    // Server returns rows already sorted by failureCount DESC then name ASC.
    // Chart.js plots y-axis top-to-bottom, so the first row sits at the top.
    const labels   = rows.map(r => truncate(r.jobName));
    const fullNames = rows.map(r => r.jobName);
    const counts   = rows.map(r => r.failureCount);

    this.chart = new Chart(canvas, {
      type: 'bar',
      data: {
        labels,
        datasets: [{
          label:           'Failures',
          data:            counts,
          // Single neutral blue accent — this is a ranking chart, not a severity
          // chart. Red was reading as "alarm" for what's actually "bigger bar
          // means more failures here, that's it". --info (#2563eb) is the
          // app's blue tone (note: --primary is orange in this app).
          backgroundColor: 'rgba(37, 99, 235, 0.75)',
          borderColor:     'rgba(37, 99, 235, 1)',
          borderWidth:     1,
          borderRadius:    3,
        }],
      },
      options: {
        indexAxis: 'y',         // horizontal bars
        responsive: true,
        maintainAspectRatio: false,
        animation: { duration: 250, easing: 'easeOutQuad' },
        layout: { padding: { top: 4, right: 8, bottom: 0, left: 0 } },
        scales: {
          x: {
            beginAtZero: true,
            ticks: { precision: 0, color: '#6b7280', font: { size: 10 }, maxTicksLimit: 5 },
            grid:  { color: '#e8ebf0' },
          },
          y: {
            ticks: { color: '#374151', font: { size: 11 } },
            grid:  { display: false },
          },
        },
        plugins: {
          // Single-series chart — legend hides; the panel title carries the meaning.
          legend: { display: false },
          tooltip: {
            callbacks: {
              // Restore the full job name in the tooltip header (the y-axis
              // label may be truncated for very long names).
              title: items => fullNames[items[0].dataIndex] ?? '',
            },
          },
        },
      },
    });
  }
}
