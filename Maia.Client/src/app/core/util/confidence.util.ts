export type ConfidenceBand = 'low' | 'mid' | 'high';

/**
 * Value-encoded confidence band for the shared `.confidence-bar` fill colour.
 * Input is a 0–1 score. Thresholds (on the 0–100 scale): <50 low (red),
 * 50–79 mid (amber), ≥80 high (green). Single source of truth so every
 * confidence bar — Recommendations, failure drawer, classification rules —
 * agrees on where the colour boundaries sit.
 */
export function confidenceBand(score: number): ConfidenceBand {
  const pct = (score ?? 0) * 100;
  if (pct < 50) return 'low';
  if (pct < 80) return 'mid';
  return 'high';
}
