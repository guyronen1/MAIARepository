import { Injectable, inject, signal } from '@angular/core';
import { AuthService, MaiaRole } from './auth.service';
import { MonitoredJobsService } from './monitored-jobs.service';
import { MonitoredJob } from '../models';

export type SearchResultKind = 'nav' | 'failure' | 'job';

export interface SearchResult {
  kind:        SearchResultKind;
  key:         string;                       // stable @for track id
  icon:        string;
  title:       string;
  subtitle?:   string;
  route:       unknown[];                    // router navigate commands
  queryParams?: Record<string, unknown>;
}

interface NavDest {
  label: string; icon: string; route: string; minRole: MaiaRole; keywords?: string;
}

/**
 * Backs the command palette (Ctrl/Cmd+K). Two responsibilities:
 *   - `open` signal + open/close/toggle — shared UI state so the top-bar trigger,
 *     the global shortcut, and the palette component all coordinate.
 *   - `query(text)` — the deterministic result list (navigation destinations +
 *     failure-by-id + monitored jobs), role-filtered. This is a PURE function of
 *     (text, role, cached jobs); it's the piece a future LLM "navigate" tool can
 *     call directly — the palette is just its UI shell.
 */
@Injectable({ providedIn: 'root' })
export class SearchService {
  private auth    = inject(AuthService);
  private jobsSvc = inject(MonitoredJobsService);

  readonly open = signal(false);
  openPalette(): void { this.open.set(true); }
  closePalette(): void { this.open.set(false); }
  toggle(): void { this.open.update(v => !v); }

  private jobs = signal<MonitoredJob[]>([]);
  private jobsLoaded = false;

  /** Lazy-load the job list once (first palette open). Cheap + cached; on error
   *  we leave it un-loaded so the next open retries. */
  ensureLoaded(): void {
    if (this.jobsLoaded) return;
    this.jobsLoaded = true;
    this.jobsSvc.getAll().subscribe({
      next:  j => this.jobs.set(j ?? []),
      error: () => { this.jobsLoaded = false; },
    });
  }

  query(text: string): SearchResult[] {
    const raw = text.trim();
    const q = raw.toLowerCase();
    const results: SearchResult[] = [];

    // 1) Failure by id — "1622" or "#1622" jumps straight into the drawer.
    const m = raw.match(/^#?(\d{1,9})$/);
    if (m) {
      const id = m[1];
      results.push({
        kind: 'failure', key: 'failure:' + id, icon: '⚠',
        title: `Go to failure #${id}`, subtitle: 'Open in the failures drawer',
        route: ['/failures'], queryParams: { selected: +id },
      });
    }

    // 2) Navigation destinations, role-filtered. Empty query = show them all
    //    (a menu of jump targets); non-empty = substring match on label/keywords.
    for (const d of NAV_CATALOG) {
      if (!this.auth.hasAtLeast(d.minRole)) continue;
      if (q && !(d.label.toLowerCase().includes(q) || (d.keywords?.includes(q) ?? false))) continue;
      results.push({
        kind: 'nav', key: 'nav:' + d.route, icon: d.icon,
        title: d.label, subtitle: 'Go to page', route: [d.route],
      });
    }

    // 3) Monitored jobs by name — Operator+ only (job config is Operator-gated;
    //    a plain User would just get 403s on the config reads).
    if (q && this.auth.hasAtLeast('Operator')) {
      let shown = 0;
      for (const j of this.jobs()) {
        const name = j.displayName ?? j.name;
        if (!(name.toLowerCase().includes(q) || j.name.toLowerCase().includes(q))) continue;
        results.push({
          kind: 'job', key: 'job:' + j.monitoredJobId, icon: '🗂',
          title: name, subtitle: `Configure job · ${j.jobTypeName}`,
          route: ['/config/monitored-jobs', j.monitoredJobId],
        });
        if (++shown >= 8) break;   // cap the job list; refine the query for more
      }
    }

    return results;
  }
}

// Mirrors the side-menu (hand-kept — the menu changes rarely). Only destinations
// the current role can reach are ever surfaced (checked in query()). keywords are
// lowercase synonyms so "history"/"gaps"/"home" find the right page.
const NAV_CATALOG: NavDest[] = [
  { label: 'Dashboard',            icon: '📊', route: '/dashboard',                   minRole: 'User',          keywords: 'home overview' },
  { label: 'Failures',             icon: '⚠',  route: '/failures',                    minRole: 'User',          keywords: 'errors incidents' },
  { label: 'Recommendations',      icon: '💡', route: '/recommendations',             minRole: 'User',          keywords: 'ai suggestions fixes' },
  { label: 'Unconfigured',         icon: '🛠', route: '/unconfigured',                minRole: 'User',          keywords: 'gaps coverage triage' },
  { label: 'Operator Actions',     icon: '📋', route: '/operator-actions',            minRole: 'Operator',      keywords: 'history decisions approvals rejections' },
  { label: 'Scan Jobs',            icon: '🔍', route: '/scan-jobs',                   minRole: 'Operator',      keywords: 'scan run trigger' },
  { label: 'Monitored Jobs',       icon: '🗂', route: '/config/monitored-jobs',       minRole: 'Operator',      keywords: 'config jobs sources' },
  { label: 'Classification Rules', icon: '≡',  route: '/config/classification-rules', minRole: 'Operator',      keywords: 'config patterns classify' },
  { label: 'Error Types',          icon: '🏷', route: '/config/error-types',          minRole: 'Operator',      keywords: 'config' },
  { label: 'Users',                icon: '👤', route: '/config/users',                minRole: 'Administrator', keywords: 'accounts admin roles' },
  { label: 'Audit Log',            icon: '🧾', route: '/config/audit',                minRole: 'Administrator', keywords: 'history admin events' },
];
