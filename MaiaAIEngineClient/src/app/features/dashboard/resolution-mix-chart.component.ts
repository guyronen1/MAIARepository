import {
  AfterViewInit, Component, ElementRef, OnDestroy, ViewChild,
  inject, signal,
} from '@angular/core';
import {
  BarController, BarElement, CategoryScale, Chart, LinearScale, Legend, Tooltip,
} from 'chart.js';
import { FailuresService, ResolutionMixItem } from '../../core/services/failures.service';

Chart.register(BarController, BarElement, CategoryScale, LinearScale, Legend, Tooltip);

// Four stacked series — colors picked to match the operator's reading model:
// green = "MAIA handled it autonomously", blue = "operator decided + MAIA executed",
// amber = "needs you" (action required), gray = "in progress" (no terminal state yet).
//
// Stored as rgba with alpha 0.85 for a slightly muted, ops-dashboard tone —
// the saturated CSS-variable hex felt cartoonish next to the rest of the
// dashboard's muted surfaces. No `*-muted` variants exist in styles.scss
// (verified), so the alpha is baked into the chart color directly.
const COLORS = {
  autoHealed:       'rgba(22, 163, 74, 0.85)',  // --success @ 0.85
  operatorApproved: 'rgba(37, 99, 235, 0.85)',  // --info @ 0.85
  manualRequired:   'rgba(217, 119, 6, 0.85)',  // --warning @ 0.85
  stillActive:      'rgba(156, 163, 175, 0.85)', // neutral gray @ 0.85
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
        <div class="chart-wrap">
          <canvas #canvas></canvas>
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
    .chart-wrap { position: relative; height: 220px; }
    .chart-skeleton {
      height: 220px;
      border-radius: var(--radius-sm);
      background: linear-gradient(90deg,
        var(--surface-2) 0%, var(--surface-3) 50%, var(--surface-2) 100%);
      background-size: 200% 100%;
      animation: shimmer 1.8s ease-in-out infinite;
    }
    @keyframes shimmer { 0% { background-position: 200% 0; } 100% { background-position: -200% 0; } }
    .chart-empty {
      height: 220px;
      display: flex; align-items: center; justify-content: center;
      color: var(--text-muted);
    }
  `]
})
export class ResolutionMixChartComponent implements AfterViewInit, OnDestroy {
  private svc = inject(FailuresService);

  @ViewChild('canvas') canvasRef?: ElementRef<HTMLCanvasElement>;

  loading = signal(true);
  data    = signal<ResolutionMixItem[]>([]);

  /** True only when every day has zero counts across all stacks. The 7
   *  gap-filled rows are always present, so "no data" really means "no
   *  failures detected at all", not "missing payload". */
  isEmpty = () => this.data().every(r =>
    r.autoHealed + r.operatorApproved + r.manualRequired + r.stillActive === 0);

  private chart?: Chart;
  private pendingRender = false;

  ngAfterViewInit(): void {
    this.fetch();
  }

  ngOnDestroy(): void {
    this.chart?.destroy();
  }

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
    const canvas = this.canvasRef?.nativeElement;
    const rows = this.data();
    if (!canvas || rows.length === 0 || this.isEmpty()) {
      requestAnimationFrame(() => {
        if (!this.pendingRender) return;
        const c2 = this.canvasRef?.nativeElement;
        if (c2 && this.data().length > 0 && !this.isEmpty()) {
          this.buildChart(c2, this.data());
          this.pendingRender = false;
        }
      });
      return;
    }
    this.buildChart(canvas, rows);
    this.pendingRender = false;
  }

  private buildChart(canvas: HTMLCanvasElement, rows: ResolutionMixItem[]): void {
    // Display labels: MM-DD form. Bucket order is already oldest-first from
    // the backend gap-fill; today sits on the right of the chart.
    const labels = rows.map(r => {
      const [_, m, d] = r.bucketDay.split('-');
      return `${m}-${d}`;
    });

    this.chart = new Chart(canvas, {
      type: 'bar',
      data: {
        labels,
        datasets: [
          {
            label:           'Auto-Healed',
            data:            rows.map(r => r.autoHealed),
            backgroundColor: COLORS.autoHealed,
            stack:           'mix',
            borderWidth:     0,
          },
          {
            label:           'Operator-Approved',
            data:            rows.map(r => r.operatorApproved),
            backgroundColor: COLORS.operatorApproved,
            stack:           'mix',
            borderWidth:     0,
          },
          {
            label:           'Manual-Required',
            data:            rows.map(r => r.manualRequired),
            backgroundColor: COLORS.manualRequired,
            stack:           'mix',
            borderWidth:     0,
          },
          {
            label:           'Still Active',
            data:            rows.map(r => r.stillActive),
            backgroundColor: COLORS.stillActive,
            stack:           'mix',
            borderWidth:     0,
          },
        ],
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'index', intersect: false },
        animation: { duration: 250, easing: 'easeOutQuad' },
        layout: { padding: { top: 4, right: 4, bottom: 0, left: 0 } },
        scales: {
          x: {
            stacked: true,
            // Subtle vertical gridlines between days. Even with sparse data
            // (some days at zero), the grid frames each bucket so the chart
            // reads as "well-formed" instead of "missing chunks". Lighter
            // than the y-axis grid color so it visually recedes.
            grid:  { display: true, color: '#f3f4f6', drawTicks: false },
            ticks: { color: '#6b7280', font: { size: 10 } },
          },
          y: {
            stacked: true,
            beginAtZero: true,
            // precision: 0 already forces integers; maxTicksLimit caps density
            // (Chart.js will auto-pick round multiples — typical output:
            // 0, 10, 20, 30, 40 for max ~33). No fractional values ever.
            ticks: { precision: 0, maxTicksLimit: 5, color: '#6b7280', font: { size: 10 } },
            grid:  { color: '#e8ebf0' },
          },
        },
        plugins: {
          legend: {
            position: 'bottom',
            align: 'center',
            labels: { boxWidth: 10, boxHeight: 10, padding: 10, font: { size: 11 }, color: '#6b7280' },
          },
          tooltip: {
            callbacks: {
              footer: items => {
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
