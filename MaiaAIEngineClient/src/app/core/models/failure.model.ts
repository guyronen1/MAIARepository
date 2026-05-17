import { Recommendation } from './recommendation.model';

export type FailureStage = 'Failed' | 'Classified' | 'Recommended' | 'Fixed';
export type JobStatus    = 'Failed' | 'Resolved' | 'ManualRequired';

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
