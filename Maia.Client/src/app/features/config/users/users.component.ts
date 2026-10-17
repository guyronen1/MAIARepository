import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Observable, of, switchMap } from 'rxjs';
import { AuthService, MaiaRole } from '../../../core/services/auth.service';
import { AppUser, JobAccess, UsersService } from '../../../core/services/users.service';
import { ConfigService } from '../../../core/services/config.service';
import { MonitoredJob } from '../../../core/models/monitored-job.model';

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
            Operators and Users can be limited to the monitored jobs you pick; Administrators see all jobs.
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
                <th style="width:110px">Jobs</th>
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
                    @if (u.role === 'Administrator' || u.allJobs) {
                      <span class="text-muted text-sm" [title]="u.role === 'Administrator' ? 'Administrators always see every job' : 'Sees every monitored job'">All</span>
                    } @else if (u.jobCount === 0) {
                      <span class="badge badge-medium" title="Sees no jobs until at least one is assigned">None</span>
                    } @else {
                      <span class="badge badge-muted">{{ u.jobCount }} {{ u.jobCount === 1 ? 'job' : 'jobs' }}</span>
                    }
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

          <!-- JOB ACCESS (create + edit) -->
          @if (drawer() === 'create' || drawer() === 'edit') {
            <div class="job-access">
              <div class="ja-title">Job access</div>
              @if (drawerRole() === 'Administrator') {
                <span class="field-hint">Administrators always see every job — this setting applies only to Operators and Users.</span>
              }
              <label class="ja-all">
                <input type="checkbox" [checked]="jaAll()" (change)="jaAll.set($any($event.target).checked)"
                       [disabled]="drawerRole() === 'Administrator' || jaLoading()" />
                <span>All jobs <span class="text-muted text-sm">— every monitored job, including jobs added later</span></span>
              </label>

              <div class="ja-list" [class.ja-off]="jaListDisabled()">
                <div class="ja-tools">
                  <input [ngModel]="jobFilter()" (ngModelChange)="jobFilter.set($event)" placeholder="Filter jobs…"
                         [disabled]="jaListDisabled()" />
                  <button type="button" class="btn btn-ghost btn-sm" (click)="selectAllShown()" [disabled]="jaListDisabled()">Select all</button>
                  <button type="button" class="btn btn-ghost btn-sm" (click)="jaIds.set(emptySet())" [disabled]="jaListDisabled()">Clear</button>
                  <span class="text-muted text-sm ja-count">{{ jaIds().size }} selected</span>
                </div>
                @if (jaLoading()) {
                  <div class="ja-empty"><span class="spinner"></span> Loading jobs…</div>
                } @else if (shownJobs().length === 0) {
                  <div class="ja-empty text-muted text-sm">{{ jobs().length === 0 ? 'No monitored jobs yet.' : 'No jobs match.' }}</div>
                } @else {
                  <div class="ja-items">
                    @for (j of shownJobs(); track j.monitoredJobId) {
                      <label class="ja-item">
                        <input type="checkbox" [checked]="jaIds().has(j.monitoredJobId)"
                               (change)="toggleJob(j.monitoredJobId)" [disabled]="jaListDisabled()" />
                        <span dir="auto">{{ j.displayName || j.name }}</span>
                        @if (j.displayName && j.displayName !== j.name) { <span class="text-muted text-sm font-mono" dir="auto">{{ j.name }}</span> }
                        @if (!j.isActive) { <span class="badge badge-muted">Inactive</span> }
                      </label>
                    }
                  </div>
                }
              </div>
              @if (!jaListDisabled() && jaIds().size === 0) {
                <span class="field-hint ja-warn">This user will see no jobs until you tick at least one.</span>
              }
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

    .job-access { margin-top: 20px; padding-top: 16px; border-top: 1px solid var(--border); display: flex; flex-direction: column; gap: 10px; }
    .ja-title { font-weight: 600; font-size: 14px; }
    .ja-all { display: flex; align-items: center; gap: 8px; cursor: pointer; }
    .ja-list { border: 1px solid var(--border); border-radius: var(--radius); overflow: hidden; }
    .ja-list.ja-off { opacity: 0.5; }
    .ja-tools { display: flex; align-items: center; gap: 6px; padding: 8px; border-bottom: 1px solid var(--border); }
    .ja-tools input { flex: 1; min-width: 0; }
    .ja-count { margin-left: auto; white-space: nowrap; }
    .ja-items { max-height: 300px; overflow-y: auto; padding: 4px 0; }
    .ja-item { display: flex; align-items: center; gap: 8px; padding: 5px 10px; cursor: pointer; }
    .ja-item:hover { background: var(--surface-2, rgba(0,0,0,0.03)); }
    .ja-empty { padding: 14px; text-align: center; }
    .ja-warn { color: var(--warning); }
  `]
})
export class UsersComponent implements OnInit {
  private svc = inject(UsersService);
  private config = inject(ConfigService);
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

  // ── Job access (per-user job visibility) ──────────────────────────────────
  /** Every monitored job (active + inactive), loaded once when a drawer first needs it. */
  jobs       = signal<MonitoredJob[]>([]);
  private jobsLoaded = false;
  jaLoading  = signal(false);
  jaAll      = signal(true);
  jaIds      = signal<Set<number>>(new Set());
  jobFilter  = signal('');
  /** Job access as loaded for the user being edited — save skips the PUT when unchanged. */
  private jaOriginal: JobAccess | null = null;

  /** The role the drawer is currently set to (Administrators see every job). */
  drawerRole(): MaiaRole { return this.drawer() === 'create' ? this.cf.role : this.ef.role; }
  jaListDisabled(): boolean { return this.jaAll() || this.drawerRole() === 'Administrator' || this.jaLoading(); }

  shownJobs = computed(() => {
    const q = this.jobFilter().trim().toLowerCase();
    return this.jobs().filter(j => !q
      || j.name.toLowerCase().includes(q)
      || (j.displayName ?? '').toLowerCase().includes(q));
  });

  emptySet(): Set<number> { return new Set(); }

  toggleJob(id: number) {
    const next = new Set(this.jaIds());
    if (next.has(id)) next.delete(id); else next.add(id);
    this.jaIds.set(next);
  }

  /** Adds every job matching the current filter (keeps ticks outside the filter). */
  selectAllShown() {
    const next = new Set(this.jaIds());
    for (const j of this.shownJobs()) next.add(j.monitoredJobId);
    this.jaIds.set(next);
  }

  private ensureJobsLoaded() {
    if (this.jobsLoaded) return;
    this.config.getAllJobs().subscribe({
      next: list => {
        this.jobsLoaded = true;
        this.jobs.set([...list].sort((a, b) =>
          (a.displayName || a.name).localeCompare(b.displayName || b.name, undefined, { sensitivity: 'base' })));
      },
      error: () => this.drawerError.set('Failed to load monitored jobs.'),
    });
  }

  private resetJobAccess(allJobs: boolean, ids: number[]) {
    this.jaAll.set(allJobs);
    this.jaIds.set(new Set(ids));
    this.jobFilter.set('');
  }

  private currentJobAccess(): JobAccess {
    return { allJobs: this.jaAll(), jobIds: [...this.jaIds()].sort((a, b) => a - b) };
  }

  private jobAccessChanged(): boolean {
    const o = this.jaOriginal, c = this.currentJobAccess();
    return !o || o.allJobs !== c.allJobs
      || o.jobIds.length !== c.jobIds.length
      || o.jobIds.some((id, i) => id !== c.jobIds[i]);
  }

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
  openCreate() {
    this.drawerError.set(null); this.cf = this.blankCreate(); this.editing.set(null);
    // New users start scoped with nothing ticked — the admin picks their jobs.
    this.jaOriginal = null; this.resetJobAccess(false, []);
    this.ensureJobsLoaded();
    this.drawer.set('create');
  }
  openEdit(u: AppUser) {
    this.drawerError.set(null); this.editing.set(u); this.ef = { role: u.role, isActive: u.isActive };
    this.ensureJobsLoaded();
    this.jaOriginal = null; this.resetJobAccess(u.allJobs, []);
    this.jaLoading.set(true);
    this.svc.getJobs(u.userId).subscribe({
      next: a => {
        if (this.editing()?.userId !== u.userId) return;   // drawer moved on
        this.jaOriginal = { allJobs: a.allJobs, jobIds: [...a.jobIds].sort((x, y) => x - y) };
        this.resetJobAccess(a.allJobs, a.jobIds);
        this.jaLoading.set(false);
      },
      error: () => { this.jaLoading.set(false); this.drawerError.set('Failed to load job access.'); },
    });
    this.drawer.set('edit');
  }
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
      case 'edit':   return !this.jaLoading();
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
    const access = this.currentJobAccess();
    this.svc.create({
      username: this.cf.username.trim(), password: this.cf.password, role: this.cf.role,
      allJobs: access.allJobs, jobIds: access.jobIds,
    }).subscribe({
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
    const saveJobs$: Observable<void> = this.jobAccessChanged()
      ? this.svc.setJobs(u.userId, this.currentJobAccess())
      : of(undefined);
    this.svc.update(u.userId, { role: this.ef.role, isActive: this.ef.isActive }).pipe(
      switchMap(() => saveJobs$),
    ).subscribe({
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
