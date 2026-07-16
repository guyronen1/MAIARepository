import { Component, OnDestroy, OnInit, computed, effect, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { PolledData, WorkerStatusService } from '../../core/services/worker-status.service';
import { WorkerStatus } from '../../core/models';
import { pluralize } from '../../core/pipes/pluralize.pipe';
import { AuthService } from '../../core/services/auth.service';
import { ScanService } from '../../core/services/scan.service';
import { ThemeService } from '../../core/services/theme.service';
import { SearchService } from '../../core/services/search.service';
import { LanguageService, LangCode } from '../../core/services/language.service';

type DotState = 'green' | 'yellow' | 'red' | 'gray' | 'paused';

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
          <span class="brand-subtitle">Monitoring Assistant & Intelligent Automation</span>
        </div>
      </div>

      <div class="topbar-right">
        <!-- Command palette trigger (also Ctrl/Cmd+K). Jump to pages, jobs,
             failures (item 6). -->
        <button class="search-trigger" type="button" (click)="search.openPalette()"
                [title]="'Search & jump (' + shortcutHint + ')'" aria-label="Search and jump">
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
            <circle cx="11" cy="11" r="7"/><path d="M21 21l-4.35-4.35"/>
          </svg>
          <span class="st-label">Search</span>
          <span class="st-kbd">{{ shortcutHint }}</span>
        </button>
        <!-- Data-freshness indicator. Only shown when the worker-status poll
             (the shared 5s heartbeat) has failed and we're serving a cached
             snapshot — so operators know the dashboard is frozen, not live. -->
        @if (isStale()) {
          <div class="stale-chip" [title]="staleTooltip()">
            <span class="stale-icon">⚠</span>
            <span>Reconnecting… · {{ lastGoodRelative() }}</span>
          </div>
        }
        <!-- Worker status + pause/resume control, consolidated here (was also a
             duplicate pill on the dashboard). Admins get a clickable toggle;
             everyone else sees the same pill read-only. -->
        @if (canControlWorker()) {
          <button class="live-badge control" [class]="'state-' + dotState()"
                  [class.data-stale]="isStale()" [disabled]="pauseToggling()"
                  (click)="togglePause()" [title]="tooltip()">
            @if (pauseToggling()) { <span class="pill-spin"></span> } @else { <span class="dot"></span> }
            <span class="live-label">{{ liveLabel() }}</span>
          </button>
        } @else {
          <div class="live-badge" [class]="'state-' + dotState()" [class.data-stale]="isStale()" [title]="tooltip()">
            <span class="dot"></span>
            <span class="live-label">{{ liveLabel() }}</span>
          </div>
        }
        <!-- Account menu — avatar-only circle. Its dropdown holds preferences
             (theme, language) + Sign out, so the bar stays uncluttered. -->
        @if (user(); as u) {
          <div class="account">
            <button class="avatar-btn" type="button"
                    (click)="accountMenuOpen.set(!accountMenuOpen())"
                    [attr.aria-expanded]="accountMenuOpen()" aria-haspopup="menu"
                    [title]="u.username + ' · ' + u.role">
              {{ initials() }}
            </button>
            @if (accountMenuOpen()) {
              <div class="account-backdrop" (click)="accountMenuOpen.set(false)"></div>
              <div class="account-menu" role="menu">
                <div class="acct-header">
                  <div class="acct-avatar">{{ initials() }}</div>
                  <div class="acct-id">
                    <span class="acct-name">{{ u.username }}</span>
                    <span class="acct-role">{{ u.role }}</span>
                  </div>
                </div>

                <div class="acct-sep"></div>

                <div class="acct-label">Theme</div>
                <div class="acct-seg">
                  <button type="button" [class.active]="theme.resolved() === 'light'"
                          (click)="theme.set('light')">☀ Light</button>
                  <button type="button" [class.active]="theme.resolved() === 'dark'"
                          (click)="theme.set('dark')">🌙 Dark</button>
                </div>

                <div class="acct-label">Language</div>
                @for (l of lang.languages; track l.code) {
                  <button class="acct-item" role="menuitemradio" type="button"
                          [disabled]="!l.enabled"
                          [attr.aria-checked]="l.code === lang.current()"
                          (click)="pickLang(l.code)">
                    <span>{{ l.nativeLabel }}</span>
                    @if (l.code === lang.current()) {
                      <span class="acct-check">✓</span>
                    } @else if (!l.enabled) {
                      <span class="acct-soon">soon</span>
                    }
                  </button>
                }

                <div class="acct-sep"></div>

                <button class="acct-item acct-signout" type="button" (click)="logout()">
                  Sign out
                </button>
              </div>
            }
          </div>
        }
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
    /* Drop the long subtitle on laptop-ish widths so the bar doesn't crowd. */
    @media (max-width: 1100px) { .brand-subtitle { display: none; } }

    .topbar-right { display: flex; align-items: center; gap: 16px; }

    .search-trigger {
      display: inline-flex; align-items: center; gap: 7px; flex-shrink: 0;
      height: 30px; padding: 0 10px;
      border: 1px solid var(--border); border-radius: 8px;
      background: var(--surface-2); color: var(--text-muted);
      cursor: pointer; font: inherit; font-size: 12px;
      transition: background var(--transition), color var(--transition);
      &:hover { background: var(--surface-3); color: var(--text); }
      svg { width: 14px; height: 14px; }
    }
    .st-kbd {
      font-size: 10px; border: 1px solid var(--border); border-radius: 4px;
      padding: 0 5px; color: var(--text-dim);
    }
    @media (max-width: 720px) { .st-label, .st-kbd { display: none; } }

    /* Data-stale chip — amber, calm (this is a "refresh failed" heads-up, not
       an alarm). Sits just left of the worker pill. */
    .stale-chip {
      display: flex; align-items: center; gap: 5px;
      font-size: 11px; font-weight: 600;
      color: var(--warning);
      background: var(--warning-bg, #fef3c7);
      border: 1px solid #fcd34d;
      padding: 3px 10px; border-radius: 20px;
      cursor: default;
    }
    .stale-icon { font-size: 11px; line-height: 1; }

    /* When the heartbeat is stale the pill must not keep pulsing green as if
       everything is live — dim it and freeze the pulse; the amber chip carries
       the truth. */
    .live-badge.data-stale { opacity: 0.5; }
    .live-badge.data-stale .dot { animation: none !important; }

    .live-badge {
      display: flex; align-items: center; gap: 6px;
      font-size: 11px; font-weight: 600;
      padding: 3px 10px; border-radius: 20px;
      transition: background var(--transition), color var(--transition), border-color var(--transition);
      cursor: default;
    }
    /* Admin control variant — button reset + interactive affordances. */
    .live-badge.control { font: inherit; font-size: 11px; font-weight: 600; cursor: pointer; }
    .live-badge.control:hover:not(:disabled) { filter: brightness(0.96); }
    .live-badge.control:disabled { cursor: default; opacity: 0.6; }
    .dot { width: 8px; height: 8px; border-radius: 50%; }
    .pill-spin {
      width: 9px; height: 9px; border-radius: 50%;
      border: 2px solid currentColor; border-top-color: transparent;
      animation: spin 0.7s linear infinite;
    }

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
    /* Paused = operator paused the scan loop */
    .state-paused {
      color: var(--warn-text);
      background: var(--warn-bg-2);
      border: 1px solid var(--warn-border);
      .dot { background: var(--warn-text); }
    }
    @keyframes dot-pulse {
      0%, 100% { opacity: 0.65; }
      50%      { opacity: 1; }
    }

    /* Account menu — avatar-only circle trigger + dropdown. */
    .account { position: relative; flex-shrink: 0; }
    .avatar-btn {
      width: 32px; height: 32px; border-radius: 50%;
      background: var(--primary); color: #fff;
      border: 1px solid transparent;
      display: flex; align-items: center; justify-content: center;
      font: inherit; font-size: 11px; font-weight: 700; cursor: pointer;
      transition: box-shadow var(--transition), filter var(--transition);
      &:hover { filter: brightness(1.05); box-shadow: 0 0 0 3px var(--primary-glow); }
    }
    .account-backdrop { position: fixed; inset: 0; z-index: 90; }
    .account-menu {
      position: absolute; top: calc(100% + 8px); right: 0; z-index: 100;
      min-width: 220px; padding: 6px;
      background: var(--surface); border: 1px solid var(--border);
      border-radius: var(--radius); box-shadow: var(--shadow);
    }
    .acct-header { display: flex; align-items: center; gap: 10px; padding: 6px 8px 8px; }
    .acct-avatar {
      width: 34px; height: 34px; border-radius: 50%;
      background: var(--primary); color: #fff;
      display: flex; align-items: center; justify-content: center;
      font-size: 12px; font-weight: 700; flex-shrink: 0;
    }
    .acct-id { display: flex; flex-direction: column; line-height: 1.25; min-width: 0; }
    .acct-name { font-size: 13px; font-weight: 600; color: var(--text);
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .acct-role { font-size: 11px; color: var(--text-muted); }
    .acct-sep { height: 1px; background: var(--border); margin: 4px 0; }
    .acct-label {
      font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.06em;
      color: var(--text-dim); padding: 6px 8px 3px;
    }
    /* Theme segmented control */
    .acct-seg {
      display: flex; gap: 4px; padding: 0 8px 4px;
      button {
        flex: 1; padding: 6px 8px; border: 1px solid var(--border);
        border-radius: var(--radius-sm); background: var(--surface-2);
        color: var(--text-muted); font: inherit; font-size: 12px; cursor: pointer;
        transition: background var(--transition), color var(--transition);
        &:hover { background: var(--surface-3); color: var(--text); }
        &.active { background: var(--primary-light); color: var(--primary); border-color: var(--primary); font-weight: 600; }
      }
    }
    .acct-item {
      display: flex; align-items: center; justify-content: space-between; gap: 12px;
      width: 100%; padding: 8px; border: none; border-radius: var(--radius-sm);
      background: transparent; color: var(--text); cursor: pointer;
      font: inherit; font-size: 13px; text-align: left;
      &:hover:not(:disabled) { background: var(--surface-2); }
      &:disabled { color: var(--text-dim); cursor: default; }
    }
    .acct-check { color: var(--primary); font-weight: 700; }
    .acct-soon {
      font-size: 10px; text-transform: uppercase; letter-spacing: 0.05em;
      color: var(--text-dim); border: 1px solid var(--border);
      border-radius: 4px; padding: 0 5px;
    }
    .acct-signout { font-weight: 600; }
    .acct-signout:hover:not(:disabled) { background: var(--danger-bg); color: var(--danger); }
  `]
})
export class TopBarComponent implements OnInit, OnDestroy {
  private auth = inject(AuthService);
  private router = inject(Router);
  private statusSvc = inject(WorkerStatusService);
  private scanSvc = inject(ScanService);
  readonly theme = inject(ThemeService);
  readonly search = inject(SearchService);
  readonly lang = inject(LanguageService);
  readonly accountMenuOpen = signal(false);

  pickLang(code: LangCode): void {
    this.lang.set(code);          // no-op for not-yet-enabled languages
    this.accountMenuOpen.set(false);
  }

  /** Ctrl on Windows/Linux, ⌘ on Mac — cosmetic hint on the search trigger. */
  readonly shortcutHint =
    typeof navigator !== 'undefined' && /mac/i.test(navigator.platform || navigator.userAgent)
      ? '⌘K' : 'Ctrl K';

  readonly user = this.auth.currentUser;
  readonly initials = computed(() => {
    const name = this.user()?.username ?? '';
    return name.slice(0, 2).toUpperCase() || '—';
  });

  // The live badge is always visible (top bar sits in the shell), so the top
  // bar owns an always-on worker-status heartbeat. Without this, the badge
  // showed gray "no data yet" on any page that doesn't itself start polling
  // (everything except the dashboard + scan-jobs). Refcounted: dashboard /
  // scan-jobs add their own ref while mounted; a single timer serves all.
  ngOnInit(): void  { this.statusSvc.start(); }
  ngOnDestroy(): void { this.statusSvc.stop(); }

  logout(): void {
    this.auth.logout().subscribe({
      next: () => this.router.navigate(['/login']),
      error: () => this.router.navigate(['/login']),
    });
  }

  // The service emits PolledData<WorkerStatus>; unwrap `.value` so existing
  // computed logic sees the raw WorkerStatus shape (or null before the first
  // response). Polling itself is started/stopped in ngOnInit/ngOnDestroy above.
  private static readonly EMPTY: PolledData<WorkerStatus> = {
    value: null, isStale: false, lastUpdatedAt: null,
  };
  private statusPolled = toSignal(
    this.statusSvc.status$,
    { initialValue: TopBarComponent.EMPTY });
  private status = computed(() => this.statusPolled().value);

  // ── Data freshness (surfaced item 2) ──────────────────────────────────
  // "Stale" means: we had a good payload once, and the most recent refresh
  // failed. Before the first success (value === null) we're merely
  // "connecting" — the gray pill covers that; don't cry "reconnecting".
  readonly isStale = computed(() => {
    const p = this.statusPolled();
    return p.isStale && p.value !== null;
  });

  /** Age of the last successful refresh, e.g. "45s ago". */
  readonly lastGoodRelative = computed(() => {
    const at = this.statusPolled().lastUpdatedAt;
    return at ? this.relativeDate(at) : '';
  });

  readonly staleTooltip = computed(() => {
    const p = this.statusPolled();
    const rel = p.lastUpdatedAt ? this.relativeDate(p.lastUpdatedAt) : 'unknown';
    const err = p.lastError ? ` (${p.lastError})` : '';
    return `Couldn't refresh dashboard data${err}. Showing the last snapshot from ${rel}. Retrying every 5s.`;
  });

  dotState = computed<DotState>(() => {
    const s = this.status();
    // Optimistic pause state wins so the pill flips the instant the operator
    // clicks — even before the confirming poll arrives (see effectivePaused).
    if (this.effectivePaused()) return 'paused';
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
      case 'paused': return 'Paused';
      default:       return '—';
    }
  });

  tooltip = computed(() => {
    const s = this.status();
    if (!s) return 'Worker status: no data yet';
    const admin = this.canControlWorker();
    if (this.effectivePaused())
      return admin ? 'Monitoring is paused — click to resume' : 'Monitoring is paused';
    const base = s.activeScans.length > 0
      ? `Worker active — ${pluralize(s.activeScans.length, 'scan')} running`
      : !s.lastActivityAt
        ? 'Worker has not completed a scan yet'
        : `Last activity: ${this.relative(s.lastActivityAt)}`;
    return admin ? `${base} · click to pause` : base;
  });

  // ── Pause / resume control (consolidated from the dashboard, item 3) ──────
  // Pausing/resuming the worker is an Admin operation (POST /api/admin/worker/*).
  readonly canControlWorker = computed(() => this.auth.hasAtLeast('Administrator'));
  readonly pauseToggling     = signal(false);

  // Optimistic pause state: set the instant the operator clicks so the pill
  // flips immediately instead of waiting ≤5s for the confirming poll. null =
  // "no pending override, trust the poll." Cleared on error (revert) or once
  // the poll confirms the same value (reconcile effect below).
  private readonly pauseOverride = signal<boolean | null>(null);
  private readonly effectivePaused = computed(() =>
    this.pauseOverride() ?? (this.status()?.isPaused ?? false));

  // Drop the optimistic override once the polled truth matches it — from then
  // on the poll is the source of truth again. (Signal writes in effects are
  // used elsewhere in this codebase, e.g. the dashboard banner effect.)
  private reconcilePauseOverride = effect(() => {
    const ov = this.pauseOverride();
    if (ov !== null && this.status()?.isPaused === ov) this.pauseOverride.set(null);
  });

  togglePause(): void {
    if (this.pauseToggling() || !this.canControlWorker()) return;
    const target = !this.effectivePaused();   // where we're heading
    this.pauseOverride.set(target);            // optimistic — pill flips now
    this.pauseToggling.set(true);
    const call = target ? this.scanSvc.pauseWorker() : this.scanSvc.resumeWorker();
    // Override holds the new state on the pill until the confirming poll (≤5s)
    // catches up, at which point reconcilePauseOverride clears it.
    call.subscribe({
      next:  () => this.pauseToggling.set(false),
      error: () => {
        this.pauseOverride.set(null);   // revert the optimistic flip
        this.pauseToggling.set(false);
      },
    });
  }

  private relative(iso: string): string {
    return this.relativeDate(new Date(iso));
  }

  private relativeDate(d: Date): string {
    const sec = Math.max(0, Math.floor((Date.now() - d.getTime()) / 1000));
    if (sec < 60)   return `${sec}s ago`;
    if (sec < 3600) return `${Math.floor(sec / 60)}m ago`;
    if (sec < 86400)return `${Math.floor(sec / 3600)}h ago`;
    return `${Math.floor(sec / 86400)}d ago`;
  }
}
