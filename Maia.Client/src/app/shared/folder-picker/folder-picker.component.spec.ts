import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { FolderPickerComponent } from './folder-picker.component';
import type { FolderListing } from '../../core/services/config.service';

describe('FolderPickerComponent', () => {
  let fixture: ComponentFixture<FolderPickerComponent>;
  let c: FolderPickerComponent;
  let http: HttpTestingController;

  const roots = [{ name: 'C:\\', fullPath: 'C:\\' }];
  const listing = (path: string | null, parent: string | null, folders: string[] = []): FolderListing => ({
    path, parent, roots, truncated: false,
    folders: folders.map(f => ({ name: f, fullPath: `${path}\\${f}` })),
  });
  const browseReq = (): TestRequest => http.expectOne(r => r.url.endsWith('/config/browse-folders'));

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FolderPickerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    fixture = TestBed.createComponent(FolderPickerComponent);
    c = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
  });

  const openAt = (initialPath: string | null) => {
    fixture.componentRef.setInput('initialPath', initialPath);
    fixture.componentRef.setInput('open', true);
    fixture.detectChanges();
  };

  it('opens at the roots when there is no initial path', () => {
    openAt(null);
    const req = browseReq();
    expect(req.request.params.has('path')).toBe(false);
    req.flush(listing(null, null));
    expect(c.listing()?.roots.length).toBe(1);
  });

  it('navigates into a folder, back Up, and selects the current folder', () => {
    const picked: string[] = [];
    c.picked.subscribe(p => picked.push(p));
    openAt('C:\\logs');
    const first = browseReq();
    expect(first.request.params.get('path')).toBe('C:\\logs');
    first.flush(listing('C:\\logs', 'C:\\', ['app']));

    c.load('C:\\logs\\app');
    browseReq().flush(listing('C:\\logs\\app', 'C:\\logs'));
    expect(c.pathInput).toBe('C:\\logs\\app');

    c.up();
    const upReq = browseReq();
    expect(upReq.request.params.get('path')).toBe('C:\\logs');
    upReq.flush(listing('C:\\logs', 'C:\\'));

    c.select();
    expect(picked).toEqual(['C:\\logs']);
  });

  it('falls back to the roots with a note when the initial path is missing', () => {
    openAt('C:\\gone');
    browseReq().flush({ error: 'FolderNotFound', message: 'Folder does not exist' }, { status: 404, statusText: 'NF' });
    browseReq().flush(listing(null, null));
    expect(c.note()).toContain('does not exist');
    expect(c.error()).toBeNull();
  });

  it('Esc cancels the picker and never reaches document listeners (the drawer)', () => {
    let cancelled = 0;
    c.cancelled.subscribe(() => cancelled++);
    let documentSaw = 0;
    const docListener = () => documentSaw++;
    document.addEventListener('keydown', docListener);
    openAt(null);
    browseReq().flush(listing(null, null));

    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(cancelled).toBe(1);
    expect(documentSaw).toBe(0);

    // Closed → the capture listener is gone and Esc flows normally again.
    fixture.componentRef.setInput('open', false);
    fixture.detectChanges();
    document.body.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(cancelled).toBe(1);
    expect(documentSaw).toBe(1);
    document.removeEventListener('keydown', docListener);
  });
});
