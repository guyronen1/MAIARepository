import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable, Subject, Subscription, timer } from 'rxjs';
import { takeUntil } from 'rxjs/operators';
import { environment } from '../../../environments/environment';
import { JobFailure, MonitoredJob, PagedResult, WorkerStatus } from '../models';
import { DashboardStats } from './failures.service';

/**
 * Wrapper for every polled slice. `value` holds the last successful payload
 * (null before the first success); `isStale` flips to true when the most
 * recent fetch failed and the value is a cached older snapshot. The UI may
 * surface a per-panel "couldn't refresh" indicator off this flag — for now
 * we just emit it without rendering.
 */
export interface PolledData<T> {
  value:         T | null;
  isStale:       boolean;
  lastUpdatedAt: Date | null;
  lastError?:    string;
}

/**
 * Coordinator for the dashboard's polling lifecycle. One 5s timer drives
 * four independent fetches every tick:
 *
 *   - GET /api/data/worker-status
 *   - GET /api/data/dashboard-stats
 *   - GET /api/data/failures?page=1&pageSize=10
 *   - GET /api/data/monitored-jobs
 *
 * Each endpoint is fully isolated: a failure on one does not block or
 * invalidate the others, and an in-flight request blocks only its own
 * endpoint from re-firing on the next tick (the other three keep ticking).
 * The failing slice retains its last successful value and marks itself
 * `isStale=true` until the next success.
 *
 * Refcount-based start/stop — multiple consumers (dashboard, scan-jobs)
 * activate polling independently; when refcount hits 0 the timer stops
 * and any in-flight requests are unsubscribed via `takeUntil(cancel$)`.
 */
@Injectable({ providedIn: 'root' })
export class WorkerStatusService {
  private http = inject(HttpClient);

  private readonly statusUrl         = `${environment.apiUrl}/data/worker-status`;
  private readonly statsUrl          = `${environment.apiUrl}/data/dashboard-stats`;
  private readonly failuresUrl       = `${environment.apiUrl}/data/failures?page=1&pageSize=10`;
  private readonly monitoredJobsUrl  = `${environment.apiUrl}/data/monitored-jobs`;
  private readonly intervalMs        = environment.dashboardRefreshIntervalMs ?? 5000;

  private static readonly EMPTY = <T>(): PolledData<T> => ({
    value: null, isStale: false, lastUpdatedAt: null,
  });

  private statusSubject         = new BehaviorSubject<PolledData<WorkerStatus>>(WorkerStatusService.EMPTY());
  private statsSubject          = new BehaviorSubject<PolledData<DashboardStats>>(WorkerStatusService.EMPTY());
  private recentFailuresSubject = new BehaviorSubject<PolledData<JobFailure[]>>(WorkerStatusService.EMPTY());
  private monitoredJobsSubject  = new BehaviorSubject<PolledData<MonitoredJob[]>>(WorkerStatusService.EMPTY());

  /** Latest worker-status payload (lease state, active scans, recent scans). */
  public status$:         Observable<PolledData<WorkerStatus>>   = this.statusSubject.asObservable();
  /** Latest aggregate KPI counts. */
  public stats$:          Observable<PolledData<DashboardStats>> = this.statsSubject.asObservable();
  /** Top-10 most-recent failures (no view filter). */
  public recentFailures$: Observable<PolledData<JobFailure[]>>   = this.recentFailuresSubject.asObservable();
  /** All active monitored jobs with rules + lease state. */
  public monitoredJobs$:  Observable<PolledData<MonitoredJob[]>> = this.monitoredJobsSubject.asObservable();

  private refcount = 0;
  private sub?:    Subscription;
  private cancel$ = new Subject<void>();

  // Per-endpoint in-flight gate — boxed booleans so closures over them mutate
  // a shared cell rather than a captured primitive. Each tick checks its own
  // box and skips if a prior request is still running for that endpoint.
  private pendingStatus        = { v: false };
  private pendingStats         = { v: false };
  private pendingFailures      = { v: false };
  private pendingMonitoredJobs = { v: false };

  /** Begin (or join) polling. Idempotent — refcount + 1 per consumer. */
  start(): void {
    this.refcount++;
    if (this.sub) return;

    // Fresh cancel channel each time we start, so a previous stop()'s emit
    // doesn't pre-cancel future fetches.
    this.cancel$ = new Subject<void>();

    this.sub = timer(0, this.intervalMs).subscribe(() => {
      this.fetchInto(this.statusUrl,        this.statusSubject,         this.pendingStatus);
      this.fetchInto(this.statsUrl,         this.statsSubject,          this.pendingStats);
      this.fetchPaged(this.failuresUrl,     this.recentFailuresSubject, this.pendingFailures);
      this.fetchInto(this.monitoredJobsUrl, this.monitoredJobsSubject,  this.pendingMonitoredJobs);
    });
  }

  /** Decrement refcount; tear down timer + abort in-flight requests at 0. */
  stop(): void {
    if (this.refcount > 0) this.refcount--;
    if (this.refcount === 0 && this.sub) {
      this.sub.unsubscribe();
      this.sub = undefined;
      this.cancel$.next();
      this.cancel$.complete();
      // Clear in-flight gates so a subsequent start() begins from a clean slate.
      this.pendingStatus.v = false;
      this.pendingStats.v = false;
      this.pendingFailures.v = false;
      this.pendingMonitoredJobs.v = false;
    }
  }

  /** Standard GET → subject pump with isolation + skip-while-pending. */
  private fetchInto<T>(
    url: string,
    subject: BehaviorSubject<PolledData<T>>,
    pending: { v: boolean },
  ): void {
    if (pending.v) return;
    pending.v = true;
    this.http.get<T>(url).pipe(takeUntil(this.cancel$)).subscribe({
      next: value => {
        subject.next({ value, isStale: false, lastUpdatedAt: new Date() });
        pending.v = false;
      },
      error: err => {
        const prev = subject.value;
        subject.next({
          value:         prev.value,
          isStale:       true,
          lastUpdatedAt: prev.lastUpdatedAt,
          lastError:     this.errorMessage(err),
        });
        pending.v = false;
      },
    });
  }

  /** /failures returns a PagedResult — unwrap `.items` into the subject. */
  private fetchPaged<T>(
    url: string,
    subject: BehaviorSubject<PolledData<T[]>>,
    pending: { v: boolean },
  ): void {
    if (pending.v) return;
    pending.v = true;
    this.http.get<PagedResult<T>>(url).pipe(takeUntil(this.cancel$)).subscribe({
      next: page => {
        subject.next({ value: page.items, isStale: false, lastUpdatedAt: new Date() });
        pending.v = false;
      },
      error: err => {
        const prev = subject.value;
        subject.next({
          value:         prev.value,
          isStale:       true,
          lastUpdatedAt: prev.lastUpdatedAt,
          lastError:     this.errorMessage(err),
        });
        pending.v = false;
      },
    });
  }

  private errorMessage(err: unknown): string {
    if (err instanceof Error)            return err.message;
    if (typeof err === 'string')         return err;
    if (err && typeof err === 'object')  return (err as { message?: string }).message ?? 'fetch failed';
    return 'fetch failed';
  }
}
