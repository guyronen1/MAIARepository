import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';

@Component({
  selector: 'app-change-password',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="auth-wrap">
      <form class="auth-card" (ngSubmit)="submit()">
        <div class="brand">
          <span class="brand-name">Change password</span>
          @if (forced()) {
            <span class="brand-sub">You must set a new password before continuing.</span>
          } @else {
            <span class="brand-sub">Update your account password.</span>
          }
        </div>

        @if (error()) { <div class="auth-error" role="alert">{{ error() }}</div> }

        <label class="field">
          <span>Current password</span>
          <input name="current" type="password" [(ngModel)]="current"
                 autocomplete="current-password" required [disabled]="busy()" />
        </label>
        <label class="field">
          <span>New password</span>
          <input name="next" type="password" [(ngModel)]="next"
                 autocomplete="new-password" required [disabled]="busy()" />
        </label>
        <label class="field">
          <span>Confirm new password</span>
          <input name="confirm" type="password" [(ngModel)]="confirm"
                 autocomplete="new-password" required [disabled]="busy()" />
        </label>

        <button type="submit" class="btn-primary" [disabled]="!canSubmit()">
          {{ busy() ? 'Saving…' : 'Change password' }}
        </button>

        @if (canSkip()) {
          <button type="button" class="btn-skip" [disabled]="busy()" (click)="skip()">
            Skip for now
          </button>
        }
      </form>
    </div>
  `,
  styles: [`
    .auth-wrap { min-height: 100vh; display: flex; align-items: center; justify-content: center; background: var(--bg, #f1f5f9); }
    .auth-card { width: 340px; display: flex; flex-direction: column; gap: 14px;
      background: var(--surface, #fff); border: 1px solid var(--border, #e2e8f0);
      border-radius: 12px; padding: 28px; box-shadow: 0 10px 30px rgba(0,0,0,0.08); }
    .brand { display: flex; flex-direction: column; margin-bottom: 6px; }
    .brand-name { font-size: 20px; font-weight: 800; letter-spacing: -0.02em; color: var(--text, #0f172a); }
    .brand-sub { font-size: 12px; color: var(--text-muted, #64748b); }
    .field { display: flex; flex-direction: column; gap: 4px; font-size: 12px; color: var(--text-muted, #64748b); }
    .field input { padding: 9px 11px; border: 1px solid var(--border, #cbd5e1); border-radius: 8px; font-size: 14px; }
    .btn-primary { margin-top: 6px; padding: 10px; border: none; border-radius: 8px;
      background: var(--primary, #2563eb); color: #fff; font-weight: 600; font-size: 14px; cursor: pointer; }
    .btn-primary:disabled { opacity: 0.6; cursor: default; }
    .btn-skip { padding: 8px; border: none; background: transparent; color: var(--text-muted, #64748b);
      font-size: 13px; cursor: pointer; text-decoration: underline; }
    .btn-skip:disabled { opacity: 0.6; cursor: default; }
    .auth-error { background: #fee2e2; color: #b91c1c; border: 1px solid #fca5a5;
      border-radius: 8px; padding: 8px 10px; font-size: 13px; }
  `],
})
export class ChangePasswordComponent {
  private auth = inject(AuthService);
  private router = inject(Router);

  current = '';
  next = '';
  confirm = '';
  busy = signal(false);
  error = signal<string | null>(null);

  /** True when the server flagged a forced rotation (seeded admin / admin reset). */
  forced = computed(() => this.auth.currentUser()?.mustChangePassword ?? false);

  /** Skip is offered only when the server permits it (Development only) AND a rotation
   *  is actually pending. In prod this is always false → rotation is mandatory. */
  canSkip = computed(() => this.forced() && (this.auth.currentUser()?.canSkipPasswordChange ?? false));

  canSubmit = computed(() => !this.busy());

  submit(): void {
    if (this.busy()) return;
    this.error.set(null);
    if (!this.current || !this.next) { this.error.set('All fields are required.'); return; }
    if (this.next !== this.confirm) { this.error.set('New passwords do not match.'); return; }
    if (this.next === this.current) { this.error.set('New password must differ from the current one.'); return; }

    this.busy.set(true);
    this.auth.changePassword(this.current, this.next).subscribe({
      next: () => { this.busy.set(false); this.router.navigateByUrl('/dashboard'); },
      error: () => { this.busy.set(false); this.error.set('Current password is incorrect.'); },
    });
  }

  /** Skip the one-time prompt — acknowledge without changing, then continue. */
  skip(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.auth.dismissPasswordChange().subscribe({
      next:  () => { this.busy.set(false); this.router.navigateByUrl('/dashboard'); },
      error: () => { this.busy.set(false); this.router.navigateByUrl('/dashboard'); },
    });
  }
}
