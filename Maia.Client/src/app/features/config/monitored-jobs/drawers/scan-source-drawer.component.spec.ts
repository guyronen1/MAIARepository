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

  const flushConnections = (names: string[] = ['B2BTest', 'DefaultConnection']) =>
    http.expectOne(r => r.url.endsWith('/config/connection-names')).flush(names);

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
    flushConnections();
    expect(c.editingId()).toBeNull();
    expect(c.form.scanTypeId).toBe(1);
    expect(c.form.name).toBe('');
    expect(c.isOpen()).toBe(true);
  });

  it('open(existing) loads the source and records its id as the edit target', () => {
    c.open({ scanSourceId: 3, name: 'Logs', scanTypeId: 2, connectionName: 'B2B' } as unknown as ScanSource);
    flushConnections();
    expect(c.editingId()).toBe(3);
    expect(c.form.name).toBe('Logs');
    expect(c.form.scanTypeId).toBe(2);
    expect(c.form.connectionName).toBe('B2B');
  });

  it('save() blocks on a blank name and issues no request', () => {
    c.open(null);
    flushConnections();
    c.form.name = '   ';
    c.save();
    expect(c.error()).toContain('required');
    http.expectNone(() => true);
  });

  it('loads the configured connection names once and caches them across opens', () => {
    c.open(null);
    flushConnections(['A', 'B']);
    c.open(null);
    http.expectNone(r => r.url.endsWith('/config/connection-names'));
    expect(c.connectionNames()).toEqual(['A', 'B']);
  });

  it('renders a Database connection as a dropdown, flagging a stale saved key', () => {
    c.open({ scanSourceId: 3, name: 'Orders', scanTypeId: 2, connectionName: 'Gone' } as unknown as ScanSource);
    flushConnections(['B2BTest']);
    fixture.detectChanges();
    const opts = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('select option')).map(o => o.textContent?.trim());
    expect(opts).toContain('Gone (not configured)');
    expect(opts).toContain('B2BTest');
    expect(c.isStaleConnection(['B2BTest'])).toBe(true);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('is not in the API');
  });

  it('falls back to free text when the connection lookup fails', () => {
    c.open({ scanSourceId: 3, name: 'Orders', scanTypeId: 2, connectionName: 'X' } as unknown as ScanSource);
    http.expectOne(r => r.url.endsWith('/config/connection-names')).flush('nope', { status: 500, statusText: 'err' });
    fixture.detectChanges();
    expect(c.connectionNames()).toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('input[placeholder="appsettings connection key"]')).not.toBeNull();
  });

  it('save() blocks a Database source with no connection selected', () => {
    c.open(null);
    flushConnections();
    c.form.name = 'db';
    c.form.scanTypeId = 2;
    c.save();
    expect(c.error()).toContain('Connection is required');
    http.expectNone(() => true);
  });

  it('browse() opens the picker at the field value; picked fills that field and closes it', () => {
    c.open(null);
    flushConnections();
    c.form.logFolder = 'C:\\logs';
    c.browse('logFolder');
    expect(c.pickerField()).toBe('logFolder');
    expect(c.pickerInitialPath()).toBe('C:\\logs');
    c.onFolderPicked('D:\\data\\app');
    expect(c.form.logFolder).toBe('D:\\data\\app');
    expect(c.pickerField()).toBeNull();

    c.browse('inputFolder');
    c.onFolderPicked('D:\\in');
    expect(c.form.inputFolder).toBe('D:\\in');
    expect(c.form.logFolder).toBe('D:\\data\\app');
  });
});
