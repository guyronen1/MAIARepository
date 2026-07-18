import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import {
  ConfigService, ClassificationRule, JobType, ErrorType,
  UpsertClassificationRuleRequest,
} from '../../../core/services/config.service';
import { NotificationService } from '../../../core/services/notification.service';
import { PluralizePipe } from '../../../core/pipes/pluralize.pipe';

@Component({
  selector: 'app-classification-rules',
  standalone: true,
  imports: [FormsModule, PluralizePipe],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Classification Rules</h1>
          <p class="text-muted text-sm">
            Substring patterns (with optional <code>*</code> wildcards) that map error messages to job type + error type combinations
          </p>
        </div>
        <button class="btn btn-primary" (click)="openDrawer(null)">+ Add Rule</button>
      </div>

      @if (deleteError()) {
        <div style="margin-bottom:12px;padding:8px 12px;border-radius:6px;background:#fef2f2;border:1px solid #fecaca;color:#991b1b;font-size:13px;display:flex;align-items:center;gap:8px">
          ⚠ {{ deleteError() }}
          <button class="btn btn-ghost btn-sm" style="margin-left:auto" (click)="deleteError.set(null)">✕</button>
        </div>
      }

      <!-- Filter bar -->
      <div class="filter-bar">
        <div class="form-group">
          <label>Search pattern</label>
          <input [(ngModel)]="filterText" placeholder="Filter by pattern…" (ngModelChange)="applyFilter()" />
        </div>
        <div class="form-group">
          <label>Job Type</label>
          <select [(ngModel)]="filterJobType" (ngModelChange)="applyFilter()">
            <option value="">All job types</option>
            @for (t of jobTypes(); track t.jobTypeId) {
              <option [value]="t.name">{{ t.name }}</option>
            }
          </select>
        </div>
        <div class="form-group">
          <label>Error Type</label>
          <select [(ngModel)]="filterErrorType" (ngModelChange)="applyFilter()">
            <option value="">All error types</option>
            @for (e of errorTypes(); track e.errorTypeId) {
              <option [value]="e.code">{{ e.code }}</option>
            }
          </select>
        </div>
        <div class="form-group" style="justify-content:flex-end;padding-top:16px">
          <button class="btn btn-ghost btn-sm" (click)="clearFilters()">Clear</button>
        </div>
      </div>

      <!-- Rules table -->
      @if (loading()) {
        <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
      } @else if (filtered().length === 0) {
        <div class="card">
          <div class="empty-state">
            <span class="empty-icon">🏷️</span>
            <p>{{ rules().length === 0 ? 'No classification rules configured yet' : 'No rules match the current filter' }}</p>
            @if (rules().length === 0) {
              <button class="btn btn-primary btn-sm" (click)="openDrawer(null)">Add first rule</button>
            }
          </div>
        </div>
      } @else {
        <div class="card" style="padding:0;overflow:hidden">
          <div class="table-header">
            <span class="text-muted text-sm">{{ filtered().length | pluralize:'rule' }}</span>
          </div>
          <div class="table-wrap">
          <table class="data-table compact">
            <thead>
              <tr>
                <th>Pattern</th>
                <th>Job Type</th>
                <th>Scope</th>
                <th>Error Type</th>
                <th>Confidence</th>
                <th>Priority</th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (r of filtered(); track r.ruleId) {
                <tr>
                  <td class="font-mono pattern-cell">{{ r.pattern }}</td>
                  <td>
                    <span class="badge badge-info">{{ r.jobTypeName }}</span>
                  </td>
                  <td>
                    @if (r.linkedJobNames.length === 0) {
                      <span class="badge badge-muted" title="JobType default — applies to every {{ r.jobTypeName }} job">
                        Default · all {{ r.jobTypeName }}
                      </span>
                    } @else {
                      <span class="badge badge-resolved" [title]="'Linked to: ' + r.linkedJobNames.join(', ')">
                        Linked · {{ r.linkedJobNames.join(', ') }}
                      </span>
                    }
                  </td>
                  <td>
                    <span class="badge badge-classified">{{ r.errorTypeCode }}</span>
                  </td>
                  <td>
                    <div class="confidence-bar">
                      <div class="bar-track">
                        <div class="bar-fill" [style.width.%]="r.confidence * 100"></div>
                      </div>
                      <span class="bar-value">{{ (r.confidence * 100).toFixed(0) }}%</span>
                    </div>
                  </td>
                  <td class="text-muted text-sm">#{{ r.priority }}</td>
                  <td>
                    <span class="badge" [class]="r.isActive ? 'badge-resolved' : 'badge-failed'">
                      {{ r.isActive ? 'Active' : 'Inactive' }}
                    </span>
                  </td>
                  <td>
                    <div style="display:flex;gap:4px">
                      <button class="btn btn-ghost btn-sm" (click)="openDrawer(r)">Edit</button>
                      <button class="btn btn-danger btn-sm" (click)="deleteRule(r)">Delete</button>
                    </div>
                  </td>
                </tr>
              }
            </tbody>
          </table>
          </div>
        </div>
      }
    </div>

    <!-- ── Drawer: Add / Edit Classification Rule ──────────────────────────── -->
    @if (drawerOpen()) {
      <div class="drawer-overlay" (click)="closeDrawer()"></div>
      <div class="drawer">
        <div class="drawer-header">
          <h3>{{ editing() ? 'Edit Classification Rule' : 'New Classification Rule' }}</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          <div class="form-grid">

            <div class="form-group span2">
              <label>Match Pattern *</label>
              <input [(ngModel)]="form.pattern" (ngModelChange)="checkDup()"
                     placeholder="e.g. FileNotFoundException  or  Error code * occurred" />
              <span class="field-hint">Case-insensitive substring of the error message. Use <code>*</code> as a wildcard for any text (e.g. <code>Login failed for user *</code>). Other regex characters are matched literally.</span>
              @if (liveDupRuleId(); as dupId) {
                <div class="dup-warn">
                  ⚠ An enabled rule with this pattern already exists for this job type.
                  <button type="button" class="link-btn" (click)="openExisting(dupId)">Open existing rule</button>
                </div>
              }
            </div>

            <div class="form-group">
              <label>Job Type *</label>
              <select [(ngModel)]="form.jobTypeId" (ngModelChange)="checkDup()">
                <option [ngValue]="0" disabled>Select job type…</option>
                @for (t of jobTypes(); track t.jobTypeId) {
                  <option [ngValue]="t.jobTypeId">{{ t.name }}</option>
                }
              </select>
            </div>

            <div class="form-group">
              <label>Error Type *</label>
              <select [(ngModel)]="form.errorTypeId">
                <option [ngValue]="0" disabled>Select error type…</option>
                @for (e of errorTypes(); track e.errorTypeId) {
                  <option [ngValue]="e.errorTypeId">{{ e.code }} — {{ e.displayName }}</option>
                }
              </select>
            </div>

            <div class="form-group">
              <label>Confidence (0 – 1)</label>
              <input type="number" [(ngModel)]="form.confidence" min="0" max="1" step="0.05" />
              <span class="field-hint">How certain this pattern identifies the error type.</span>
            </div>

            <div class="form-group">
              <label>Priority</label>
              <input type="number" [(ngModel)]="form.priority" min="1" />
              <span class="field-hint">Lower = evaluated first when multiple rules match.</span>
            </div>

            <div class="form-group" style="padding-top:14px">
              <label>Active</label>
              <label class="toggle" style="margin-top:6px">
                <input type="checkbox" [(ngModel)]="form.isActive" />
                <span class="slider"></span>
              </label>
            </div>

          </div>

          <!-- Preview -->
          @if (form.pattern && form.jobTypeId && form.errorTypeId) {
            <div class="preview-box">
              <span class="preview-label">Preview</span>
              <span class="font-mono">{{ form.pattern }}</span>
              <span class="preview-arrow">→</span>
              <span class="badge badge-info">{{ jobTypeName(form.jobTypeId) }}</span>
              <span class="badge badge-classified">{{ errorTypeCode(form.errorTypeId) }}</span>
              <span class="text-muted text-sm">({{ (form.confidence * 100).toFixed(0) }}% confidence, priority #{{ form.priority }})</span>
            </div>
          }
        </div>
        @if (saveConflict(); as c) {
          <div class="dup-warn save-error">
            ⚠ {{ c.message }}
            <button type="button" class="link-btn" (click)="openExisting(c.conflictingRuleId)">Open existing rule</button>
          </div>
        }
        <div class="drawer-footer">
          <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
          <button class="btn btn-primary" (click)="save()" [disabled]="saving()">
            @if (saving()) { <span class="spinner"></span> }
            {{ editing() ? 'Save Changes' : 'Add Rule' }}
          </button>
        </div>
      </div>
    }
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }

    .table-header {
      padding: 10px 16px 8px;
      border-bottom: 1px solid var(--border-light);
      background: var(--surface-2);
    }

    .pattern-cell {
      max-width: 320px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    /* Horizontal-scroll safety net — table never clips its last column. */
    .table-wrap { overflow-x: auto; }

    /* Compact density so all 8 columns fit on a 14" laptop. */
    .data-table.compact th,
    .data-table.compact td { padding: 7px 10px; }
    .data-table.compact .badge { font-size: 11px; padding: 1px 7px; }
    .data-table.compact .pattern-cell { max-width: 240px; }
    .data-table.compact .confidence-bar .bar-track { width: 48px; }
    .data-table.compact td > div[style*="display:flex"] .btn { padding: 3px 8px; }

    /* Responsive: drop the two least-essential columns as width shrinks.
       Columns: 1 Pattern · 2 Job Type · 3 Scope · 4 Error Type ·
                5 Confidence · 6 Priority · 7 Status · 8 Actions */
    @media (max-width: 1500px) {
      .data-table.compact th:nth-child(5),
      .data-table.compact td:nth-child(5) { display: none; }  /* Confidence */
    }
    @media (max-width: 1280px) {
      .data-table.compact th:nth-child(6),
      .data-table.compact td:nth-child(6) { display: none; }  /* Priority */
    }

    /* Drawer */
    .drawer-overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.25); z-index: 200; }
    .drawer {
      position: fixed; top: 0; right: 0; height: 100vh; width: 500px;
      background: var(--surface); border-left: 1px solid var(--border);
      box-shadow: -4px 0 24px rgba(0,0,0,0.12); z-index: 201;
      display: flex; flex-direction: column; animation: slideIn 0.2s ease;
    }
    @keyframes slideIn { from { transform: translateX(100%); } to { transform: translateX(0); } }
    .drawer-header {
      display: flex; align-items: center; justify-content: space-between;
      padding: 16px 20px; border-bottom: 1px solid var(--border);
      h3 { font-size: 15px; font-weight: 600; }
    }
    .drawer-body   { flex: 1; overflow-y: auto; padding: 20px; display: flex; flex-direction: column; gap: 16px; }
    .drawer-footer { display: flex; justify-content: flex-end; gap: 8px; padding: 14px 20px; border-top: 1px solid var(--border); }

    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; .span2 { grid-column: span 2; } }
    .field-hint { font-size: 11px; color: var(--text-dim); margin-top: 2px; }

    .preview-box {
      display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
      background: var(--surface-2); border: 1px solid var(--border);
      border-radius: var(--radius-sm); padding: 10px 14px;
      font-size: 12px;
    }
    .preview-label {
      font-size: 10px; font-weight: 700; text-transform: uppercase;
      letter-spacing: 0.06em; color: var(--text-dim);
    }
    .preview-arrow { color: var(--text-dim); font-size: 14px; }

    .dup-warn {
      display: block; margin-top: 6px; padding: 8px 10px; border-radius: var(--radius-sm);
      background: var(--warn-bg-2); border: 1px solid var(--warn-border); font-size: 12px; color: var(--warn-text); line-height: 1.4;
    }
    .dup-warn.save-error { background: var(--danger-bg); border-color: var(--danger); color: var(--danger); margin: 0 20px 8px; }
    .dup-warn .link-btn {
      background: transparent; border: none; padding: 0; margin-left: 4px;
      color: var(--warn-text); font-weight: 600; cursor: pointer; text-decoration: underline; font-size: inherit;
    }
    .dup-warn .link-btn:hover { color: var(--warn-strong); }

    .badge-muted { background: #e0e7ff; color: #3730a3; border: 1px solid #c7d2fe; }
  `]
})
export class ClassificationRulesComponent implements OnInit {
  private svc = inject(ConfigService);
  private notify = inject(NotificationService);

  loading     = signal(false);
  saving      = signal(false);
  drawerOpen  = signal(false);
  deleteError = signal<string | null>(null);
  rules      = signal<ClassificationRule[]>([]);
  jobTypes   = signal<JobType[]>([]);
  errorTypes = signal<ErrorType[]>([]);
  editing    = signal<ClassificationRule | null>(null);
  /** Live inline dup warning (client-side) + post-save 409 banner. Same
   *  three-layer guard shape as FixPolicyRule. */
  liveDupRuleId = signal<number | null>(null);
  saveConflict  = signal<{ message: string; conflictingRuleId: number } | null>(null);

  filterText      = '';
  filterJobType   = '';
  filterErrorType = '';
  filtered        = signal<ClassificationRule[]>([]);

  form: UpsertClassificationRuleRequest = this.blank();

  ngOnInit() {
    this.loading.set(true);
    this.svc.getAllClassificationRules().subscribe({
      next: r => { this.rules.set(r); this.filtered.set(r); this.loading.set(false); },
      error: () => { this.loading.set(false); this.notify.error('Could not load classification rules.'); },
    });
    this.svc.getJobTypes().subscribe({ next: t => this.jobTypes.set(t) });
    this.svc.getErrorTypes().subscribe({ next: t => this.errorTypes.set(t) });
  }

  applyFilter() {
    const txt = this.filterText.toLowerCase();
    this.filtered.set(this.rules().filter(r =>
      (!txt || r.pattern.toLowerCase().includes(txt)) &&
      (!this.filterJobType   || r.jobTypeName   === this.filterJobType) &&
      (!this.filterErrorType || r.errorTypeCode === this.filterErrorType)
    ));
  }

  clearFilters() {
    this.filterText = '';
    this.filterJobType = '';
    this.filterErrorType = '';
    this.filtered.set(this.rules());
  }

  openDrawer(rule: ClassificationRule | null) {
    this.editing.set(rule);
    this.form = rule
      ? { jobTypeId: rule.jobTypeId, errorTypeId: rule.errorTypeId,
          pattern: rule.pattern, confidence: rule.confidence,
          priority: rule.priority, isActive: rule.isActive }
      : this.blank();
    this.saveConflict.set(null);
    this.drawerOpen.set(true);
    this.checkDup();
  }

  /** Client-side live duplicate check — at most one enabled rule per
   *  (JobType, Pattern). Case-insensitive, matches the DB index + backend. */
  checkDup() {
    const p = (this.form.pattern || '').trim().toLowerCase();
    if (!p || !this.form.jobTypeId) { this.liveDupRuleId.set(null); return; }
    const editingId = this.editing()?.ruleId;
    const hit = this.rules().find(r =>
      r.isActive && r.jobTypeId === this.form.jobTypeId
      && r.pattern.trim().toLowerCase() === p && r.ruleId !== editingId);
    this.liveDupRuleId.set(hit?.ruleId ?? null);
  }

  /** "Open existing rule" — re-open the drawer loaded with the conflict. */
  openExisting(ruleId: number) {
    const rule = this.rules().find(r => r.ruleId === ruleId);
    if (rule) this.openDrawer(rule);
  }

  save() {
    if (!this.form.pattern || !this.form.jobTypeId || !this.form.errorTypeId) return;
    this.saving.set(true);
    this.saveConflict.set(null);
    const id   = this.editing()?.ruleId;
    const req$: Observable<any> = id
      ? this.svc.updateClassificationRule(id, this.form)
      : this.svc.createClassificationRule(this.form);
    req$.subscribe({
      next: () => { this.closeDrawer(); this.reload(); },
      error: (err) => {
        this.saving.set(false);
        const body = err?.error;
        if (err?.status === 409 && body?.error === 'DuplicateClassificationRule' && body?.conflictingRuleId) {
          this.saveConflict.set({ message: body.message ?? 'A duplicate active rule exists.', conflictingRuleId: body.conflictingRuleId });
        }
      },
    });
  }

  deleteRule(rule: ClassificationRule) {
    if (!confirm(`Delete rule "${rule.pattern}"?`)) return;
    this.deleteError.set(null);
    this.svc.deleteClassificationRule(rule.ruleId).subscribe({
      next:  () => this.reload(),
      error: (err) => this.deleteError.set(err?.error?.message ?? 'Delete failed — check console for details.'),
    });
  }

  closeDrawer() { this.drawerOpen.set(false); this.saving.set(false); }

  jobTypeName(id: number):   string { return this.jobTypes().find(t => t.jobTypeId === id)?.name   ?? String(id); }
  errorTypeCode(id: number): string { return this.errorTypes().find(e => e.errorTypeId === id)?.code ?? String(id); }

  private reload() {
    this.svc.getAllClassificationRules().subscribe({
      next: r => { this.rules.set(r); this.applyFilter(); this.saving.set(false); },
    });
  }

  private blank(): UpsertClassificationRuleRequest {
    return { jobTypeId: 0, errorTypeId: 0, pattern: '', confidence: 0.9, priority: 1, isActive: true };
  }
}
