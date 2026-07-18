import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { ScanSourceDrawerComponent } from './scan-source-drawer.component';
import type { ScanSource } from '../../../../core/models';

describe('ScanSourceDrawerComponent', () => {
  let fixture: ComponentFixture<ScanSourceDrawerComponent>;
  let c: ScanSourceDrawerComponent;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ScanSourceDrawerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    fixture = TestBed.createComponent(ScanSourceDrawerComponent);
    c = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('jobId', 9);
  });

  it('isFileBased is true only for FileSystem(1) / FileContent(4); scanTypeName maps ids', () => {
    expect(c.isFileBased(1)).toBe(true);
    expect(c.isFileBased(4)).toBe(true);
    expect(c.isFileBased(2)).toBe(false);
    expect(c.isFileBased(3)).toBe(false);
    expect(c.scanTypeName(2)).toBe('Database');
  });

  it('open(null) starts a blank FileSystem source in create mode', () => {
    c.open(null);
    expect(c.editingId()).toBeNull();
    expect(c.form.scanTypeId).toBe(1);
    expect(c.form.name).toBe('');
    expect(c.isOpen()).toBe(true);
  });

  it('open(existing) loads the source and records its id as the edit target', () => {
    c.open({ scanSourceId: 3, name: 'Logs', scanTypeId: 2, connectionName: 'B2B' } as unknown as ScanSource);
    expect(c.editingId()).toBe(3);
    expect(c.form.name).toBe('Logs');
    expect(c.form.scanTypeId).toBe(2);
    expect(c.form.connectionName).toBe('B2B');
  });

  it('save() blocks on a blank name and issues no request', () => {
    c.open(null);
    c.form.name = '   ';
    c.save();
    expect(c.error()).toContain('required');
    http.expectNone(() => true);
  });
});
