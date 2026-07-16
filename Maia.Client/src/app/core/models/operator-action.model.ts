/** One operator decision (Approve / Reject / Retry) on a recommendation —
 *  a row on the Operator Actions history screen. Joined context comes from
 *  the backend so the row is self-contained. */
export interface OperatorActionEntry {
  actionId:         number;
  actionTimestamp:  string;
  operatorId:       string;
  actionTaken:      'Approve' | 'Reject' | 'Retry';
  recommendationId: number;
  suggestedAction:  string | null;
  fixCategory:      string | null;
  /** Whether the recommendation ultimately executed. */
  isExecuted:       boolean;
  failureId:        number | null;
  errorTypeCode:    string | null;
  monitoredJobName: string | null;
  /** The failure's CURRENT status — where the decision led. */
  failureStatus:    string | null;
}
