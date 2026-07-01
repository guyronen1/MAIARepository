import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService, MaiaRole } from '../../../core/services/auth.service';
import { AppUser, UsersService } from '../../../core/services/users.service';

const ROLES: MaiaRole[] = ['User', 'Operator', 'Administrator'];
const MIN_PASSWORD = 8;   // mirror the server-side PasswordPolicy floor

type DrawerMode = 'create' | 'edit' | 'reset';

@Component({
  selector: 'app-users',
  standalone: true,
  imports: [FormsModule, DatePipe],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Users</h1>
          <p class="text-muted text-sm">
            Local accounts and roles. New and reset accounts get a temporary password and
            must change it on first login. Roles are fixed (User · Operator · Administrator).
          </p>
        </div>
        <button class="btn btn-primary" (click)="openCreate()">+ Add User</button>
      </div>

      @if (banner()) {
        <div class="info-banner">{{ banner() }}
          <button class="btn btn-ghost btn-sm" (click)="banner.set(null)">✕</button>
        </div>
      }

      <div class="page-filters">
        <input [(ngModel)]="filterText" placeholder="Filter by username…" (input)="applyFilter()" style="min-width:240px" />
      </div>

      <div class="card" style="padding:0;overflow:hidden">
        @if (loading()) {
          <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
        } @else if (filtered().length === 0) {
          <div class="empty-state"><span class="empty-icon">👤</span><p>No users match</p></div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th>Username</th>
                <th style="width:130px">Role</th>
                <th style="width:90px">Status</th>
                <th style="width:120px">Must change</th>
                <th style="width:140px">Created</th>
                <th style="width:150px">Last login</th>
                <th style="width:240px"></th>
              </tr>
            </thead>
            <tbody>
              @for (u of filtered(); track u.userId) {
                <tr [class.row-inactive]="!u.isActive">
                  <td class="font-mono" dir="auto">
                    {{ u.username }}
                    @if (isSelf(u)) { <span class="self-tag">you</span> }
                  </td>
                  <td><span class="badge" [class]="roleBadge(u.role)">{{ u.role }}</span></td>
                  <td>
                    @if (u.isActive) { <span class="badge badge-resolved">Active</span> }
                    @else { <span class="badge badge-muted">Inactive</span> }
                  </td>
                  <td>
                    @if (u.mustChangePassword) { <span class="badge badge-medium">Pending</span> }
                    @else { <span class="text-muted">–</span> }
                  </td>
                  <td class="text-sm text-muted">{{ u.createdAt | date:'dd/MM/yy HH:mm' }}</td>
                  <td class="text-sm text-muted">
                    {{ u.lastLoginAt ? (u.lastLoginAt | date:'dd/MM/yy HH:mm') : 'Never' }}
                  </td>
                  <td>
                    <div style="display:flex;gap:4px">
                      <button class="btn btn-ghost btn-sm" (click)="openEdit(u)">Edit</button>
                      <button class="btn btn-ghost btn-sm" (click)="openReset(u)">Reset password</button>
                      @if (u.isActive) {
                        <button class="btn btn-danger btn-sm"
                                [disabled]="isLastActiveAdmin(u)"
                                [title]="isLastActiveAdmin(u) ? 'Cannot deactivate the last active administrator' : ''"
                                (click)="setActive(u, false)">Deactivate</button>
                      } @else {
                        <button class="btn btn-ghost btn-sm" (click)="setActive(u, true)">Reactivate</button>
                      }
                    </div>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    </div>

    @if (drawer()) {
      <div class="drawer-overlay" (click)="closeDrawer()"></div>
      <div class="drawer">
        <div class="drawer-header">
          <h3>{{ drawerTitle() }}</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          @if (drawerError()) { <div class="dup-warn">{{ drawerError() }}</div> }

          <!-- CREATE -->
          @if (drawer() === 'create') {
            <div class="form-grid">
              <div class="form-group span2">
                <label>Username *</label>
                <input [(ngModel)]="cf.username" dir="auto" placeholder="e.g. alice" />
                <span class="field-hint">Unique, case-insensitive. Immutable after creation (it's the audit actor).</span>
              </div>
              <div class="form-group">
                <label>Role *</label>
                <select [(ngModel)]="cf.role">
                  @for (r of roles; track r) { <option [ngValue]="r">{{ r }}</option> }
                </select>
              </div>
              <div class="form-group">
                <label>Temp password *</label>
                <input [(ngModel)]="cf.password" type="text" autocomplete="off" placeholder="min 8 chars" />
                <span class="field-hint">Hand off out-of-band; they must change it on first login.</span>
              </div>
            </div>
          }

          <!-- EDIT (role + active only; username immutable) -->
          @if (drawer() === 'edit') {
            <div class="form-grid">
              <div class="form-group span2">
                <label>Username</label>
                <input [value]="editing()?.username" dir="auto" readonly />
                <span class="field-hint">Immutable — keeps the audit trail's actor stable.</span>
              </div>
              <div class="form-group">
                <label>Role *</label>
                <select [(ngModel)]="ef.role">
                  @for (r of roles; track r) { <option [ngValue]="r">{{ r }}</option> }
                </select>
              </div>
              <div class="form-group" style="padding-top:14px">
                <label>Active</label>
                <label class="toggle" style="margin-top:6px">
                  <input type="checkbox" [(ngModel)]="ef.isActive" />
                  <span class="slider"></span>
                </label>
              </div>
            </div>
          }

          <!-- RESET PASSWORD -->
          @if (drawer() === 'reset') {
            <div class="form-grid">
              <div class="form-group span2">
                <label>New temp password for <strong dir="auto">{{ editing()?.username }}</strong> *</label>
                <input [(ngModel)]="rf.newPassword" type="text" autocomplete="off" placeholder="min 8 chars" />
                <span class="field-hint">Sets a temp password and forces a change on next login.</span>
              </div>
            </div>
          }
        </div>
        <div class="drawer-footer">
          <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
          <button class="btn btn-primary" (click)="save()" [disabled]="saving() || !valid()">
            @if (saving()) { <span class="spinner"></span> }
            {{ saveLabel() }}
          </button>
        </div>
      </div>
    }
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }
    .page-filters { display: flex; gap: 12px; align-items: center; margin: 12px 0; }
    .row-inactive td { opacity: 0.55; }
    .self-tag { margin-left: 6px; font-size: 10px; font-weight: 700; text-transform: uppercase;
      color: var(--primary); background: var(--primary-light); border-radius: 8px; padding: 1px 6px; }
    .info-banner { display:flex; align-items:center; gap:12px; background:var(--info-bg); border:1px solid rgba(56,189,248,0.3); border-radius:var(--radius); padding:10px 16px; font-size:13px; color:var(--info); button { margin-left:auto; } }
    .dup-warn { background:#fee2e2; color:#b91c1c; border:1px solid #fca5a5; border-radius:8px; padding:8px 10px; font-size:13px; margin-bottom:14px; }

    .drawer-overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.25); z-index: 200; }
    .drawer {
      position: fixed; top: 0; right: 0; height: 100vh; width: 500px;
      background: var(--surface); border-left: 1px solid var(--border);
      box-shadow: -4px 0 24px rgba(0,0,0,0.12); z-index: 201;
      display: flex; flex-direction: column; animation: slideIn 0.2s ease;
    }
    @keyframes slideIn { from { transform: translateX(100%); } to { transform: translateX(0); } }
    .drawer-header { display: flex; justify-content: space-between; align-items: center; padding: 16px 20px; border-bottom: 1px solid var(--border); }
    .drawer-body { flex: 1; padding: 20px; overflow-y: auto; }
    .drawer-footer { padding: 14px 20px; border-top: 1px solid var(--border); display: flex; gap: 8px; justify-content: flex-end; }
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
    .form-group.span2 { grid-column: 1 / -1; }
  `]
})
export class UsersComponent implements OnInit {
  private svc = inject(UsersService);
  private auth = inject(AuthService);
  private router = inject(Router);

  loading    = signal(false);
  saving     = signal(false);
  banner     = signal<string | null>(null);
  drawerError = signal<string | null>(null);
  all        = signal<AppUser[]>([]);
  filtered   = signal<AppUser[]>([]);
  drawer     = signal<DrawerMode | null>(null);
  editing    = signal<AppUser | null>(null);
  filterText = '';

  roles = ROLES;
  cf = this.blankCreate();
  ef: { role: MaiaRole; isActive: boolean } = { role: 'User', isActive: true };
  rf = { newPassword: '' };

  /** Number of active administrators — drives the last-admin guard. */
  private activeAdminCount = computed(() =>
    this.all().filter(u => u.role === 'Administrator' && u.isActive).length);

  ngOnInit() { this.load(); }

  load() {
    this.loading.set(true);
    this.svc.list().subscribe({
      next: list => { this.all.set(list); this.applyFilter(); this.loading.set(false); },
      error: () => { this.loading.set(false); this.banner.set('Failed to load users.'); },
    });
  }

  applyFilter() {
    const q = this.filterText.toLowerCase();
    this.filtered.set(this.all().filter(u => !q || u.username.toLowerCase().includes(q)));
  }

  isSelf(u: AppUser): boolean { return u.username === this.auth.currentUser()?.username; }

  isLastActiveAdmin(u: AppUser): boolean {
    return u.role === 'Administrator' && u.isActive && this.activeAdminCount() === 1;
  }

  // ── Drawer open/close ─────────────────────────────────────────────────────
  openCreate() { this.drawerError.set(null); this.cf = this.blankCreate(); this.editing.set(null); this.drawer.set('create'); }
  openEdit(u: AppUser) { this.drawerError.set(null); this.editing.set(u); this.ef = { role: u.role, isActive: u.isActive }; this.drawer.set('edit'); }
  openReset(u: AppUser) { this.drawerError.set(null); this.editing.set(u); this.rf = { newPassword: '' }; this.drawer.set('reset'); }
  closeDrawer() { this.drawer.set(null); this.editing.set(null); this.drawerError.set(null); }

  drawerTitle = computed(() => {
    switch (this.drawer()) {
      case 'create': return 'New User';
      case 'edit':   return 'Edit User';
      case 'reset':  return 'Reset Password';
      default:       return '';
    }
  });
  saveLabel = computed(() => {
    switch (this.drawer()) {
      case 'create': return 'Add User';
      case 'reset':  return 'Reset Password';
      default:       return 'Save Changes';
    }
  });

  valid(): boolean {
    switch (this.drawer()) {
      case 'create': return !!this.cf.username.trim() && this.cf.password.length >= MIN_PASSWORD;
      case 'edit':   return true;
      case 'reset':  return this.rf.newPassword.length >= MIN_PASSWORD;
      default:       return false;
    }
  }

  save() {
    if (!this.valid() || this.saving()) return;
    switch (this.drawer()) {
      case 'create': return this.saveCreate();
      case 'edit':   return this.saveEdit();
      case 'reset':  return this.saveReset();
    }
  }

  private saveCreate() {
    this.saving.set(true); this.drawerError.set(null);
    this.svc.create({ username: this.cf.username.trim(), password: this.cf.password, role: this.cf.role }).subscribe({
      next: () => {
        this.saving.set(false);
        this.banner.set(`Created "${this.cf.username.trim()}". Hand off the temp password — they must change it on first login.`);
        this.closeDrawer(); this.load();
      },
      error: (err: any) => {
        this.saving.set(false);
        this.drawerError.set(err?.error?.message ?? 'Create failed.');
      },
    });
  }

  private saveEdit() {
    const u = this.editing();
    if (!u) return;
    const losingAdmin = this.isSelf(u) && (this.ef.role !== 'Administrator' || !this.ef.isActive);
    if (losingAdmin && !confirm('This changes your own access — you will lose administrator rights and be redirected. Continue?'))
      return;

    this.saving.set(true); this.drawerError.set(null);
    this.svc.update(u.userId, { role: this.ef.role, isActive: this.ef.isActive }).subscribe({
      next: () => {
        this.saving.set(false);
        this.banner.set(`Updated "${u.username}".`);
        this.closeDrawer();
        if (losingAdmin) { this.handleSelfDowngrade(this.ef.isActive); return; }
        this.load();
      },
      error: (err: any) => {
        this.saving.set(false);
        this.drawerError.set(err?.error?.message ?? 'Update failed.');
      },
    });
  }

  private saveReset() {
    const u = this.editing();
    if (!u) return;
    this.saving.set(true); this.drawerError.set(null);
    this.svc.resetPassword(u.userId, { newPassword: this.rf.newPassword }).subscribe({
      next: () => {
        this.saving.set(false);
        this.banner.set(`Password reset for "${u.username}". Hand off the temp — they must change it on next login.`);
        this.closeDrawer(); this.load();
      },
      error: (err: any) => {
        this.saving.set(false);
        this.drawerError.set(err?.error?.message ?? 'Reset failed.');
      },
    });
  }

  setActive(u: AppUser, active: boolean) {
    if (!active) {
      if (this.isLastActiveAdmin(u)) return;   // guarded in the UI; server enforces too
      const selfNote = this.isSelf(u) ? ' This is YOUR account — you will be signed out.' : '';
      if (!confirm(`Deactivate "${u.username}"?${selfNote}`)) return;
    }
    this.svc.update(u.userId, { role: u.role, isActive: active }).subscribe({
      next: () => {
        this.banner.set(`${active ? 'Reactivated' : 'Deactivated'} "${u.username}".`);
        if (!active && this.isSelf(u)) { this.handleSelfDowngrade(false); return; }
        this.load();
      },
      error: (err: any) => this.banner.set(err?.error?.message ?? 'Update failed.'),
    });
  }

  /** Self lost admin or got deactivated — role/active is looked up live, so it takes
   *  effect on the next request. Deactivated → bounce to login; demoted → dashboard. */
  private handleSelfDowngrade(stillActive: boolean) {
    if (!stillActive) { this.auth.clear(); this.router.navigate(['/login']); return; }
    this.auth.refresh().subscribe(() => this.router.navigate(['/dashboard']));
  }

  roleBadge(r: MaiaRole): string {
    switch (r) {
      case 'Administrator': return 'badge-failed';
      case 'Operator':      return 'badge-medium';
      default:              return 'badge-muted';
    }
  }

  private blankCreate(): { username: string; role: MaiaRole; password: string } {
    return { username: '', role: 'User', password: '' };
  }
}
