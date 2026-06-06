import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

/** Case A — a suggested grouping of unclassified failures. */
export interface UnclassifiedCluster {
  suggestedPattern:  string;
  normalizedSample:  string;   // what the analyzer clustered on (prefix/timestamp/ids masked)
  failureCount:      number;
  sampleFailureIds:  number[];
  sampleMessages:    string[];
  analyzerVersion:   string;   // "ngram-v1" — recorded as rule provenance on accept
  suggestedFromHash: string;   // provenance hash, passed back on create
  confidenceScore:   number | null;
}

export interface ClustersResponse {
  window:             string;
  analyzerVersion:    string;
  totalUnclassified:  number;
  clusteredCount:     number;
  uncategorizedCount: number;
  clusters:           UnclassifiedCluster[];
}

/** Case B — a classified failure context with no effective fix policy. */
export interface PolicyGap {
  errorTypeId:      number;
  errorTypeCode:    string;
  jobTypeId:        number;
  jobTypeName:      string;
  monitoredJobId:   number | null;
  monitoredJobName: string | null;
  count:            number;
  sampleFailureId:  number;
}

export interface PolicyGapsResponse {
  window:    string;
  totalGaps: number;
  gaps:      PolicyGap[];
}

export type UnconfiguredWindow = '30d' | 'all';

@Injectable({ providedIn: 'root' })
export class UnconfiguredService {
  private http = inject(HttpClient);
  private base = `${environment.apiUrl}`;

  getClusters(window: UnconfiguredWindow): Observable<ClustersResponse> {
    return this.http.get<ClustersResponse>(`${this.base}/unconfigured/clusters?window=${window}`);
  }

  getPolicyGaps(window: UnconfiguredWindow): Observable<PolicyGapsResponse> {
    return this.http.get<PolicyGapsResponse>(`${this.base}/unconfigured/policy-gaps?window=${window}`);
  }
}
