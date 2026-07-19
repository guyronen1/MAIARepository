export type SeverityTone = 'critical' | 'high' | 'medium' | 'low' | 'none';

/**
 * Maps an ErrorType severity string (Critical/High/Medium/Low, or null) to a
 * tone used for the coloured severity dot in operator queues. Critical+High
 * read red (urgent), Medium amber, Low blue, unclassified/unknown neutral.
 * Single source of truth so the Failures list and Recommendations agree.
 */
export function severityTone(sev: string | null | undefined): SeverityTone {
  switch ((sev ?? '').toLowerCase()) {
    case 'critical': return 'critical';
    case 'high':     return 'high';
    case 'medium':   return 'medium';
    case 'low':      return 'low';
    default:         return 'none';
  }
}

/** Display label for a severity value; em dash when unclassified. */
export function severityLabel(sev: string | null | undefined): string {
  return sev && sev.length ? sev : '—';
}
