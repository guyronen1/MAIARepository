import { Component, computed, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { DbProvider, SourceColumnInfo } from '../../../../core/services/config.service';
import type { ColumnCategory } from '../../../../core/util/db-check-kinds.util';
import { columnOptions, columnRef, findColumn } from '../../../../core/util/source-columns.util';

/**
 * One column field of the scan-rule drawer. With a column list (`columns` non-null) it
 * is a dropdown of the source table's columns narrowed to `allowed` type families; a
 * saved value the table doesn't have stays selected as "(not in table)" (the drawer
 * shows the warning). Without a list — lookup failed, JOIN in Source Table, non-admin —
 * it is the plain free-text input, exactly as before. Never blocks a save.
 */
@Component({
  selector: 'app-column-picker',
  standalone: true,
  imports: [FormsModule],
  template: `
    @if (columns(); as cols) {
      <select [ngModel]="selectedRef()" (ngModelChange)="value.set($event)"
              [class.is-stale]="isStale()" [attr.aria-label]="ariaLabel()"
              [attr.title]="isStale() ? value() + ' is not a column of this table' : null">
        @if (required()) {
          <option [ngValue]="''" disabled>Select a column…</option>
        } @else {
          <option [ngValue]="null">— none —</option>
        }
        @if (isStale()) { <option [ngValue]="value()">{{ value() }} (not in table)</option> }
        @for (c of options(); track c.name) {
          <option [ngValue]="ref(c)">{{ c.name }} · {{ c.dataType }}</option>
        }
      </select>
    } @else {
      <input [ngModel]="value()" (ngModelChange)="value.set($event)"
             [placeholder]="placeholder()" [attr.aria-label]="ariaLabel()" />
    }
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    select, input {
      box-sizing: border-box; width: 100%; min-width: 0;
      padding: 7px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm);
      font: inherit; background: var(--surface); color: var(--text);
    }
    :host(.compact) select, :host(.compact) input { font-size: 12px; padding: 4px 6px; }
    select.is-stale { border-color: var(--warn-border); background: var(--warn-bg); color: var(--warn-text); }
  `],
})
export class ColumnPickerComponent {
  /** The rule field (column reference). '' / null = not set. */
  value       = model<string | null>(null);
  /** Source table columns; null = free text. */
  columns     = input<readonly SourceColumnInfo[] | null>(null);
  /** Type families offered; null = any. */
  allowed     = input<readonly ColumnCategory[] | null>(null);
  provider    = input<DbProvider>('SqlServer');
  required    = input(false);
  placeholder = input('');
  ariaLabel   = input<string | null>(null);

  private current = computed(() => findColumn(this.columns() ?? [], this.value(), this.provider()));

  options = computed(() => columnOptions(this.columns() ?? [], this.allowed(), this.value(), this.provider()));

  /** The option matching the saved value — by catalog name, so `fileid` selects `FileId`
   *  without rewriting the stored value until the operator picks something. */
  selectedRef = computed(() => {
    const c = this.current();
    if (c) return this.ref(c);
    const v = this.value();
    return v?.trim() ? v : (this.required() ? '' : null);
  });

  isStale = computed(() => !!this.value()?.trim() && !this.current());

  ref(c: SourceColumnInfo): string { return columnRef(c.name, this.provider()); }
}
