import { Component, ElementRef, OnDestroy, computed, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FailuresService } from '../../core/services/failures.service';
import { SourceData } from '../../core/models';

/** A run of file lines rendered as one block — consecutive plain lines share a block,
 *  every matched line gets its own (highlighted) one. Keeps a 20k-line log to a
 *  handful of DOM nodes instead of one per line. */
interface LineSegment {
  hit:     boolean;
  numbers: string;
  text:    string;
}

/**
 * "View Source" modal: the live source data behind a failure, fetched from
 * GET /data/failures/{id}/source when opened —
 *  • Database → every column of the source row (rule-checked columns highlighted),
 *  • FileSystem → the log file, scrolled to the matched line,
 *  • FileContent → the data file (XML pretty-printed by the API).
 *
 * Layered above the failure drawer (z 59/60 vs 49/50), same shell as the folder
 * picker. While open, a window CAPTURE-phase listener takes Esc (closes only this
 * modal, never the drawer) and swallows ↑/↓ before the failures list's
 * document:keydown navigation can switch the failure underneath — default scrolling
 * still happens.
 */
@Component({
  selector: 'app-source-viewer',
  standalone: true,
  imports: [DatePipe],
  template: `
    @if (open()) {
      <div class="sv-backdrop" (click)="close()" aria-hidden="true"></div>
      <div #dialog class="sv" role="dialog" aria-modal="true" aria-labelledby="sv-title" tabindex="-1">
        <header class="sv-head">
          <div class="sv-title-wrap">
            <span id="sv-title" class="sv-title">{{ title() }}</span>
            @if (data(); as d) {
              @if (d.kind === 'DbRows') {
                <span class="sv-meta" dir="auto">
                  {{ d.connectionName }}@if (d.provider === 'Oracle') { <span class="sv-chip">Oracle</span> }
                  · {{ d.table === 'query' ? 'rule query' : d.table }}
                  · <span class="font-mono">{{ d.keyColumn }} = {{ d.keyValue }}</span>
                </span>
              } @else if (d.path) {
                <span class="sv-meta font-mono" dir="auto" [title]="d.path">{{ d.path }}</span>
              }
            }
          </div>
          <button class="btn btn-ghost btn-sm" (click)="close()" title="Close (Esc)" aria-label="Close">✕</button>
        </header>

        @if (data(); as d) {
          @if (d.kind === 'File') {
            <div class="sv-sub">
              <span>{{ formatSize(d.sizeBytes) }}</span>
              @if (d.lastModified) { <span>· modified {{ d.lastModified | date:'yyyy-MM-dd HH:mm:ss' }}</span> }
              @if (d.format === 'xml') { <span class="sv-chip">XML · formatted</span> }
              @if (d.matchLines.length) {
                <span>· {{ d.matchLines.length }} matched line{{ d.matchLines.length === 1 ? '' : 's' }}</span>
              }
            </div>
            @if (d.truncated) {
              <div class="soft-warn">
                ⚠ Large file — showing lines {{ d.windowStartLine }}–{{ lastLine() }}
                {{ d.matchLines.length ? 'around the matched line' : 'at the end of the file' }}.
              </div>
            }
          }
          @if (d.kind === 'DbRows' && d.reason) { <div class="soft-warn">⚠ {{ d.reason }}</div> }
        }

        <div class="sv-body" #body>
          @if (loading()) {
            <div class="sv-empty"><span class="spinner"></span> Loading source data…</div>
          } @else if (error()) {
            <div class="sv-error">⚠ {{ error() }}</div>
          } @else if (data(); as d) {
            @switch (d.kind) {
              @case ('Unavailable') {
                <div class="sv-unavailable">
                  <div class="sv-unavailable-icon">ℹ</div>
                  <div>
                    <strong>Source data can't be shown</strong>
                    <p dir="auto">{{ d.reason }}</p>
                    @if (d.path) { <p class="font-mono" dir="auto">{{ d.path }}</p> }
                  </div>
                </div>
              }
              @case ('DbRows') {
                @for (row of d.rows; track $index; let i = $index) {
                  @if (d.rows.length > 1) { <div class="sv-row-label">Row {{ i + 1 }} of {{ d.rows.length }}</div> }
                  <table class="sv-cols" [attr.aria-label]="'Source row ' + (i + 1)">
                    <tbody>
                      @for (c of row; track $index) {
                        <tr [class.hl]="isHighlighted(c.name)">
                          <th scope="row" dir="auto">{{ c.name }}</th>
                          <td dir="auto">
                            @if (c.value === null) { <span class="sv-null">NULL</span> }
                            @else { {{ c.value }}@if (c.truncated) { <span class="sv-null"> … (cut)</span> } }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                }
              }
              @case ('File') {
                @if (!d.content) {
                  <div class="sv-empty">The file is empty.</div>
                } @else {
                  <div class="sv-code">
                    <div class="sv-lines">
                      @for (s of segments(); track $index) {
                        <div class="sv-seg" [class.hit]="s.hit">
                          <pre class="sv-gut" aria-hidden="true">{{ s.numbers }}</pre>
                          <pre class="sv-txt" dir="auto">{{ s.text }}</pre>
                        </div>
                      }
                    </div>
                  </div>
                }
              }
            }
          }
        </div>

        <footer class="sv-foot">
          <span class="field-hint">Live data, read just now — it may have changed since the failure was detected.</span>
          <span class="sv-foot-actions">
            <button class="btn btn-ghost btn-sm" (click)="load()" [disabled]="loading()" title="Read the source again">↻ Reload</button>
            @if (copyText()) {
              <button class="btn btn-ghost btn-sm" (click)="copy()">{{ copied() ? '✓ Copied' : 'Copy' }}</button>
            }
            <button class="btn btn-primary btn-sm" (click)="close()">Close</button>
          </span>
        </footer>
      </div>
    }
  `,
  styles: [`
    .sv-backdrop { position: fixed; inset: 0; background: rgba(15, 23, 42, 0.28); z-index: 59; }
    .sv {
      /* Below the top bar, full remaining height — the drawer's stacking context
         (z 50) can't lift this above the top bar, so stay out from under it. */
      position: fixed; top: calc(var(--topbar-h, 56px) + 16px); bottom: 16px; left: 50%; transform: translateX(-50%); z-index: 60;
      width: min(1000px, 94vw); display: flex; flex-direction: column; gap: 10px;
      padding: 14px 16px; background: var(--surface); color: var(--text);
      border: 1px solid var(--border); border-radius: var(--radius-sm); box-shadow: 0 12px 32px rgba(15, 23, 42, 0.22);
      outline: none;
    }
    .sv-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
    .sv-title-wrap { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
    .sv-title { font-size: 15px; font-weight: 600; }
    .sv-meta { font-size: 12px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .sv-sub { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; font-size: 12px; color: var(--text-muted); }
    .sv-chip {
      display: inline-block; padding: 1px 7px; border-radius: 10px; font-size: 11px;
      background: var(--neutral-bg); color: var(--neutral); border: 1px solid var(--neutral-bd);
    }
    .sv-body {
      flex: 1 1 auto; min-height: 0; overflow: auto;
      border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-2);
    }
    .sv-empty { padding: 24px; color: var(--text-muted); font-size: 13px; text-align: center; }
    .sv-error { margin: 12px; padding: 8px 10px; border-radius: var(--radius-sm); background: var(--danger-bg); color: var(--danger); font-size: 12px; }
    .sv-unavailable { display: flex; gap: 12px; padding: 20px; font-size: 13px;
      p { margin: 6px 0 0; color: var(--text-muted); }
    }
    .sv-unavailable-icon {
      flex: none; width: 28px; height: 28px; border-radius: 50%; display: grid; place-items: center;
      background: var(--info-bg); color: var(--info); font-weight: 700;
    }

    /* Database row: column | value. */
    .sv-row-label { padding: 8px 12px 0; font-size: 11px; font-weight: 600; color: var(--text-muted); }
    .sv-cols { width: 100%; border-collapse: collapse; font-size: 12px; background: var(--surface);
      th, td { padding: 6px 12px; border-bottom: 1px solid var(--border-light); text-align: start; vertical-align: top; }
      th { width: 1%; white-space: nowrap; font-weight: 600; color: var(--text-muted); font-family: 'Fira Code', ui-monospace, Menlo, Consolas, monospace; font-size: 11px; }
      td { color: var(--text); font-family: 'Fira Code', ui-monospace, Menlo, Consolas, monospace; font-size: 11px; white-space: pre-wrap; word-break: break-word; }
      tr.hl th, tr.hl td { background: var(--warning-bg); }
      tr.hl th { color: var(--text); }
    }
    .sv-cols + .sv-row-label { margin-top: 10px; }
    .sv-null { color: var(--text-dim); font-style: italic; }

    /* File: gutter + text per segment, both white-space: pre with the same metrics
       so the line numbers stay aligned; the whole block scrolls horizontally. */
    .sv-lines { width: max-content; min-width: 100%; padding: 6px 0; }
    .sv-seg { display: flex; }
    .sv-seg pre {
      margin: 0; padding: 0; white-space: pre; background: none; border: 0;
      font-family: 'Fira Code', ui-monospace, Menlo, Consolas, monospace; font-size: 11px; line-height: 17px;
    }
    .sv-gut {
      flex: none; min-width: 6ch; padding: 0 10px !important; text-align: right; user-select: none;
      color: var(--text-dim); border-right: 1px solid var(--border-light);
    }
    .sv-txt { flex: 1; padding: 0 12px !important; color: var(--text); }
    .sv-seg.hit { background: var(--warning-bg); box-shadow: inset 3px 0 0 var(--warning); }
    .sv-seg.hit .sv-gut { color: var(--text); font-weight: 600; }

    .sv-foot { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; }
    .sv-foot-actions { display: inline-flex; gap: 8px; }
    .field-hint { font-size: 11px; color: var(--text-dim); }
    .soft-warn { padding: 7px 10px; border-radius: var(--radius-sm); background: var(--warn-bg); border: 1px solid var(--warn-border); color: var(--warn-text); font-size: 12px; }
  `],
})
export class SourceViewerComponent implements OnDestroy {
  private svc = inject(FailuresService);

  open      = input<boolean>(false);
  failureId = input.required<number>();
  closed    = output<void>();

  loading = signal(false);
  error   = signal<string | null>(null);
  data    = signal<SourceData | null>(null);
  copied  = signal(false);

  private dialog = viewChild<ElementRef<HTMLElement>>('dialog');
  private body   = viewChild<ElementRef<HTMLElement>>('body');
  private loadSeq = 0;

  title = computed(() => {
    const d = this.data();
    if (!d || d.kind === 'Unavailable') return 'Source data';
    if (d.kind === 'DbRows') return d.rows.length > 1 ? 'Source rows' : 'Source row';
    return d.scanType === 'FileContent' ? 'Data file' : 'Log file';
  });

  segments = computed<LineSegment[]>(() => {
    const d = this.data();
    if (d?.kind !== 'File' || !d.content) return [];
    const hits = new Set(d.matchLines);
    const out: LineSegment[] = [];
    let nums: number[] = [], text: string[] = [];
    const flush = () => {
      if (!nums.length) return;
      out.push({ hit: false, numbers: nums.join('\n'), text: text.join('\n') });
      nums = []; text = [];
    };
    d.content.split('\n').forEach((raw, i) => {
      const no   = d.windowStartLine + i;
      // A trailing empty line in a white-space:pre block draws no line box, which
      // would shift every later number — give empty lines a space.
      const line = raw.replace(/\r$/, '') || ' ';
      if (hits.has(no)) {
        flush();
        out.push({ hit: true, numbers: String(no), text: line });
      } else {
        nums.push(no); text.push(line);
      }
    });
    flush();
    return out;
  });

  lastLine = computed(() => {
    const d = this.data();
    return d?.content ? d.windowStartLine + d.content.split('\n').length - 1 : 0;
  });

  private highlighted = computed(() => new Set((this.data()?.highlightColumns ?? []).map(c => c.toLowerCase())));

  copyText = computed(() => {
    const d = this.data();
    if (d?.kind === 'File') return d.content ?? '';
    if (d?.kind === 'DbRows')
      return d.rows.map(r => r.map(c => `${c.name}\t${c.value ?? 'NULL'}`).join('\n')).join('\n\n');
    return '';
  });

  private readonly onKeydown = (e: KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      e.preventDefault();
      this.close();
    } else if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      // Not preventDefault: arrow keys still scroll the source body.
      e.stopPropagation();
    }
  };

  constructor() {
    effect(() => {
      const isOpen = this.open();
      untracked(() => isOpen ? this.start() : this.detach());
    });
  }

  ngOnDestroy() { this.detach(); }

  isHighlighted(name: string): boolean { return this.highlighted().has(name.toLowerCase()); }

  load() {
    const seq = ++this.loadSeq;
    this.loading.set(true);
    this.error.set(null);
    this.copied.set(false);
    this.svc.getFailureSource(this.failureId()).subscribe({
      next: d => {
        if (seq !== this.loadSeq) return;
        this.data.set(d);
        this.loading.set(false);
        setTimeout(() => this.scrollToMatch());
      },
      error: e => {
        if (seq !== this.loadSeq) return;
        this.data.set(null);
        this.loading.set(false);
        this.error.set(e?.error?.message ?? e?.error?.Message ?? 'Could not load the source data.');
      },
    });
  }

  copy() {
    navigator.clipboard?.writeText(this.copyText()).then(() => {
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 1500);
    });
  }

  close() { this.closed.emit(); }

  formatSize(bytes: number | null): string {
    if (bytes == null) return '';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  /** Newest (last) matched line into view — the one the scan most likely reported. */
  private scrollToMatch() {
    const hits = this.body()?.nativeElement.querySelectorAll<HTMLElement>('.sv-seg.hit');
    hits?.[hits.length - 1]?.scrollIntoView({ block: 'center' });
  }

  private start() {
    window.addEventListener('keydown', this.onKeydown, true);
    this.data.set(null);
    this.load();
    setTimeout(() => this.dialog()?.nativeElement.focus());
  }

  private detach() {
    this.loadSeq++;   // drop a response that lands after close
    window.removeEventListener('keydown', this.onKeydown, true);
  }
}
