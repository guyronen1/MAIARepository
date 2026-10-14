import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { FixOptionDrawerComponent } from './fix-option-drawer.component';
import { FixPolicyRule } from '../../../../core/services/config.service';
import { MonitoredJob } from '../../../../core/models';

/**
 * Unit coverage for the Fix Option drawer's form logic — the trickiest piece
 * extracted from JobConfigComponent in the task-4b decomposition. Exercises the
 * component's public methods directly (no template render, no HTTP) so the
 * step-rows → request mapping (1 row = single action, 2+ = Composite), the
 * category derivation, the SqlScript "scope to failing row" shortcut, and
 * step management are pinned.
 */
describe('FixOptionDrawerComponent (form logic)', () => {
  let fixture: ComponentFixture<FixOptionDrawerComponent>;
  let c: FixOptionDrawerComponent;

  const policy = (over: Partial<FixPolicyRule>): FixPolicyRule => ({
    ruleId: 7, jobTypeId: 1, errorTypeId: 2, monitoredJobId: 3, actionToApply: 'Fix it',
    fixCategory: 'DbFix', actionType: 'SqlScript', actionPayload: null,
    isAutoHealEligible: false, enabled: true, steps: [],
    ...over,
  } as FixPolicyRule);

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FixOptionDrawerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    fixture = TestBed.createComponent(FixOptionDrawerComponent);
    c = fixture.componentInstance;
    fixture.componentRef.setInput('job', { monitoredJobId: 3, sources: [] } as unknown as MonitoredJob);
  });

  // ── One row = single action ────────────────────────────────────────────────
  it('a new fix starts with one empty row and is not saveable until it has a type', () => {
    c.openFor(null);
    expect(c.stepRows.length).toBe(1);
    expect(c.stepsValid()).toBe(false);
    c.setStepType(0, 'ApiCall');
    expect(c.stepsValid()).toBe(true);
  });

  it('one row saves as that ActionType with the payload on the policy and no steps', () => {
    c.openFor(null);
    c.setStepType(0, 'SqlScript');
    c.stepRows[0].actionPayload = "UPDATE t SET a=1 WHERE Id='{sourceId}'";
    c.syncFixRuleSignal();
    expect(c.form.actionType).toBe('SqlScript');
    expect(c.form.actionPayload).toBe("UPDATE t SET a=1 WHERE Id='{sourceId}'");
    expect(c.form.steps).toEqual([]);
  });

  it('derives the natural Fix Category from the resulting execution type', () => {
    c.openFor(null);
    c.setStepType(0, 'SqlScript');
    expect(c.form.fixCategory).toBe('DbFix');
    c.setStepType(0, 'CopyFile');
    expect(c.form.fixCategory).toBe('FileRepair');
    c.setStepType(0, 'ApiCall');
    expect(c.form.fixCategory).toBe('Retry');
  });

  it('changing a row type clears its stale payload (SqlScript payload ≠ Script payload)', () => {
    c.openFor(null);
    c.setStepType(0, 'SqlScript');
    c.stepRows[0].actionPayload = "UPDATE t SET a=1 WHERE Id='{sourceId}'";
    c.setStepType(0, 'Script');
    expect(c.stepRows[0].actionPayload).toBe('');
    expect(c.form.actionPayload).toBeNull();
  });

  // ── 2+ rows = Composite ────────────────────────────────────────────────────
  it('two rows save as Composite with the rows as steps and a null header payload', () => {
    c.openFor(null);
    c.setStepType(0, 'SqlScript');
    c.stepRows[0].actionPayload = 'UPDATE a';
    c.addStep();
    c.setStepType(1, 'CopyFile');
    c.stepRows[1].actionPayload = 'x|y';
    c.stepRows[1].description = '  copy back  ';
    c.syncFixRuleSignal();
    expect(c.form.actionType).toBe('Composite');
    expect(c.form.actionPayload).toBeNull();
    expect(c.form.fixCategory).toBe('Retry');
    expect(c.form.steps).toEqual([
      { stepOrder: 1, actionType: 'SqlScript', actionPayload: 'UPDATE a', description: null },
      { stepOrder: 2, actionType: 'CopyFile',  actionPayload: 'x|y',      description: 'copy back' },
    ]);
  });

  it('removing back to one row saves as a single action again', () => {
    c.openFor(null);
    c.setStepType(0, 'ApiCall');
    c.addStep();
    c.setStepType(1, 'Script');
    c.stepRows[1].actionPayload = 'run.ps1';
    c.removeStep(0);
    expect(c.form.actionType).toBe('Script');
    expect(c.form.actionPayload).toBe('run.ps1');
    expect(c.form.steps).toEqual([]);
  });

  it('the last row cannot be removed', () => {
    c.openFor(null);
    c.removeStep(0);
    expect(c.stepRows.length).toBe(1);
  });

  // ── Manual ─────────────────────────────────────────────────────────────────
  it('Manual locks category=Manual, nulls the payload, and blocks + Add Step', () => {
    c.openFor(null);
    c.stepRows[0].actionPayload = 'something';
    c.setStepType(0, 'Manual');
    expect(c.form.actionType).toBe('Manual');
    expect(c.form.fixCategory).toBe('Manual');
    expect(c.form.actionPayload).toBeNull();
    expect(c.canAddStep()).toBe(false);
    c.addStep();
    expect(c.stepRows.length).toBe(1);
  });

  // ── Loading existing policies ──────────────────────────────────────────────
  it('opens a Composite policy as one row per step, in StepOrder', () => {
    c.openFor(policy({
      actionType: 'Composite', fixCategory: 'Retry',
      steps: [
        { stepId: 11, stepOrder: 2, actionType: 'CopyFile',  actionPayload: 'a|b', description: 'second' },
        { stepId: 10, stepOrder: 1, actionType: 'SqlScript', actionPayload: 'UPDATE', description: null },
      ],
    }));
    expect(c.stepRows.map(s => s.actionType)).toEqual(['SqlScript', 'CopyFile']);
    expect(c.stepRows.map(s => s.stepOrder)).toEqual([1, 2]);
    expect(c.form.actionType).toBe('Composite');
  });

  it('opens a single-action policy as one row carrying the policy payload', () => {
    c.openFor(policy({ actionType: 'ApiCall', fixCategory: 'Retry', actionPayload: 'http://x/{failureId}' }));
    expect(c.stepRows.length).toBe(1);
    expect(c.stepRows[0].actionType).toBe('ApiCall');
    expect(c.stepRows[0].actionPayload).toBe('http://x/{failureId}');
  });

  it('keeps the stored Fix Category while the execution type is unchanged', () => {
    c.openFor(policy({ actionType: 'SqlScript', fixCategory: 'Retry', actionPayload: 'UPDATE' }));
    expect(c.form.fixCategory).toBe('Retry');
    c.setStepType(0, 'CopyFile');
    expect(c.form.fixCategory).toBe('FileRepair');
  });

  // ── SqlScript "scope to the failing row" shortcut ──────────────────────────
  it('sqlFixNeedsScopeShortcut: true only for a write missing {sourceId} that is not an EXEC', () => {
    expect(c.sqlFixNeedsScopeShortcut('UPDATE t SET a=1')).toBe(true);
    expect(c.sqlFixNeedsScopeShortcut("UPDATE t SET a=1 WHERE Id='{sourceId}'")).toBe(false);
    expect(c.sqlFixNeedsScopeShortcut('EXEC dbo.sp_Fix')).toBe(false);
    expect(c.sqlFixNeedsScopeShortcut('')).toBe(false);
    expect(c.sqlFixNeedsScopeShortcut(null)).toBe(false);
  });

  it('scopeClauseFor: WHERE when the payload has none, AND when a WHERE already exists', () => {
    expect(c.scopeClauseFor('UPDATE t SET a=1')).toBe('WHERE');
    expect(c.scopeClauseFor('UPDATE t SET a=1 WHERE b=2')).toBe('AND');
  });

  it('scopeStepToSourceId appends the scope clause and flows to the policy payload', () => {
    c.openFor(null);
    c.setStepType(0, 'SqlScript');
    c.stepRows[0].actionPayload = 'UPDATE t SET a=1;';
    c.scopeStepToSourceId(c.stepRows[0]);
    expect(c.stepRows[0].actionPayload).toBe("UPDATE t SET a=1 WHERE [KeyColumn] = '{sourceId}'");
    expect(c.form.actionPayload).toBe("UPDATE t SET a=1 WHERE [KeyColumn] = '{sourceId}'");
  });

  // ── Step management ────────────────────────────────────────────────────────
  it('addStep / moveStep / removeStep keep StepOrder packed 1..N', () => {
    c.openFor(null);
    c.addStep(); c.addStep();
    expect(c.stepRows.map(s => s.stepOrder)).toEqual([1, 2, 3]);

    c.stepRows[0].actionPayload = 'first';
    c.moveStep(0, 1);                                  // move step 1 down
    expect(c.stepRows.map(s => s.stepOrder)).toEqual([1, 2, 3]);
    expect(c.stepRows[1].actionPayload).toBe('first');

    c.removeStep(0);
    expect(c.stepRows.map(s => s.stepOrder)).toEqual([1, 2]);
  });

  it('moveStep is a no-op at the boundaries', () => {
    c.openFor(null);
    c.setStepType(0, 'SqlScript');
    c.addStep();
    c.setStepType(1, 'Script');
    const before = c.stepRows.map(s => s.actionType);
    c.moveStep(0, -1);                                 // already at top
    c.moveStep(1, +1);                                 // already at bottom
    expect(c.stepRows.map(s => s.actionType)).toEqual(before);
  });
});
