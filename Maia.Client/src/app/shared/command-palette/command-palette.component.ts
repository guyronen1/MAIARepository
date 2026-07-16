import {
  Component, ElementRef, HostListener, ViewChild, effect, inject, signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { SearchService, SearchResult } from '../../core/services/search.service';

/**
 * Ctrl/Cmd+K command palette — fast "jump to" across pages, monitored jobs, and
 * failures (by id). Deterministic navigation only (no reasoning); designed so a
 * future LLM assistant plugs in as an "Ask" mode rather than a second surface —
 * it can reuse SearchService.query() as a navigate tool.
 *
 * Lives once in the shell; its host listens for the global shortcut even when the
 * overlay is closed.
 */
@Component({
  selector: 'app-command-palette',
  standalone: true,
  imports: [FormsModule],
  template: `
    @if (search.open()) {
      <div class="cp-backdrop" (click)="close()">
        <div class="cp-panel" role="dialog" aria-modal="true" aria-label="Search and navigate"
             (click)="$event.stopPropagation()">
          <div class="cp-input-row">
            <span class="cp-lead">🔍</span>
            <input #box class="cp-input" type="text" [(ngModel)]="q" (ngModelChange)="onInput()"
                   placeholder="Jump to a page, job, or failure id…"
                   autocomplete="off" autocorrect="off" spellcheck="false" />
            <span class="cp-esc">Esc</span>
          </div>

          @if (results().length === 0) {
            <div class="cp-empty">No matches for “{{ q }}”</div>
          } @else {
            <ul class="cp-list" role="listbox">
              @for (r of results(); track r.key; let i = $index) {
                <li class="cp-item" role="option" [attr.aria-selected]="i === activeIndex()"
                    [class.active]="i === activeIndex()"
                    (click)="go(r)" (mouseenter)="activeIndex.set(i)">
                  <span class="cp-icon">{{ r.icon }}</span>
                  <span class="cp-text">
                    <span class="cp-title" dir="auto">{{ r.title }}</span>
                    @if (r.subtitle) { <span class="cp-sub" dir="auto">{{ r.subtitle }}</span> }
                  </span>
                  <span class="cp-kind">{{ r.kind }}</span>
                </li>
              }
            </ul>
          }

          <div class="cp-foot">
            <span><kbd>↑</kbd><kbd>↓</kbd> navigate</span>
            <span><kbd>↵</kbd> open</span>
            <span><kbd>esc</kbd> close</span>
          </div>
        </div>
      </div>
    }
  `,
  styles: [`
    .cp-backdrop {
      position: fixed; inset: 0; z-index: 2000;
      background: rgba(0,0,0,0.45);
      display: flex; align-items: flex-start; justify-content: center;
      padding-top: 12vh;
    }
    .cp-panel {
      width: min(600px, 92vw);
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      box-shadow: var(--shadow);
      overflow: hidden;
      display: flex; flex-direction: column;
      max-height: 70vh;
    }
    .cp-input-row {
      display: flex; align-items: center; gap: 10px;
      padding: 12px 14px; border-bottom: 1px solid var(--border);
    }
    .cp-lead { font-size: 15px; opacity: 0.7; flex-shrink: 0; }
    .cp-input {
      flex: 1; border: none; outline: none; background: transparent;
      font-size: 15px; color: var(--text); padding: 0;
    }
    .cp-input::placeholder { color: var(--text-dim); }
    .cp-esc {
      font-size: 10px; color: var(--text-dim); border: 1px solid var(--border);
      border-radius: 4px; padding: 1px 6px; flex-shrink: 0;
    }
    .cp-empty { padding: 24px 16px; text-align: center; color: var(--text-muted); font-size: 13px; }
    .cp-list { list-style: none; margin: 0; padding: 6px; overflow-y: auto; }
    .cp-item {
      display: flex; align-items: center; gap: 11px;
      padding: 9px 10px; border-radius: var(--radius-sm); cursor: pointer;
    }
    .cp-item.active { background: var(--primary-light); }
    .cp-icon { width: 20px; text-align: center; font-size: 15px; flex-shrink: 0; }
    .cp-text { display: flex; flex-direction: column; min-width: 0; flex: 1; }
    .cp-title { font-size: 13px; font-weight: 600; color: var(--text);
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .cp-sub { font-size: 11px; color: var(--text-muted); }
    .cp-item.active .cp-title { color: var(--primary); }
    .cp-kind {
      font-size: 10px; text-transform: uppercase; letter-spacing: 0.05em;
      color: var(--text-dim); flex-shrink: 0;
    }
    .cp-foot {
      display: flex; gap: 16px; padding: 8px 14px;
      border-top: 1px solid var(--border); background: var(--surface-2);
      font-size: 11px; color: var(--text-muted);
    }
    .cp-foot kbd {
      font-family: inherit; font-size: 10px; border: 1px solid var(--border);
      border-radius: 3px; padding: 0 4px; margin-right: 2px; background: var(--surface);
    }
  `]
})
export class CommandPaletteComponent {
  search = inject(SearchService);
  private router = inject(Router);

  q = '';
  results = signal<SearchResult[]>([]);
  activeIndex = signal(0);

  @ViewChild('box') private box?: ElementRef<HTMLInputElement>;

  constructor() {
    // Whenever the palette opens (from the shortcut OR the top-bar trigger):
    // load jobs, reset input, seed the default result list, focus the box.
    effect(() => {
      if (!this.search.open()) return;
      this.search.ensureLoaded();
      this.q = '';
      this.activeIndex.set(0);
      this.results.set(this.search.query(''));
      setTimeout(() => this.box?.nativeElement.focus(), 0);
    });
  }

  @HostListener('document:keydown', ['$event'])
  onKey(ev: KeyboardEvent): void {
    // Global open/toggle — works from anywhere, including inside inputs.
    if ((ev.ctrlKey || ev.metaKey) && (ev.key === 'k' || ev.key === 'K')) {
      ev.preventDefault();
      this.search.toggle();
      return;
    }
    if (!this.search.open()) return;   // the rest only applies while open
    switch (ev.key) {
      case 'Escape':    ev.preventDefault(); this.close(); break;
      case 'ArrowDown': ev.preventDefault(); this.move(1);  break;
      case 'ArrowUp':   ev.preventDefault(); this.move(-1); break;
      case 'Enter': {
        ev.preventDefault();
        const r = this.results()[this.activeIndex()];
        if (r) this.go(r);
        break;
      }
    }
  }

  onInput(): void {
    this.results.set(this.search.query(this.q));
    this.activeIndex.set(0);
  }

  private move(delta: number): void {
    const n = this.results().length;
    if (!n) return;
    this.activeIndex.set((this.activeIndex() + delta + n) % n);
  }

  go(r: SearchResult): void {
    this.close();
    this.router.navigate(r.route, r.queryParams ? { queryParams: r.queryParams } : {});
  }

  close(): void { this.search.closePalette(); }
}
