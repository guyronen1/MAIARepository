/**
 * Column-name matching for the scan-rule drawer's column dropdowns. The options come
 * from the source table's catalog (`GET …/scan-sources/{id}/columns`) in the catalog's
 * spelling; the rule stores a column *reference* the scan quotes with the engine's
 * identifier rule (backend `SqlDialect`). The two differ on Oracle:
 *
 *   • SQL Server — the name as-is; case follows the column collation (almost always
 *     case-insensitive), so a saved `fileid` still matches the catalog's `FileId`.
 *   • Oracle — a plain name means UPPER-case (`Status` → STATUS), a "Quoted" name is
 *     exact. So a catalog `ORDER_ID` is written bare, but a mixed- or lower-case
 *     catalog name must be written `"MixedCase"` or the scan would upper-case it.
 */
import type { DbProvider, SourceColumnInfo } from '../services/config.service';
import { ColumnCategory, columnFits } from './db-check-kinds.util';

const ORACLE_PLAIN       = /^[A-Za-z][A-Za-z0-9_$#]*$/;
const ORACLE_PLAIN_UPPER = /^[A-Z][A-Z0-9_$#]*$/;

/** The reference to store for a catalog column so the scan finds exactly that column. */
export function columnRef(name: string, provider: DbProvider): string {
  if (provider !== 'Oracle') return name;
  return ORACLE_PLAIN_UPPER.test(name) ? name : `"${name}"`;
}

/** Catalog spelling a saved Oracle reference resolves to (mirrors `OracleDialect.CatalogName`). */
function oracleCatalogName(ref: string): string {
  let s = ref.trim();
  if (s.length >= 2 && s.startsWith('"') && s.endsWith('"')) return s.slice(1, -1);
  s = s.replace(/^\[+|\]+$/g, '');
  return ORACLE_PLAIN.test(s) ? s.toUpperCase() : s;
}

/** The catalog column a saved reference points at, or undefined (a stale / missing column). */
export function findColumn(columns: readonly SourceColumnInfo[], ref: string | null | undefined,
                           provider: DbProvider): SourceColumnInfo | undefined {
  const v = ref?.trim();
  if (!v) return undefined;
  if (provider === 'Oracle') {
    const target = oracleCatalogName(v);
    return columns.find(c => c.name === target);
  }
  const bare = v.replace(/^\[|\]$/g, '').toLowerCase();
  return columns.find(c => c.name.toLowerCase() === bare);
}

/**
 * Dropdown options for one field: the columns whose type fits `allowed` (other /
 * unknown types always fit), plus the currently selected column even when its type
 * doesn't — switching a check kind must never silently drop the operator's choice.
 */
export function columnOptions(columns: readonly SourceColumnInfo[], allowed: readonly ColumnCategory[] | null,
                              selected: string | null | undefined, provider: DbProvider): SourceColumnInfo[] {
  const current = findColumn(columns, selected, provider);
  return columns.filter(c => c === current || columnFits(c.typeCategory, allowed));
}

/** Detail Columns CSV → trimmed, non-empty references (same split as the backend). */
export function splitColumnList(csv: string | null | undefined): string[] {
  return (csv ?? '').split(',').map(s => s.trim()).filter(s => s.length > 0);
}
