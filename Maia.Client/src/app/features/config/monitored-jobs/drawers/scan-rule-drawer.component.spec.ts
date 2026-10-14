import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { ScanRuleDrawerComponent } from './scan-rule-drawer.component';
import type { ScanSource } from '../../../../core/models';

/**
 * Scan Rule drawer (task-4b extraction). Pins the ScanType-driven blank-form
 * defaulting on open() and the soft SqlQuery "no WHERE" warning.
 */
describe('ScanRuleDrawerComponent', () => {
  let fixture: ComponentFixture<ScanRuleDrawerComponent>;
  let c: ScanRuleDrawerComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ScanRuleDrawerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    fixture = TestBed.createComponent(ScanRuleDrawerComponent);
    c = fixture.componentInstance;
  });

  const source = (scanTypeId: number): ScanSource =>
    ({ scanSourceId: 5, scanTypeId, name: 'S' } as unknown as ScanSource);

  const oracleSource = (): ScanSource =>
    ({ scanSourceId: 6, scanTypeId: 2, name: 'Billing', connectionName: 'Billing' } as unknown as ScanSource);
  const flushConnections = () =>
    TestBed.inject(HttpTestingController).expectOne(r => r.url.endsWith('/config/connection-names'))
      .flush([{ name: 'Billing', provider: 'Oracle' }, { name: 'DefaultConnection', provider: 'SqlServer' }]);

  it('reads the engine from the source connection and switches the SqlQuery guidance to Oracle', () => {
    c.open(oracleSource(), null);
    expect(c.isOracle()).toBe(false);                          // until the lookup answers
    flushConnections();
    expect(c.isOracle()).toBe(true);
    expect(c.sqlQueryPlaceholder()).not.toContain('EXEC');
    expect(c.sqlQueryTip()).toContain('Oracle');

    c.onRuleModeChange('SqlQuery');
    c.form.sourceTable = 'BEGIN check_orders; END;';
    expect(c.oracleQueryNotSelect()).toBe(true);
    c.form.sourceTable = 'SELECT order_id FROM orders WHERE stuck = 1';
    expect(c.oracleQueryNotSelect()).toBe(false);
  });

  it('keeps T-SQL guidance (EXEC allowed) for SQL Server connections and caches the lookup', () => {
    c.open(source(2), null);                                  // no connectionName → DefaultConnection
    flushConnections();
    expect(c.isOracle()).toBe(false);
    c.form.sourceTable = 'EXEC sp_Check @x=1';
    expect(c.oracleQueryNotSelect()).toBe(false);
    expect(c.sqlQueryPlaceholder()).toContain('EXEC');

    c.open(oracleSource(), null);                             // cached — no second request
    TestBed.inject(HttpTestingController).expectNone(r => r.url.endsWith('/config/connection-names'));
    expect(c.isOracle()).toBe(true);
  });

  it('does not look up connections for file-based sources', () => {
    c.open(source(1), null);
    TestBed.inject(HttpTestingController).expectNone(r => r.url.endsWith('/config/connection-names'));
    expect(c.isOracle()).toBe(false);
  });

  it('open(new) defaults the check type from the source ScanType', () => {
    c.open(source(1), null);                       // FileSystem
    expect(c.form.checkType).toBe('ErrorKeyword');
    expect(c.editingId()).toBeNull();
    expect(c.isOpen()).toBe(true);

    c.open(source(4), null);                        // FileContent
    expect(c.form.checkType).toBe('FileContent');
    expect(c.form.extractorType).toBe('Xml');

    c.open(source(2), null);                        // Database
    expect(c.form.checkType).toBe('ValueEquals');
  });

  it('open(existing) loads the rule and records its id as the edit target', () => {
    c.open(source(2), { checkRuleId: 42, checkType: 'ColumnRange', targetField: 'Amount' } as any);
    expect(c.editingId()).toBe(42);
    expect(c.form.checkType).toBe('ColumnRange');
    expect(c.form.targetField).toBe('Amount');
  });

  it('sqlQueryNeedsWhereWarning: only a non-empty, non-EXEC query lacking WHERE/HAVING warns', () => {
    c.form.sourceTable = 'SELECT * FROM Orders';
    expect(c.sqlQueryNeedsWhereWarning()).toBe(true);

    c.form.sourceTable = 'SELECT * FROM Orders WHERE Stuck = 1';
    expect(c.sqlQueryNeedsWhereWarning()).toBe(false);

    c.form.sourceTable = 'SELECT COUNT(*) c FROM Orders HAVING COUNT(*) > 5';
    expect(c.sqlQueryNeedsWhereWarning()).toBe(false);

    c.form.sourceTable = 'EXEC dbo.sp_CheckStuck';   // proc filters internally
    expect(c.sqlQueryNeedsWhereWarning()).toBe(false);

    c.form.sourceTable = '';
    expect(c.sqlQueryNeedsWhereWarning()).toBe(false);
  });

  // ── Composite scan rule — extra AND'd conditions (DB table rules) ──────────

  it('blank form has no conditions; addCondition appends a ValueEquals row with packed order', () => {
    c.open(source(2), null);
    expect(c.conditions()).toEqual([]);

    c.addCondition();
    c.addCondition();
    expect(c.conditions().length).toBe(2);
    expect(c.conditions()[0].kind).toBe('ValueEquals');
    expect(c.conditions().map(x => x.conditionOrder)).toEqual([1, 2]);
  });

  it('open(existing) copies conditions into the form without conditionId', () => {
    c.open(source(2), { checkRuleId: 7, checkType: 'ValueEquals', targetField: 'Status', expectedValue: '5',
      conditions: [
        { conditionId: 99, conditionOrder: 1, kind: 'ValueEquals', targetField: 'Region', expectedValue: 'EU', minValue: null, maxValue: null, predicateType: null, description: null },
        { conditionId: 98, conditionOrder: 2, kind: 'ColumnRange', targetField: 'Amount', expectedValue: null, minValue: 10, maxValue: 100, predicateType: null, description: null },
      ] } as any);
    const conds = c.conditions();
    expect(conds.length).toBe(2);
    expect((conds[0] as any).conditionId).toBeUndefined();
    expect(conds[1]).toEqual(jasmine.objectContaining({ kind: 'ColumnRange', targetField: 'Amount', minValue: 10, maxValue: 100 }));
  });

  it('removeCondition and moveCondition re-pack conditionOrder to 1..N', () => {
    c.open(source(2), null);
    c.addCondition(); c.addCondition(); c.addCondition();
    c.conditions()[0].targetField = 'A'; c.conditions()[1].targetField = 'B'; c.conditions()[2].targetField = 'C';

    c.moveCondition(2, -1);                                   // C up → A, C, B
    expect(c.conditions().map(x => x.targetField)).toEqual(['A', 'C', 'B']);
    expect(c.conditions().map(x => x.conditionOrder)).toEqual([1, 2, 3]);

    c.moveCondition(0, -1);                                   // no-op at the boundary
    expect(c.conditions().map(x => x.targetField)).toEqual(['A', 'C', 'B']);

    c.removeCondition(0);                                     // → C, B
    expect(c.conditions().map(x => x.targetField)).toEqual(['C', 'B']);
    expect(c.conditions().map(x => x.conditionOrder)).toEqual([1, 2]);
  });

  it('setConditionKind clears the fields the new kind does not use', () => {
    c.open(source(2), null);
    c.addCondition();
    const cond = c.conditions()[0];
    cond.expectedValue = 'EU';
    c.setConditionKind(cond, 'ColumnRange');
    expect(cond.expectedValue).toBeNull();
    cond.minValue = 1; cond.maxValue = 9;
    c.setConditionKind(cond, 'ValueEquals');
    expect(cond.minValue).toBeNull();
    expect(cond.maxValue).toBeNull();
  });

  it('switching the DB check type to SqlQuery drops the conditions', () => {
    c.open(source(2), null);
    c.addCondition();
    c.form.expectedValue = '5';
    c.onCheckTypeChange('ColumnRange');
    expect(c.conditions().length).toBe(1);                   // table-rule ↔ table-rule keeps them
    expect(c.form.expectedValue).toBeNull();                 // line 1 swaps its value fields like any row
    c.form.minValue = 1;
    c.onCheckTypeChange('ValueEquals');
    expect(c.form.minValue).toBeNull();
    c.onCheckTypeChange('SqlQuery');
    expect(c.form.checkType).toBe('SqlQuery');
    expect(c.conditions()).toEqual([]);
  });

  it('header Check Type is a shape selector: SqlQuery ↔ column conditions, kind stays on the rows', () => {
    c.open(source(2), null);                                  // ValueEquals by default
    expect(c.ruleMode()).toBe('Table');
    c.onCheckTypeChange('ColumnRange');                       // line-1 kind change
    expect(c.ruleMode()).toBe('Table');                       // header unaffected by the kind
    c.addCondition();

    c.onRuleModeChange('SqlQuery');
    expect(c.form.checkType).toBe('SqlQuery');
    expect(c.ruleMode()).toBe('SqlQuery');
    expect(c.conditions()).toEqual([]);                      // conditions dropped

    c.onRuleModeChange('Table');
    expect(c.form.checkType).toBe('ValueEquals');            // line-1 default restored

    c.onCheckTypeChange('ColumnRange');
    c.onRuleModeChange('Table');                              // re-selecting Table keeps the chosen kind
    expect(c.form.checkType).toBe('ColumnRange');
  });

  it('save() blocks on an incomplete condition with a per-condition message', () => {
    c.open(source(2), null);
    c.form.targetField = 'Status'; c.form.expectedValue = '5';
    c.addCondition();                                         // no column yet — shown as line 2 (line 1 = the rule's own check)
    c.save();
    expect(c.error()).toContain('Line 2');
    expect(c.saving()).toBe(false);

    c.conditions()[0].targetField = 'Region';                 // ValueEquals without a value
    c.save();
    expect(c.error()).toContain('expected value');

    c.setConditionKind(c.conditions()[0], 'ColumnRange');     // ColumnRange without bounds
    c.save();
    expect(c.error()).toContain('min or a max');
  });

  it('onPredicateTypeChange clears the stale predicate value when set to None', () => {
    c.form.extractorPredicateValue = 'ERROR';
    c.onPredicateTypeChange(null);
    expect(c.form.extractorPredicateType).toBeNull();
    expect(c.form.extractorPredicateValue).toBeNull();

    c.onPredicateTypeChange('Equals');
    expect(c.form.extractorPredicateType).toBe('Equals');
  });
});
