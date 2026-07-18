import { Component, inject, input, output, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { ConfigService, UpsertScanSourceRequest } from '../../../../core/services/config.service';
import { ScanSource } from '../../../../core/models';
import { DrawerComponent } from '../../../../shared/drawer/drawer.component';

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
  imports: [FormsModule, DrawerComponent],
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
            <input [(ngModel)]="form.logFolder" placeholder="C:\\logs\\app" />
          </div>
          @if (form.scanTypeId === 1) {
            <div class="form-group span2">
              <label>Search Patterns</label>
              <input [(ngModel)]="form.searchPatterns" placeholder="app*.log, error*.log" />
            </div>
            <div class="form-group span2">
              <label>Input Folder</label>
              <input [(ngModel)]="form.inputFolder" placeholder="Optional — base for relative input paths" />
            </div>
          }
          <div class="form-group span2">
            <label class="toggle-label"><input type="checkbox" [(ngModel)]="form.includeSubfolders" /> Include subfolders (recurse)</label>
          </div>
        }
        @if (form.scanTypeId === 2) {
          <div class="form-group span2">
            <label>Connection Name *</label>
            <input [(ngModel)]="form.connectionName" placeholder="appsettings connection key" />
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
  `,
  styles: [`
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; max-width: 560px; }
    .form-grid .span2 { grid-column: span 2; }
    .form-group { display: flex; flex-direction: column; gap: 4px; }
    .form-group label { font-size: 12px; font-weight: 600; color: var(--text); }
    .form-group input:not([type="checkbox"]), .form-group select, .form-group textarea {
      padding: 7px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); font: inherit; background: var(--surface); color: var(--text);
    }
    .form-group input[disabled] { background: var(--surface-2, #f8fafc); color: var(--text-muted); }
    .toggle-label { flex-direction: row; align-items: center; gap: 8px; cursor: pointer; }
    .toggle-label input[type="checkbox"] { width: 16px; height: 16px; margin: 0; flex: none; }
    .field-hint { font-size: 11px; color: var(--text-dim); }
    .drawer-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
    .edit-error { margin-top: 10px; padding: 8px 10px; border-radius: var(--radius-sm); background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; font-size: 12px; }
  `],
})
export class ScanSourceDrawerComponent {
  private svc = inject(ConfigService);

  readonly scanTypes = SCAN_TYPES;
  jobId = input.required<number>();
  saved = output<void>();

  isOpen    = signal(false);
  saving    = signal(false);
  error     = signal<string | null>(null);
  editingId = signal<number | null>(null);
  form: UpsertScanSourceRequest = this.blank();

  isFileBased(scanTypeId: number): boolean { return scanTypeId === 1 || scanTypeId === 4; }
  scanTypeName(id: number): string { return this.scanTypes.find(t => t.id === id)?.name ?? String(id); }

  open(s: ScanSource | null) {
    this.error.set(null);
    this.editingId.set(s?.scanSourceId ?? null);
    this.form = s
      ? { name: s.name, scanTypeId: s.scanTypeId, logFolder: s.logFolder, searchPatterns: s.searchPatterns,
          inputFolder: s.inputFolder, includeSubfolders: s.includeSubfolders,
          connectionName: s.connectionName, logSourceUrl: s.logSourceUrl, isActive: s.isActive }
      : this.blank();
    this.isOpen.set(true);
  }

  save() {
    if (!this.form.name?.trim()) { this.error.set('Name is required.'); return; }
    this.saving.set(true);
    this.error.set(null);
    const id = this.editingId();
    const req$: Observable<unknown> = id
      ? this.svc.updateScanSource(id, this.form)
      : this.svc.createScanSource(this.jobId(), this.form);
    req$.subscribe({
      next: () => { this.isOpen.set(false); this.saving.set(false); this.saved.emit(); },
      // 400 from the validation matrix (SourceFolderConflict, LogFolderRequired, …)
      // carries { error, message } — surface the message in the drawer footer.
      error: e => { this.error.set(e?.error?.message ?? 'Save failed.'); this.saving.set(false); },
    });
  }

  private blank(): UpsertScanSourceRequest {
    return { name: '', scanTypeId: 1, logFolder: null, searchPatterns: null, inputFolder: null,
             includeSubfolders: false, connectionName: null, logSourceUrl: null, isActive: true };
  }
}
