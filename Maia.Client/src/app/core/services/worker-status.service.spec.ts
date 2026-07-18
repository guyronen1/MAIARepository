import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { environment } from '../../../environments/environment';
import { WorkerStatusService, PolledData } from './worker-status.service';

/**
 * The dashboard polling coordinator — the highest-risk stateful client unit.
 * Pins: one 5s timer drives four ISOLATED slices; a slice failing keeps its last
 * value + flips isStale (others unaffected); an in-flight endpoint is skipped next
 * tick (others keep ticking); refcounted start/stop; PagedResult unwrapping.
 */
describe('WorkerStatusService', () => {
  const api = environment.apiUrl;
  const statusUrl = `${api}/data/worker-status`;
  const statsUrl  = `${api}/data/dashboard-stats`;
  const failsUrl  = `${api}/data/failures?page=1&pageSize=10`;
  const jobsUrl   = `${api}/data/monitored-jobs`;

  let svc: WorkerStatusService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [WorkerStatusService, provideHttpClient(), provideHttpClientTesting()],
    });
    svc = TestBed.inject(WorkerStatusService);
    http = TestBed.inject(HttpTestingController);
  });

  // Capture the always-current value of each BehaviorSubject-backed slice.
  function latest<T>(obs: { subscribe: (f: (v: PolledData<T>) => void) => unknown }): () => PolledData<T> {
    let cur!: PolledData<T>;
    obs.subscribe(v => (cur = v));
    return () => cur;
  }

  function flushAll(opts: { status?: unknown; stats?: unknown; items?: unknown[]; jobs?: unknown[] } = {}) {
    http.expectOne(statusUrl).flush(opts.status ?? { isPaused: false });
    http.expectOne(statsUrl).flush(opts.stats ?? { active: 0 });
    http.expectOne(failsUrl).flush({ items: opts.items ?? [] });
    http.expectOne(jobsUrl).flush(opts.jobs ?? []);
  }

  it('start() immediately fetches all four slices and emits fresh values (PagedResult unwrapped)', fakeAsync(() => {
    const status = latest(svc.status$);
    const fails = latest(svc.recentFailures$);

    svc.start();
    tick(0);                                    // timer(0, …) fires the first tick
    flushAll({ status: { isPaused: true }, items: [{ failureId: 7 }] });

    expect(status().value).toEqual({ isPaused: true } as any);
    expect(status().isStale).toBe(false);
    expect(status().lastUpdatedAt).not.toBeNull();
    expect(fails().value).toEqual([{ failureId: 7 }] as any);   // .items unwrapped

    svc.stop();
    http.verify();
  }));

  it('a failing slice keeps its last value + isStale=true; the other slices stay fresh (isolation)', fakeAsync(() => {
    const status = latest(svc.status$);
    const stats  = latest(svc.stats$);

    svc.start();
    tick(0);
    flushAll({ status: { isPaused: false }, stats: { active: 3 } });
    expect(status().value).toEqual({ isPaused: false } as any);

    tick(environment.dashboardRefreshIntervalMs ?? 5000);       // next tick
    http.expectOne(statusUrl).flush('boom', { status: 500, statusText: 'Server Error' });
    http.expectOne(statsUrl).flush({ active: 9 });
    http.expectOne(failsUrl).flush({ items: [] });
    http.expectOne(jobsUrl).flush([]);

    // Failing status slice: cached value retained, marked stale, error recorded.
    expect(status().value).toEqual({ isPaused: false } as any);
    expect(status().isStale).toBe(true);
    expect(status().lastError).toBeTruthy();
    // Unaffected stats slice refreshed normally.
    expect(stats().value).toEqual({ active: 9 } as any);
    expect(stats().isStale).toBe(false);

    svc.stop();
    http.verify();
  }));

  it('skips an endpoint whose request is still in flight, but keeps ticking the others', fakeAsync(() => {
    svc.start();
    tick(0);
    // Answer everything EXCEPT status → status stays in-flight (pending gate set).
    http.expectOne(statsUrl).flush({ active: 0 });
    http.expectOne(failsUrl).flush({ items: [] });
    http.expectOne(jobsUrl).flush([]);

    tick(environment.dashboardRefreshIntervalMs ?? 5000);       // next tick
    // match() consumes, so capture each set exactly once.
    const statusReqs = http.match(statusUrl);
    const statsReqs  = http.match(statsUrl);
    expect(statusReqs.length).toBe(1);   // status did NOT re-fire (still in flight)
    expect(statsReqs.length).toBe(1);    // stats DID fire again

    // Drain everything so verify() is clean.
    statusReqs.forEach(r => r.flush({ isPaused: false }));
    statsReqs.forEach(r => r.flush({ active: 0 }));
    http.match(failsUrl).forEach(r => r.flush({ items: [] }));
    http.match(jobsUrl).forEach(r => r.flush([]));

    svc.stop();
    http.verify();
  }));

  it('refcounts start/stop — two consumers share one timer; polling stops only at refcount 0', fakeAsync(() => {
    svc.start();
    svc.start();                                 // second consumer
    tick(0);
    // flushAll's expectOne(statusUrl) asserts EXACTLY one status request — proves
    // the two start()s share a single timer (a double timer → expectOne throws).
    flushAll();

    svc.stop();                                  // refcount 2 → 1: still polling
    tick(environment.dashboardRefreshIntervalMs ?? 5000);
    flushAll();                                  // fires again → expectOne confirms

    svc.stop();                                  // refcount 1 → 0: timer torn down
    tick(environment.dashboardRefreshIntervalMs ?? 5000);
    http.expectNone(statusUrl);                  // no request → polling stopped

    http.verify();
  }));
});
