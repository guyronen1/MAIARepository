import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { PolledData, WorkerStatusService } from '../../core/services/worker-status.service';
import { WorkerStatus } from '../../core/models';
import { pluralize } from '../../core/pipes/pluralize.pipe';

type DotState = 'green' | 'yellow' | 'red' | 'gray';

@Component({
  selector: 'app-top-bar',
  standalone: true,
  imports: [],
  template: `
    <header class="topbar">
      <div class="topbar-left">
        <img src="ness-icon.png" alt="NESS" class="brand-logo" />
        <div class="brand-divider"></div>
        <div class="brand-text">
          <span class="brand-name">MAIA</span>
          <span class="brand-subtitle">Intelligent Monitoring & Auto-Healing Platform</span>
        </div>
      </div>

      <div class="topbar-right">
        <div class="live-badge" [class]="'state-' + dotState()" [title]="tooltip()">
          <span class="dot"></span>
          <span class="live-label">{{ liveLabel() }}</span>
        </div>
        <div class="user-chip">
          <div class="user-avatar">OP</div>
          <div class="user-info">
            <span class="user-name">Operator</span>
            <span class="user-role">Admin</span>
          </div>
        </div>
      </div>
    </header>
  `,
  styles: [`
    .topbar {
      height: var(--topbar-h);
      background: var(--surface);
      border-bottom: 2px solid var(--primary);
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 0 20px;
      flex-shrink: 0;
      box-shadow: 0 2px 6px rgba(0,0,0,0.08);
      z-index: 100;
    }
    .topbar-left  { display: flex; align-items: center; gap: 14px; }
    .brand-logo   { height: 38px; width: 38px; object-fit: contain; border-radius: 50%; }
    .brand-divider{ width: 1px; height: 28px; background: var(--border); }
    .brand-text   { display: flex; flex-direction: column; line-height: 1.2; }
    .brand-name   { font-size: 15px; font-weight: 700; color: var(--text); letter-spacing: -0.01em; }
    .brand-subtitle { font-size: 10px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.08em; }

    .topbar-right { display: flex; align-items: center; gap: 16px; }

    .live-badge {
      display: flex; align-items: center; gap: 6px;
      font-size: 11px; font-weight: 600;
      padding: 3px 10px; border-radius: 20px;
      transition: background var(--transition), color var(--transition), border-color var(--transition);
      cursor: default;
    }
    .dot { width: 8px; height: 8px; border-radius: 50%; }

    /* Green = worker actively scanning or activity within 1× window — subtle pulse */
    .state-green {
      color: var(--success);
      background: var(--success-bg);
      border: 1px solid #86efac;
      .dot { background: var(--success); animation: dot-pulse 2s ease-in-out infinite; }
    }
    /* Yellow = idle but last activity within 2× window */
    .state-yellow {
      color: var(--warning);
      background: var(--warning-bg, rgba(245,158,11,0.08));
      border: 1px solid rgba(245,158,11,0.4);
      .dot { background: var(--warning); }
    }
    /* Red = no activity in last 2× window */
    .state-red {
      color: var(--danger);
      background: var(--danger-bg);
      border: 1px solid rgba(239,68,68,0.4);
      .dot { background: var(--danger); }
    }
    /* Gray = no data yet (initial load) */
    .state-gray {
      color: var(--text-muted);
      background: var(--surface-2);
      border: 1px solid var(--border);
      .dot { background: var(--text-muted); }
    }
    @keyframes dot-pulse {
      0%, 100% { opacity: 0.65; }
      50%      { opacity: 1; }
    }

    .user-chip {
      display: flex; align-items: center; gap: 8px;
      padding: 4px 12px 4px 4px;
      background: var(--surface-2); border: 1px solid var(--border);
      border-radius: 20px; cursor: pointer;
      transition: background var(--transition);
      &:hover { background: var(--surface-3); }
    }
    .user-avatar {
      width: 28px; height: 28px;
      background: var(--primary);
      border-radius: 50%;
      display: flex; align-items: center; justify-content: center;
      font-size: 10px; font-weight: 700; color: #fff;
    }
    .user-info    { display: flex; flex-direction: column; line-height: 1.2; }
    .user-name    { font-size: 12px; font-weight: 600; color: var(--text); }
    .user-role    { font-size: 10px; color: var(--text-muted); }
  `]
})
export class TopBarComponent {
  // Subscribe only — never start the polling timer here. The service emits
  // PolledData<WorkerStatus>; unwrap `.value` so existing computed logic
  // sees the raw WorkerStatus shape (or null before the first response).
  private static readonly EMPTY: PolledData<WorkerStatus> = {
    value: null, isStale: false, lastUpdatedAt: null,
  };
  private statusPolled = toSignal(
    inject(WorkerStatusService).status$,
    { initialValue: TopBarComponent.EMPTY });
  private status = computed(() => this.statusPolled().value);

  dotState = computed<DotState>(() => {
    const s = this.status();
    if (!s) return 'gray';
    if (!s.workerAlive) return 'red';
    if (s.activeScans.length > 0) return 'green';
    // workerAlive=true with no active scans → check how recent
    if (!s.lastActivityAt) return 'gray';
    const ageSec = (Date.now() - new Date(s.lastActivityAt).getTime()) / 1000;
    return ageSec < s.aliveWindowSeconds ? 'green' : 'yellow';
  });

  liveLabel = computed(() => {
    switch (this.dotState()) {
      case 'green':  return 'Live';
      case 'yellow': return 'Idle';
      case 'red':    return 'Offline';
      default:       return '—';
    }
  });

  tooltip = computed(() => {
    const s = this.status();
    if (!s) return 'Worker status: no data yet';
    if (s.activeScans.length > 0)
      return `Worker active — ${pluralize(s.activeScans.length, 'scan')} running`;
    if (!s.lastActivityAt) return 'Worker has not completed a scan yet';
    return `Last activity: ${this.relative(s.lastActivityAt)}`;
  });

  private relative(iso: string): string {
    const sec = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
    if (sec < 60)   return `${sec}s ago`;
    if (sec < 3600) return `${Math.floor(sec / 60)}m ago`;
    if (sec < 86400)return `${Math.floor(sec / 3600)}h ago`;
    return `${Math.floor(sec / 86400)}d ago`;
  }
}
