import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ConfigService, JobType, UpsertJobRequest } from '../../../core/services/config.service';
import { MonitoredJob } from '../../../core/models';
import { PluralizePipe } from '../../../core/pipes/pluralize.pipe';
import { DrawerComponent } from '../../../shared/drawer/drawer.component';
import { scanTypeLabelFromNames, scanTypeTitleFromSources, scanIconForSources }
  from '../../../core/util/scan-type-label.util';
import { AuthService } from '../../../core/services/auth.service';
import { JobFlowComponent } from './job-flow.component';

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
  imports: [FormsModule, RouterLink, PluralizePipe, DrawerComponent, JobFlowComponent],
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
                  <span class="scan-icon">{{ jobIcon(j) }}</span>
                  <div>
                    <div class="job-name">{{ j.displayName ?? j.name }}</div>
                    <div class="text-muted text-sm" [title]="jobScanTitle(j)">{{ j.name }} · {{ j.jobTypeName }} · {{ jobScanLabel(j) }}</div>
                  </div>
                </div>
                <div class="job-meta">
                  <!-- Tier 2.5 Option 1: scan config is per-source, so the chips come
                       from the job's sources, not the (vestigial) job-level columns. -->
                  @for (s of j.sources; track s.scanSourceId) {
                    @if (s.logFolder) { <span class="meta-chip">📁 {{ s.logFolder }}</span> }
                    @else if (s.connectionName) { <span class="meta-chip">🔌 {{ s.connectionName }}</span> }
                    @else if (s.logSourceUrl) { <span class="meta-chip">🌐 {{ s.logSourceUrl }}</span> }
                  }
                  @if (j.sources.length === 0) { <span class="meta-chip meta-warn">⚠ no sources</span> }
                  <span class="meta-chip">⏱ {{ j.pollingIntervalSeconds }}s</span>
                </div>
                <div class="job-actions">
                  <span class="badge" [class]="j.isActive ? 'badge-resolved' : 'badge-failed'">
                    {{ j.isActive ? 'Active' : 'Inactive' }}
                  </span>
                  @if (canViewFlow()) {
                    <button class="btn btn-ghost btn-sm flow-toggle"
                            [class.active]="isFlowOpen(j.monitoredJobId)"
                            [attr.aria-expanded]="isFlowOpen(j.monitoredJobId)"
                            [title]="isFlowOpen(j.monitoredJobId) ? 'Hide process flow' : 'View process flow (read-only)'"
                            (click)="toggleFlow(j.monitoredJobId)">👁 Flow</button>
                  }
                  <a class="btn btn-primary btn-sm" [routerLink]="['/config/monitored-jobs', j.monitoredJobId]">Configure →</a>
                  <button class="btn btn-ghost btn-sm" (click)="openJobDrawer(j)">Edit</button>
                  <button class="btn btn-danger btn-sm" (click)="deleteJob(j)">Delete</button>
                </div>
              </div>
              @if (canViewFlow() && isFlowOpen(j.monitoredJobId)) {
                <app-job-flow [jobId]="j.monitoredJobId" />
              }
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
            @if (!editingJob()?.monitoredJobId) {
              <div class="form-group span2">
                <span class="field-hint create-hint">A job is just identity. After you create it, you'll land on its Configure screen to add one or more <strong>Scan Sources</strong> (FileSystem / Database / API / FileContent) — that's what the job actually scans.</span>
              </div>
            }
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
    .flow-toggle.active { background: var(--surface-2); color: var(--text); border-color: var(--border); }

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
  private svc    = inject(ConfigService);
  private router = inject(Router);
  private auth   = inject(AuthService);

  loading  = signal(false);
  saving   = signal(false);
  jobs     = signal<MonitoredJob[]>([]);
  jobTypes = signal<JobType[]>([]);

  drawerOpen  = signal(false);
  editingJob  = signal<MonitoredJob | null>(null);
  jobForm: UpsertJobRequest = this.blankJob();

  // ── Read-only flow view (Operator+); Users never see the icon ──────────────
  canViewFlow = computed(() => this.auth.hasAtLeast('Operator'));
  private _flowOpen = signal<Set<number>>(new Set<number>());
  isFlowOpen(jobId: number): boolean { return this._flowOpen().has(jobId); }
  toggleFlow(jobId: number): void {
    const next = new Set(this._flowOpen());
    next.has(jobId) ? next.delete(jobId) : next.add(jobId);
    this._flowOpen.set(next);
  }

  ngOnInit() {
    this.loading.set(true);
    this.svc.getAllJobs().subscribe({
      next: j => { this.jobs.set(j); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
    this.svc.getJobTypes().subscribe({ next: t => this.jobTypes.set(t) });
  }

  // Tier 2.5 Option 1: a job's icon/label come from its SOURCES (shared with the
  // dashboard via scan-type-label.util), not the vestigial job-level ScanType. Fall
  // back to the legacy job field only until the first payload / for sourceless jobs.
  jobIcon(j: MonitoredJob): string {
    return j.sources.length ? scanIconForSources(j.sources) : '📋';
  }
  jobScanLabel(j: MonitoredJob): string {
    return scanTypeLabelFromNames(j.sources.map(s => s.scanTypeName), '');
  }
  jobScanTitle(j: MonitoredJob): string {
    return scanTypeTitleFromSources(j.sources);
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
    if (id) {
      this.svc.updateJob(id, this.jobForm).subscribe({
        next: () => { this.closeDrawer(); this.reload(); },
        error: () => this.saving.set(false),
      });
    } else {
      // Two-step create (Option 1): a bare job has no sources yet and won't scan.
      // Land the operator on its Configure screen, where "Add Source" is the obvious
      // next action.
      this.svc.createJob(this.jobForm).subscribe({
        next: res => { this.closeDrawer(); this.router.navigate(['/config/monitored-jobs', res.monitoredJobId]); },
        error: () => this.saving.set(false),
      });
    }
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
    return { name: '', displayName: null, jobTypeId: 0,
             pollingIntervalSeconds: 300, isActive: true, description: null };
  }
}
