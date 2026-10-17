import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { UsersComponent } from './users.component';
import type { AppUser } from '../../../core/services/users.service';

/**
 * Users screen — job access (per-user job visibility). Pins: new users start scoped
 * with nothing ticked; create sends the ticks; edit loads the user's access and PUTs
 * /jobs only when it changed; Administrators get a disabled checklist.
 */
describe('UsersComponent job access', () => {
  let fixture: ComponentFixture<UsersComponent>;
  let c: UsersComponent;
  let http: HttpTestingController;

  const jobs = [
    { monitoredJobId: 2, name: 'b-job', displayName: null, isActive: true },
    { monitoredJobId: 1, name: 'a-job', displayName: 'Alpha', isActive: false },
  ];
  const operator: AppUser = {
    userId: 7, username: 'op', role: 'Operator', isActive: true, mustChangePassword: false,
    createdAt: '2026-10-17T10:00:00', lastLoginAt: null, allJobs: false, jobCount: 1,
  };

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [UsersComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    fixture = TestBed.createComponent(UsersComponent);
    c = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const flushJobs = () =>
    http.expectOne(r => r.url.endsWith('/config/monitored-jobs')).flush(jobs);

  it('starts a new user scoped with no jobs ticked and sends the ticks on create', () => {
    c.openCreate();
    flushJobs();
    expect(c.jaAll()).toBe(false);
    expect(c.jaIds().size).toBe(0);
    // Sorted by display name: "Alpha" before "b-job".
    expect(c.shownJobs().map(j => j.monitoredJobId)).toEqual([1, 2]);

    c.cf = { username: 'newbie', role: 'Operator', password: 'Temp!1234' };
    c.toggleJob(2);
    c.save();

    const req = http.expectOne(r => r.method === 'POST' && r.url.endsWith('/users'));
    expect(req.request.body).toEqual(jasmine.objectContaining({ allJobs: false, jobIds: [2] }));
    req.flush({ userId: 9 });
    http.expectOne(r => r.method === 'GET' && r.url.endsWith('/users')).flush([]);
  });

  it('loads job access on edit and skips the PUT when unchanged', () => {
    c.openEdit(operator);
    flushJobs();
    http.expectOne(r => r.url.endsWith('/users/7/jobs')).flush({ allJobs: false, jobIds: [1] });
    expect(c.jaIds().has(1)).toBe(true);

    c.save();
    http.expectOne(r => r.method === 'PUT' && r.url.endsWith('/users/7')).flush(null);
    http.expectNone(r => r.method === 'PUT' && r.url.endsWith('/users/7/jobs'));
    http.expectOne(r => r.method === 'GET' && r.url.endsWith('/users')).flush([]);
  });

  it('PUTs the new job set after the role update when ticks change', () => {
    c.openEdit(operator);
    flushJobs();
    http.expectOne(r => r.url.endsWith('/users/7/jobs')).flush({ allJobs: false, jobIds: [1] });

    c.toggleJob(2);
    c.save();
    http.expectOne(r => r.method === 'PUT' && r.url.endsWith('/users/7')).flush(null);
    const put = http.expectOne(r => r.method === 'PUT' && r.url.endsWith('/users/7/jobs'));
    expect(put.request.body).toEqual({ allJobs: false, jobIds: [1, 2] });
    put.flush(null);
    http.expectOne(r => r.method === 'GET' && r.url.endsWith('/users')).flush([]);
  });

  it('disables the checklist for Administrators and while All jobs is on', () => {
    c.openCreate();
    flushJobs();
    expect(c.jaListDisabled()).toBe(false);

    c.jaAll.set(true);
    expect(c.jaListDisabled()).toBe(true);

    c.jaAll.set(false);
    c.cf.role = 'Administrator';
    expect(c.jaListDisabled()).toBe(true);
  });
});
