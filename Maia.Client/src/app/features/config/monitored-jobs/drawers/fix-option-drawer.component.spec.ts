import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { FixOptionDrawerComponent } from './fix-option-drawer.component';

/**
 * Unit coverage for the Fix Option drawer's form logic — the trickiest piece
 * extracted from JobConfigComponent in the task-4b decomposition. Exercises the
 * component's public methods directly (no template render, no HTTP) so the
 * execution-type→category derivation, the SqlScript "scope to failing row"
 * shortcut, and composite-step management are pinned.
 */
describe('FixOptionDrawerComponent (form logic)', () => {
  let fixture: ComponentFixture<FixOptionDrawerComponent>;
  let c: FixOptionDrawerComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FixOptionDrawerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    fixture = TestBed.createComponent(FixOptionDrawerComponent);
    c = fixture.componentInstance;
  });

  // ── Execution type → Fix Category derivation (Fix Category is read-only) ────
  it('derives the natural Fix Category from the execution type', () => {
    c.setFixRuleActionType('SqlScript');
    expect(c.form.actionType).toBe('SqlScript');
    expect(c.form.fixCategory).toBe('DbFix');

    c.setFixRuleActionType('CopyFile');
    expect(c.form.fixCategory).toBe('FileRepair');

    c.setFixRuleActionType('ApiCall');
    expect(c.form.fixCategory).toBe('Retry');
  });

  it('Manual execution locks category=Manual and clears payload + steps', () => {
    c.form.actionPayload = 'something';
    c.form.steps = [{ stepOrder: 1, actionType: 'SqlScript', actionPayload: 'x', description: null }];
    c.setFixRuleActionType('Manual');
    expect(c.form.fixCategory).toBe('Manual');
    expect(c.form.actionPayload).toBeNull();
    expect(c.form.steps).toEqual([]);
  });

  it('switching TO Composite clears the header payload; switching AWAY clears steps', () => {
    c.setFixRuleActionType('SqlScript');
    c.form.actionPayload = 'UPDATE t SET a=1';
    c.setFixRuleActionType('Composite');
    expect(c.form.actionPayload).toBeNull();

    c.addStep();
    expect(c.form.steps!.length).toBe(1);
    c.setFixRuleActionType('Script');
    expect(c.form.steps).toEqual([]);
  });

  it('changing execution type clears a stale payload (SqlScript payload ≠ Script payload)', () => {
    c.setFixRuleActionType('SqlScript');
    c.form.actionPayload = "UPDATE t SET a=1 WHERE Id='{sourceId}'";
    c.setFixRuleActionType('Script');
    expect(c.form.actionPayload).toBeNull();
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

  it('scopeFixPayloadToSourceId appends the scope clause with the placeholder column', () => {
    c.setFixRuleActionType('SqlScript');
    c.form.actionPayload = 'UPDATE t SET a=1;';
    c.scopeFixPayloadToSourceId();
    expect(c.form.actionPayload).toBe("UPDATE t SET a=1 WHERE [KeyColumn] = '{sourceId}'");
  });

  // ── Composite step management ──────────────────────────────────────────────
  it('addStep / moveStep / removeStep keep StepOrder packed 1..N', () => {
    c.addStep(); c.addStep(); c.addStep();
    expect(c.form.steps!.map(s => s.stepOrder)).toEqual([1, 2, 3]);

    c.form.steps![0].actionPayload = 'first';
    c.moveStep(0, 1);                                  // move step 1 down
    expect(c.form.steps!.map(s => s.stepOrder)).toEqual([1, 2, 3]);
    expect(c.form.steps![1].actionPayload).toBe('first');

    c.removeStep(0);
    expect(c.form.steps!.map(s => s.stepOrder)).toEqual([1, 2]);
  });

  it('moveStep is a no-op at the boundaries', () => {
    c.addStep(); c.addStep();
    const before = c.form.steps!.map(s => s.actionType);
    c.moveStep(0, -1);                                 // already at top
    c.moveStep(1, +1);                                 // already at bottom
    expect(c.form.steps!.map(s => s.actionType)).toEqual(before);
  });
});
