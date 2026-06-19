import { Component, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService, MaiaRole } from '../../core/services/auth.service';

interface NavItem { label: string; icon: string; route: string; }
interface NavSection { title: string; minRole: MaiaRole; items: NavItem[]; }

@Component({
  selector: 'app-side-menu',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  template: `
    <nav class="sidenav">
      @for (section of visibleSections(); track section.title) {
        <div class="nav-section">
          <span class="section-title">{{ section.title }}</span>
          @for (item of section.items; track item.route) {
            <a class="nav-item" [routerLink]="item.route" routerLinkActive="active"
               [routerLinkActiveOptions]="{ exact: item.route === '/dashboard' }">
              <span class="nav-icon" [innerHTML]="item.icon"></span>
              <span>{{ item.label }}</span>
            </a>
          }
        </div>
      }
    </nav>
  `,
  styles: [`
    .sidenav {
      width: var(--sidebar-w);
      flex-shrink: 0;
      background: var(--surface);
      border-right: 1px solid var(--border);
      padding: 12px 0 20px;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
    }
    .nav-section { display: flex; flex-direction: column; margin-bottom: 4px; }
    .section-title {
      font-size: 10px; font-weight: 700; text-transform: uppercase;
      letter-spacing: 0.08em; color: var(--text-dim);
      padding: 10px 16px 4px;
    }
    .nav-item {
      display: flex; align-items: center; gap: 9px;
      padding: 8px 16px;
      color: var(--text-muted);
      font-size: 13px;
      font-weight: 500;
      text-decoration: none;
      transition: all var(--transition);
      border-left: 3px solid transparent;
      &:hover { background: var(--surface-2); color: var(--text); }
      &.active {
        background: var(--primary-light);
        color: var(--primary);
        border-left-color: var(--primary);
        font-weight: 600;
      }
    }
    .nav-icon {
      width: 16px; height: 16px; flex-shrink: 0;
      display: flex; align-items: center; justify-content: center;
      ::ng-deep svg { width: 15px; height: 15px; }
    }
  `]
})
export class SideMenuComponent {
  private auth = inject(AuthService);

  /** Cosmetic gating — the API enforces independently. Monitor = any authenticated
   *  user; Actions + Configuration = Operator and up (config writes within are still
   *  Admin-gated server-side, surfaced via the 403 toast). */
  readonly visibleSections = computed<NavSection[]>(() =>
    this.sections.filter(s => this.auth.hasAtLeast(s.minRole)));

  sections: NavSection[] = [
    {
      title: 'Monitor',
      minRole: 'User',
      items: [
        { label: 'Dashboard',       icon: svgIcon('M3 12l2-2m0 0l7-7 7 7M5 10v10a1 1 0 001 1h3m10-11l2 2m-2-2v10a1 1 0 01-1 1h-3m-6 0a1 1 0 001-1v-4a1 1 0 011-1h2a1 1 0 011 1v4a1 1 0 001 1m-6 0h6'), route: '/dashboard' },
        { label: 'Failures',        icon: svgIcon('M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z'), route: '/failures' },
        { label: 'Recommendations', icon: svgIcon('M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z'), route: '/recommendations' },
        { label: 'Unconfigured',    icon: svgIcon('M12 9v2m0 4h.01M5.07 19h13.86c1.54 0 2.5-1.67 1.73-3L13.73 4c-.77-1.33-2.69-1.33-3.46 0L3.34 16c-.77 1.33.19 3 1.73 3z'), route: '/unconfigured' },
      ]
    },
    {
      title: 'Actions',
      minRole: 'Operator',
      items: [
        { label: 'Operator Actions', icon: svgIcon('M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-6 9l2 2 4-4'), route: '/operator-actions' },
        { label: 'Scan Jobs',        icon: svgIcon('M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z'), route: '/scan-jobs' },
      ]
    },
    {
      title: 'Configuration',
      minRole: 'Operator',
      items: [
        { label: 'Monitored Jobs',        icon: svgIcon('M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10'), route: '/config/monitored-jobs' },
        { label: 'Classification Rules',  icon: svgIcon('M4 6h16M4 10h16M4 14h16M4 18h16'), route: '/config/classification-rules' },
        { label: 'Error Types',           icon: svgIcon('M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.994 1.994 0 013 12V7a4 4 0 014-4z'), route: '/config/error-types' },
      ]
    },
  ];
}

function svgIcon(d: string): string {
  return `<svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="1.8"><path stroke-linecap="round" stroke-linejoin="round" d="${d}"/></svg>`;
}
