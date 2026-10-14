import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { SourceViewerComponent } from './source-viewer.component';
import type { SourceData } from '../../core/models';

describe('SourceViewerComponent', () => {
  let fixture: ComponentFixture<SourceViewerComponent>;
  let c: SourceViewerComponent;
  let http: HttpTestingController;
  let el: HTMLElement;

  const base: SourceData = {
    kind: 'Unavailable', scanType: 'Database', reason: null,
    connectionName: null, provider: null, table: null, keyColumn: null, keyValue: null,
    rows: [], highlightColumns: [],
    path: null, sizeBytes: null, lastModified: null, content: null,
    truncated: false, windowStartLine: 1, matchLines: [], format: null,
  };
  const sourceReq = (): TestRequest => http.expectOne(r => r.url.endsWith('/data/failures/7/source'));

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SourceViewerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(SourceViewerComponent);
    c = fixture.componentInstance;
    el = fixture.nativeElement;
    http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('failureId', 7);
  });

  afterEach(() => http.verify());

  const openWith = (d: SourceData) => {
    fixture.componentRef.setInput('open', true);
    fixture.detectChanges();
    sourceReq().flush(d);
    fixture.detectChanges();
  };

  it('renders every column of a DB row, NULLs muted and rule columns highlighted', () => {
    openWith({
      ...base, kind: 'DbRows', connectionName: 'B2BTest', provider: 'SqlServer', table: 'dbo.Files',
      keyColumn: 'FileId', keyValue: '42', highlightColumns: ['filestatuscode'],
      rows: [[
        { name: 'FileId', value: '42', truncated: false },
        { name: 'FileStatusCode', value: '8', truncated: false },
        { name: 'Details', value: null, truncated: false },
      ]],
    });

    const rows = el.querySelectorAll('.sv-cols tr');
    expect(rows.length).toBe(3);
    expect(rows[1].classList).toContain('hl');          // case-insensitive match
    expect(rows[0].classList).not.toContain('hl');
    expect(rows[2].querySelector('.sv-null')?.textContent).toContain('NULL');
    expect(el.querySelector('.sv-meta')?.textContent).toContain('FileId = 42');
    expect(c.title()).toBe('Source row');
  });

  it('numbers file lines from the window start and highlights the matched line', () => {
    openWith({
      ...base, kind: 'File', scanType: 'FileSystem', path: 'C:\\logs\\app.log', sizeBytes: 2048,
      content: 'a\nERROR boom\n\nc', windowStartLine: 10, matchLines: [11], truncated: true, format: 'text',
    });

    const segs = c.segments();
    expect(segs.map(s => s.hit)).toEqual([false, true, false]);
    expect(segs[0].numbers).toBe('10');
    expect(segs[1]).toEqual({ hit: true, numbers: '11', text: 'ERROR boom' });
    expect(segs[2].numbers).toBe('12\n13');
    expect(segs[2].text).toBe(' \nc');                   // empty line keeps its line box
    expect(c.lastLine()).toBe(13);
    expect(el.querySelector('.soft-warn')?.textContent).toContain('lines 10–13');
    expect(el.querySelectorAll('.sv-seg.hit').length).toBe(1);
  });

  it('shows the reason when the source is unavailable', () => {
    openWith({ ...base, reason: 'The scan rule has no Source ID Column.' });
    expect(el.querySelector('.sv-unavailable')?.textContent).toContain('no Source ID Column');
    expect(c.copyText()).toBe('');
  });

  it('Esc closes only the viewer; arrows never reach the failures-list navigation', () => {
    let closed = 0;
    c.closed.subscribe(() => closed++);
    const seen: string[] = [];
    const docListener = (e: KeyboardEvent) => seen.push(e.key);
    document.addEventListener('keydown', docListener);
    openWith({ ...base });

    for (const key of ['Escape', 'ArrowDown', 'ArrowUp'])
      document.body.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    expect(closed).toBe(1);
    expect(seen).toEqual([]);

    // Closed → the capture listener is gone and keys flow normally again.
    fixture.componentRef.setInput('open', false);
    fixture.detectChanges();
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
    expect(seen).toEqual(['ArrowDown']);
    document.removeEventListener('keydown', docListener);
  });

  it('surfaces an HTTP error', () => {
    fixture.componentRef.setInput('open', true);
    fixture.detectChanges();
    sourceReq().flush({ message: 'JobFailure 7 not found.' }, { status: 404, statusText: 'NF' });
    fixture.detectChanges();
    expect(c.error()).toContain('not found');
  });
});
