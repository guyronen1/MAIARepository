export interface StatusMeta {
  /** Canonical human label — one wording per state, everywhere. */
  label: string;
  /** Glyph so state never depends on colour alone (colourblind + RTL safe). */
  icon: string;
  /** Semantic badge class from styles.scss. */
  badgeClass: string;
}

/**
 * Single source of truth for JobStatus presentation. Before this, failures
 * list + dashboard rendered the raw camelCase enum ("AwaitingManualAction")
 * while the detail view space-expanded it — same state, different wording.
 *
 * Note the deliberately distinct states are kept distinct (NOT merged):
 * ManualRequired ("system stuck, needs you") vs AwaitingManualAction
 * ("you approved, doing off-system work"). Only the rendering is unified.
 */
const STATUS_META: Record<string, StatusMeta> = {
  failed:               { label: 'Failed',                 icon: '✕', badgeClass: 'badge-failed' },
  resolved:             { label: 'Resolved',               icon: '✓', badgeClass: 'badge-resolved' },
  manualrequired:       { label: 'Manual Required',        icon: '✋', badgeClass: 'badge-manualrequired' },
  awaitingmanualaction: { label: 'Awaiting Manual Action', icon: '⏳', badgeClass: 'badge-awaitingmanualaction' },
};

/** Presentation for a JobStatus. Unknown values fall back to a space-expanded
 *  label + the derived badge class, so nothing ever renders unstyled. */
export function statusMeta(status: string | null | undefined): StatusMeta {
  const key = (status ?? '').toLowerCase();
  return STATUS_META[key] ?? {
    label: (status ?? '—').replace(/([A-Z])/g, ' $1').trim(),
    icon: '•',
    badgeClass: 'badge-' + key,
  };
}
