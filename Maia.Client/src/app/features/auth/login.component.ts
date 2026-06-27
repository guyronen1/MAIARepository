import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="auth-wrap">
      <form class="auth-card" (ngSubmit)="submit()" #f="ngForm">
        <div class="brand">
          <span class="brand-name">MAIA</span>
          <span class="brand-sub">Sign in to continue</span>
        </div>

        @if (error()) {
          <div class="auth-error" role="alert">{{ error() }}</div>
        }

        <label class="field">
          <span>Username</span>
          <input name="username" [(ngModel)]="username" autocomplete="username"
                 autofocus required [disabled]="busy()" />
        </label>

        <label class="field">
          <span>Password</span>
          <input name="password" type="password" [(ngModel)]="password"
                 autocomplete="current-password" required [disabled]="busy()" />
        </label>

        <button type="submit" class="btn-primary" [disabled]="busy() || !username || !password">
          {{ busy() ? 'Signing in…' : 'Sign in' }}
        </button>
      </form>
    </div>
  `,
  styles: [`
    .auth-wrap { min-height: 100vh; display: flex; align-items: center; justify-content: center; background: var(--bg, #f1f5f9); }
    .auth-card { width: 320px; display: flex; flex-direction: column; gap: 14px;
      background: var(--surface, #fff); border: 1px solid var(--border, #e2e8f0);
      border-radius: 12px; padding: 28px; box-shadow: 0 10px 30px rgba(0,0,0,0.08); }
    .brand { display: flex; flex-direction: column; margin-bottom: 6px; }
    .brand-name { font-size: 22px; font-weight: 800; letter-spacing: -0.02em; color: var(--primary, #2563eb); }
    .brand-sub { font-size: 12px; color: var(--text-muted, #64748b); }
    .field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--text-muted, #64748b); }
    .field input { padding: 9px 11px; border: 1px solid var(--border, #cbd5e1); border-radius: 8px; font-size: 14px; }
    .btn-primary { margin-top: 6px; padding: 10px; border: none; border-radius: 8px;
      background: var(--primary, #2563eb); color: #fff; font-weight: 600; font-size: 14px; cursor: pointer; }
    .btn-primary:disabled { opacity: 0.6; cursor: default; }
    .auth-error { background: #fee2e2; color: #b91c1c; border: 1px solid #fca5a5;
      border-radius: 8px; padding: 8px 10px; font-size: 13px; }
  `],
})
export class LoginComponent {
  private auth = inject(AuthService);
  private router = inject(Router);
  private route = inject(ActivatedRoute);

  username = '';
  password = '';
  busy = signal(false);
  error = signal<string | null>(null);

  submit(): void {
    if (this.busy() || !this.username || !this.password) return;
    this.busy.set(true);
    this.error.set(null);
    this.auth.login(this.username, this.password).subscribe({
      next: u => {
        this.busy.set(false);
        if (u.mustChangePassword) {
          this.router.navigate(['/change-password']);
          return;
        }
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        this.router.navigateByUrl(returnUrl && !returnUrl.startsWith('/login') ? returnUrl : '/dashboard');
      },
      error: () => {
        this.busy.set(false);
        this.error.set('Invalid username or password.');
      },
    });
  }
}
