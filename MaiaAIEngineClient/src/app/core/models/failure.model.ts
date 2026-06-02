import { Recommendation } from './recommendation.model';

export type FailureStage = 'Failed' | 'Classified' | 'Recommended' | 'Acknowledged' | 'Manual' | 'Fixed';
export type JobStatus    = 'Failed' | 'Resolved' | 'ManualRequired' | 'AwaitingManualAction';

export interface JobFailure {
  failureId:        number;
  jobId:            number;
  jobTypeName:      string;
  sourceId:         string | null;
  stepName:         string | null;
  errorMessage:     string | null;
  detectedAt:       string;
  status:           JobStatus;
  errorTypeCode:    string | null;
  monitoredJobName: string | null;
  /** True when this failure has a Success=false FixExecutionLog row since
   *  today-midnight. Drives the "Failed to Execute" marker in the failures
   *  list, independent of the active view filter. Backend computes this
   *  via a batched lookup after paging. */
  hasRecentFixFailure: boolean;
}

export interface FailureStatus {
  failureId:        number;
  sourceId:         string | null;
  stepName:         string | null;
  errorMessage:     string | null;
  detectedAt:       string;
  status:           JobStatus;
  stage:            FailureStage;
  errorTypeCode:    string | null;
  monitoredJobName: string | null;
  recommendations:  Recommendation[];
}

export interface PagedResult<T> {
  totalCount: number;
  totalPages: number;
  page:       number;
  pageSize:   number;
  items:      T[];
}
