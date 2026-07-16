import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { TopBarComponent } from '../top-bar/top-bar.component';
import { SideMenuComponent } from '../side-menu/side-menu.component';
import { NavigationHistoryService } from '../../core/services/navigation-history.service';
import { NotificationService } from '../../core/services/notification.service';
import { CommandPaletteComponent } from '../../shared/command-palette/command-palette.component';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, TopBarComponent, SideMenuComponent, CommandPaletteComponent],
  template: `
    <div class="shell">
      <app-top-bar />
      <div class="shell-body">
        <app-side-menu />
        <main class="shell-content">
          <router-outlet />
        </main>
      </div>

      <!-- Global Ctrl/Cmd+K command palette (always mounted; overlay is
           self-gated on its open signal). -->
      <app-command-palette />

      @if (notices().length) {
        <div class="toast-stack">
          @for (n of notices(); track n.id) {
            <div class="toast" [class.toast-error]="n.kind === 'error'">
              <span class="toast-msg">{{ n.message }}</span>
              <button class="toast-x" type="button" (click)="dismiss(n.id)" aria-label="Dismiss">✕</button>
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: [`
    .shell { display: flex; flex-direction: column; height: 100vh; overflow: hidden; }
    .shell-body { display: flex; flex: 1; overflow: hidden; }
    .shell-content { flex: 1; overflow-y: auto; background: var(--bg); display: flex; justify-content: flex-start; }
    .toast-stack { position: fixed; bottom: 18px; right: 18px; display: flex; flex-direction: column; gap: 8px; z-index: 1000; }
    .toast { display: flex; align-items: center; gap: 12px; max-width: 360px;
      background: var(--surface, #fff); border: 1px solid var(--border, #e2e8f0);
      border-left: 4px solid var(--text-muted, #64748b); border-radius: 8px;
      padding: 10px 12px; box-shadow: 0 6px 20px rgba(0,0,0,0.12); font-size: 13px; }
    .toast-error { border-left-color: #ef4444; }
    .toast-msg { flex: 1; color: var(--text, #0f172a); }
    .toast-x { border: none; background: transparent; cursor: pointer; color: var(--text-muted, #64748b); font-size: 12px; }
  `]
})
export class ShellComponent {
  // Eager-instantiate so history tracking starts at app boot — the service
  // needs to be alive before the first NavigationEnd or the very first
  // referrer is lost.
  private _navHistory = inject(NavigationHistoryService);
  private notifications = inject(NotificationService);

  readonly notices = this.notifications.notices;
  dismiss(id: number): void { this.notifications.dismiss(id); }
}
