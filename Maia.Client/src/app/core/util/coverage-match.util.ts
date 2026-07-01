/**
 * Coverage-match logic — the SINGLE source of truth for "which classification
 * rule matches a given scan rule's predicted output", shared by:
 *   - the config screen's coverage markers (job-config.component.ts), and
 *   - the read-only Job Flow view (job-flow.component.ts).
 *
 * Both call these functions so the two surfaces can NEVER disagree about whether
 * a scan rule is classified. This is a design-time HEURISTIC over rule config —
 * NOT the runtime classifier (RuleBasedClassifier / ClassificationMatcher on the
 * backend, which matches real failure messages). It deliberately mirrors the
 * markers' confident-vs-uncertain boundary:
 *
 *   ValueEquals / ColumnRange  → predictable output → link is computable.
 *   ErrorKeyword / FileContent → broad keyword; with rules present the link is
 *                                NOT asserted (only flagged as a gap when there
 *                                are zero effective rules at all).
 *   SqlQuery                   → arbitrary operator-defined output → never
 *                                determinable.
 *
 * Invariant: scanRuleNeedsClassification(rule, eff) === (match.state === 'gap').
 */
import type { ScanCheckRule, MonitoredJob } from '../models';
import type { ClassificationRule, FixPolicyRule } from '../services/config.service';

export interface EffectiveClassRule {
  ruleId: number;
  pattern: string;
  errorTypeCode: string;
}

/** Whether the scan-rule → classification link can be drawn. */
export type ClassificationLinkState = 'matched' | 'gap' | 'not-determinable';

export interface ScanRuleClassificationMatch {
  state: ClassificationLinkState;
  /** Matching effective classification rules when state === 'matched'; else []. */
  matched: EffectiveClassRule[];
}

/**
 * Effective classifier rules for a job: rules linked to this job ∪ JobType-global
 * defaults (active rules of this JobType linked to NO job). Mirrors the backend's
 * GetEffectiveRulesAsync and the (previously inline) job-config computation.
 */
export function computeEffectiveClassRules(
  job: MonitoredJob,
  allJobs: MonitoredJob[],
  allClassRules: ClassificationRule[],
  jobTypeId: number,
): EffectiveClassRule[] {
  const linked = job.rules ?? [];
  const linkedToThisJob = new Set(linked.map(r => r.ruleId));
  const linkedAnywhere = new Set(allJobs.flatMap(j => (j.rules ?? []).map(r => r.ruleId)));
  const defaults = allClassRules.filter(r =>
    r.jobTypeId === jobTypeId && r.isActive
    && !linkedToThisJob.has(r.ruleId) && !linkedAnywhere.has(r.ruleId));
  return [
    ...linked.map(r => ({ ruleId: r.ruleId, pattern: r.pattern, errorTypeCode: r.errorTypeCode })),
    ...defaults.map(r => ({ ruleId: r.ruleId, pattern: r.pattern, errorTypeCode: r.errorTypeCode })),
  ];
}

/**
 * The representative keyword a scan rule's failure message will contain.
 *   - ErrorKeyword / FileContent → the keyword / filename pattern itself
 *   - ValueEquals → "TargetField=ExpectedValue" (e.g. "FileStatusCode=5")
 *   - ColumnRange → the column name
 */
export function scanRulePredictedPattern(rule: ScanCheckRule): string {
  const target = (rule.targetField ?? '').replace(/\*/g, '').trim();
  switch (rule.checkType) {
    case 'ErrorKeyword': return target;
    case 'FileContent':  return target;
    case 'ValueEquals':  return target && rule.expectedValue ? `${target}=${rule.expectedValue}` : target;
    case 'ColumnRange':  return target;
    default:             return target;
  }
}

/**
 * Resolve a scan rule's classification link against the effective rules. See the
 * file header for the confident-vs-uncertain boundary this encodes.
 */
export function matchScanRuleClassification(
  rule: ScanCheckRule,
  effective: EffectiveClassRule[],
): ScanRuleClassificationMatch {
  // SqlQuery output is operator-defined — never assert a link.
  if (rule.checkType === 'SqlQuery') return { state: 'not-determinable', matched: [] };
  // No classifier wired up at all → every non-SqlQuery rule is a dead path.
  if (effective.length === 0) return { state: 'gap', matched: [] };
  // Broad keywords: a matched line could be anything, so with rules present we
  // neither assert nor flag — same conservative stance as the markers.
  if (rule.checkType === 'ErrorKeyword' || rule.checkType === 'FileContent')
    return { state: 'not-determinable', matched: [] };

  const keyword = scanRulePredictedPattern(rule).toLowerCase();
  if (!keyword) return { state: 'not-determinable', matched: [] };

  const matched = effective.filter(cr => {
    const literal = cr.pattern.replace(/\*/g, '').trim().toLowerCase();
    if (!literal) return false;
    // Both are Field=Value: require exact match so "=2" doesn't cover "=22".
    if (keyword.includes('=') && literal.includes('=')) return keyword === literal;
    return keyword.includes(literal);
  });
  return matched.length ? { state: 'matched', matched } : { state: 'gap', matched: [] };
}

/**
 * Boolean coverage-gap proxy used by the config screen's ⚠ markers. Defined in
 * terms of matchScanRuleClassification so the marker and the flow view share one
 * computation (gap === the matcher says 'gap').
 */
export function scanRuleNeedsClassification(rule: ScanCheckRule, effective: EffectiveClassRule[]): boolean {
  return matchScanRuleClassification(rule, effective).state === 'gap';
}

/**
 * The fix policy that effectively applies for an ErrorType: enabled only, with a
 * job-scoped override winning over the JobType default — mirrors the backend's
 * IFixPolicyRepository.GetForAsync priority. Null = no enabled fix (Case-B gap).
 */
export function effectiveFixForErrorType(errorTypeCode: string, policies: FixPolicyRule[]): FixPolicyRule | null {
  const enabled = policies.filter(p => p.enabled && p.errorTypeCode === errorTypeCode);
  if (enabled.length === 0) return null;
  return enabled.find(p => p.monitoredJobId !== null) ?? enabled[0];
}
