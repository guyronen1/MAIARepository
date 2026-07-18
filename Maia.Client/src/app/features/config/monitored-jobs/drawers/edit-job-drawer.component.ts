import { Component, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConfigService, UpsertJobRequest, JobType } from '../../../../core/services/config.service';
import { MonitoredJob } from '../../../../core/models';
import { DrawerComponent } from '../../../../shared/drawer/drawer.component';

/**
 * Edit-job drawer (identity only — scan config lives on sources in Tier 2.5).
 * Extracted from the JobConfigComponent god component. The parent calls
 * {@link open} (via a viewChild ref) and reloads on {@link saved}; the drawer
 * owns its own form state + the updateJob call.
 */
@Component({
  selector: 'app-edit-job-drawer',
  standalone: true,
  imports: [FormsModule, DrawerComponent],
  template: `
    <app-drawer [open]="isOpen()" [ariaLabel]="'Edit job ' + jobName()" (close)="isOpen.set(false)">
      <ng-container drawer-title>Edit Job &nbsp;<span class="drawer-title-sub">{{ jobName() }}</span></ng-container>
      <div class="form-grid">
        <div class="form-group span2">
          <label>Name *</label>
          <input [(ngModel)]="form.name" />
        </div>
        <div class="form-group span2">
          <label>Display Name</label>
          <input [(ngModel)]="form.displayName" placeholder="Optional friendly name" />
        </div>
        <div class="form-group">
          <label>Job Type *</label>
          <select [(ngModel)]="form.jobTypeId">
            <option [ngValue]="0" disabled>Select…</option>
            @for (t of jobTypes(); track t.jobTypeId) { <option [ngValue]="t.jobTypeId">{{ t.name }}</option> }
          </select>
        </div>
        <div class="form-group">
          <label>Poll Interval (seconds)</label>
          <input type="number" [(ngModel)]="form.pollingIntervalSeconds" min="10" />
          <span class="field-hint">Scan cadence at the job level. All sources of this job scan together within each tick, sequentially — sources cannot scan at independent frequencies in this version.</span>
        </div>
        <div class="form-group">
          <label class="toggle-label"><input type="checkbox" [(ngModel)]="form.isActive" /> Active</label>
        </div>
        <div class="form-group span2">
          <label>Description</label>
          <textarea [(ngModel)]="form.description" rows="2" placeholder="Optional notes"></textarea>
        </div>
      </div>
      @if (error()) { <div class="edit-error">⚠ {{ error() }}</div> }
      <div class="drawer-foot">
        <button class="btn btn-ghost" (click)="isOpen.set(false)">Cancel</button>
        <button class="btn btn-primary" (click)="save()" [disabled]="saving()">
          @if (saving()) { <span class="spinner"></span> } Save Changes
        </button>
      </div>
    </app-drawer>
  `,
  styles: [`
    .drawer-title-sub { font-weight: 400; color: var(--text-muted); font-size: 13px; }
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; max-width: 560px; }
    .form-grid .span2 { grid-column: span 2; }
    .form-group { display: flex; flex-direction: column; gap: 4px; }
    .form-group label { font-size: 12px; font-weight: 600; color: var(--text); }
    .form-group input:not([type="checkbox"]), .form-group select, .form-group textarea {
      padding: 7px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); font: inherit; background: var(--surface); color: var(--text);
    }
    .toggle-label { flex-direction: row; align-items: center; gap: 8px; cursor: pointer; }
    .toggle-label input[type="checkbox"] { width: 16px; height: 16px; margin: 0; flex: none; }
    .field-hint { font-size: 11px; color: var(--text-dim); }
    .drawer-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
    .edit-error { margin-top: 10px; padding: 8px 10px; border-radius: var(--radius-sm); background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; font-size: 12px; }
  `],
})
export class EditJobDrawerComponent {
  private svc = inject(ConfigService);

  jobTypes = input<JobType[]>([]);
  jobId    = input.required<number>();
  saved    = output<void>();

  isOpen  = signal(false);
  saving  = signal(false);
  error   = signal<string | null>(null);
  jobName = signal('');
  form: UpsertJobRequest = this.blank();

  open(j: MonitoredJob) {
    this.error.set(null);
    this.jobName.set(j.name);
    const jt = this.jobTypes().find(t => t.name === j.jobTypeName);
    this.form = {
      name: j.name, displayName: j.displayName, jobTypeId: jt?.jobTypeId ?? 0,
      pollingIntervalSeconds: j.pollingIntervalSeconds, isActive: j.isActive,
      description: j.description,
    };
    this.isOpen.set(true);
  }

  save() {
    if (!this.form.name || !this.form.jobTypeId) {
      this.error.set('Name and Job Type are required.');
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.svc.updateJob(this.jobId(), this.form).subscribe({
      next: () => { this.isOpen.set(false); this.saving.set(false); this.saved.emit(); },
      error: e => { this.error.set(e?.error?.message ?? 'Save failed.'); this.saving.set(false); },
    });
  }

  private blank(): UpsertJobRequest {
    return { name: '', displayName: null, jobTypeId: 0,
             pollingIntervalSeconds: 300, isActive: true, description: null };
  }
}
