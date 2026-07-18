import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { EditJobDrawerComponent } from './edit-job-drawer.component';
import type { MonitoredJob } from '../../../../core/models';

describe('EditJobDrawerComponent', () => {
  let fixture: ComponentFixture<EditJobDrawerComponent>;
  let c: EditJobDrawerComponent;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [EditJobDrawerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    fixture = TestBed.createComponent(EditJobDrawerComponent);
    c = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('jobId', 5);
    fixture.componentRef.setInput('jobTypes', [{ jobTypeId: 1, name: 'DTSX' }]);
  });

  const job = (): MonitoredJob => ({
    name: 'Job1', displayName: 'Job One', jobTypeName: 'DTSX',
    pollingIntervalSeconds: 300, isActive: true, description: 'd',
  } as unknown as MonitoredJob);

  it('open() builds the form, resolving jobTypeId from jobTypeName, and opens', () => {
    c.open(job());
    expect(c.isOpen()).toBe(true);
    expect(c.jobName()).toBe('Job1');
    expect(c.form.name).toBe('Job1');
    expect(c.form.jobTypeId).toBe(1);           // resolved from 'DTSX'
    expect(c.form.pollingIntervalSeconds).toBe(300);
  });

  it('save() blocks on a blank name/jobType and issues no request', () => {
    c.open(job());
    c.form.name = '';
    c.save();
    expect(c.error()).toContain('required');
    http.expectNone(() => true);                 // nothing fired
  });

  it('save() PUTs and emits (saved) on success', () => {
    let saved = false;
    c.saved.subscribe(() => (saved = true));
    c.open(job());
    c.save();
    http.expectOne(r => r.method === 'PUT').flush({});
    expect(saved).toBe(true);
    expect(c.isOpen()).toBe(false);
    http.verify();
  });
});
