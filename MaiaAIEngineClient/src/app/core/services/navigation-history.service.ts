import { Injectable, computed, inject, signal } from '@angular/core';
import { Location } from '@angular/common';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs/operators';

/**
 * Tracks the previous "distinct page" the operator came from so detail
 * surfaces (currently the failures drawer) can render a smart back button
 * pointing at the actual referrer.
 *
 * Two distinct concepts live here:
 *
 * 1. **prevUrl** — the URL of the previous distinct path. "Distinct" = path
 *    changed (e.g. /dashboard → /failures). Query-param-only changes
 *    (drawer ?selected= toggles, paging, search) DON'T shift this — otherwise
 *    pressing ↓ once would hide the back button forever.
 *
 * 2. **drawerOrigin** — *how* the currently-open drawer was opened:
 *    - `drill-in`: the same NavigationEnd both changed the path AND introduced
 *      ?selected= (e.g. dashboard → /failures?selected=123). Back button
 *      shows the referrer label.
 *    - `browse`: ?selected= was added while the operator was already on the
 *      current path (clicked a row on the failures list they were browsing).
 *      Back button hides — there's no meaningful elsewhere-to-go-back-to.
 *    - `null`: no drawer open.
 *
 *    Without this distinction, an operator who navigated dashboard → failures
 *    (looking around) and then clicked a row would see "← Back to Dashboard"
 *    in the drawer, even though they intentionally moved past dashboard to
 *    browse failures. That misleads.
 *
 * Eagerly instantiated by ShellComponent so history tracking starts at app
 * boot, before any NavigationEnd has fired.
 */
type DrawerOrigin = 'drill-in' | 'browse' | null;

@Injectable({ providedIn: 'root' })
export class NavigationHistoryService {
  private router   = inject(Router);
  private location = inject(Location);

  // Hand-maintained — only top-level menu destinations have labels. Other
  // paths (e.g. the legacy /failures/:id redirect target) return null →
  // back button hides rather than rendering a confusing label.
  private static readonly LABELS: Record<string, string> = {
    '/dashboard':                   'Dashboard',
    '/failures':                    'Failures',
    '/recommendations':             'Recommendations',
    '/operator-actions':            'Operator Actions',
    '/scan-jobs':                   'Scan Jobs',
    '/config/monitored-jobs':       'Monitored Jobs',
    '/config/classification-rules': 'Classification Rules',
    '/config/error-types':          'Error Types',
  };

  private prevUrl       = signal<string | null>(null);
  private drawerOrigin  = signal<DrawerOrigin>(null);
  private currentUrl: string | null = null;

  /** Friendly label for the back button. Returns null (back button hides)
   *  unless the drawer was opened via a drill-in transition (path changed
   *  AND ?selected= introduced in the same NavigationEnd) AND the referrer
   *  is in the LABELS map. */
  previousLabel = computed(() => {
    if (this.drawerOrigin() !== 'drill-in') return null;
    const url = this.prevUrl();
    if (!url) return null;
    const path = url.split('?')[0];
    return NavigationHistoryService.LABELS[path] ?? null;
  });

  constructor() {
    this.router.events
      .pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd))
      .subscribe(e => {
        const newPath        = e.urlAfterRedirects.split('?')[0];
        const curPath        = this.currentUrl?.split('?')[0] ?? null;
        const newHasSelected = e.urlAfterRedirects.includes('selected=');
        const curHasSelected = this.currentUrl?.includes('selected=') ?? false;
        const pathChanged    = curPath !== null && newPath !== curPath;

        if (pathChanged) {
          this.prevUrl.set(this.currentUrl);
        }

        // Drawer lifecycle. ?selected= going absent→present means the drawer
        // is opening; present→absent means it's closing. Anything else (in
        // particular present→present as the operator navigates ↑/↓) leaves
        // the origin classification intact — once a drawer is opened as
        // drill-in, it stays drill-in until closed.
        if (!curHasSelected && newHasSelected) {
          this.drawerOrigin.set(pathChanged ? 'drill-in' : 'browse');
        } else if (curHasSelected && !newHasSelected) {
          this.drawerOrigin.set(null);
        }

        this.currentUrl = e.urlAfterRedirects;
      });
  }

  /** Browser-back to the previous page. Uses Location.back() so the
   *  history stack stays correct (forward button works after, etc.). */
  back() {
    this.location.back();
  }
}
