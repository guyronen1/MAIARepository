import { Component, ElementRef, OnDestroy, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConfigService, FolderListing } from '../../core/services/config.service';

/**
 * Server-side folder browser modal. Lists directories on the MAIA API host (what the
 * scan worker will actually read — a browser can't return a full local path anyway)
 * via GET /config/browse-folders. Opens at {@link initialPath} when set, else at the
 * roots; the path box accepts a pasted/typed path (incl. UNC shares, which can't be
 * enumerated from \\server). Emits {@link picked} with the current folder.
 *
 * Layered above the drawer (z 59/60 vs 49/50). Esc is caught in the window CAPTURE
 * phase while open, so it closes only this modal — never the drawer's
 * document:keydown.escape — regardless of where focus sits.
 */
@Component({
  selector: 'app-folder-picker',
  standalone: true,
  imports: [FormsModule],
  template: `
    @if (open()) {
      <div class="fp-backdrop" (click)="cancel()" aria-hidden="true"></div>
      <div class="fp" role="dialog" aria-modal="true" aria-label="Browse server folders">
        <header class="fp-head">
          <span>Browse server folders</span>
          <button class="btn btn-ghost btn-sm" (click)="cancel()" title="Close (Esc)">✕</button>
        </header>

        <div class="fp-bar">
          <button class="btn btn-ghost btn-sm" (click)="up()" [disabled]="!listing()?.path || loading()" title="Up one level">↑ Up</button>
          <input #pathBox [(ngModel)]="pathInput" (keydown.enter)="go()" dir="auto"
                 placeholder="C:\\logs  or  \\\\server\\share" aria-label="Folder path" />
          <button class="btn btn-ghost btn-sm" (click)="go()" [disabled]="loading()">Go</button>
        </div>

        @if (listing()?.roots?.length) {
          <div class="fp-roots">
            @for (r of listing()!.roots; track r.fullPath) {
              <button class="fp-root" [class.active]="isActiveRoot(r.fullPath)" (click)="load(r.fullPath)" dir="auto">💽 {{ r.name }}</button>
            }
          </div>
        }

        @if (note()) { <div class="soft-warn">⚠ {{ note() }}</div> }

        <div class="fp-list">
          @if (loading()) {
            <div class="fp-empty"><span class="spinner"></span> Loading…</div>
          } @else if (error()) {
            <div class="edit-error">⚠ {{ error() }}</div>
          } @else if (!listing()?.path) {
            <div class="fp-empty">Pick a drive / root above, or type a path.</div>
          } @else if (!listing()!.folders.length) {
            <div class="fp-empty">No sub-folders.</div>
          } @else {
            @for (f of listing()!.folders; track f.fullPath) {
              <button class="fp-item" (click)="load(f.fullPath)" dir="auto">📁 {{ f.name }}</button>
            }
          }
        </div>
        @if (listing()?.truncated) { <span class="field-hint">Showing the first 500 folders — type a more specific path to narrow down.</span> }
        <span class="field-hint">Folders on the MAIA server, as its service account sees them.</span>

        <footer class="fp-foot">
          <button class="btn btn-ghost" (click)="cancel()">Cancel</button>
          <button class="btn btn-primary" (click)="select()" [disabled]="!listing()?.path || loading()">Select this folder</button>
        </footer>
      </div>
    }
  `,
  styles: [`
    .fp-backdrop { position: fixed; inset: 0; background: rgba(15, 23, 42, 0.28); z-index: 59; }
    .fp {
      position: fixed; top: 50%; left: 50%; transform: translate(-50%, -50%); z-index: 60;
      width: min(560px, 94vw); max-height: 80vh; display: flex; flex-direction: column; gap: 10px;
      padding: 14px 16px; background: var(--surface); color: var(--text);
      border: 1px solid var(--border); border-radius: var(--radius-sm); box-shadow: 0 12px 32px rgba(15, 23, 42, 0.22);
    }
    .fp-head { display: flex; align-items: center; justify-content: space-between; font-size: 15px; font-weight: 600; }
    .fp-bar { display: flex; gap: 6px; align-items: center; }
    .fp-bar input {
      flex: 1; min-width: 0; padding: 6px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm);
      font: inherit; font-family: var(--font-mono, monospace); font-size: 12px; background: var(--surface); color: var(--text);
    }
    .fp-roots { display: flex; flex-wrap: wrap; gap: 6px; }
    .fp-root {
      padding: 3px 10px; border: 1px solid var(--border); border-radius: 12px; background: var(--surface);
      color: var(--text); font: inherit; font-size: 12px; cursor: pointer;
    }
    .fp-root.active { border-color: var(--primary, #6366f1); color: var(--primary, #6366f1); }
    .fp-list {
      flex: 1 1 auto; min-height: 180px; overflow-y: auto; display: flex; flex-direction: column;
      border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 4px;
    }
    .fp-item {
      text-align: start; padding: 6px 8px; border: 0; border-radius: var(--radius-sm); background: none;
      color: var(--text); font: inherit; font-size: 13px; cursor: pointer;
    }
    .fp-item:hover, .fp-root:hover { background: var(--primary-glow, rgba(99, 102, 241, 0.08)); }
    .fp-empty { padding: 16px; color: var(--text-muted); font-size: 12px; text-align: center; }
    .field-hint { font-size: 11px; color: var(--text-dim); }
    .soft-warn { padding: 7px 10px; border-radius: var(--radius-sm); background: var(--warn-bg); border: 1px solid var(--warn-border); color: var(--warn-text); font-size: 12px; }
    .edit-error { padding: 8px 10px; border-radius: var(--radius-sm); background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; font-size: 12px; }
    .fp-foot { display: flex; justify-content: flex-end; gap: 8px; }
  `],
})
export class FolderPickerComponent implements OnDestroy {
  private svc = inject(ConfigService);

  open        = input<boolean>(false);
  initialPath = input<string | null>(null);
  picked      = output<string>();
  cancelled   = output<void>();

  listing = signal<FolderListing | null>(null);
  loading = signal(false);
  error   = signal<string | null>(null);
  note    = signal<string | null>(null);
  pathInput = '';

  private pathBox = viewChild<ElementRef<HTMLInputElement>>('pathBox');
  private readonly onKeydown = (e: KeyboardEvent) => {
    if (e.key !== 'Escape') return;
    e.stopPropagation();
    e.preventDefault();
    this.cancel();
  };

  constructor() {
    effect(() => {
      const isOpen = this.open();
      untracked(() => isOpen ? this.start() : this.detachEsc());
    });
  }

  ngOnDestroy() { this.detachEsc(); }

  /** Navigate to `path` (null = roots view). */
  load(path: string | null, fallbackToRoots = false) {
    this.loading.set(true);
    this.error.set(null);
    this.svc.browseFolders(path).subscribe({
      next: l => {
        this.listing.set(l);
        this.pathInput = l.path ?? '';
        this.loading.set(false);
      },
      error: e => {
        const msg = e?.error?.message ?? 'Could not read that folder.';
        this.loading.set(false);
        if (fallbackToRoots) {
          // The field's current value no longer exists / isn't reachable — show the
          // roots so the operator can still navigate, and say why.
          this.note.set(msg);
          this.load(null);
        } else {
          this.error.set(msg);
        }
      },
    });
  }

  go() {
    this.note.set(null);
    this.load(this.pathInput.trim() || null);
  }

  up() {
    this.note.set(null);
    this.load(this.listing()?.parent ?? null);
  }

  select() {
    const path = this.listing()?.path;
    if (path) this.picked.emit(path);
  }

  cancel() { this.cancelled.emit(); }

  isActiveRoot(root: string): boolean {
    const p = this.listing()?.path?.toLowerCase();
    return !!p && p.startsWith(root.toLowerCase());
  }

  private start() {
    window.addEventListener('keydown', this.onKeydown, true);
    this.listing.set(null);
    this.note.set(null);
    const initial = this.initialPath()?.trim() || null;
    this.pathInput = initial ?? '';
    this.load(initial, !!initial);
    setTimeout(() => this.pathBox()?.nativeElement.focus());
  }

  private detachEsc() { window.removeEventListener('keydown', this.onKeydown, true); }
}
