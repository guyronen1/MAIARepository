import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
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

  it('onPredicateTypeChange clears the stale predicate value when set to None', () => {
    c.form.extractorPredicateValue = 'ERROR';
    c.onPredicateTypeChange(null);
    expect(c.form.extractorPredicateType).toBeNull();
    expect(c.form.extractorPredicateValue).toBeNull();

    c.onPredicateTypeChange('Equals');
    expect(c.form.extractorPredicateType).toBe('Equals');
  });
});
