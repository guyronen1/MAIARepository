import { columnOptions, columnRef, findColumn, splitColumnList } from './source-columns.util';
import { columnCategoriesForKind, columnFits } from './db-check-kinds.util';
import type { SourceColumnInfo } from '../services/config.service';

const col = (name: string, typeCategory: SourceColumnInfo['typeCategory'] = 'string'): SourceColumnInfo =>
  ({ name, dataType: 'x', isNullable: true, typeCategory });

describe('source-columns.util', () => {
  it('columnRef: SQL Server stores the name as-is', () => {
    expect(columnRef('FileId', 'SqlServer')).toBe('FileId');
    expect(columnRef('My Column', 'SqlServer')).toBe('My Column');
  });

  it('columnRef: Oracle quotes anything an unquoted name would upper-case away', () => {
    expect(columnRef('ORDER_ID', 'Oracle')).toBe('ORDER_ID');
    expect(columnRef('MixedCase', 'Oracle')).toBe('"MixedCase"');
    expect(columnRef('lower', 'Oracle')).toBe('"lower"');
    expect(columnRef('HAS SPACE', 'Oracle')).toBe('"HAS SPACE"');
  });

  it('findColumn: SQL Server matches case-insensitively and ignores brackets', () => {
    const cols = [col('FileId'), col('Status')];
    expect(findColumn(cols, 'fileid', 'SqlServer')?.name).toBe('FileId');
    expect(findColumn(cols, '[Status]', 'SqlServer')?.name).toBe('Status');
    expect(findColumn(cols, 'Gone', 'SqlServer')).toBeUndefined();
    expect(findColumn(cols, '  ', 'SqlServer')).toBeUndefined();
  });

  it('findColumn: Oracle follows the scan identifier rule (plain → UPPER, "Quoted" → exact)', () => {
    const cols = [col('STATUS'), col('MixedCase')];
    expect(findColumn(cols, 'status', 'Oracle')?.name).toBe('STATUS');
    expect(findColumn(cols, '"MixedCase"', 'Oracle')?.name).toBe('MixedCase');
    expect(findColumn(cols, 'MixedCase', 'Oracle')).toBeUndefined();       // would mean MIXEDCASE
    expect(findColumn(cols, '"status"', 'Oracle')).toBeUndefined();        // quoted lower ≠ STATUS
  });

  it('columnFits / columnCategoriesForKind: other and unknown types always fit', () => {
    expect(columnCategoriesForKind('ValueEquals')).toBeNull();
    expect(columnCategoriesForKind('Contains')).toEqual(['string']);
    expect(columnFits('number', ['string'])).toBe(false);
    expect(columnFits('other', ['string'])).toBe(true);
    expect(columnFits('mystery', ['number'])).toBe(true);
    expect(columnFits('date', null)).toBe(true);
  });

  it('columnOptions keeps the selected column even when its type does not fit', () => {
    const cols = [col('A', 'string'), col('N', 'number')];
    expect(columnOptions(cols, ['string'], null, 'SqlServer').map(c => c.name)).toEqual(['A']);
    expect(columnOptions(cols, ['string'], 'n', 'SqlServer').map(c => c.name)).toEqual(['A', 'N']);
  });

  it('splitColumnList trims and drops empty items', () => {
    expect(splitColumnList(' A , ,B,')).toEqual(['A', 'B']);
    expect(splitColumnList(null)).toEqual([]);
  });
});
