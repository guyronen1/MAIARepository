import { Component, inject, input, output, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { ConfigService, ConnectionInfo, UpsertScanSourceRequest, CreateScanSourceResponse } from '../../../../core/services/config.service';
import { NotificationService } from '../../../../core/services/notification.service';
import { ScanSource } from '../../../../core/models';
import { DrawerComponent } from '../../../../shared/drawer/drawer.component';
import { FolderPickerComponent } from '../../../../shared/folder-picker/folder-picker.component';

const SCAN_TYPES = [
  { id: 1, name: 'FileSystem' }, { id: 2, name: 'Database' },
  { id: 3, name: 'ApiEndpoint' }, { id: 4, name: 'FileContent' },
];

/**
 * Scan Source drawer (config branches on ScanType; ScanType is immutable on edit).
 * Extracted from JobConfigComponent. Parent calls {@link open} and reloads on
 * {@link saved}; the drawer owns its form + the create/update ScanSource calls.
 * (Delete stays on the parent panel — it isn't part of this editor.)
 */
@Component({
  selector: 'app-scan-source-drawer',
  standalone: true,
  imports: [FormsModule, DrawerComponent, FolderPickerComponent],
  template: `
    <app-drawer [open]="isOpen()" [ariaLabel]="'Scan source'" (close)="isOpen.set(false)">
      <ng-container drawer-title>{{ editingId() ? 'Edit' : 'New' }} Scan Source</ng-container>
      <div class="form-grid">
        <div class="form-group span2">
          <label>Name *</label>
          <input [(ngModel)]="form.name" placeholder="e.g. App logs, Orders DB" />
        </div>
        <div class="form-group span2">
          <label>Scan Type *</label>
          @if (editingId()) {
            <input [value]="scanTypeName(form.scanTypeId)" disabled />
            <span class="field-hint">Scan type can't change after creation — delete and recreate to switch it.</span>
          } @else {
            <select [(ngModel)]="form.scanTypeId">
              @for (t of scanTypes; track t.id) { <option [ngValue]="t.id">{{ t.name }}</option> }
            </select>
          }
        </div>

        @if (isFileBased(form.scanTypeId)) {
          <div class="form-group span2">
            <label>Folder to Scan *</label>
            <div class="input-group">
              <input [(ngModel)]="form.logFolder" placeholder="C:\\logs\\app" dir="auto" />
              <button type="button" class="btn btn-ghost btn-sm" (click)="browse('logFolder')" title="Browse folders on the MAIA server">📁 Browse…</button>
            </div>
          </div>
          @if (form.scanTypeId === 1) {
            <div class="form-group span2">
              <label>Search Patterns</label>
              <input [(ngModel)]="form.searchPatterns" placeholder="app*.log, error*.log" />
            </div>
            <div class="form-group span2">
              <label>Input Folder</label>
              <div class="input-group">
                <input [(ngModel)]="form.inputFolder" placeholder="Optional — base for relative input paths" dir="auto" />
                <button type="button" class="btn btn-ghost btn-sm" (click)="browse('inputFolder')" title="Browse folders on the MAIA server">📁 Browse…</button>
              </div>
            </div>
          }
          <div class="form-group span2">
            <label class="toggle-label"><input type="checkbox" [(ngModel)]="form.includeSubfolders" /> Include subfolders (recurse)</label>
          </div>
          <div class="form-group span2">
            <label class="toggle-label"><input type="checkbox" [(ngModel)]="form.seedWatermarkFromNow" /> Start scanning from now (skip existing files)
              <span class="info" data-tip="Marks every file currently in the folder as already read, so the next scan only picks up content appended or modified after you save. Files that already have a position are left untouched. Unticked: existing files without a position are processed from the beginning on the next scan — slow, and a failure for every historical error.">ⓘ</span>
            </label>
            @if (form.seedWatermarkFromNow) {
              <span class="field-hint">Existing files are skipped; only content {{ form.scanTypeId === 1 ? 'appended' : 'modified' }} after save is scanned.</span>
            } @else {
              <div class="soft-warn">⚠ All existing files will be processed from the beginning (a failure per historical error).</div>
            }
          </div>
        }
        @if (form.scanTypeId === 2) {
          <div class="form-group span2">
            <label>Connection Name *</label>
            @if (connections(); as conns) {
              <select [(ngModel)]="form.connectionName">
                <option [ngValue]="null" disabled>Select a connection…</option>
                @if (isStaleConnection(conns)) {
                  <option [ngValue]="form.connectionName">{{ form.connectionName }} (not configured)</option>
                }
                @for (c of conns; track c.name) { <option [ngValue]="c.name">{{ connectionLabel(c) }}</option> }
              </select>
              @if (isStaleConnection(conns)) {
                <div class="soft-warn">⚠ '{{ form.connectionName }}' is not in the API's ConnectionStrings — pick a configured connection.</div>
              } @else if (selectedProvider(conns) === 'Oracle') {
                <span class="field-hint">Oracle connection — this source's scan rules run as Oracle SQL. A TNS alias in <code>Data Source</code> resolves through <code>tnsnames.ora</code> on the MAIA server.</span>
              } @else if (selectedProvider(conns) === 'Unknown') {
                <div class="soft-warn">⚠ '{{ form.connectionName }}' has an unrecognised <code>{{ form.connectionName }}_ProviderName</code> in appsettings — set it to SqlServer or Oracle.</div>
              }
            } @else {
              <!-- Lookup failed / nothing configured: never strand the drawer on an empty select. -->
              <input [(ngModel)]="form.connectionName" placeholder="appsettings connection key" />
              <span class="field-hint">{{ connectionsLoading() ? 'Loading configured connections…' : 'Could not load the configured connections — type the appsettings key.' }}</span>
            }
          </div>
        }
        @if (form.scanTypeId === 3) {
          <div class="form-group span2">
            <label>API URL *</label>
            <input [(ngModel)]="form.logSourceUrl" placeholder="https://api.example.com/health" />
          </div>
        }
        @if (editingId()) {
          <div class="form-group span2">
            <label class="toggle-label"><input type="checkbox" [(ngModel)]="form.isActive" /> Active</label>
          </div>
        }
      </div>
      @if (error()) { <div class="edit-error">⚠ {{ error() }}</div> }
      <div class="drawer-foot">
        <button class="btn btn-ghost" (click)="isOpen.set(false)">Cancel</button>
        <button class="btn btn-primary" (click)="save()" [disabled]="saving()">
          @if (saving()) { <span class="spinner"></span> } Save
        </button>
      </div>
    </app-drawer>
    <app-folder-picker [open]="pickerField() !== null" [initialPath]="pickerInitialPath()"
                       (picked)="onFolderPicked($event)" (cancelled)="pickerField.set(null)" />
  `,
  styles: [`
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; max-width: 560px; }
    .form-grid .span2 { grid-column: span 2; }
    .form-group { display: flex; flex-direction: column; gap: 4px; }
    .form-group label { font-size: 12px; font-weight: 600; color: var(--text); }
    .form-group input:not([type="checkbox"]), .form-group select, .form-group textarea {
      padding: 7px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); font: inherit; background: var(--surface); color: var(--text);
    }
    .input-group { display: flex; gap: 6px; align-items: center; }
    .input-group input { flex: 1; min-width: 0; }
    .form-group input[disabled] { background: var(--surface-2, #f8fafc); color: var(--text-muted); }
    .toggle-label { flex-direction: row; align-items: center; gap: 8px; cursor: pointer; }
    .toggle-label input[type="checkbox"] { width: 16px; height: 16px; margin: 0; flex: none; }
    .field-hint { font-size: 11px; color: var(--text-dim); }
    /* Long description tooltip: hover anywhere on the field (or focus the input) and the
       full text from data-tip slides open under the field. Pure CSS, no title= delay. */
    .form-group { position: relative; }
    .info { cursor: help; font-weight: 400; color: var(--text-muted); margin-left: 2px; }
    .info::after {
      content: attr(data-tip); position: absolute; left: 0; right: 0; top: 100%; z-index: 30; margin-top: 6px;
      padding: 8px 10px; border-radius: var(--radius-sm); background: var(--text); color: var(--surface);
      font-size: 11px; font-weight: 400; line-height: 1.45; white-space: normal; text-align: start;
      box-shadow: 0 4px 14px rgba(0,0,0,.18); pointer-events: none;
      opacity: 0; visibility: hidden; transition: opacity .12s ease .3s, visibility 0s linear .3s;
    }
    .form-group:hover .info::after, .form-group:focus-within .info::after { opacity: 1; visibility: visible; }
    .form-group:hover .info { color: var(--text); }
    .soft-warn[data-tip] { position: relative; cursor: help; }
    .soft-warn[data-tip]::after {
      content: attr(data-tip); position: absolute; left: 0; right: 0; top: 100%; z-index: 30; margin-top: 6px;
      padding: 8px 10px; border-radius: var(--radius-sm); background: var(--text); color: var(--surface);
      font-size: 11px; font-weight: 400; line-height: 1.45; white-space: normal; pointer-events: none;
      opacity: 0; visibility: hidden; transition: opacity .12s ease .3s, visibility 0s linear .3s;
    }
    .soft-warn[data-tip]:hover::after { opacity: 1; visibility: visible; }
    .soft-warn { padding: 7px 10px; border-radius: var(--radius-sm); background: var(--warn-bg); border: 1px solid var(--warn-border); color: var(--warn-text); font-size: 12px; }
    .drawer-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
    .edit-error { margin-top: 10px; padding: 8px 10px; border-radius: var(--radius-sm); background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; font-size: 12px; }
  `],
})
export class ScanSourceDrawerComponent {
  private svc = inject(ConfigService);
  private notify = inject(NotificationService);

  readonly scanTypes = SCAN_TYPES;
  jobId = input.required<number>();
  saved = output<void>();

  isOpen    = signal(false);
  saving    = signal(false);
  error     = signal<string | null>(null);
  editingId = signal<number | null>(null);
  form: UpsertScanSourceRequest = this.blank();

  /** ConnectionStrings keys (+ engine) for the Database dropdown; null = not loaded /
   *  failed / empty (falls back to the free-text input). Loaded once per drawer lifetime. */
  connections        = signal<ConnectionInfo[] | null>(null);
  connectionsLoading = signal(false);
  /** Which folder field the server-side picker is filling; null = picker closed. */
  pickerField        = signal<'logFolder' | 'inputFolder' | null>(null);
  pickerInitialPath  = signal<string | null>(null);

  isFileBased(scanTypeId: number): boolean { return scanTypeId === 1 || scanTypeId === 4; }
  scanTypeName(id: number): string { return this.scanTypes.find(t => t.id === id)?.name ?? String(id); }

  open(s: ScanSource | null) {
    this.error.set(null);
    this.editingId.set(s?.scanSourceId ?? null);
    this.form = s
      ? { name: s.name, scanTypeId: s.scanTypeId, logFolder: s.logFolder, searchPatterns: s.searchPatterns,
          inputFolder: s.inputFolder, includeSubfolders: s.includeSubfolders,
          connectionName: s.connectionName, logSourceUrl: s.logSourceUrl, isActive: s.isActive,
          seedWatermarkFromNow: true }
      : this.blank();
    this.pickerField.set(null);
    this.loadConnections();
    this.isOpen.set(true);
  }

  /** A saved connection key the API no longer has — kept visible, flagged. */
  isStaleConnection(conns: ConnectionInfo[]): boolean {
    return !!this.form.connectionName && !conns.some(c => c.name === this.form.connectionName);
  }

  /** SQL Server is the default engine, so only the others are named in the option. */
  connectionLabel(c: ConnectionInfo): string {
    return c.provider === 'Oracle'  ? `${c.name} — Oracle`
         : c.provider === 'Unknown' ? `${c.name} — unknown provider`
         : c.name;
  }

  selectedProvider(conns: ConnectionInfo[]): ConnectionInfo['provider'] | null {
    return conns.find(c => c.name === this.form.connectionName)?.provider ?? null;
  }

  browse(field: 'logFolder' | 'inputFolder') {
    this.pickerInitialPath.set(this.form[field]);
    this.pickerField.set(field);
  }

  onFolderPicked(path: string) {
    const field = this.pickerField();
    if (field) this.form[field] = path;
    this.pickerField.set(null);
  }

  private loadConnections() {
    if (this.connections() !== null || this.connectionsLoading()) return;
    this.connectionsLoading.set(true);
    this.svc.getConnections().subscribe({
      next:  conns => { this.connections.set(conns.length ? conns : null); this.connectionsLoading.set(false); },
      error: ()    => this.connectionsLoading.set(false),
    });
  }

  save() {
    if (!this.form.name?.trim()) { this.error.set('Name is required.'); return; }
    if (this.form.scanTypeId === 2 && !this.form.connectionName?.trim()) { this.error.set('Connection is required.'); return; }
    this.saving.set(true);
    this.error.set(null);
    const id = this.editingId();
    const req$: Observable<CreateScanSourceResponse | { warnings?: string[] | null } | null> = id
      ? this.svc.updateScanSource(id, this.form)
      : this.svc.createScanSource(this.jobId(), this.form);
    req$.subscribe({
      // Create may return non-blocking warnings (watermark seed couldn't run) —
      // the source IS saved, so close + reload as normal and toast the warning.
      next: res => {
        this.isOpen.set(false); this.saving.set(false); this.saved.emit();
        for (const w of res?.warnings ?? []) this.notify.show(`⚠ ${w}`, 'info', 12000);
      },
      // 400 from the validation matrix (SourceFolderConflict, LogFolderRequired, …)
      // carries { error, message } — surface the message in the drawer footer.
      error: e => { this.error.set(e?.error?.message ?? 'Save failed.'); this.saving.set(false); },
    });
  }

  private blank(): UpsertScanSourceRequest {
    return { name: '', scanTypeId: 1, logFolder: null, searchPatterns: null, inputFolder: null,
             includeSubfolders: false, connectionName: null, logSourceUrl: null, isActive: true,
             seedWatermarkFromNow: true };
  }
}
