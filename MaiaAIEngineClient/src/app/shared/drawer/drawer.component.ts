import { Component, HostListener, inject, input, output } from '@angular/core';
import { NavigationHistoryService } from '../../core/services/navigation-history.service';

/**
 * Reusable right-anchored drawer shell.
 *
 * Owns the generic chrome only: backdrop (click-to-close), the 760px slide-in
 * panel, the smart "← Back to {referrer}" button, the ✕ close button, and
 * Esc-to-close. Everything host-specific (what's inside, ↑/↓ navigation,
 * URL ?selected plumbing, toasts) stays in the consuming component and is
 * supplied via three projection slots:
 *
 *   [drawer-title]    — left side of the header (label, id, position hint)
 *   [drawer-controls] — right side, before ✕ (e.g. ↑/↓ nav buttons); optional
 *   (default)         — the drawer body content (e.g. <app-failure-detail>)
 *
 * The host controls visibility via [open] and reacts to (close). Body content
 * lives inside the @if so it mounts/unmounts with the drawer — closing tears
 * down children (and their polling) the same way the inline version did.
 */
@Component({
  selector: 'app-drawer',
  standalone: true,
  template: `
    @if (open()) {
      <div class="drawer-backdrop" (click)="close.emit()" aria-hidden="true"></div>
      <aside class="drawer" role="dialog" aria-modal="true" [attr.aria-label]="ariaLabel()">
        <header class="drawer-header">
          <div class="drawer-title">
            @if (navHistory.previousLabel(); as backLabel) {
              <!-- Smart back button — only when the previous route is a known
                   top-level destination. Location.back() keeps history sane. -->
              <button class="btn btn-ghost btn-sm drawer-back"
                      (click)="navHistory.back()"
                      [title]="'Return to ' + backLabel">← Back to {{ backLabel }}</button>
              <span class="drawer-title-divider" aria-hidden="true">·</span>
            }
            <ng-content select="[drawer-title]"></ng-content>
          </div>
          <div class="drawer-controls">
            <ng-content select="[drawer-controls]"></ng-content>
            <button class="btn btn-ghost btn-sm btn-close" (click)="close.emit()" title="Close (Esc)">✕</button>
          </div>
        </header>
        <div class="drawer-body">
          <ng-content></ng-content>
        </div>
      </aside>
    }
  `,
  styles: [`
    .drawer-backdrop {
      position: fixed; inset: 0;
      background: rgba(15, 23, 42, 0.28);
      z-index: 49;
      animation: backdrop-fade 180ms ease-out;
    }
    @keyframes backdrop-fade { from { opacity: 0; } to { opacity: 1; } }

    .drawer {
      position: fixed;
      top: 0; right: 0;
      width: 760px;
      max-width: 100vw;
      /* Auto-size to content; cap at viewport and let the body scroll. */
      height: auto;
      max-height: 100vh;
      background: var(--surface);
      border-left: 1px solid var(--border);
      box-shadow: -8px 0 24px rgba(15, 23, 42, 0.12);
      z-index: 50;
      display: flex;
      flex-direction: column;
      animation: drawer-slide-in 220ms cubic-bezier(0.16, 1, 0.3, 1);
    }
    @keyframes drawer-slide-in {
      from { transform: translateX(100%); }
      to   { transform: translateX(0); }
    }

    .drawer-header {
      display: flex; align-items: center; justify-content: space-between;
      padding: 12px 16px;
      border-bottom: 1px solid var(--border);
      background: var(--surface);
      flex-shrink: 0;
    }
    .drawer-title { display: flex; align-items: center; gap: 6px; font-size: 14px; flex-wrap: wrap; }
    .drawer-back {
      font-size: 12px;
      color: var(--primary, #6366f1);
      padding: 2px 8px;
      border-radius: 12px;
      &:hover { background: var(--primary-glow, rgba(99, 102, 241, 0.08)); }
    }
    .drawer-title-divider { color: var(--text-muted); margin: 0 2px; }
    .drawer-controls { display: flex; gap: 4px; align-items: center; }
    .drawer-controls .btn-close { font-size: 16px; line-height: 1; }

    .drawer-body {
      flex: 1 1 auto;
      min-height: 0;
      overflow-y: auto;
      padding: 14px 16px;
    }
  `]
})
export class DrawerComponent {
  /** Public so a host's back button / title can reuse it if needed. */
  navHistory = inject(NavigationHistoryService);

  open      = input<boolean>(false);
  ariaLabel = input<string>('Detail');

  close = output<void>();

  /** Esc closes the drawer (generic; host keeps its own arrow-key handling). */
  @HostListener('document:keydown.escape')
  onEscape() {
    if (this.open()) this.close.emit();
  }
}
