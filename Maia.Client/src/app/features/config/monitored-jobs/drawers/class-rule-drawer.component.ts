import { Component, computed, inject, input, output, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import {
  ConfigService, ErrorType, ClassificationRule,
  UpsertJobClassificationRuleRequest, UpsertClassificationRuleRequest,
} from '../../../../core/services/config.service';
import { MonitoredJob, RuleOverride } from '../../../../core/models';
import { DrawerComponent } from '../../../../shared/drawer/drawer.component';

/**
 * Classification-rule management for one job: the create/edit editor (with the
 * duplicate-409 → "link existing" flow) plus the "Link Existing" picker.
 * Extracted from JobConfigComponent. Parent calls {@link openNew}/{@link openEdit}/
 * {@link openLink} and reloads on {@link saved}. The picker fetches the full rule
 * list itself and re-emits it via {@link allClassRulesRefreshed} so the parent's
 * shared allClassRules (which feeds the effective-classifier computeds) stays fresh
 * — exactly as the pre-split openLinkDrawer did.
 */
@Component({
  selector: 'app-class-rule-drawer',
  standalone: true,
  imports: [FormsModule, DrawerComponent],
  template: `
    <!-- ── Classification Rule editor ────────────────────────────────────────── -->
    <app-drawer [open]="editOpen()" [ariaLabel]="'Classification rule'" (close)="editOpen.set(false)">
      <ng-container drawer-title>{{ editingRule() ? 'Edit' : 'New' }} Classification Rule</ng-container>
      <div class="form-grid">
        <div class="form-group span2">
          <label>Match Pattern *</label>
          <input [(ngModel)]="form.pattern" placeholder="e.g. FileNotFoundException  or  Error code * occurred" />
          <span class="field-hint">Case-insensitive substring of the error message. Use <code>*</code> as a wildcard for any text; other characters are literal.</span>
        </div>
        <div class="form-group span2">
          <label>Error Type *</label>
          <select [(ngModel)]="form.errorTypeId">
            <option [ngValue]="0" disabled>Select error type…</option>
            @for (et of errorTypes(); track et.errorTypeId) { <option [ngValue]="et.errorTypeId">{{ et.code }} — {{ et.displayName }}</option> }
          </select>
        </div>
        <div class="form-group">
          <label>Confidence (0 – 1)</label>
          <input type="number" [(ngModel)]="form.confidence" min="0" max="1" step="0.05" />
        </div>
        <div class="form-group">
          <label>Priority</label>
          <input type="number" [(ngModel)]="form.priority" min="1" />
          <span class="field-hint">Lower = evaluated first.</span>
        </div>
        <div class="form-group">
          <label class="toggle-label"><input type="checkbox" [(ngModel)]="form.isActive" /> Active</label>
        </div>
      </div>
      @if (saveError()) {
        <div class="dup-warn save-error" role="alert">
          ⚠ {{ saveError() }}
          @if (conflictId()) {
            <br><button class="link-btn" (click)="linkConflicting()">{{ editingRule() ? 'Swap to existing rule' : 'Link the existing rule instead' }}</button>
          }
        </div>
      }
      <div class="drawer-foot">
        <button class="btn btn-ghost" (click)="editOpen.set(false)">Cancel</button>
        <button class="btn btn-primary" (click)="save()" [disabled]="saving()">
          @if (saving()) { <span class="spinner"></span> } {{ editingRule() ? 'Save Changes' : 'Add Rule' }}
        </button>
      </div>
    </app-drawer>

    <!-- ── Link Existing Classification Rule picker ──────────────────────────── -->
    <app-drawer [open]="linkOpen()" [ariaLabel]="'Link classification rule'" (close)="linkOpen.set(false)">
      <ng-container drawer-title>Link Existing Classification Rule</ng-container>
      <div class="form-group" style="margin-bottom:12px">
        <input [ngModel]="linkSearch()" (ngModelChange)="linkSearch.set($event)" placeholder="Search by pattern or error type…" />
      </div>
      @if (loadingLink()) {
        <div class="loading-overlay" style="padding:20px 0"><span class="spinner"></span> Loading…</div>
      } @else if (filteredLinkableRules().length === 0) {
        <div class="empty-state"><span class="empty-icon">🏷️</span><p>No unlinked rules found{{ linkSearch() ? ' matching "' + linkSearch() + '"' : '' }}.</p></div>
      } @else {
        <div class="link-rule-list">
          @for (r of filteredLinkableRules(); track r.ruleId) {
            <div class="link-rule-item" (click)="confirmLink(r)">
              <div class="link-rule-pattern font-mono">{{ r.pattern }}</div>
              <div class="link-rule-meta">
                <span class="badge badge-classified">{{ r.errorTypeCode }}</span>
                <span class="text-muted text-sm">{{ r.jobTypeName }}</span>
                <span class="text-muted text-sm">conf {{ (r.confidence * 100).toFixed(0) }}%</span>
              </div>
            </div>
          }
        </div>
      }
      <div class="drawer-foot">
        <button class="btn btn-ghost" (click)="linkOpen.set(false)">Cancel</button>
      </div>
    </app-drawer>
  `,
  styles: [`
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
    .empty-state { padding: 24px; text-align: center; color: var(--text-muted); }
    .empty-icon { font-size: 28px; display: block; margin-bottom: 6px; }
    .link-btn { background: transparent; border: none; padding: 0; color: var(--primary); font-weight: 600; cursor: pointer; text-decoration: underline; font-size: inherit; }
    .dup-warn { display: block; margin-top: 6px; padding: 8px 10px; border-radius: var(--radius-sm); background: var(--warn-bg-2); border: 1px solid var(--warn-border); font-size: 12px; color: var(--warn-text); line-height: 1.4; }
    .dup-warn .link-btn { color: var(--warn-text); font-weight: 600; text-decoration: underline; margin-left: 4px; }
    .dup-warn.save-error { background: var(--danger-bg); border-color: var(--danger); color: var(--danger); margin-top: 12px; }
    .link-rule-list { display: flex; flex-direction: column; gap: 6px; }
    .link-rule-item { padding: 10px 12px; border: 1px solid var(--border); border-radius: var(--radius-sm); cursor: pointer; transition: all var(--transition); }
    .link-rule-item:hover { border-color: var(--primary); background: var(--primary-light); }
    .link-rule-pattern { font-size: 12px; margin-bottom: 4px; word-break: break-all; }
    .link-rule-meta { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
  `],
})
export class ClassRuleDrawerComponent {
  private svc = inject(ConfigService);

  job         = input<MonitoredJob | null>(null);
  jobTypeId   = input<number>(0);
  errorTypes  = input<ErrorType[]>([]);
  saved                  = output<void>();
  allClassRulesRefreshed = output<ClassificationRule[]>();

  // ── Editor state ──────────────────────────────────────────────────────────
  editOpen     = signal(false);
  saving       = signal(false);
  editingRule  = signal<RuleOverride | null>(null);
  saveError    = signal<string | null>(null);
  conflictId   = signal<number | null>(null);
  form: UpsertJobClassificationRuleRequest = this.blank();

  // ── Link-picker state ─────────────────────────────────────────────────────
  linkOpen     = signal(false);
  loadingLink  = signal(false);
  linkSearch   = signal('');
  private linkableRules = signal<ClassificationRule[]>([]);
  filteredLinkableRules = computed(() => {
    const linked = new Set(this.job()?.rules.map(r => r.ruleId) ?? []);
    const q = this.linkSearch().toLowerCase();
    return this.linkableRules().filter(r =>
      !linked.has(r.ruleId) &&
      (!q || r.pattern.toLowerCase().includes(q) || r.errorTypeCode.toLowerCase().includes(q)));
  });

  // ── Editor ────────────────────────────────────────────────────────────────

  openNew(prefill?: { pattern?: string; errorTypeId?: number }) {
    this.editingRule.set(null);
    this.saveError.set(null);
    this.conflictId.set(null);
    this.form = { ...this.blank(), ...(prefill?.pattern ? { pattern: prefill.pattern } : {}),
                  ...(prefill?.errorTypeId ? { errorTypeId: prefill.errorTypeId } : {}) };
    this.editOpen.set(true);
  }

  openEdit(rule: RuleOverride) {
    this.editingRule.set(rule);
    this.saveError.set(null);
    this.conflictId.set(null);
    const et = this.errorTypes().find(e => e.code === rule.errorTypeCode);
    this.form = { errorTypeId: et?.errorTypeId ?? 0, pattern: rule.pattern,
                  confidence: rule.confidence, priority: rule.priority, isActive: true };
    this.editOpen.set(true);
  }

  save() {
    if (!this.form.pattern?.trim() || !this.form.errorTypeId) return;
    this.saving.set(true);
    this.saveError.set(null);
    this.conflictId.set(null);
    const job = this.job()!;
    const ruleId = this.editingRule()?.ruleId;
    const req$: Observable<unknown> = ruleId
      ? this.svc.updateClassificationRule(ruleId, {
          jobTypeId: this.jobTypeId(), errorTypeId: this.form.errorTypeId,
          pattern: this.form.pattern, confidence: this.form.confidence,
          priority: this.form.priority, isActive: this.form.isActive,
        } as UpsertClassificationRuleRequest)
      : this.svc.createJobClassificationRule(job.monitoredJobId, this.form);
    req$.subscribe({
      next: () => { this.editOpen.set(false); this.saving.set(false); this.saved.emit(); },
      error: (err: any) => {
        this.saving.set(false);
        const body = err?.error;
        if (err?.status === 409 && body?.error === 'DuplicateClassificationRule') {
          this.conflictId.set(body.conflictingRuleId ?? null);
          this.saveError.set(body.message);
        } else {
          this.saveError.set(body?.message || 'Save failed. Check the server logs.');
        }
      },
    });
  }

  linkConflicting() {
    const ruleId  = this.conflictId();
    const oldRule = this.editingRule(); // set when editing, null when creating
    const jobId   = this.job()!.monitoredJobId;
    if (!ruleId) return;
    this.saving.set(true);
    this.svc.linkJobClassificationRule(jobId, ruleId).subscribe({
      next: () => {
        // Edit mode: unlink the rule that was being edited so the job swaps
        // to the existing rule instead of holding both.
        if (oldRule && oldRule.ruleId !== ruleId) {
          this.svc.deleteJobClassificationRule(jobId, oldRule.ruleId).subscribe({
            next:  () => { this.editOpen.set(false); this.saving.set(false); this.saved.emit(); },
            error: () => { this.editOpen.set(false); this.saving.set(false); this.saved.emit(); },
          });
        } else {
          this.editOpen.set(false); this.saving.set(false); this.saved.emit();
        }
      },
      error: (err: any) => {
        this.saving.set(false);
        this.saveError.set(err?.error?.message || 'Link failed.');
      },
    });
  }

  private blank(): UpsertJobClassificationRuleRequest {
    return { errorTypeId: 0, pattern: '', confidence: 0.9, priority: 1, isActive: true };
  }

  // ── Link picker ───────────────────────────────────────────────────────────

  openLink() {
    this.linkSearch.set('');
    this.loadingLink.set(true);
    this.linkOpen.set(true);
    this.svc.getAllClassificationRules().subscribe({
      next: r => { this.linkableRules.set(r); this.allClassRulesRefreshed.emit(r); this.loadingLink.set(false); },
      error: () => this.loadingLink.set(false),
    });
  }

  confirmLink(rule: ClassificationRule) {
    this.saving.set(true);
    this.svc.linkJobClassificationRule(this.job()!.monitoredJobId, rule.ruleId).subscribe({
      next: () => { this.linkOpen.set(false); this.saving.set(false); this.saved.emit(); },
      error: () => this.saving.set(false),
    });
  }
}
