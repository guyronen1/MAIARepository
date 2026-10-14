/**
 * Database table-rule check kinds — the client mirror of the backend's
 * `Maia.Core.Scanning.DbCheckKinds`. Used by the scan-rule drawer (kind select,
 * value field, validation) and by the rule summaries on the job-config list and
 * the Flow view, so every surface describes a check the same way.
 *
 *   ValueEquals — col = value
 *   NotEquals   — col <> value (rows where the column is NULL are not flagged)
 *   Contains    — col LIKE '%text%' (the text is matched literally)
 *   InList      — col IN (1, 2, 3) — numbers only
 *   ColumnRange — col outside [min, max]
 */

export const DB_CHECK_KINDS = ['ValueEquals', 'NotEquals', 'Contains', 'InList', 'ColumnRange'] as const;
export type DbCheckKind = typeof DB_CHECK_KINDS[number];

/** Most numbers an In List check accepts (backend `DbCheckKinds.MaxInListItems`). */
export const MAX_IN_LIST_ITEMS = 100;

const LABELS: Record<DbCheckKind, string> = {
  ValueEquals: 'Equals (=)',
  NotEquals:   'Not equal (≠)',
  Contains:    'Contains (text)',
  InList:      'In list (numbers)',
  ColumnRange: 'Outside range',
};

export function dbCheckKindLabel(kind: string): string {
  return LABELS[kind as DbCheckKind] ?? kind;
}

/** The kind compares against `expectedValue` (everything except ColumnRange). */
export function usesExpectedValue(kind: string): boolean {
  return kind === 'ValueEquals' || kind === 'NotEquals' || kind === 'Contains' || kind === 'InList';
}

export function valuePlaceholder(kind: string): string {
  switch (kind) {
    case 'NotEquals': return '≠ value';
    case 'Contains':  return 'text to find';
    case 'InList':    return '1, 2, 3';
    default:          return '= value';
  }
}

/** Numbers parsed from an In List value, or null when it isn't 1..MAX numbers.
 *  Same grammar as the backend (comma-separated, '.' decimal point). */
export function parseInList(value: string | null | undefined): number[] | null {
  if (!value?.trim()) return null;
  const items = value.split(',').map(s => s.trim());
  if (items.length > MAX_IN_LIST_ITEMS || items.some(s => !/^[+-]?(\d+\.?\d*|\.\d+)$/.test(s))) return null;
  return items.map(Number);
}

/** What's missing for this kind, or null when the check is complete. Mirrors the
 *  backend's CheckValueRequired / InvalidInList / ConditionIncomplete 400s. */
export function checkValueError(kind: string, expectedValue: string | number | null | undefined,
                                minValue: number | null | undefined, maxValue: number | null | undefined): string | null {
  const text = (expectedValue ?? '').toString().trim();
  switch (kind) {
    case 'ColumnRange': return minValue == null && maxValue == null ? 'set a min or a max' : null;
    case 'InList':      return parseInList(text) ? null : `enter 1–${MAX_IN_LIST_ITEMS} comma-separated numbers, e.g. 1, 2, 3`;
    case 'Contains':    return text ? null : 'enter the text to search for';
    case 'NotEquals':
    case 'ValueEquals': return text ? null : 'expected value is required';
    default:            return null;
  }
}

/** Short summary of one check, e.g. "= 5", "≠ 0", "contains 'timeout'", "in (1, 2)". */
export function describeCheck(kind: string, expectedValue: string | null, minValue: number | null, maxValue: number | null): string {
  switch (kind) {
    case 'ValueEquals': return `= ${expectedValue ?? ''}`;
    case 'NotEquals':   return `≠ ${expectedValue ?? ''}`;
    case 'Contains':    return `contains '${expectedValue ?? ''}'`;
    case 'InList':      return `in (${expectedValue ?? ''})`;
    case 'ColumnRange': return `outside ${minValue ?? '−∞'} – ${maxValue ?? '+∞'}`;
    default:            return '';
  }
}

/** Column type families a check kind can test, for the scan-rule drawer's column
 *  dropdowns; null = any column. `other` / unknown types are always offered on top
 *  (see `columnFits`) — the filter narrows, it never hides a column the API couldn't type. */
export type ColumnCategory = 'string' | 'number' | 'date' | 'other';
const STRING_ONLY: readonly ColumnCategory[] = ['string'];
const NUMBER_ONLY: readonly ColumnCategory[] = ['number'];
/** A scan cursor must sort: dates or numbers. */
export const WATERMARK_COLUMN_CATEGORIES: readonly ColumnCategory[] = ['date', 'number'];

export function columnCategoriesForKind(kind: string): readonly ColumnCategory[] | null {
  switch (kind) {
    case 'Contains':    return STRING_ONLY;
    case 'InList':
    case 'ColumnRange': return NUMBER_ONLY;
    default:            return null;            // ValueEquals / NotEquals compare any column
  }
}

/** A column of `category` may be offered where `allowed` applies. */
export function columnFits(category: string | null | undefined, allowed: readonly ColumnCategory[] | null): boolean {
  if (!allowed) return true;
  if (category !== 'string' && category !== 'number' && category !== 'date') return true;   // other / unknown
  return allowed.includes(category);
}
