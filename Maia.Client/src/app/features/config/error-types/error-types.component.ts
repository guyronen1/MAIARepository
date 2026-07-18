import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import { ConfigService, ErrorType, UpsertErrorTypeRequest } from '../../../core/services/config.service';
import { NotificationService } from '../../../core/services/notification.service';

const SEVERITIES = ['Low', 'Medium', 'High', 'Critical'];

@Component({
  selector: 'app-error-types',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Error Types</h1>
          <p class="text-muted text-sm">
            Catalog of error classifications. Each ErrorType pairs with a JobType in <em>FixPolicyRules</em>
            to define what fix runs and whether it auto-heals.
          </p>
        </div>
        <button class="btn btn-primary" (click)="openDrawer(null)">+ Add Error Type</button>
      </div>

      @if (banner()) {
        <div class="info-banner">{{ banner() }}
          <button class="btn btn-ghost btn-sm" (click)="banner.set(null)">✕</button>
        </div>
      }

      <div class="page-filters">
        <input [(ngModel)]="filterText" placeholder="Filter by code, name…" (input)="applyFilter()" style="min-width:240px" />
        <label class="toggle-inline">
          <input type="checkbox" [(ngModel)]="includeInactive" (ngModelChange)="load()" />
          <span>Include inactive</span>
        </label>
      </div>

      <div class="card" style="padding:0;overflow:hidden">
        @if (loading()) {
          <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
        } @else if (filtered().length === 0) {
          <div class="empty-state">
            <span class="empty-icon">🏷️</span>
            <p>No error types match</p>
          </div>
        } @else {
          <table class="data-table">
            <thead>
              <tr>
                <th style="width:60px">ID</th>
                <th style="width:200px">Code</th>
                <th>Display Name</th>
                <th>Description</th>
                <th style="width:100px">Severity</th>
                <th style="width:80px">Active</th>
                <th style="width:160px"></th>
              </tr>
            </thead>
            <tbody>
              @for (et of filtered(); track et.errorTypeId) {
                <tr [class.row-inactive]="!et.isActive">
                  <td class="text-muted text-sm">#{{ et.errorTypeId }}</td>
                  <td class="font-mono">{{ et.code }}</td>
                  <td>{{ et.displayName }}</td>
                  <td class="text-sm text-muted desc-cell">{{ et.description }}</td>
                  <td><span class="badge" [class]="sevBadge(et.severity)">{{ et.severity }}</span></td>
                  <td>
                    @if (et.isActive) {
                      <span class="badge badge-resolved">Active</span>
                    } @else {
                      <span class="badge badge-muted">Inactive</span>
                    }
                  </td>
                  <td>
                    <div style="display:flex;gap:4px">
                      <button class="btn btn-ghost btn-sm" (click)="openDrawer(et)">Edit</button>
                      <button class="btn btn-danger btn-sm" [disabled]="!et.isActive" (click)="deactivate(et)">Deactivate</button>
                    </div>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    </div>

    @if (drawerOpen()) {
      <div class="drawer-overlay" (click)="closeDrawer()"></div>
      <div class="drawer">
        <div class="drawer-header">
          <h3>{{ editing() ? 'Edit Error Type' : 'New Error Type' }}</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          <div class="form-grid">
            <div class="form-group span2">
              <label>Code *</label>
              <input [(ngModel)]="form.code" placeholder="e.g. FilesCorrupted" />
              <span class="field-hint">Stable machine identifier. Used as the natural key (must be unique). Avoid spaces.</span>
            </div>

            <div class="form-group span2">
              <label>Display Name *</label>
              <input [(ngModel)]="form.displayName" placeholder="e.g. Files: Corrupted Status (5)" />
              <span class="field-hint">Human-readable label shown in the recommendations and config screens.</span>
            </div>

            <div class="form-group span2">
              <label>Description</label>
              <textarea [(ngModel)]="form.description" rows="3"
                        placeholder="What this error means and how to recognise it"></textarea>
            </div>

            <div class="form-group">
              <label>Severity *</label>
              <select [(ngModel)]="form.severity">
                @for (s of severities; track s) {
                  <option [ngValue]="s">{{ s }}</option>
                }
              </select>
            </div>

            <div class="form-group" style="padding-top:14px">
              <label>Active</label>
              <label class="toggle" style="margin-top:6px">
                <input type="checkbox" [(ngModel)]="form.isActive" />
                <span class="slider"></span>
              </label>
            </div>
          </div>
        </div>
        <div class="drawer-footer">
          <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
          <button class="btn btn-primary" (click)="save()" [disabled]="saving() || !valid()">
            @if (saving()) { <span class="spinner"></span> }
            {{ editing() ? 'Save Changes' : 'Add Error Type' }}
          </button>
        </div>
      </div>
    }
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }
    .page-filters { display: flex; gap: 12px; align-items: center; margin: 12px 0; }
    .toggle-inline { display: flex; align-items: center; gap: 6px; font-size: 13px; color: var(--text-muted); }
    .desc-cell { max-width: 380px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .row-inactive td { opacity: 0.55; }
    .info-banner { display:flex; align-items:center; gap:12px; background:var(--info-bg); border:1px solid rgba(56,189,248,0.3); border-radius:var(--radius); padding:10px 16px; font-size:13px; color:var(--info); button { margin-left:auto; } }

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
export class ErrorTypesComponent implements OnInit {
  private svc = inject(ConfigService);
  private notify = inject(NotificationService);

  loading        = signal(false);
  saving         = signal(false);
  banner         = signal<string | null>(null);
  all            = signal<ErrorType[]>([]);
  filtered       = signal<ErrorType[]>([]);
  drawerOpen     = signal(false);
  editing        = signal<ErrorType | null>(null);
  filterText     = '';
  includeInactive = false;

  severities = SEVERITIES;
  form: UpsertErrorTypeRequest = this.blankForm();

  ngOnInit() { this.load(); }

  load() {
    this.loading.set(true);
    this.svc.getErrorTypes(this.includeInactive).subscribe({
      next: list => { this.all.set(list); this.applyFilter(); this.loading.set(false); },
      error: () => { this.loading.set(false); this.notify.error('Could not load error types.'); }
    });
  }

  applyFilter() {
    const q = this.filterText.toLowerCase();
    this.filtered.set(this.all().filter(e =>
      !q || e.code.toLowerCase().includes(q) || e.displayName.toLowerCase().includes(q)
        || (e.description?.toLowerCase().includes(q) ?? false)
    ));
  }

  openDrawer(et: ErrorType | null) {
    this.editing.set(et);
    this.form = et
      ? { code: et.code, displayName: et.displayName, description: et.description ?? null,
          severity: et.severity, isActive: et.isActive ?? true }
      : this.blankForm();
    this.drawerOpen.set(true);
  }

  closeDrawer() {
    this.drawerOpen.set(false);
    this.editing.set(null);
    this.form = this.blankForm();
  }

  valid(): boolean {
    return !!this.form.code?.trim() && !!this.form.displayName?.trim() && !!this.form.severity;
  }

  save() {
    if (!this.valid()) return;
    this.saving.set(true);
    const edit = this.editing();
    const obs: Observable<any> = edit
      ? this.svc.updateErrorType(edit.errorTypeId, this.form)
      : this.svc.createErrorType(this.form);

    obs.subscribe({
      next: () => {
        this.saving.set(false);
        this.banner.set(edit ? `Updated "${this.form.code}"` : `Created "${this.form.code}"`);
        this.closeDrawer();
        this.load();
      },
      error: (err: any) => {
        this.saving.set(false);
        this.banner.set(err?.error?.message ?? 'Save failed. Check the console for details.');
      }
    });
  }

  deactivate(et: ErrorType) {
    if (!confirm(`Deactivate "${et.code}"? Existing rules referencing it stay intact; it just won't appear in new selections.`)) return;
    this.svc.deleteErrorType(et.errorTypeId).subscribe({
      next: () => { this.banner.set(`Deactivated "${et.code}"`); this.load(); },
      error: () => this.banner.set('Deactivate failed.')
    });
  }

  sevBadge(s: string): string {
    switch ((s ?? '').toLowerCase()) {
      case 'critical': return 'badge-failed';
      case 'high':     return 'badge-high';
      case 'medium':   return 'badge-medium';
      case 'low':      return 'badge-low';
      default:         return 'badge-muted';
    }
  }

  private blankForm(): UpsertErrorTypeRequest {
    return { code: '', displayName: '', description: null, severity: 'Medium', isActive: true };
  }
}
