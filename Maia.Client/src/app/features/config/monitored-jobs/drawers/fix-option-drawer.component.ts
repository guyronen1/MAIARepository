import { Component, computed, inject, input, output, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import {
  ConfigService, FixPolicyRule, FixPolicyStepDto, ErrorType, UpsertFixPolicyRuleRequest,
} from '../../../../core/services/config.service';
import { MonitoredJob } from '../../../../core/models';
import { DrawerComponent } from '../../../../shared/drawer/drawer.component';

/** Minimal shape of an effective classification rule (shared with the parent's
 *  effectiveClassRules computed). */
interface EffectiveClassRule { ruleId: number; pattern: string; errorTypeCode: string; }

/**
 * Fix Option drawer — the largest editor: execution steps as rows (one row =
 * single action, 2+ = Composite; category derived from the result),
 * coverage/reachability warnings, the "scope to failing
 * row" SqlScript shortcut, and two-pronged duplicate detection. Extracted from
 * JobConfigComponent. Parent calls {@link openFor} (edit or new, with optional
 * ErrorType prefill for gap-clicks / deep-links) and reloads on {@link saved}.
 * effectiveClassRules is passed in (the parent owns that computed, shared with
 * the coverage markers).
 */
@Component({
  selector: 'app-fix-option-drawer',
  standalone: true,
  imports: [FormsModule, DrawerComponent],
  template: `
    <app-drawer [open]="isOpen()" [ariaLabel]="'Fix option'" (close)="isOpen.set(false)">
      <ng-container drawer-title>{{ editingFixRule() ? 'Edit' : 'New' }} Fix Option</ng-container>
      @if (job(); as j) {
      <div class="drawer-context-banner">
        <span class="banner-icon" aria-hidden="true">i</span>
        <span>
          @if (form.monitoredJobId !== null) {
            Fix policy for <strong>{{ j.displayName ?? j.name }}</strong> — applies to this job only.
          } @else {
            Fix policy for <strong>all {{ j.jobTypeName }} jobs</strong> (JobType-wide default) — a per-job policy overrides it.
          }
        </span>
      </div>
      <div class="form-grid">
        <div class="span2 scope-line">
          @if (form.monitoredJobId !== null) {
            <span class="scope-current">Scope: <strong>This job</strong></span>
            <button type="button" class="link-btn" (click)="setFixRuleScope(null)">Apply to all {{ j.jobTypeName }} jobs instead</button>
          } @else {
            <span class="scope-current">Scope: <strong>All {{ j.jobTypeName }} jobs</strong> (default)</span>
            <button type="button" class="link-btn" (click)="setFixRuleScope(j.monitoredJobId)">Scope to just this job instead</button>
          }
        </div>
        @if (effectiveClassRules().length > 0) {
          <div class="form-group span2">
            <label>Target a classification rule <span class="text-muted">(shortcut)</span></label>
            <select [ngModel]="shortcutRuleId" (ngModelChange)="pickClassificationRuleById($event)">
              <option [ngValue]="null" disabled>Pick a symptom to target…</option>
              @for (cr of effectiveClassRules(); track cr.ruleId) { <option [ngValue]="cr.ruleId">{{ cr.pattern }} → {{ cr.errorTypeCode }}</option> }
            </select>
            <span class="field-hint">Sets the Error Type below from the rule's type.</span>
          </div>
        }
        <div class="form-group span2">
          <label>Error Type *</label>
          <select [(ngModel)]="form.errorTypeId" (ngModelChange)="shortcutRuleId = null; syncFixRuleSignal()">
            <option [ngValue]="0" disabled>Select error type…</option>
            @for (et of errorTypes(); track et.errorTypeId) { <option [ngValue]="et.errorTypeId">{{ et.code }} — {{ et.displayName }}</option> }
          </select>
          @if (fixRuleDuplicateConflict(); as conflict) {
            <div class="dup-warn">
              ⚠ An active fix policy already exists for this Error Type at the
              <strong>{{ form.monitoredJobId === null ? 'default (all jobs)' : 'override (this job)' }}</strong> scope.
              Existing: <strong>{{ conflict.fixCategory }} / {{ conflict.actionType }}</strong>.
              <button type="button" class="link-btn" (click)="openConflictingPolicy(conflict)">Edit existing policy instead?</button>
            </div>
          } @else if (fixRuleSaveConflict(); as conflict) {
            <div class="dup-warn">⚠ {{ conflict.message }}
              <button type="button" class="link-btn" (click)="openConflictingPolicyById(conflict.conflictingPolicyId)">Open existing policy</button>
            </div>
          }
          @if (selectedErrorTypeCode(); as code) {
            @if (classRulesForSelectedErrorType().length > 0) {
              <span class="field-hint covers-hint">
                Covers {{ classRulesForSelectedErrorType().length }} classification {{ classRulesForSelectedErrorType().length === 1 ? 'rule' : 'rules' }} on this job:
                @for (cr of classRulesForSelectedErrorType(); track cr.ruleId; let last = $last) { <code>{{ cr.pattern }}</code>{{ last ? '' : ', ' }} }
              </span>
            } @else {
              <div class="dup-warn reachability-warn">
                ⚠ No classification rule on this job maps to <strong>{{ code }}</strong> — this fix won't trigger until one exists. Add a matching rule in <strong>Classification Rules</strong> above.
              </div>
            }
          }
        </div>
        <div class="form-group span2">
          <label>Action Description *</label>
          <input [(ngModel)]="form.actionToApply" placeholder="e.g. Retry DTSX job via management API" />
        </div>
        <div class="form-group">
          <label>Fix Category</label>
          <!-- Read-only — derived from the steps: one step → that type's natural
               category (Manual → Manual); several steps → Composite's category. -->
          <input [value]="form.fixCategory || 'Derived from the steps'" readonly />
        </div>
        <div class="form-group">
          <label>Behaviour</label>
          <div class="toggles-row">
            <label class="toggle-pair">
              <span class="toggle"><input type="checkbox" [(ngModel)]="form.isAutoHealEligible" /><span class="slider"></span></span>
              <span class="toggle-text">Auto-Heal</span>
            </label>
            <label class="toggle-pair">
              <span class="toggle"><input type="checkbox" [(ngModel)]="form.enabled" (ngModelChange)="syncFixRuleSignal()" /><span class="slider"></span></span>
              <span class="toggle-text">Enabled</span>
            </label>
          </div>
        </div>
        <!-- Execution steps: one row per action, same row layout as the scan-rule
             conditions editor. Each row carries its own execution type. One row is
             saved as a single-action policy (payload on the policy); two or more as
             ActionType=Composite with the rows as Steps. Manual is single-row only. -->
        <div class="form-group span2 step-block">
          <label>Execution Steps * <span class="text-muted">(run in order)</span></label>
          <div class="step-editor">
            @for (step of stepRows; track $index; let i = $index) {
              <div class="step-row" [class.step-main]="stepRows.length === 1">
                <span class="step-order">{{ i + 1 }}.</span>
                <select [ngModel]="step.actionType" (ngModelChange)="setStepType(i, $event)" class="step-type" [attr.aria-label]="'Step ' + (i + 1) + ' execution type'">
                  <option [ngValue]="''" disabled>Execution type…</option>
                  @for (a of stepTypes; track a) { <option [ngValue]="a">{{ a }}</option> }
                  @if (stepRows.length === 1) { <option [ngValue]="'Manual'">Manual</option> }
                </select>
                @if (step.actionType === 'Manual') {
                  <span class="step-manual">No automated action — the operator performs the fix after approving.</span>
                } @else if (step.actionType === 'SqlScript') {
                  <textarea [ngModel]="step.actionPayload" (ngModelChange)="step.actionPayload = $event; syncFixRuleSignal()" [rows]="stepRows.length === 1 ? 4 : 2" class="step-payload" [placeholder]="payloadPlaceholderFor(step.actionType)" [attr.aria-label]="'Step ' + (i + 1) + ' payload'"></textarea>
                } @else {
                  <input [ngModel]="step.actionPayload" (ngModelChange)="step.actionPayload = $event; syncFixRuleSignal()" class="step-payload" [placeholder]="payloadPlaceholderFor(step.actionType)" [disabled]="!step.actionType" [attr.aria-label]="'Step ' + (i + 1) + ' payload'" />
                }
                <div class="step-controls">
                  @if (stepRows.length === 1) {
                    <span class="step-main-tag" title="A fix always has at least one step">main</span>
                  } @else {
                    <button type="button" class="btn btn-ghost btn-icon" title="Move up" (click)="moveStep(i, -1)" [disabled]="i === 0">↑</button>
                    <button type="button" class="btn btn-ghost btn-icon" title="Move down" (click)="moveStep(i, +1)" [disabled]="i === stepRows.length - 1">↓</button>
                    <button type="button" class="btn btn-ghost btn-icon" title="Remove step" (click)="removeStep(i)">✕</button>
                  }
                </div>
                @if (step.actionType === 'SqlScript' && sqlFixNeedsScopeShortcut(step.actionPayload)) {
                  <button type="button" class="link-btn step-sub" (click)="scopeStepToSourceId(step)">+ scope to the failing row — add <code>{{ scopeClauseFor(step.actionPayload) }} {{ fixScopeColumn }} = '{{'{'}}sourceId{{'}'}}'</code></button>
                }
                @if (stepRows.length > 1) {
                  <input [(ngModel)]="step.description" class="step-desc step-sub" placeholder="Step description (optional)" [attr.aria-label]="'Step ' + (i + 1) + ' description'" />
                }
              </div>
            }
          </div>
          <button type="button" class="btn btn-ghost btn-sm step-add" (click)="addStep()" [disabled]="!canAddStep()"
                  [title]="canAddStep() ? '' : 'A Manual fix has no automated steps'">+ Add Step</button>
          @if (stepRows.length > 1) {
            <span class="field-hint">Steps run in order. Any step failure routes the failure to <strong>ManualRequired</strong>; subsequent steps still run (best-effort). One log row per step.</span>
          }
          @if (usesStepType('SqlScript')) {
            <span class="field-hint">SqlScript runs against the job's configured connection. Key the source row on <code>'{{'{'}}sourceId{{'}'}}'</code> (quoted) — <em>not</em> <code>{{'{'}}failureId{{'}'}}</code> (MAIA's internal id). A fix must be scoped to the failing row or it won't save.</span>
          }
          @if (usesStepType('CopyFile')) {
            <span class="field-hint">CopyFile format <code>SOURCE|DEST</code>. Atomic copy, overwrite by default. <code>{{'{'}}sourceFilePath{{'}'}}</code> needs Input File Extraction (FS) or File Path Column (DB) on a scan rule.</span>
          }
        </div>
        @if (fixRuleSourcePathWarning()) {
          <div class="form-group span2">
            <div class="dup-warn">⚠ This payload uses <code>{{'{'}}sourceFilePath{{'}'}}</code>, but no scan rule on <strong>{{ j.displayName ?? j.name }}</strong> captures a file path. Set <strong>Input File Extraction</strong> (FS) or <strong>File Path Column</strong> (DB) on a scan rule, or the fix fails at runtime with an empty source path.</div>
          </div>
        }
        @if (form.actionType && form.actionType !== 'Manual') {
          <div class="form-group span2">
            <details class="token-legend">
              <summary>Available placeholders</summary>
              <dl>
                <dt><code>{{'{'}}failureId{{'}'}}</code></dt><dd>This failure's numeric id.</dd>
                <dt><code>{{'{'}}sourceId{{'}'}}</code></dt><dd>Source row's natural key (DB scan) or matched id.</dd>
                <dt><code>{{'{'}}referenceId{{'}'}}</code></dt><dd>Related row's identity (parent/FK key) — needs Reference ID Column on the scan rule.</dd>
                <dt><code>{{'{'}}sourceLogPath{{'}'}}</code></dt><dd>Log file/source where the error was detected.</dd>
                <dt><code>{{'{'}}sourceFilePath{{'}'}}</code></dt><dd>Input file path — needs Input File Extraction (FS) or File Path Column (DB).</dd>
                <dt><code>{{'{'}}sourceFileName{{'}'}}</code></dt><dd>Filename only, sliced from {{'{'}}sourceFilePath{{'}'}}.</dd>
                <dt><code>{{'{'}}jobFolder{{'}'}}</code></dt><dd>The source's scanned folder.</dd>
                <dt><code>{{'{'}}inputFolder{{'}'}}</code></dt><dd>The source's input folder.</dd>
              </dl>
              <span class="token-note">Unknown tokens are left as-is. Matching is case-insensitive.</span>
            </details>
          </div>
        }
        @if (!form.enabled && form.monitoredJobId === null && editingFixRule() !== null && editingFixRule()!.enabled) {
          <div class="dup-warn span2">⚠ Disabling this default — jobs of this JobType without their own override for this error type fall back to the built-in catalogue. Overrides on other jobs are unaffected.</div>
        }
      </div>
      @if (form.isAutoHealEligible) {
        <div class="auto-heal-banner"><span>⚡</span><span>Auto-heal is ON — this fix executes <strong>automatically</strong> without operator approval whenever this error type is detected.</span></div>
      }
      @if (fixRuleSaveError(); as msg) {
        <div class="dup-warn save-error" role="alert">⚠ Save failed: {{ msg }}</div>
      }
      <div class="drawer-foot">
        <button class="btn btn-ghost" (click)="isOpen.set(false)">Cancel</button>
        <button class="btn btn-primary" (click)="save()"
                [disabled]="saving() || !stepsValid()"
                [title]="!stepsValid() ? 'Pick an execution type for every step first' : ''">
          @if (saving()) { <span class="spinner"></span> } {{ editingFixRule() ? 'Save Changes' : 'Add Fix Option' }}
        </button>
      </div>
      }
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
    .form-group input[readonly] { background: var(--surface-2, #f8fafc); color: var(--text-muted); cursor: default; border-style: dashed; }
    .field-hint { font-size: 11px; color: var(--text-dim); }
    .drawer-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }

    .drawer-context-banner { display: flex; align-items: center; gap: 8px; background: var(--primary-light); border: 1px solid var(--primary); border-radius: var(--radius-sm); padding: 8px 12px; font-size: 12px; color: var(--primary-dark); margin-bottom: 14px; }
    .banner-icon { display: inline-flex; align-items: center; justify-content: center; width: 18px; height: 18px; flex-shrink: 0; border-radius: 50%; background: var(--primary); color: #fff; font-style: italic; font-weight: 700; font-size: 12px; font-family: Georgia, serif; }
    .auto-heal-banner { display: flex; align-items: flex-start; gap: 8px; background: var(--warn-bg); border: 1px solid var(--warn-border); border-radius: var(--radius-sm); padding: 10px 12px; font-size: 12px; color: var(--warn-text); line-height: 1.5; margin-top: 12px; }
    .auto-heal-banner span:first-child { font-size: 16px; }
    .scope-line { display: flex; align-items: baseline; flex-wrap: wrap; gap: 4px 10px; font-size: 12px; color: var(--text-muted); }
    .scope-current strong { color: var(--text); }
    .link-btn { background: transparent; border: none; padding: 0; color: var(--primary); font-weight: 600; cursor: pointer; text-decoration: underline; font-size: inherit; }
    .link-btn:hover { color: var(--primary-dark); }
    .toggles-row { display: flex; gap: 24px; align-items: center; padding-top: 4px; }
    .toggle-pair { display: inline-flex; align-items: center; gap: 8px; cursor: pointer; }
    .toggle-pair .toggle-text { font-size: 13px; color: var(--text); }
    .covers-hint { display: block; }
    .dup-warn { display: block; margin-top: 6px; padding: 8px 10px; border-radius: var(--radius-sm); background: var(--warn-bg-2); border: 1px solid var(--warn-border); font-size: 12px; color: var(--warn-text); line-height: 1.4; }
    .dup-warn .link-btn { color: var(--warn-text); font-weight: 600; text-decoration: underline; margin-left: 4px; }
    .dup-warn .link-btn:hover { color: var(--warn-strong); }
    .dup-warn.save-error { background: var(--danger-bg); border-color: var(--danger); color: var(--danger); margin-top: 12px; }
    .dup-warn.reachability-warn { margin-top: 6px; }

    .step-block { padding-top: 4px; border-top: 1px dashed var(--border); }
    .step-editor { display: flex; flex-direction: column; gap: 6px; margin-top: 4px; }
    .step-row { display: grid; grid-template-columns: 22px 130px 1fr 88px; gap: 6px; align-items: center; padding: 6px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); }
    .step-row.step-main { background: var(--surface-2); }
    .step-row .step-order { font-weight: 600; color: var(--text-dim); text-align: right; }
    .step-row select.step-type, .step-row input.step-payload, .step-row input.step-desc { font-size: 12px; padding: 4px 6px; min-width: 0; width: 100%; }
    .step-row textarea.step-payload { font-size: 12px; padding: 4px 6px; min-width: 0; width: 100%; font-family: ui-monospace, Menlo, Consolas, monospace; resize: vertical; }
    .step-row .step-sub { grid-column: 2 / 4; justify-self: start; }
    .step-row input.step-desc { justify-self: stretch; }
    .step-row .link-btn.step-sub { font-size: 11px; }
    .step-manual { font-size: 12px; color: var(--text-muted); font-style: italic; }
    .step-main-tag { font-size: 10px; font-weight: 600; letter-spacing: .02em; color: var(--text-dim); border: 1px solid var(--border); border-radius: 999px; padding: 1px 7px; }
    .step-controls { display: flex; gap: 2px; justify-content: flex-end; }
    .step-row .btn-icon { padding: 2px 6px; font-size: 13px; line-height: 1.2; }
    .step-add { align-self: flex-start; margin-top: 4px; }

    .token-legend { margin-top: 6px; font-size: 12px; border: 1px solid var(--border-light); border-radius: var(--radius-sm); background: var(--surface-2); }
    .token-legend > summary { cursor: pointer; padding: 6px 10px; font-weight: 600; color: var(--text-muted); user-select: none; }
    .token-legend[open] > summary { border-bottom: 1px solid var(--border-light); }
    .token-legend dl { margin: 0; padding: 8px 10px; display: grid; grid-template-columns: auto 1fr; gap: 4px 12px; }
    .token-legend dt, .token-legend dd { margin: 0; }
    .token-legend dd { color: var(--text-muted); }
    .token-legend code { font-size: 11px; }
    .token-note { display: block; padding: 0 10px 8px; font-size: 11px; color: var(--text-dim); }
  `],
})
export class FixOptionDrawerComponent {
  private svc = inject(ConfigService);

  job                = input<MonitoredJob | null>(null);
  jobTypeId          = input<number>(0);
  errorTypes         = input<ErrorType[]>([]);
  fixPolicies        = input<FixPolicyRule[]>([]);
  effectiveClassRules = input<EffectiveClassRule[]>([]);
  saved              = output<void>();

  isOpen         = signal(false);
  saving         = signal(false);
  editingFixRule = signal<FixPolicyRule | null>(null);
  form: UpsertFixPolicyRuleRequest = this.blank();
  shortcutRuleId: number | null = null;
  /** Mirror of form so the warning/dup computeds re-evaluate on change. */
  private formSignal = signal<UpsertFixPolicyRuleRequest>(this.blank());
  fixRuleSaveConflict = signal<{ message: string; conflictingPolicyId: number } | null>(null);
  fixRuleSaveError    = signal<string | null>(null);

  /** Execution types a step row can take. `Manual` is offered separately and only
   *  while there is a single row (steps can't be Manual — DB CHECK + backend 400);
   *  `Composite` is never picked — it is what two or more rows save as. */
  readonly stepTypes = ['SqlScript', 'StoredProcedure', 'ApiCall', 'Script', 'CopyFile'];

  /** The editor's working list. Row 1 always exists. {@link applyStepsToForm}
   *  maps it onto the request: 1 row → that ActionType with the payload on the
   *  policy; 2+ rows → ActionType=Composite, null payload, rows as Steps. */
  stepRows: FixPolicyStepDto[] = [this.blankStep()];

  /** Two-pronged duplicate detection — same key shape as the backend 409. */
  fixRuleDuplicateConflict = computed<FixPolicyRule | null>(() => {
    const form = this.formSignal();
    if (!form.enabled || !form.errorTypeId || !form.jobTypeId) return null;
    const editingId = this.editingFixRule()?.ruleId;
    return this.fixPolicies().find(p => {
      if (!p.enabled || p.ruleId === editingId) return false;
      if (p.errorTypeId !== form.errorTypeId)   return false;
      return form.monitoredJobId !== null
        ? p.monitoredJobId === form.monitoredJobId
        : p.monitoredJobId === null && p.jobTypeId === form.jobTypeId;
    }) ?? null;
  });

  /** Soft config-time warning: payload references {sourceFilePath} but no scan
   *  rule on the job captures one (no InputPathPattern / FilePathColumn). */
  fixRuleSourcePathWarning = computed<boolean>(() => {
    const form = this.formSignal();
    const usesToken = (s: string | null | undefined) => !!s && /\{sourceFilePath\}/i.test(s);
    const referenced = usesToken(form.actionPayload) || (form.steps ?? []).some(s => usesToken(s.actionPayload));
    if (!referenced) return false;
    const captures = (this.job()?.sources ?? []).flatMap(s => s.scanCheckRules)
      .some(r => !!r.inputPathPattern?.trim() || !!r.filePathColumn?.trim());
    return !captures;
  });

  selectedErrorTypeCode = computed<string | null>(() => {
    const id = this.formSignal().errorTypeId;
    if (!id) return null;
    return this.errorTypes().find(e => e.errorTypeId === id)?.code ?? null;
  });

  classRulesForSelectedErrorType = computed(() => {
    const code = this.selectedErrorTypeCode();
    if (!code) return [];
    return this.effectiveClassRules().filter(r => r.errorTypeCode === code);
  });

  readonly fixScopeColumn = '[KeyColumn]';

  /**
   * Open for edit (rule) or new (null). optional errorTypeId prefill handles the
   * classification-gap click and the /unconfigured deep-link.
   */
  openFor(rule: FixPolicyRule | null, prefill?: { errorTypeId?: number }) {
    const job = this.job()!;
    this.editingFixRule.set(rule);
    this.shortcutRuleId = null;
    this.fixRuleSaveConflict.set(null);
    this.fixRuleSaveError.set(null);
    if (rule) {
      // Normalize legacy mismatches: only Manual↔Manual is a valid pair.
      let fixCategory = rule.fixCategory;
      let actionType  = rule.actionType;
      if (fixCategory === 'Manual' && actionType !== 'Manual') actionType  = 'Manual';
      if (actionType  === 'Manual' && fixCategory !== 'Manual') fixCategory = 'Manual';
      this.form = {
        jobTypeId: rule.jobTypeId, errorTypeId: rule.errorTypeId, monitoredJobId: rule.monitoredJobId,
        actionToApply: rule.actionToApply, fixCategory, actionType,
        actionPayload: rule.actionPayload, isAutoHealEligible: rule.isAutoHealEligible, enabled: rule.enabled,
        steps: [],
      };
      this.loaded = { actionType, fixCategory };
      const steps = [...(rule.steps ?? [])].sort((a, b) => a.stepOrder - b.stepOrder);
      this.stepRows = actionType === 'Composite' && steps.length > 0
        ? steps.map((s, i) => ({ stepOrder: i + 1, actionType: s.actionType,
                                 actionPayload: s.actionPayload ?? '', description: s.description }))
        : [{ ...this.blankStep(), actionType: actionType === 'Composite' ? '' : actionType,
             actionPayload: actionType === 'Composite' ? '' : (rule.actionPayload ?? '') }];
    } else {
      // New rule defaults to THIS job (per-job override) — the common case.
      this.form = { ...this.blank(), jobTypeId: this.jobTypeId(), monitoredJobId: job.monitoredJobId };
      if (prefill?.errorTypeId) this.form.errorTypeId = prefill.errorTypeId;
      this.loaded = { actionType: '', fixCategory: '' };
      this.stepRows = [this.blankStep()];
    }
    this.syncFixRuleSignal();
    this.isOpen.set(true);
  }

  save() {
    this.applyStepsToForm();
    if (!this.stepsValid() || !this.form.actionToApply || !this.form.errorTypeId) return;
    this.saving.set(true);
    this.fixRuleSaveConflict.set(null);
    this.fixRuleSaveError.set(null);
    const id = this.editingFixRule()?.ruleId;
    const req$: Observable<unknown> = id
      ? this.svc.updateFixPolicyRule(id, this.form)
      : this.svc.createFixPolicyRule(this.form);
    req$.subscribe({
      next: () => { this.isOpen.set(false); this.saving.set(false); this.saved.emit(); },
      error: (err: { status?: number; error?: { error?: string; message?: string; conflictingPolicyId?: number } | string; message?: string }) => {
        const body = err?.error;
        if (err?.status === 409 && typeof body === 'object' && body?.error === 'DuplicateFixPolicy' && body?.conflictingPolicyId) {
          this.fixRuleSaveConflict.set({ message: body.message ?? 'A duplicate active policy exists.', conflictingPolicyId: body.conflictingPolicyId });
        } else if (err?.status === 400 && typeof body === 'object' && body?.message) {
          this.fixRuleSaveError.set(body.message);
        } else if (err?.status === 400 && typeof body === 'string') {
          this.fixRuleSaveError.set(body);
        } else {
          this.fixRuleSaveError.set(err?.message || 'Save failed. Check the server logs and try again.');
        }
        this.saving.set(false);
      },
    });
  }

  /** Change one row's execution type. A changed type clears that row's payload
   *  (a SqlScript payload is not a valid Script payload). */
  setStepType(index: number, next: string) {
    const step = this.stepRows[index];
    if (!step) return;
    if (next !== step.actionType) step.actionPayload = '';
    step.actionType = next;
    this.fixRuleSaveError.set(null);
    this.syncFixRuleSignal();
  }

  /** Manual means "no automated action", so it can't be combined with steps. */
  canAddStep(): boolean {
    return !this.stepRows.some(s => s.actionType === 'Manual');
  }

  /** Every row has an execution type (and Manual only ever as the sole row). */
  stepsValid(): boolean {
    if (this.stepRows.length === 0 || this.stepRows.some(s => !s.actionType)) return false;
    return this.stepRows.length === 1 || this.canAddStep();
  }

  usesStepType(actionType: string): boolean {
    return this.stepRows.some(s => s.actionType === actionType);
  }

  /** Project the rows onto the request shape the backend expects. Fix Category is
   *  read-only, so it is re-derived here from the resulting ActionType. */
  private applyStepsToForm() {
    const rows = this.stepRows;
    if (rows.length > 1) {
      this.form.actionType    = 'Composite';
      this.form.actionPayload = null;
      this.form.steps         = rows.map((s, i) => ({ stepOrder: i + 1, actionType: s.actionType,
                                                      actionPayload: s.actionPayload, description: s.description?.trim() || null }));
    } else {
      const only = rows[0];
      this.form.actionType    = only?.actionType ?? '';
      this.form.actionPayload = only && only.actionType !== 'Manual' && only.actionPayload ? only.actionPayload : null;
      this.form.steps         = [];
    }
    // An untouched type keeps the stored category; a changed one re-derives it.
    this.form.fixCategory = !this.form.actionType ? ''
      : this.form.actionType === this.loaded.actionType ? this.loaded.fixCategory
      : this.defaultCategoryFor(this.form.actionType);
  }

  /** ActionType/FixCategory as opened (after Manual normalisation); blank for a new rule. */
  private loaded = { actionType: '', fixCategory: '' };

  // Reverse of the order-map above: execution type → most natural fix category.
  private defaultCategoryFor(actionType: string): string {
    const map: Record<string, string> = {
      'SqlScript': 'DbFix', 'StoredProcedure': 'DbFix',
      'CopyFile':  'FileRepair',
      'Manual':    'Manual',
      'ApiCall': 'Retry', 'Script': 'Retry', 'Composite': 'Retry',
    };
    return map[actionType] ?? 'Retry';
  }

  syncFixRuleSignal() {
    this.applyStepsToForm();
    this.formSignal.set({ ...this.form });
  }

  pickClassificationRuleById(ruleId: number | null) {
    if (ruleId == null) return;
    const cr = this.effectiveClassRules().find(r => r.ruleId === ruleId);
    const et = cr ? this.errorTypes().find(e => e.code === cr.errorTypeCode) : null;
    if (et) { this.shortcutRuleId = ruleId; this.form.errorTypeId = et.errorTypeId; this.syncFixRuleSignal(); }
  }

  setFixRuleScope(monitoredJobId: number | null) {
    this.form.monitoredJobId = monitoredJobId;
    this.fixRuleSaveConflict.set(null);
    this.syncFixRuleSignal();
  }

  openConflictingPolicy(conflict: FixPolicyRule) { this.openFor(conflict); }
  openConflictingPolicyById(id: number) {
    const rule = this.fixPolicies().find(p => p.ruleId === id);
    if (rule) this.openFor(rule);
  }

  addStep() {
    if (!this.canAddStep()) return;
    this.stepRows.push({ ...this.blankStep(), stepOrder: this.stepRows.length + 1 });
    this.syncFixRuleSignal();
  }
  removeStep(index: number) {
    if (this.stepRows.length <= 1) return;   // a fix always keeps its first row
    this.stepRows.splice(index, 1);
    this.stepRows.forEach((s, i) => s.stepOrder = i + 1);
    this.syncFixRuleSignal();
  }
  moveStep(index: number, delta: number) {
    const rows = this.stepRows;
    const target = index + delta;
    if (target < 0 || target >= rows.length) return;
    [rows[index], rows[target]] = [rows[target], rows[index]];
    rows.forEach((s, i) => s.stepOrder = i + 1);
    this.syncFixRuleSignal();
  }
  payloadPlaceholderFor(actionType: string): string {
    switch (actionType) {
      case 'SqlScript':       return 'UPDATE dbo.Files SET FileStatusCode = 0 WHERE Id = {sourceId}';
      case 'Script':          return 'powershell.exe C:\\scripts\\fix.ps1 {failureId}';
      case 'ApiCall':         return 'http://jobs.internal/api/jobs/{failureId}/retry';
      case 'StoredProcedure': return 'dbo.sp_RetryJob  or  ConnName|dbo.sp_RetryJob';
      case 'CopyFile':        return '{sourceFilePath}|{inputFolder}\\reprocess\\{sourceFileName}';
      default:                return 'payload';
    }
  }

  private blank(): UpsertFixPolicyRuleRequest {
    return { jobTypeId: 0, errorTypeId: 0, monitoredJobId: null, actionToApply: '', fixCategory: '',
             actionType: '', actionPayload: null, isAutoHealEligible: false, enabled: true, steps: [] };
  }

  private blankStep(): FixPolicyStepDto {
    return { stepOrder: 1, actionType: '', actionPayload: '', description: null };
  }

  // ── SqlScript "scope to failing row" shortcut ─────────────────────────────
  sqlFixNeedsScopeShortcut(payload: string | null | undefined): boolean {
    const q = (payload ?? '').trim();
    if (!q) return false;
    if (/^\s*EXEC\b/i.test(q)) return false;   // EXEC: add {sourceId} as a parameter by hand
    return !/\{sourceId\}/i.test(q);            // already scoped → hide
  }

  scopeClauseFor(payload: string | null | undefined): string {
    return /\bWHERE\b/i.test(payload ?? '') ? 'AND' : 'WHERE';
  }

  private appendSourceIdScope(payload: string | null | undefined): string {
    const base = (payload ?? '').replace(/;\s*$/, '').trimEnd();   // drop a trailing ';'
    return `${base} ${this.scopeClauseFor(base)} ${this.fixScopeColumn} = '{sourceId}'`;
  }

  scopeStepToSourceId(step: { actionPayload: string }) {
    step.actionPayload = this.appendSourceIdScope(step.actionPayload);
    this.syncFixRuleSignal();
  }
}
