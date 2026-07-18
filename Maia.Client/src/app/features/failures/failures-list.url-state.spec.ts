import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, ParamMap, convertToParamMap } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';
import { FailuresListComponent } from './failures-list.component';
import { FailuresService } from '../../core/services/failures.service';
import { NotificationService } from '../../core/services/notification.service';

/**
 * The URL-as-source-of-truth behaviour of the failures list: inbound
 * queryParamMap → signals, and outbound patchUrl default-stripping (so shared
 * links stay clean). Driven by a mocked ActivatedRoute + a spied Router; the
 * component is exercised via ngOnInit() directly, so the template (and the
 * drawer / failure-detail children) never render.
 */
describe('FailuresListComponent — URL state round-trip', () => {
  let component: FailuresListComponent;
  let qpm$: Subject<ParamMap>;
  let navigate: jasmine.Spy;
  let getFailures: jasmine.Spy;
  let notifyError: jasmine.Spy;

  const pagedFixture = (items: unknown[] = [], totalPages = 1) =>
    ({ items, totalCount: items.length, totalPages, page: 1, pageSize: 50 });

  /** Last queryParams object handed to router.navigate. */
  const lastQP = () => navigate.calls.mostRecent().args[1].queryParams as Record<string, string | null>;

  beforeEach(() => {
    qpm$ = new Subject<ParamMap>();
    navigate = jasmine.createSpy('navigate');
    getFailures = jasmine.createSpy('getFailures').and.returnValue(of(pagedFixture()));
    notifyError = jasmine.createSpy('error');

    TestBed.configureTestingModule({
      imports: [FailuresListComponent],
      providers: [
        { provide: ActivatedRoute, useValue: { queryParamMap: qpm$.asObservable() } },
        // `events` is consumed by NavigationHistoryService (pulled in transitively
        // via the DrawerComponent import); an empty stream is enough here.
        { provide: Router, useValue: { navigate, events: of() } },
        { provide: FailuresService, useValue: { getFailures } },
        { provide: NotificationService, useValue: { error: notifyError } },
      ],
    });
    component = TestBed.createComponent(FailuresListComponent).componentInstance;
  });

  const emit = (params: Record<string, string>) => qpm$.next(convertToParamMap(params));

  // ── Inbound: URL → state ────────────────────────────────────────────────
  it('maps query params onto the state signals and fetches', () => {
    component.ngOnInit();
    emit({ view: 'active', status: 'Failed', q: 'abc', page: '3', selected: '77', sort: 'job', dir: 'asc' });

    expect(component.view()).toBe('active');
    expect(component.filterStatus()).toBe('Failed');
    expect(component.filterText()).toBe('abc');
    expect(component.page()).toBe(3);
    expect(component.selectedId()).toBe(77);
    expect(component.sort()).toBe('job');
    expect(component.dir()).toBe('asc');
    expect(getFailures).toHaveBeenCalledWith(3, 50, 'active', 'job', 'asc');
  });

  it('surfaces a toast (not silent failure) when the page fails to load', () => {
    getFailures.and.returnValue(throwError(() => new Error('server down')));
    component.ngOnInit();
    emit({});                                     // first emission → fetchPage → error
    expect(component.loading()).toBe(false);      // spinner cleared
    expect(notifyError).toHaveBeenCalled();       // operator gets a signal
  });

  it('normalizes edge inputs: view="all"→null, bad page→1, unknown sort→default, non-asc dir→desc', () => {
    component.ngOnInit();
    emit({ view: 'all', page: 'oops', sort: 'nonsense', dir: 'sideways' });

    expect(component.view()).toBeNull();
    expect(component.page()).toBe(1);
    expect(component.sort()).toBe('detected');   // SORT_DEFAULT
    expect(component.dir()).toBe('desc');         // DIR_DEFAULT
  });

  // ── Outbound: patchUrl strips defaults ──────────────────────────────────
  it('setFilterStatus emits the status but strips the default page and null selected', () => {
    component.setFilterStatus('Failed');
    expect(lastQP()).toEqual({ status: 'Failed', page: null, selected: null });
  });

  it('clearFilters nulls every filter key', () => {
    component.clearFilters();
    expect(lastQP()).toEqual({ status: null, q: null, page: null, selected: null });
  });

  it('openDrawer sets ?selected; closeDrawer clears it', () => {
    component.openDrawer(42);
    expect(lastQP()).toEqual({ selected: '42' });
    component.closeDrawer();
    expect(lastQP()).toEqual({ selected: null });
  });

  // ── Sort toggling ───────────────────────────────────────────────────────
  it('onSort flips direction on the active column (asc is non-default so it is kept)', () => {
    component.sort.set('id');
    component.dir.set('desc');
    component.onSort('id');                        // same column → flip desc→asc
    const qp = lastQP();
    expect(qp['sort']).toBe('id');
    expect(qp['dir']).toBe('asc');
    expect(qp['page']).toBeNull();                // reset to 1 → stripped
  });

  it('onSort on a new column uses that column\'s default direction', () => {
    component.sort.set('detected');
    component.dir.set('desc');
    component.onSort('job');                       // new column → default 'asc'
    expect(lastQP()['sort']).toBe('job');
    expect(lastQP()['dir']).toBe('asc');
  });

  // ── Client-side filter + nav computeds ──────────────────────────────────
  it('applies free-text filtering client-side across job / step / type / message', () => {
    component.paged.set(pagedFixture([
      { failureId: 1, monitoredJobName: 'OrdersJob', stepName: 's', errorTypeCode: 'E', errorMessage: 'm', status: 'Failed' },
      { failureId: 2, monitoredJobName: 'FilesJob',  stepName: 's', errorTypeCode: 'E', errorMessage: 'm', status: 'Failed' },
    ]) as any);
    component.onFilterTextChange('orders');
    expect(component.filtered().map((f: any) => f.failureId)).toEqual([1]);
  });

  it('selectedIndex / canNavPrev / canNavNext reflect position within the page', () => {
    const rows = [{ failureId: 10 }, { failureId: 20 }, { failureId: 30 }];
    component.filtered.set(rows as any);
    component.paged.set(pagedFixture(rows, 1) as any);
    component.page.set(1);

    component.selectedId.set(20);                 // middle row
    expect(component.selectedIndex()).toBe(1);
    expect(component.canNavPrev()).toBe(true);
    expect(component.canNavNext()).toBe(true);

    component.selectedId.set(30);                 // last row, only page
    expect(component.canNavNext()).toBe(false);
    expect(component.canNavPrev()).toBe(true);
  });
});
