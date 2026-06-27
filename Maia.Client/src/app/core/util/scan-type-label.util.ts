/**
 * Shared scan-type-label derivation (Tier 2.5 Option 1: a job is a container of
 * typed ScanSources; it has no authoritative scan type of its own). The dashboard
 * compact rows and the Monitored Jobs list both render a job's type FROM its sources
 * via these helpers, so they always agree on how a single/multi/mixed-source job reads.
 *
 *   one type           → that type        (e.g. "FileSystem")
 *   N sources, 1 type  → "FileSystem · N sources"
 *   mixed types        → "Mixed · N sources"
 *   no sources         → caller's fallback (e.g. the legacy job.scanTypeName)
 */
export function scanTypeLabelFromNames(typeNames: readonly string[], fallback = ''): string {
  if (typeNames.length === 0) return fallback;
  const distinct = [...new Set(typeNames)];
  if (distinct.length === 1)
    return typeNames.length === 1 ? distinct[0] : `${distinct[0]} · ${typeNames.length} sources`;
  return `Mixed · ${typeNames.length} sources`;
}

/** Tooltip for a rolled-up label — lists each source as "name (Type)" so a
 *  "Mixed · 3 sources" stays inspectable on hover. */
export function scanTypeTitleFromSources(
  sources: readonly { name: string; scanTypeName: string }[],
): string {
  return sources.map(s => `${s.name} (${s.scanTypeName})`).join('\n');
}

const SCAN_ICONS: Record<number, string> = { 1: '📁', 2: '🗄', 3: '🌐', 4: '📦' };

/** Icon for a single scan-type id (1=FS, 2=DB, 3=Api, 4=FileContent). */
export function scanIconForId(id: number): string {
  return SCAN_ICONS[id] ?? '📋';
}

/** Icon derived from a job's sources: one distinct type → that type's icon;
 *  mixed or none → the generic icon. */
export function scanIconForSources(sources: readonly { scanTypeId: number }[]): string {
  const distinct = [...new Set(sources.map(s => s.scanTypeId))];
  return distinct.length === 1 ? scanIconForId(distinct[0]) : '📋';
}
