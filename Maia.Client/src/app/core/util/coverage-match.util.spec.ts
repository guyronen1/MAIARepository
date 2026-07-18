import {
  computeEffectiveClassRules, scanRulePredictedPattern,
  matchScanRuleClassification, scanRuleNeedsClassification,
  effectiveFixForErrorType, EffectiveClassRule,
} from './coverage-match.util';
import type { ScanCheckRule, MonitoredJob, RuleOverride } from '../models';
import type { ClassificationRule, FixPolicyRule } from '../services/config.service';

// ── Minimal typed fixtures (functions read only a few fields each) ────────────
const override = (ruleId: number, pattern: string, errorTypeCode = 'E'): RuleOverride =>
  ({ ruleId, pattern, errorTypeCode, confidence: 0.9, priority: 1 });

const job = (rules: RuleOverride[]): MonitoredJob =>
  ({ rules } as unknown as MonitoredJob);

const classRule = (p: Partial<ClassificationRule>): ClassificationRule =>
  ({ ruleId: 0, jobTypeId: 1, isActive: true, pattern: '', errorTypeCode: 'E', ...p } as unknown as ClassificationRule);

const scanRule = (p: Partial<ScanCheckRule>): ScanCheckRule =>
  ({ checkType: 'ValueEquals', targetField: '', expectedValue: null, ...p } as unknown as ScanCheckRule);

const eff = (pattern: string, ruleId = 1, errorTypeCode = 'E'): EffectiveClassRule =>
  ({ ruleId, pattern, errorTypeCode });

const fix = (p: Partial<FixPolicyRule>): FixPolicyRule =>
  ({ ruleId: 0, enabled: true, errorTypeCode: 'E', monitoredJobId: null, ...p } as unknown as FixPolicyRule);

// ─────────────────────────────────────────────────────────────────────────────

describe('computeEffectiveClassRules', () => {
  it('unions this job\'s linked rules with JobType-global defaults', () => {
    const thisJob = job([override(1, 'Linked')]);
    const all = [
      classRule({ ruleId: 2, jobTypeId: 1, pattern: 'Default', errorTypeCode: 'D' }),  // default of this JobType, linked nowhere
    ];
    const result = computeEffectiveClassRules(thisJob, [thisJob], all, 1);
    expect(result.map(r => r.ruleId).sort()).toEqual([1, 2]);
  });

  it('excludes a JobType rule that is linked to ANOTHER job (not a floating default)', () => {
    const thisJob = job([]);
    const otherJob = job([override(2, 'Elsewhere')]);          // rule 2 is linked to otherJob
    const all = [classRule({ ruleId: 2, jobTypeId: 1, pattern: 'Elsewhere' })];
    const result = computeEffectiveClassRules(thisJob, [thisJob, otherJob], all, 1);
    expect(result).toEqual([]);                                 // not a default → excluded
  });

  it('excludes inactive rules and rules of a different JobType', () => {
    const thisJob = job([]);
    const all = [
      classRule({ ruleId: 3, jobTypeId: 1, isActive: false, pattern: 'Inactive' }),
      classRule({ ruleId: 4, jobTypeId: 2, pattern: 'OtherType' }),
    ];
    expect(computeEffectiveClassRules(thisJob, [thisJob], all, 1)).toEqual([]);
  });
});

describe('scanRulePredictedPattern', () => {
  it('ErrorKeyword / ColumnRange / FileContent → target field (wildcards stripped)', () => {
    expect(scanRulePredictedPattern(scanRule({ checkType: 'ErrorKeyword', targetField: '*ERROR*' }))).toBe('ERROR');
    expect(scanRulePredictedPattern(scanRule({ checkType: 'ColumnRange', targetField: 'Amount' }))).toBe('Amount');
    expect(scanRulePredictedPattern(scanRule({ checkType: 'FileContent', targetField: '*WARN*' }))).toBe('WARN');
  });

  it('ValueEquals → "Field=Value" when both present, else just the field', () => {
    expect(scanRulePredictedPattern(scanRule({ checkType: 'ValueEquals', targetField: 'FileStatusCode', expectedValue: '5' }))).toBe('FileStatusCode=5');
    expect(scanRulePredictedPattern(scanRule({ checkType: 'ValueEquals', targetField: 'FileStatusCode', expectedValue: null }))).toBe('FileStatusCode');
  });
});

describe('matchScanRuleClassification', () => {
  it('SqlQuery is never determinable, regardless of effective rules', () => {
    expect(matchScanRuleClassification(scanRule({ checkType: 'SqlQuery' }), [eff('anything')]).state).toBe('not-determinable');
  });

  it('with NO effective rules, a non-SqlQuery rule is a gap (dead path)', () => {
    expect(matchScanRuleClassification(scanRule({ checkType: 'ValueEquals', targetField: 'X', expectedValue: '1' }), []).state).toBe('gap');
  });

  it('ErrorKeyword / FileContent with rules present → not-determinable (broad keyword)', () => {
    expect(matchScanRuleClassification(scanRule({ checkType: 'ErrorKeyword', targetField: 'ERROR' }), [eff('ERROR')]).state).toBe('not-determinable');
    expect(matchScanRuleClassification(scanRule({ checkType: 'FileContent', targetField: 'x' }), [eff('x')]).state).toBe('not-determinable');
  });

  it('ValueEquals matches an effective rule whose pattern equals "Field=Value"', () => {
    const m = matchScanRuleClassification(
      scanRule({ checkType: 'ValueEquals', targetField: 'FileStatusCode', expectedValue: '5' }),
      [eff('FileStatusCode=5', 7)]);
    expect(m.state).toBe('matched');
    expect(m.matched.map(r => r.ruleId)).toEqual([7]);
  });

  it('Field=Value requires EXACT match — "=2" does not cover "=22"', () => {
    const m = matchScanRuleClassification(
      scanRule({ checkType: 'ValueEquals', targetField: 'FileStatusCode', expectedValue: '2' }),
      [eff('FileStatusCode=22')]);
    expect(m.state).toBe('gap');
  });

  it('ColumnRange matches when an effective literal is a substring of the column keyword', () => {
    const m = matchScanRuleClassification(
      scanRule({ checkType: 'ColumnRange', targetField: 'Amount' }),
      [eff('Amount')]);
    expect(m.state).toBe('matched');
  });

  it('ValueEquals with no matching effective rule → gap', () => {
    expect(matchScanRuleClassification(
      scanRule({ checkType: 'ValueEquals', targetField: 'A', expectedValue: '1' }),
      [eff('B=2')]).state).toBe('gap');
  });
});

describe('scanRuleNeedsClassification', () => {
  it('is true exactly when the matcher reports a gap', () => {
    const valueEquals = scanRule({ checkType: 'ValueEquals', targetField: 'A', expectedValue: '1' });
    expect(scanRuleNeedsClassification(valueEquals, [])).toBe(true);                 // gap
    expect(scanRuleNeedsClassification(valueEquals, [eff('A=1')])).toBe(false);      // matched
    expect(scanRuleNeedsClassification(scanRule({ checkType: 'SqlQuery' }), [])).toBe(false); // not-determinable
  });
});

describe('effectiveFixForErrorType', () => {
  it('returns null when no enabled policy matches the error type', () => {
    expect(effectiveFixForErrorType('E', [fix({ errorTypeCode: 'E', enabled: false })])).toBeNull();
    expect(effectiveFixForErrorType('E', [fix({ errorTypeCode: 'OTHER' })])).toBeNull();
  });

  it('a job-scoped override wins over the JobType default', () => {
    const def = fix({ ruleId: 1, monitoredJobId: null });
    const ovr = fix({ ruleId: 2, monitoredJobId: 99 });
    expect(effectiveFixForErrorType('E', [def, ovr])!.ruleId).toBe(2);
  });

  it('falls back to the default when there is no override', () => {
    const def = fix({ ruleId: 1, monitoredJobId: null });
    expect(effectiveFixForErrorType('E', [def])!.ruleId).toBe(1);
  });
});
