import { TestBed, ComponentFixture, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { COLUMNS_DEBOUNCE_MS, ScanRuleDrawerComponent } from './scan-rule-drawer.component';
import { AuthService } from '../../../../core/services/auth.service';
import { columnOptions } from '../../../../core/util/source-columns.util';
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

  // ── New DB check kinds: NotEquals / Contains / InList ─────────────────────
  it('offers every DB check kind on line 1 and on condition rows', () => {
    expect([...c.conditionKinds]).toEqual(['ValueEquals', 'NotEquals', 'Contains', 'InList', 'ColumnRange']);
  });

  it('value kinds keep the value field and drop range bounds; ColumnRange drops the value', () => {
    c.open(source(2), null);
    c.form.minValue = 1; c.form.maxValue = 9; c.form.expectedValue = '1, 2';
    c.onCheckTypeChange('InList');
    expect(c.form.minValue).toBeNull();
    expect(c.form.maxValue).toBeNull();
    expect(c.form.expectedValue).toBe('1, 2');

    c.addCondition();
    const cond = c.conditions()[0];
    cond.minValue = 3; cond.expectedValue = 'timeout';
    c.setConditionKind(cond, 'Contains');
    expect(cond.minValue).toBeNull();
    expect(cond.expectedValue).toBe('timeout');
    c.setConditionKind(cond, 'ColumnRange');
    expect(cond.expectedValue).toBeNull();
  });

  it('save() blocks a line-1 In List that is not numbers, and a Contains without text', () => {
    c.open(source(2), null);
    c.form.targetField = 'Code';
    c.onCheckTypeChange('InList');
    c.form.expectedValue = '1, two';
    c.save();
    expect(c.error()).toContain('Line 1');
    expect(c.error()).toContain('numbers');
    expect(c.saving()).toBe(false);

    c.form.expectedValue = '1, 2';
    c.addCondition();
    c.conditions()[0].targetField = 'Details';
    c.setConditionKind(c.conditions()[0], 'Contains');
    c.save();
    expect(c.error()).toContain('Line 2');
    expect(c.error()).toContain('text to search');
  });

  // ── Column dropdowns loaded from the source table ─────────────────────────

  const FILES_COLUMNS = [
    { name: 'FileId',     dataType: 'int',           isNullable: false, typeCategory: 'number' },
    { name: 'FileName',   dataType: 'nvarchar(260)', isNullable: true,  typeCategory: 'string' },
    { name: 'Status',     dataType: 'int',           isNullable: true,  typeCategory: 'number' },
    { name: 'UpdateDate', dataType: 'datetime2(7)',  isNullable: true,  typeCategory: 'date'   },
    { name: 'RowGuid',    dataType: 'uniqueidentifier', isNullable: false, typeCategory: 'other' },
  ];
  const asAdmin = () => TestBed.inject(AuthService).currentUser.set(
    { username: 'admin', role: 'Administrator', mustChangePassword: false, canSkipPasswordChange: false, knowledgeAssistantEnabled: false });
  const http = () => TestBed.inject(HttpTestingController);
  const columnsReq = () => http().expectOne(r => r.url.endsWith('/config/scan-sources/5/columns'));
  const filesRule = (extra: object = {}) =>
    ({ checkRuleId: 3, checkType: 'ValueEquals', sourceTable: 'dbo.Files', targetField: 'Status', expectedValue: '5', ...extra } as any);

  it('loads the Source Table columns on open for an admin and passes the table through', () => {
    asAdmin();
    c.open(source(2), filesRule());
    const req = columnsReq();
    expect(req.request.params.get('table')).toBe('dbo.Files');
    expect(c.columnsLoading()).toBe(true);
    req.flush(FILES_COLUMNS);
    expect(c.columns()?.map(x => x.name)).toEqual(['FileId', 'FileName', 'Status', 'UpdateDate', 'RowGuid']);
    expect(c.columnsLoading()).toBe(false);
    expect(c.staleColumns()).toEqual([]);
  });

  it('does not look columns up for non-admins, SqlQuery rules or a rule without a table', () => {
    c.open(source(2), filesRule());                                     // not signed in as admin
    http().expectNone(r => r.url.includes('/columns'));
    asAdmin();
    c.open(source(2), filesRule({ checkType: 'SqlQuery', sourceTable: 'SELECT 1' }));
    http().expectNone(r => r.url.includes('/columns'));
    c.open(source(2), null);                                            // new rule — no table yet
    http().expectNone(r => r.url.includes('/columns'));
    expect(c.columns()).toBeNull();
  });

  it('reloads (debounced) when Source Table changes and caches per (source, table)', fakeAsync(() => {
    asAdmin();
    c.open(source(2), filesRule());
    columnsReq().flush(FILES_COLUMNS);

    c.onSourceTableChange('dbo.J');
    c.onSourceTableChange('dbo.Jobs');
    tick(COLUMNS_DEBOUNCE_MS - 1);
    http().expectNone(r => r.url.includes('/columns'));                 // still typing
    tick(1);
    const req = columnsReq();                                           // one request, for the last value
    expect(req.request.params.get('table')).toBe('dbo.Jobs');
    req.flush([{ name: 'JobId', dataType: 'int', isNullable: false, typeCategory: 'number' }]);
    expect(c.columns()?.map(x => x.name)).toEqual(['JobId']);

    c.onSourceTableChange('dbo.Files');                                 // cached → immediate, no request
    expect(c.columns()?.length).toBe(5);
    tick(COLUMNS_DEBOUNCE_MS);
    http().expectNone(r => r.url.includes('/columns'));
  }));

  it('narrows the column options by check kind; other-typed columns stay selectable', () => {
    const names = (kind: string | null, selected: string | null = null) =>
      columnOptions(FILES_COLUMNS as any, kind === 'watermark' ? c.watermarkColumns : c.kindCategories(kind!), selected, 'SqlServer')
        .map(x => x.name);
    expect(names('Contains')).toEqual(['FileName', 'RowGuid']);
    expect(names('InList')).toEqual(['FileId', 'Status', 'RowGuid']);
    expect(names('ColumnRange')).toEqual(['FileId', 'Status', 'RowGuid']);
    expect(names('watermark')).toEqual(['FileId', 'Status', 'UpdateDate', 'RowGuid']);
    expect(names('ValueEquals')).toEqual(['FileId', 'FileName', 'Status', 'UpdateDate', 'RowGuid']);
    expect(names('NotEquals').length).toBe(5);
    // Switching a kind never drops the chosen column, even when its type doesn't fit.
    expect(names('Contains', 'Status')).toEqual(['FileName', 'Status', 'RowGuid']);
  });

  it('falls back to free text with a hint for a JOIN table, and saves anyway', () => {
    asAdmin();
    c.open(source(2), filesRule({ sourceTable: 'dbo.Files f JOIN dbo.Jobs j ON j.Id = f.JobId', targetField: 'f.Status' }));
    columnsReq().flush({ error: 'TableNotResolvable', message: 'not a single table' }, { status: 400, statusText: 'Bad Request' });
    expect(c.columns()).toBeNull();
    expect(c.columnsHint()).toContain('JOIN');
    expect(c.staleColumns()).toEqual([]);                               // free text — nothing to compare against

    c.save();
    http().expectOne(r => r.method === 'PUT' && r.url.endsWith('/scan-rules/3'));
  });

  it('a dead connection shows the reason in the hint and still saves', () => {
    asAdmin();
    c.open(source(2), filesRule());
    columnsReq().flush({ error: 'ConnectionFailed', message: "Could not connect to 'B2BTest': timeout" },
                       { status: 400, statusText: 'Bad Request' });
    expect(c.columns()).toBeNull();
    expect(c.columnsHint()).toContain("Could not connect to 'B2BTest'");
    expect(c.columnsHint()).toContain('type the column names');
    c.save();
    http().expectOne(r => r.method === 'PUT' && r.url.endsWith('/scan-rules/3'));
  });

  it('an API without the endpoint (bare 404) says so instead of a reasonless hint', () => {
    asAdmin();
    c.open(source(2), filesRule());
    columnsReq().flush(null, { status: 404, statusText: 'Not Found' });
    expect(c.columns()).toBeNull();
    expect(c.columnsHint()).toContain('restart / update Maia.API');
  });

  it('a 404 TableNotFound from the endpoint keeps the server reason', () => {
    asAdmin();
    c.open(source(2), filesRule());
    columnsReq().flush({ error: 'TableNotFound', message: "Table 'dbo.Files' was not found on 'B2BTest'" },
                       { status: 404, statusText: 'Not Found' });
    expect(c.columnsHint()).toContain("was not found on 'B2BTest'");
  });

  it('keeps a saved column missing from the table, flags it, and still saves', () => {
    asAdmin();
    c.open(source(2), filesRule({ targetField: 'status', watermarkColumn: 'Gone', detailColumns: 'FileName, Legacy' }));
    columnsReq().flush(FILES_COLUMNS);
    expect(c.isStaleColumn('status')).toBe(false);                      // case-insensitive on SQL Server
    expect(c.staleColumns()).toEqual(['Legacy', 'Gone']);
    expect(c.form.watermarkColumn).toBe('Gone');                        // kept, not cleared
    c.save();
    const put = http().expectOne(r => r.method === 'PUT' && r.url.endsWith('/scan-rules/3'));
    expect(put.request.body.watermarkColumn).toBe('Gone');
  });

  it('Detail Columns multi-select adds and removes columns as the same CSV', () => {
    asAdmin();
    c.open(source(2), filesRule({ detailColumns: 'FileName' }));
    columnsReq().flush(FILES_COLUMNS);
    expect(c.detailOptions().map(x => x.name)).not.toContain('FileName');

    const select = document.createElement('select');
    const opt = document.createElement('option'); opt.value = 'UpdateDate'; select.appendChild(opt);
    select.value = 'UpdateDate';
    c.addDetailColumn(select);
    expect(c.form.detailColumns).toBe('FileName, UpdateDate');
    expect(select.value).toBe('');                                       // reset to "+ Add column…"

    c.removeDetailColumn(0);
    expect(c.form.detailColumns).toBe('UpdateDate');
    c.removeDetailColumn(0);
    expect(c.form.detailColumns).toBeNull();
  });

  it('renders dropdowns once the columns load, inputs before', () => {
    asAdmin();
    c.open(source(2), filesRule());
    fixture.detectChanges();
    const el: HTMLElement = document.body;
    expect(el.querySelector('select[aria-label="Watermark Column"]')).toBeNull();
    expect(el.querySelector('input[aria-label="Watermark Column"]')).not.toBeNull();

    columnsReq().flush(FILES_COLUMNS);
    fixture.detectChanges();
    const wm = el.querySelector('select[aria-label="Watermark Column"]') as HTMLSelectElement;
    expect(wm).not.toBeNull();
    const labels = Array.from(wm.options).map(o => o.textContent!.trim());
    expect(labels.some(l => l.startsWith('UpdateDate'))).toBe(true);
    expect(labels.some(l => l.startsWith('FileName'))).toBe(false);     // strings aren't a scan cursor
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
