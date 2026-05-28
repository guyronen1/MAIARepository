import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { TopBarComponent } from '../top-bar/top-bar.component';
import { SideMenuComponent } from '../side-menu/side-menu.component';
import { NavigationHistoryService } from '../../core/services/navigation-history.service';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, TopBarComponent, SideMenuComponent],
  template: `
    <div class="shell">
      <app-top-bar />
      <div class="shell-body">
        <app-side-menu />
        <main class="shell-content">
          <router-outlet />
        </main>
      </div>
    </div>
  `,
  styles: [`
    .shell { display: flex; flex-direction: column; height: 100vh; overflow: hidden; }
    .shell-body { display: flex; flex: 1; overflow: hidden; }
    .shell-content { flex: 1; overflow-y: auto; background: var(--bg); display: flex; justify-content: flex-start; }
  `]
})
export class ShellComponent {
  // Eager-instantiate so history tracking starts at app boot — the service
  // needs to be alive before the first NavigationEnd or the very first
  // referrer is lost.
  private _navHistory = inject(NavigationHistoryService);
}
