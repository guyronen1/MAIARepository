export interface ActiveScan {
  monitoredJobId: number;
  jobName:        string;
  scanType:       string;
  startedAt:      string | null;
  leasedUntil:    string | null;
}

/** A ScanRunHistory row from the last 30 seconds — covers the "scan completed
 *  between polls" case where the in-flight signal would have been missed. */
export interface RecentScan {
  scanRunId:        number;
  monitoredJobId:   number;
  jobName:          string;
  completedAt:      string;
  durationMs:       number;
  outcome:          'Success' | 'Failed' | 'Timeout' | 'Stolen';
  failuresDetected: number;
  classifications:  number;
  recommendations:  number;
}

export interface JobSummary {
  total:   number;
  active:  number;
  healthy: number;
  failing: number;
}

/** A scan-run summary (most-recent completed run). Shared shape for the
 *  job-level rollup and each per-source row. */
export interface LastScanSummary {
  completedAt:      string;
  durationMs:       number;
  outcome:          'Success' | 'Failed' | 'Timeout' | 'Stolen';
  failuresDetected: number;
  classifications:  number;
  recommendations:  number;
}

/** Tier 2.5: per-ScanSource last-scan summary for the dashboard drill-down.
 *  The worker writes one ScanRunHistory row per source per tick, so this is
 *  that source's most-recent run. `lastScan` null = source never ran. */
export interface SourceLastScanRow {
  scanSourceId: number;
  name:         string;
  scanTypeName: string;
  lastScan:     LastScanSummary | null;
}

/** Latest scan summary for a single job — embedded inline on the worker-status
 *  payload so the dashboard's Monitored Jobs panel can render per-row counts
 *  without an extra round-trip. `lastScan` (job-level rollup) is null when the
 *  job has never run; `sources` carries the per-source breakdown. */
export interface JobLastScanRow {
  monitoredJobId: number;
  lastScan:       LastScanSummary | null;
  sources:        SourceLastScanRow[];
}

export interface WorkerStatus {
  workerAlive:        boolean;
  /** True while the operator has paused the scan loop via the dashboard toggle. */
  isPaused:           boolean;
  lastActivityAt:     string | null;
  /** 2 × max(PollingIntervalSeconds) across active jobs. Computed server-side. */
  aliveWindowSeconds: number;
  activeScans:        ActiveScan[];
  recentScansLast30s: RecentScan[];
  jobSummary:         JobSummary;
  jobs:               JobLastScanRow[];
}

/** Lease snapshot embedded on each MonitoredJob DTO. */
export interface MonitoredJobLease {
  leasedBy:           string | null;
  leasedAt:           string | null;
  leasedUntil:        string | null;
  nextEligibleAt:     string | null;
  lastRunStartedAt:   string | null;
  lastRunCompletedAt: string | null;
  lastRunOutcome:     'Success' | 'Failed' | 'Timeout' | 'Stolen' | null;
  lastRunError:       string | null;
  lastRunDurationMs:  number | null;
}
