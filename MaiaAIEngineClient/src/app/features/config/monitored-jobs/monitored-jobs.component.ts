import { Component, OnInit, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ConfigService, JobType, UpsertJobRequest } from '../../../core/services/config.service';
import { MonitoredJob } from '../../../core/models';
import { PluralizePipe } from '../../../core/pipes/pluralize.pipe';
import { DrawerComponent } from '../../../shared/drawer/drawer.component';

const SCAN_TYPES = [
  { id: 1, name: 'FileSystem' }, { id: 2, name: 'Database' },
  { id: 3, name: 'ApiEndpoint' }, { id: 4, name: 'FileContent' },
];

/**
 * Tier 2.5 (d2d): the Monitored Jobs LIST. Add / edit / delete a job's identity
 * and (legacy) scan columns via the drawer here; everything operational —
 * scan sources + their rules, classification rules, fix options — lives on the
 * dedicated per-job config screen reached via "Configure" (→ /config/monitored-jobs/:id).
 * The old inline-expand tabs + scan/class/fix drawers were removed once the
 * config screen could fully edit them.
 */
@Component({
  selector: 'app-monitored-jobs',
  standalone: true,
  imports: [FormsModule, RouterLink, PluralizePipe, DrawerComponent],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Monitored Jobs</h1>
          <p class="text-muted text-sm">{{ jobs().length | pluralize:'job' }} configured</p>
        </div>
        <button class="btn btn-primary" (click)="openJobDrawer(null)">+ Add Job</button>
      </div>

      @if (loading()) {
        <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
      } @else if (jobs().length === 0) {
        <div class="card"><div class="empty-state"><span class="empty-icon">📋</span><p>No jobs configured yet</p></div></div>
      } @else {
        <div class="jobs-list">
          @for (j of jobs(); track j.monitoredJobId) {
            <div class="job-card">
              <div class="job-card-header">
                <div class="job-lead">
                  <span class="scan-icon">{{ scanIcon(j.scanTypeId) }}</span>
                  <div>
                    <div class="job-name">{{ j.displayName ?? j.name }}</div>
                    <div class="text-muted text-sm">{{ j.name }} · {{ j.jobTypeName }} · {{ j.scanTypeName }}</div>
                  </div>
                </div>
                <div class="job-meta">
                  @if (j.logFolder) { <span class="meta-chip">📁 {{ j.logFolder }}</span> }
                  @if (j.connectionName) { <span class="meta-chip">🔌 {{ j.connectionName }}</span> }
                  <span class="meta-chip">⏱ {{ j.pollingIntervalSeconds }}s</span>
                </div>
                <div class="job-actions">
                  <span class="badge" [class]="j.isActive ? 'badge-resolved' : 'badge-failed'">
                    {{ j.isActive ? 'Active' : 'Inactive' }}
                  </span>
                  <a class="btn btn-primary btn-sm" [routerLink]="['/config/monitored-jobs', j.monitoredJobId]">Configure →</a>
                  <button class="btn btn-ghost btn-sm" (click)="openJobDrawer(j)">Edit</button>
                  <button class="btn btn-danger btn-sm" (click)="deleteJob(j)">Delete</button>
                </div>
              </div>
            </div>
          }
        </div>
      }
    </div>

    <!-- ── Drawer: Add / Edit Job (shared DrawerComponent — matches the config
             screen's drawers in width/height/behavior) ───────────────────── -->
    <app-drawer [open]="drawerOpen()"
                [ariaLabel]="editingJob()?.monitoredJobId ? 'Edit job' : 'New monitored job'"
                (close)="closeDrawer()">
      <ng-container drawer-title>
        <span class="text-muted text-sm">{{ editingJob()?.monitoredJobId ? 'Edit Job' : 'New Monitored Job' }}</span>
      </ng-container>
      @if (drawerOpen()) {
          <div class="form-grid">
            <div class="form-group">
              <label>Name *</label>
              <input [(ngModel)]="jobForm.name" placeholder="e.g. B2BFilesProcess" />
            </div>
            <div class="form-group">
              <label>Display Name</label>
              <input [(ngModel)]="jobForm.displayName" placeholder="Friendly label" />
            </div>
            <div class="form-group">
              <label>Job Type *</label>
              <select [(ngModel)]="jobForm.jobTypeId">
                <option [ngValue]="0" disabled>Select type…</option>
                @for (t of jobTypes(); track t.jobTypeId) {
                  <option [ngValue]="t.jobTypeId">{{ t.name }}</option>
                }
              </select>
            </div>
            <div class="form-group">
              <label>Scan Type *</label>
              <select [(ngModel)]="jobForm.scanTypeId">
                @for (s of scanTypes; track s.id) {
                  <option [ngValue]="s.id">{{ s.name }}</option>
                }
              </select>
              <span class="field-hint">Sets the job's primary scan type. Add scan sources (incl. mixed types) on the Configure screen.</span>
            </div>

            @if (jobForm.scanTypeId === 1) {
              <div class="form-group span2">
                <label>Log Folder</label>
                <input [(ngModel)]="jobForm.logFolder" placeholder="C:\logs\myapp" />
              </div>
              <div class="form-group span2">
                <label>Search Patterns</label>
                <input [(ngModel)]="jobForm.searchPatterns" placeholder="app*.log, error*.log" />
              </div>
              <div class="form-group span2">
                <label>Input Folder</label>
                <input [(ngModel)]="jobForm.inputFolder" placeholder="C:\input\deposits" />
                <span class="field-hint">Optional. Base for relative input-path captures; absolute captures ignore it.</span>
              </div>
            }
            @if (jobForm.scanTypeId === 2) {
              <div class="form-group span2">
                <label>Connection Name</label>
                <input [(ngModel)]="jobForm.connectionName" placeholder="B2BTest (key from appsettings)" />
              </div>
            }
            @if (jobForm.scanTypeId === 3) {
              <div class="form-group span2">
                <label>API URL</label>
                <input [(ngModel)]="jobForm.logSourceUrl" placeholder="https://api.example.com/health" />
              </div>
            }
            @if (jobForm.scanTypeId === 4) {
              <div class="form-group span2">
                <label>Folder to Scan</label>
                <input [(ngModel)]="jobForm.logFolder" placeholder="C:\incoming\invoices" />
                <span class="field-hint">Folder holding the input data files to inspect (not logs).</span>
              </div>
              <div class="form-group span2" style="flex-direction:row;align-items:center;gap:10px">
                <label class="toggle">
                  <input type="checkbox" [(ngModel)]="jobForm.includeSubfolders" />
                  <span class="slider"></span>
                </label>
                <span class="text-sm">Include subfolders (scan recursively)</span>
              </div>
            }

            <div class="form-group">
              <label>Poll Interval (seconds)</label>
              <input type="number" [(ngModel)]="jobForm.pollingIntervalSeconds" min="10" />
            </div>
            <div class="form-group" style="justify-content:flex-end;padding-top:18px">
              <label class="toggle-label">
                <label class="toggle">
                  <input type="checkbox" [(ngModel)]="jobForm.isActive" />
                  <span class="slider"></span>
                </label>
                <span class="text-sm">Active</span>
              </label>
            </div>
            <div class="form-group span2">
              <label>Description</label>
              <textarea [(ngModel)]="jobForm.description" rows="2" placeholder="Optional notes"></textarea>
            </div>
          </div>
          <div class="drawer-foot">
            <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
            <button class="btn btn-primary" (click)="saveJob()" [disabled]="saving()">
              @if (saving()) { <span class="spinner"></span> }
              {{ editingJob()?.monitoredJobId ? 'Save Changes' : 'Create Job' }}
            </button>
          </div>
      }
    </app-drawer>
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }

    .jobs-list  { display: flex; flex-direction: column; gap: 10px; }
    .job-card   { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); overflow: hidden; }
    .job-card-header {
      display: flex; align-items: center; justify-content: space-between;
      padding: 14px 16px; gap: 12px; flex-wrap: wrap;
    }
    .job-lead    { display: flex; align-items: center; gap: 12px; flex: 1; min-width: 0; }
    .scan-icon   { font-size: 20px; flex-shrink: 0; }
    .job-name    { font-size: 14px; font-weight: 600; }
    .job-meta    { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
    .meta-chip   {
      background: var(--surface-2); border: 1px solid var(--border-light);
      border-radius: 4px; padding: 2px 8px; font-size: 11px; color: var(--text-muted);
      font-family: 'Consolas', monospace; max-width: 200px;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    .job-actions { display: flex; align-items: center; gap: 6px; flex-shrink: 0; }

    /* Drawer form (shell chrome comes from the shared DrawerComponent; the
       form here mirrors the config screen's drawers — same 560px form column,
       same footer — so every job/source/rule drawer looks identical). */
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; max-width: 560px; .span2 { grid-column: span 2; } }
    .form-group { display: flex; flex-direction: column; gap: 4px; }
    .form-group label { font-size: 12px; font-weight: 600; color: var(--text); }
    .form-group input:not([type="checkbox"]), .form-group select, .form-group textarea {
      padding: 7px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); font: inherit; background: var(--surface); color: var(--text);
    }
    .toggle-label { display: flex; align-items: center; gap: 8px; cursor: pointer; }
    .field-hint   { font-size: 11px; color: var(--text-dim); margin-top: 2px; }
    .drawer-foot  { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
  `]
})
export class MonitoredJobsComponent implements OnInit {
  private svc = inject(ConfigService);

  loading  = signal(false);
  saving   = signal(false);
  jobs     = signal<MonitoredJob[]>([]);
  jobTypes = signal<JobType[]>([]);

  readonly scanTypes = SCAN_TYPES;
  drawerOpen  = signal(false);
  editingJob  = signal<MonitoredJob | null>(null);
  jobForm: UpsertJobRequest = this.blankJob();

  ngOnInit() {
    this.loading.set(true);
    this.svc.getAllJobs().subscribe({
      next: j => { this.jobs.set(j); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
    this.svc.getJobTypes().subscribe({ next: t => this.jobTypes.set(t) });
  }

  scanIcon(id: number): string {
    return ({ 1: '📁', 2: '🗄', 3: '🌐', 4: '📦' } as Record<number, string>)[id] ?? '📋';
  }

  getJobTypeId(job: MonitoredJob): number {
    return this.jobTypes().find(t => t.name === job.jobTypeName)?.jobTypeId ?? 0;
  }

  openJobDrawer(job: MonitoredJob | null) {
    this.editingJob.set(job);
    if (job) {
      const jt = this.jobTypes().find(t => t.name === job.jobTypeName);
      this.jobForm = {
        name: job.name, displayName: job.displayName, jobTypeId: jt?.jobTypeId ?? 0,
        scanTypeId: job.scanTypeId, logFolder: job.logFolder, searchPatterns: job.searchPatterns,
        inputFolder: job.inputFolder, includeSubfolders: job.includeSubfolders,
        connectionName: job.connectionName, logSourceUrl: job.logSourceUrl,
        pollingIntervalSeconds: job.pollingIntervalSeconds, isActive: job.isActive,
        description: job.description,
      };
    } else {
      this.jobForm = this.blankJob();
    }
    this.drawerOpen.set(true);
  }

  saveJob() {
    if (!this.jobForm.name || !this.jobForm.jobTypeId) return;
    this.saving.set(true);
    const id = this.editingJob()?.monitoredJobId;
    const req$: Observable<unknown> = id ? this.svc.updateJob(id, this.jobForm) : this.svc.createJob(this.jobForm);
    req$.subscribe({
      next: () => { this.closeDrawer(); this.reload(); },
      error: () => this.saving.set(false),
    });
  }

  deleteJob(job: MonitoredJob) {
    // Soft-delete disclosure: surface active fix-override count BEFORE confirm,
    // so the operator knows overrides go dormant with the job (and reactivate
    // if the job is reactivated later).
    const jobTypeId = this.getJobTypeId(job);
    const bareDelete = () => {
      if (!confirm(`Deactivate job "${job.name}"?`)) return;
      this.svc.deleteJob(job.monitoredJobId).subscribe({ next: () => this.reload() });
    };
    if (!jobTypeId) { bareDelete(); return; }
    this.svc.getFixPolicyRules(jobTypeId, job.monitoredJobId).subscribe({
      next: rules => {
        const overrides = rules.filter(r => r.monitoredJobId === job.monitoredJobId && r.enabled);
        const suffix = overrides.length > 0
          ? `\n\nThis job has ${overrides.length} active fix override(s). They'll become inactive with the job and reactivate if you reactivate it later.`
          : '';
        if (!confirm(`Deactivate job "${job.name}"?${suffix}`)) return;
        this.svc.deleteJob(job.monitoredJobId).subscribe({ next: () => this.reload() });
      },
      error: () => bareDelete(),
    });
  }

  closeDrawer() { this.drawerOpen.set(false); this.saving.set(false); }

  private reload() {
    this.svc.getAllJobs().subscribe({ next: j => { this.jobs.set(j); this.saving.set(false); } });
  }

  private blankJob(): UpsertJobRequest {
    return { name: '', displayName: null, jobTypeId: 0, scanTypeId: 1, logFolder: null,
             searchPatterns: null, inputFolder: null, includeSubfolders: false,
             connectionName: null, logSourceUrl: null,
             pollingIntervalSeconds: 300, isActive: true, description: null };
  }
}
