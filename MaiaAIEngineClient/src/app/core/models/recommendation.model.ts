export type FixCategory = 'AutoFix' | 'Manual' | 'Notify' | 'Escalate';

export interface Recommendation {
  recommendationId:  number;
  failureId:         number;
  errorTypeCode:     string;
  suggestedAction:   string;
  fixCategory:       FixCategory;
  confidenceScore:   number;
  explanation:       string | null;
  autoFixAvailable:  boolean;
  operatorApproved:  boolean | null;
  isExecuted:        boolean;
  recommendedAt:     string;
  // UI-only state
  stepName?:         string | null;
  monitoredJobName?: string | null;
  errorMessage?:     string | null;
}
