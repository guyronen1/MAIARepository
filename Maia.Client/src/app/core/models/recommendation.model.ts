export type FixCategory = 'Retry' | 'FileRepair' | 'DbFix' | 'Manual';

export interface Recommendation {
  recommendationId:         number;
  failureId:                number;
  errorTypeCode:            string | null;
  errorTypeId:              number;
  jobTypeId:                number | null;
  suggestedAction:          string;
  fixCategory:              FixCategory;
  confidenceScore:          number;
  explanation:              string | null;
  autoFixAvailable:         boolean;        // frozen snapshot from generation time
  operatorApproved:         boolean | null;
  isExecuted:               boolean;
  recommendedAt:            string;
  fixPolicyRuleId:          number | null;  // null when no enabled policy matches
  policyIsAutoHealEligible: boolean | null; // live value on the matching policy
  policyStepCount:          number;         // 0 for non-composite or no policy
  // The execution mechanism of the matching policy — what the drawer reads to
  // decide "Approve" vs "Acknowledge". 'Manual' = no automation runs; anything
  // else = system executes something. Null when no enabled policy matches.
  policyActionType:         string | null;
  // UI-only state
  stepName?:         string | null;
  monitoredJobName?: string | null;
  errorMessage?:     string | null;
}
