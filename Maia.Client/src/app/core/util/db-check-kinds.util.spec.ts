import { MAX_IN_LIST_ITEMS, checkValueError, describeCheck, parseInList, usesExpectedValue } from './db-check-kinds.util';

describe('db-check-kinds.util', () => {
  it('parseInList accepts comma-separated numbers, rejects anything else', () => {
    expect(parseInList('1, 2, 3')).toEqual([1, 2, 3]);
    expect(parseInList(' 1.5,-2 ')).toEqual([1.5, -2]);
    expect(parseInList('1, x')).toBeNull();
    expect(parseInList('1,,2')).toBeNull();
    expect(parseInList('')).toBeNull();
    expect(parseInList(null)).toBeNull();
    expect(parseInList(Array.from({ length: MAX_IN_LIST_ITEMS + 1 }, (_, i) => i).join(','))).toBeNull();
  });

  it('checkValueError mirrors what each kind needs', () => {
    expect(checkValueError('ValueEquals', '5', null, null)).toBeNull();
    expect(checkValueError('ValueEquals', ' ', null, null)).not.toBeNull();
    expect(checkValueError('NotEquals', null, null, null)).not.toBeNull();
    expect(checkValueError('Contains', 'timeout', null, null)).toBeNull();
    expect(checkValueError('Contains', '', null, null)).not.toBeNull();
    expect(checkValueError('InList', '4, 5', null, null)).toBeNull();
    expect(checkValueError('InList', '4, five', null, null)).not.toBeNull();
    expect(checkValueError('ColumnRange', null, 1, null)).toBeNull();
    expect(checkValueError('ColumnRange', null, null, null)).not.toBeNull();
  });

  it('usesExpectedValue is true for every kind except ColumnRange', () => {
    expect(['ValueEquals', 'NotEquals', 'Contains', 'InList'].every(usesExpectedValue)).toBeTrue();
    expect(usesExpectedValue('ColumnRange')).toBeFalse();
  });

  it('describeCheck renders a short summary per kind', () => {
    expect(describeCheck('ValueEquals', '5', null, null)).toBe('= 5');
    expect(describeCheck('NotEquals', '0', null, null)).toBe('≠ 0');
    expect(describeCheck('Contains', 'timeout', null, null)).toBe("contains 'timeout'");
    expect(describeCheck('InList', '1, 2', null, null)).toBe('in (1, 2)');
    expect(describeCheck('ColumnRange', null, null, 10)).toBe('outside −∞ – 10');
  });
});
