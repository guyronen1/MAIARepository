import { Component, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, Subject, catchError, map, of, switchMap, timer } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { ConfigService, ConnectionInfo, DbProvider, UpsertScanRuleRequest, CreateScanRuleResponse, ScanRuleConditionDto, SourceColumnInfo } from '../../../../core/services/config.service';
import { NotificationService } from '../../../../core/services/notification.service';
import { AuthService } from '../../../../core/services/auth.service';
import { ScanSource, ScanCheckRule } from '../../../../core/models';
import { DrawerComponent } from '../../../../shared/drawer/drawer.component';
import { DB_CHECK_KINDS, ColumnCategory, WATERMARK_COLUMN_CATEGORIES, checkValueError, columnCategoriesForKind, dbCheckKindLabel, usesExpectedValue, valuePlaceholder } from '../../../../core/util/db-check-kinds.util';
import { columnRef, findColumn, splitColumnList } from '../../../../core/util/source-columns.util';
import { ColumnPickerComponent } from './column-picker.component';

/** Wait after the last Source Table keystroke before looking its columns up. */
export const COLUMNS_DEBOUNCE_MS = 400;

/** One column lookup: what to load (null table = clear, use free text) and whether to debounce. */
interface ColumnsRequest { sourceId: number; table: string | null; debounce: boolean; }
interface ColumnsOutcome { columns: SourceColumnInfo[] | null; hint: string | null; }

/** Top-level shape of a Database rule. The per-check kind (Equals / Not equal /
 *  Contains / In list / Outside range) is chosen on each condition row, so the header select only distinguishes
 *  "column conditions on one table" from "free SqlQuery". */
const RULE_MODES = [
  { value: 'Table',    label: 'Column conditions' },
  { value: 'SqlQuery', label: 'SqlQuery' },
] as const;
type RuleMode = typeof RULE_MODES[number]['value'];
const FILE_FORMATS    = ['Xml'];
const PREDICATE_TYPES = ['Equals', 'NotEquals', 'Contains', 'NotContains'];
const SEVERITIES      = ['Low', 'Medium', 'High', 'Critical'];

/**
 * Scan Rule drawer — the config form branches on the owning source's ScanType
 * (FileSystem / FileContent / Database). Extracted from JobConfigComponent.
 * Parent calls {@link open} with the source + rule (null = new) and reloads on
 * {@link saved}; the drawer owns its form + the create/update ScanRule calls.
 */
@Component({
  selector: 'app-scan-rule-drawer',
  standalone: true,
  imports: [FormsModule, DrawerComponent, ColumnPickerComponent],
  template: `
    <app-drawer [open]="isOpen()" [ariaLabel]="'Scan rule'" (close)="isOpen.set(false)">
      <ng-container drawer-title>
        {{ editingId() ? 'Edit' : 'New' }} Scan Rule
        @if (source(); as src) { &nbsp;<span class="drawer-title-sub">{{ src.name }}</span> }
        @if (isOracle()) { <span class="engine-chip" title="This source's connection is Oracle — rules run as Oracle SQL">Oracle</span> }
      </ng-container>
      <div class="form-grid">
        @if (source()?.scanTypeId === 1) {
          <!-- FileSystem: keyword + optional input-path extraction -->
          <div class="form-group span2">
            <label>Keyword / Pattern * <span class="info" data-tip="Text searched in each log file line (case-insensitive). Wildcards (*) are ignored — just type the keyword, e.g. File Not Found.">ⓘ</span></label>
            <input [(ngModel)]="form.targetField" placeholder="e.g. ERROR|FAILED|Exception" />
            <span class="field-hint">Case-insensitive text matched in each log line, e.g. <code>File Not Found</code>.</span>
          </div>
          <div class="form-group span2">
            <label>Input File Extraction <span class="info" data-tip="Optional. Regex; capture group #1 must be the input file path. Full regex (not the *-wildcard shorthand). The captured path becomes the {sourceFilePath} placeholder for a fix policy's payload. Leave blank if no fix needs the input file.">ⓘ</span></label>
            <input [(ngModel)]="form.inputPathPattern" placeholder="e.g. Processing file: (.+\\.txt)" />
            <span class="field-hint">Optional regex; group #1 = input file path → <code>{{'{'}}sourceFilePath{{'}'}}</code>.</span>
          </div>
        } @else if (source()?.scanTypeId === 4) {
          <!-- FileContent: structured extraction from input data files -->
          <div class="form-group span2">
            <label>Filename Pattern * <span class="info" data-tip="Files whose name matches are examined. Uses * as a wildcard, case-insensitive — same DSL as classification patterns (not regex).">ⓘ</span></label>
            <input [(ngModel)]="form.targetField" placeholder="e.g. *WARNING*.xml  or  *.xml" />
            <span class="field-hint"><code>*</code> wildcard, case-insensitive (not regex).</span>
          </div>
          <div class="form-group span2">
            <label>Format * <span class="info" data-tip="Extractor used to read the file. XML only in v1.">ⓘ</span></label>
            <select [(ngModel)]="form.extractorType">
              @for (f of fileFormats; track f) { <option [ngValue]="f">{{ f }}</option> }
            </select>
          </div>
          <div class="form-group span2">
            <label>Value Locator (XPath) <span class="info" data-tip="Leave blank if the filename match alone signals the failure. Namespaces are ignored — write plain element names, not local-name().">ⓘ</span></label>
            <input [(ngModel)]="form.extractorLocator" placeholder="e.g. /file/status/code" />
            <span class="field-hint">Blank = filename match alone is the failure. Namespaces ignored.</span>
          </div>
          <div class="form-group">
            <label>Predicate</label>
            <!-- Two-way bind splits into [ngModel]+(ngModelChange) so we can
                 clear the stale predicate value when the operator switches to
                 "None". Without this the invisible value field retains its old
                 string and the backend returns PredicateIncomplete 400. -->
            <select [ngModel]="form.extractorPredicateType"
                    (ngModelChange)="onPredicateTypeChange($event)">
              <option [ngValue]="null">None — filename match is the failure</option>
              @for (p of predicateTypes; track p) { <option [ngValue]="p">{{ p }}</option> }
            </select>
          </div>
          @if (form.extractorPredicateType) {
            <div class="form-group">
              <label>Predicate Value *</label>
              <input [(ngModel)]="form.extractorPredicateValue" placeholder="e.g. ERROR" />
            </div>
            @if (!form.extractorLocator) {
              <div class="soft-warn span2">⚠ A predicate needs a <strong>Value Locator</strong> to extract the value it tests.</div>
            }
          }
          <div class="form-group span2">
            <label>Identifier Locator (XPath) <span class="info" data-tip="XPath to the natural key, stored as the failure's {sourceId}. Leave blank to use the filename without extension.">ⓘ</span></label>
            <input [(ngModel)]="form.identifierLocator" placeholder="e.g. /file/header/invoiceId" />
            <span class="field-hint">Natural key → <code>{{'{'}}sourceId{{'}'}}</code>. Blank = filename without extension.</span>
          </div>
        } @else {
          <!-- Database: ColumnRange / ValueEquals / SqlQuery. First line = Check Type +
               Source Table; the table-rule branch then lists every check as a uniform
               condition row (line 1 = the rule's own check, lines 2+ = added conditions). -->
          <div class="form-group">
            <label>Check Type *</label>
            <!-- Chooses the rule SHAPE only: column conditions (the per-check kind —
                 ValueEquals / ColumnRange — lives on each condition row, line 1 included)
                 vs. a free SqlQuery. Switching to SqlQuery drops the conditions list
                 (SqlQuery rules can't have conditions — 400 ConditionsNotSupported). -->
            <select [ngModel]="ruleMode()" (ngModelChange)="onRuleModeChange($event)">
              @for (m of ruleModes; track m.value) { <option [ngValue]="m.value">{{ m.label }}</option> }
            </select>
          </div>
          @if (form.checkType === 'SqlQuery') {
            <div class="form-group span2">
              <label>Source Query * <span class="info" [attr.data-tip]="sqlQueryTip()">ⓘ</span></label>
              <textarea [(ngModel)]="form.sourceTable" rows="4" class="sql-area"
                        [placeholder]="sqlQueryPlaceholder()"></textarea>
              @if (isOracle()) {
                <span class="field-hint">Oracle <code>SELECT</code>, run as-is (a trailing <code>;</code> is fine). <strong>Every returned row is a failure</strong> — filter in your WHERE.</span>
              } @else {
                <span class="field-hint">SELECT or <code>EXEC sp_Name …</code>, run as-is. <strong>Every returned row is a failure</strong> — filter in your WHERE.</span>
              }
            </div>
            @if (oracleQueryNotSelect()) {
              <div class="soft-warn span2">⚠ Oracle sources take a <code>SELECT</code> (or <code>WITH … SELECT</code>) only — procedure calls and PL/SQL blocks return no rows to scan. Select from a view or a pipelined function instead.</div>
            }
            @if (sqlQueryNeedsWhereWarning()) {
              <div class="soft-warn span2" data-tip="This query has no WHERE clause — it will flag every returned row as a failure (up to 500). Add a WHERE to target specific problem rows, or confirm this is intentional (e.g. a pre-filtered view or aggregation).">⚠ No <code>WHERE</code> clause — every returned row (up to 500) becomes a failure.</div>
            }
            <div class="form-group">
              <label>Result Column * <span class="info" data-tip="Result-set column whose value is shown on each failure.">ⓘ</span></label>
              <input [(ngModel)]="form.targetField" placeholder="IsStuck" />
              <span class="field-hint">Column shown as the failure value.</span>
            </div>
            <div class="form-group">
              <label>Source ID Column <span class="text-muted">(row identity)</span> <span class="info" data-tip="Result-set column used as the failure's {sourceId}. Blank → row number. Set this so a new problem row is detected even while an earlier row's failure is still open (per-row dedup, case-insensitive).">ⓘ</span></label>
              <input [(ngModel)]="form.sourceIdColumn" placeholder="OrderId" />
              <span class="field-hint">Row identity → <code>{{'{'}}sourceId{{'}'}}</code>; enables per-row dedup. Blank = row number.</span>
            </div>
            <div class="form-group">
              <label>Reference ID Column <span class="text-muted">(parent / FK key — optional)</span> <span class="info" data-tip="A related row's identity — e.g. the parent order id when fixing all line items. Stored as the failure's {referenceId} placeholder. Must be in your SELECT. Blank → {referenceId} resolves to empty (safe no-op, not a bulk write).">ⓘ</span></label>
              <input [(ngModel)]="form.referenceIdColumn" placeholder="OrderId" />
              <span class="field-hint">Parent / FK key → <code>{{'{'}}referenceId{{'}'}}</code>. Must be in the SELECT.</span>
            </div>
            <div class="form-group span2">
              <label>Detail Columns <span class="text-muted">(shown on the failure — optional)</span> <span class="info" data-tip="Comma-separated result-set columns whose values are appended to the failure's error message, one Column: value line each — e.g. the row's original error text. Must be in your SELECT; a missing column shows (column not in result set) on the failure. Classification patterns can match these values.">ⓘ</span></label>
              <input [(ngModel)]="form.detailColumns" placeholder="UpdateDate, Details" />
              <span class="field-hint">Comma-separated columns appended to the error message. Must be in the SELECT.</span>
            </div>
            <div class="form-group span2">
              <label>Watermark Column <span class="text-muted">(scan cursor — optional)</span> <span class="info" data-tip="Incremental scanning, same as the single-table rules: each scan only processes rows whose value here exceeds the last one seen. The column must be in your SELECT (e.g. add UpdateDate to the query). Blank → the whole query re-runs every tick and dedup relies on Source ID / open-failure state. For large result sets add ORDER BY <watermark column> ASC so the 500-row cap reads oldest-first.">ⓘ</span></label>
              <input [(ngModel)]="form.watermarkColumn" placeholder="UpdateDate" />
              <span class="field-hint">Scan cursor: only rows above the last seen value are processed. Must be in the SELECT.</span>
            </div>
          } @else {
            <div class="form-group">
              <label>Source Table * @if (isOracle()) { <span class="info" data-tip="Oracle names are case-insensitive here, as in SQL*Plus: Status means STATUS and hr.orders means HR.ORDERS. Only a name created with quotes in mixed case needs them — type it as &quot;MixedCase&quot;. The same applies to every column field below.">ⓘ</span>}</label>
              <input [ngModel]="form.sourceTable" (ngModelChange)="onSourceTableChange($event)" [placeholder]="isOracle() ? 'SCHEMA.TABLE' : 'dbo.Files'" />
              @if (isOracle()) { <span class="field-hint">Oracle — names are case-insensitive (<code>Status</code> = <code>STATUS</code>).</span> }
              @if (columnsLoading()) {
                <span class="field-hint">Loading the table's columns…</span>
              } @else if (columnsHint(); as hint) {
                <span class="field-hint cols-hint">{{ hint }}</span>
              }
            </div>
            @if (staleColumns().length) {
              <div class="soft-warn span2">⚠ Not a column of <code>{{ form.sourceTable }}</code>: <strong>{{ staleColumns().join(', ') }}</strong>. Pick an existing column; the scan fails on a missing one. Saving is still allowed.</div>
            }
            <!-- Conditions: line 1 is the rule's own check (CheckType / TargetField /
                 ExpectedValue or Min-Max on the rule itself); lines 2+ are the extra
                 AND'd conditions. Same row layout for all so they read as one list. -->
            <div class="form-group span2 cond-block">
              <label>Conditions <span class="text-muted">(all must match on the same row)</span></label>
              <div class="cond-editor">
                <div class="cond-row cond-main">
                  <span class="cond-order">1.</span>
                  <select [ngModel]="form.checkType" (ngModelChange)="onCheckTypeChange($event)" class="cond-kind" aria-label="Check 1 kind">
                    @for (k of conditionKinds; track k) { <option [ngValue]="k">{{ kindLabel(k) }}</option> }
                  </select>
                  <app-column-picker class="compact" [(value)]="form.targetField" [columns]="columns()" [allowed]="kindCategories(form.checkType)"
                                     [provider]="provider()" [required]="true" placeholder="Column *" ariaLabel="Check 1 column" />
                  @if (usesValue(form.checkType)) {
                    <input [(ngModel)]="form.expectedValue" class="cond-val" [placeholder]="valuePlaceholder(form.checkType)" aria-label="Check 1 expected value" />
                  } @else {
                    <span class="cond-range">
                      <input type="number" [(ngModel)]="form.minValue" class="cond-val" placeholder="min (−∞)" aria-label="Check 1 min" />
                      <input type="number" [(ngModel)]="form.maxValue" class="cond-val" placeholder="max (+∞)" aria-label="Check 1 max" />
                    </span>
                  }
                  <div class="cond-controls"><span class="cond-main-tag" title="The rule's own check — always present">main</span></div>
                </div>
                @for (c of conditions(); track $index; let i = $index) {
                  <div class="cond-row">
                    <span class="cond-order">{{ i + 2 }}.</span>
                    <select [ngModel]="c.kind" (ngModelChange)="setConditionKind(c, $event)" class="cond-kind" [attr.aria-label]="'Check ' + (i + 2) + ' kind'">
                      @for (k of conditionKinds; track k) { <option [ngValue]="k">{{ kindLabel(k) }}</option> }
                    </select>
                    <app-column-picker class="compact" [(value)]="c.targetField" [columns]="columns()" [allowed]="kindCategories(c.kind)"
                                       [provider]="provider()" [required]="true" placeholder="Column *" [ariaLabel]="'Check ' + (i + 2) + ' column'" />
                    @if (usesValue(c.kind)) {
                      <input [(ngModel)]="c.expectedValue" class="cond-val" [placeholder]="valuePlaceholder(c.kind)" [attr.aria-label]="'Check ' + (i + 2) + ' expected value'" />
                    } @else {
                      <span class="cond-range">
                        <input type="number" [(ngModel)]="c.minValue" class="cond-val" placeholder="min (−∞)" [attr.aria-label]="'Check ' + (i + 2) + ' min'" />
                        <input type="number" [(ngModel)]="c.maxValue" class="cond-val" placeholder="max (+∞)" [attr.aria-label]="'Check ' + (i + 2) + ' max'" />
                      </span>
                    }
                    <div class="cond-controls">
                      <button type="button" class="btn btn-ghost btn-icon" title="Move up" (click)="moveCondition(i, -1)" [disabled]="i === 0">↑</button>
                      <button type="button" class="btn btn-ghost btn-icon" title="Move down" (click)="moveCondition(i, +1)" [disabled]="i === conditions().length - 1">↓</button>
                      <button type="button" class="btn btn-ghost btn-icon" title="Remove condition" (click)="removeCondition(i)">✕</button>
                    </div>
                  </div>
                }
              </div>
              <button type="button" class="btn btn-ghost btn-sm cond-add" (click)="addCondition()">+ Add condition</button>
              <span class="field-hint">All lines are ANDed on the same row; for OR, create separate rules. <strong>Equals</strong> / <strong>Not equal</strong>: column = / ≠ value (Not equal skips empty (NULL) values). <strong>Contains</strong>: the text appears anywhere in the column. <strong>In list</strong>: column is one of the numbers, e.g. <code>1, 2, 3</code>. <strong>Outside range</strong>: column outside [min, max].</span>
            </div>
            <div class="form-group">
              <label>Source ID Column <span class="text-muted">(row identity)</span></label>
              <app-column-picker [(value)]="form.sourceIdColumn" [columns]="columns()" [provider]="provider()" placeholder="Id" ariaLabel="Source ID Column" />
            </div>
            <div class="form-group">
              <label>Reference ID Column <span class="text-muted">(parent / FK key)</span></label>
              <app-column-picker [(value)]="form.referenceIdColumn" [columns]="columns()" [provider]="provider()" placeholder="OrderId" ariaLabel="Reference ID Column" />
            </div>
            <div class="form-group span2">
              <label>File Path Column <span class="info" data-tip="Optional. Column on the source row holding the input file path → the {sourceFilePath} placeholder. No auto-JOIN — put any JOIN into Source Table and use alias.Column here.">ⓘ</span></label>
              <app-column-picker [(value)]="form.filePathColumn" [columns]="columns()" [allowed]="stringColumns" [provider]="provider()"
                                 placeholder="e.g. FilePath  or  j.FilePath" ariaLabel="File Path Column" />
              <span class="field-hint">Input file path → <code>{{'{'}}sourceFilePath{{'}'}}</code>. <code>alias.Column</code> allowed.</span>
            </div>
            <div class="form-group span2">
              <label>Detail Columns <span class="text-muted">(shown on the failure)</span> <span class="info" data-tip="Optional. Comma-separated columns on the source row whose values are appended to the failure's error message, one Column: value line each — e.g. the row's original error text. alias.Column allowed when Source Table has a JOIN. Classification patterns can match these values.">ⓘ</span></label>
              @if (columns(); as cols) {
                <!-- Multi-select as chips + an add dropdown; still writes the same CSV. -->
                <div class="detail-picker">
                  @for (d of detailList(); track $index; let i = $index) {
                    <span class="detail-chip" [class.is-stale]="isStaleColumn(d)" [attr.title]="isStaleColumn(d) ? d + ' is not a column of this table' : null">
                      {{ d }}@if (isStaleColumn(d)) { <span class="chip-note">(not in table)</span> }
                      <button type="button" class="chip-x" (click)="removeDetailColumn(i)" [attr.aria-label]="'Remove ' + d">✕</button>
                    </span>
                  }
                  <select class="detail-add" (change)="addDetailColumn($any($event.target))" aria-label="Add a detail column">
                    <option value="">+ Add column…</option>
                    @for (col of detailOptions(); track col.name) { <option [value]="colRef(col)">{{ col.name }} · {{ col.dataType }}</option> }
                  </select>
                </div>
                <span class="field-hint">Columns appended to the error message, in this order.</span>
              } @else {
                <input [(ngModel)]="form.detailColumns" placeholder="UpdateDate, Details" />
                <span class="field-hint">Comma-separated columns appended to the error message.</span>
              }
            </div>
            <!-- Watermark Column sits last so it reads directly into the
                 "Start scanning from now" checkbox that depends on it. -->
            <div class="form-group span2">
              <label>Watermark Column <span class="text-muted">(scan cursor)</span> <span class="info" data-tip="Incremental scanning: each scan only processes rows whose value here exceeds the last one seen. Blank → every tick re-reads all matching rows.">ⓘ</span></label>
              <app-column-picker [(value)]="form.watermarkColumn" [columns]="columns()" [allowed]="watermarkColumns" [provider]="provider()"
                                 placeholder="UpdateDate" ariaLabel="Watermark Column" />
              <span class="field-hint">Scan cursor: only rows above the last seen value are processed{{ columns() ? ' (date and number columns)' : '' }}.</span>
            </div>
          }
        }
        <!-- "Start scanning from now": one-shot watermark seed at save time. DB rules
             seed their own watermark (needs a Watermark Column); FS / FileContent
             rules seed the owning source's file positions. Insert-only on the backend,
             so on edit it only acts where no watermark exists yet. -->
        <div class="form-group span2 seed-block">
          <label class="toggle-label" [class.is-disabled]="seedDisabled()">
            <input type="checkbox" [(ngModel)]="form.seedWatermarkFromNow" [disabled]="seedDisabled()" />
            Start scanning from now
            <span class="text-muted">({{ seedScopeLabel() }})</span>
            <span class="info" [attr.data-tip]="seedTitle()">ⓘ</span>
          </label>
          @if (seedDisabled()) {
            <span class="field-hint">Set a <strong>Watermark Column</strong> above to enable.</span>
          } @else if (form.seedWatermarkFromNow) {
            <span class="field-hint">{{ seedHint() }}</span>
          } @else {
            <div class="soft-warn">⚠ {{ seedWarning() }}</div>
          }
        </div>
        <div class="form-group">
          <label>Severity</label>
          <select [(ngModel)]="form.severity">
            @for (sv of severities; track sv) { <option [ngValue]="sv">{{ sv }}</option> }
          </select>
        </div>
        <div class="form-group">
          <label class="toggle-label"><input type="checkbox" [(ngModel)]="form.isActive" /> Active</label>
        </div>
        <div class="form-group span2">
          <label>Description</label>
          <input [(ngModel)]="form.description" placeholder="Optional notes" />
        </div>
      </div>
      @if (error()) { <div class="edit-error">⚠ {{ error() }}</div> }
      <div class="drawer-foot">
        <button class="btn btn-ghost" (click)="isOpen.set(false)">Cancel</button>
        <button class="btn btn-primary" (click)="save()" [disabled]="saving()">
          @if (saving()) { <span class="spinner"></span> } {{ editingId() ? 'Save Changes' : 'Add Rule' }}
        </button>
      </div>
    </app-drawer>
  `,
  styles: [`
    .drawer-title-sub { font-weight: 400; color: var(--text-muted); font-size: 13px; }
    .engine-chip { margin-left: 6px; font-size: 10px; font-weight: 600; letter-spacing: .02em; color: var(--text-dim); border: 1px solid var(--border); border-radius: 999px; padding: 1px 7px; vertical-align: middle; }
    .form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; max-width: 560px; }
    .form-grid .span2 { grid-column: span 2; }
    .form-group { display: flex; flex-direction: column; gap: 4px; }
    .form-group label { font-size: 12px; font-weight: 600; color: var(--text); }
    .form-group input:not([type="checkbox"]), .form-group select, .form-group textarea {
      padding: 7px 10px; border: 1px solid var(--border); border-radius: var(--radius-sm); font: inherit; background: var(--surface); color: var(--text);
    }
    .toggle-label { flex-direction: row; align-items: center; gap: 8px; cursor: pointer; }
    .toggle-label input[type="checkbox"] { width: 16px; height: 16px; margin: 0; flex: none; }
    .sql-area { font-family: ui-monospace, Menlo, Consolas, monospace; resize: vertical; }
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
    .toggle-label.is-disabled { cursor: default; color: var(--text-muted); }
    .seed-block { padding-top: 4px; border-top: 1px dashed var(--border); }
    .soft-warn { padding: 7px 10px; border-radius: var(--radius-sm); background: var(--warn-bg); border: 1px solid var(--warn-border); color: var(--warn-text); font-size: 12px; }
    .cond-block { padding-top: 4px; border-top: 1px dashed var(--border); }
    .cond-editor { display: flex; flex-direction: column; gap: 6px; margin-top: 4px; }
    .cond-row { display: grid; grid-template-columns: 22px 110px 1fr 1fr 88px; gap: 6px; align-items: center; padding: 6px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); }
    .cond-row.cond-main { background: var(--surface-2); }
    .cond-row .cond-order { font-weight: 600; color: var(--text-dim); text-align: right; }
    .cond-main-tag { font-size: 10px; font-weight: 600; letter-spacing: .02em; color: var(--text-dim); border: 1px solid var(--border); border-radius: 999px; padding: 1px 7px; }
    .cond-row select.cond-kind, .cond-row input.cond-col, .cond-row input.cond-val { font-size: 12px; padding: 4px 6px; min-width: 0; width: 100%; }
    .cond-range { display: grid; grid-template-columns: 1fr 1fr; gap: 4px; min-width: 0; }
    .cond-controls { display: flex; gap: 2px; justify-content: flex-end; }
    .cond-row .btn-icon { padding: 2px 6px; font-size: 13px; line-height: 1.2; }
    .cond-add { align-self: flex-start; margin-top: 4px; }
    .detail-picker { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .detail-chip { display: inline-flex; align-items: center; gap: 4px; padding: 2px 4px 2px 9px; border: 1px solid var(--border); border-radius: 999px; background: var(--surface-2); font-size: 12px; }
    .detail-chip.is-stale { background: var(--warn-bg); border-color: var(--warn-border); color: var(--warn-text); }
    .chip-note { font-size: 11px; }
    .chip-x { border: 0; background: none; color: var(--text-muted); cursor: pointer; padding: 0 4px; font-size: 11px; line-height: 1; }
    .chip-x:hover { color: var(--text); }
    .form-group select.detail-add { font-size: 12px; padding: 4px 6px; }
    .drawer-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
    .edit-error { margin-top: 10px; padding: 8px 10px; border-radius: var(--radius-sm); background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; font-size: 12px; }
  `],
})
export class ScanRuleDrawerComponent {
  private svc = inject(ConfigService);
  private notify = inject(NotificationService);
  private auth = inject(AuthService);

  readonly ruleModes      = RULE_MODES;
  readonly conditionKinds = DB_CHECK_KINDS;
  readonly kindLabel        = dbCheckKindLabel;
  readonly usesValue        = usesExpectedValue;
  readonly valuePlaceholder = valuePlaceholder;
  readonly fileFormats    = FILE_FORMATS;
  readonly predicateTypes = PREDICATE_TYPES;
  readonly severities     = SEVERITIES;

  saved = output<void>();

  isOpen    = signal(false);
  saving    = signal(false);
  error     = signal<string | null>(null);
  source    = signal<ScanSource | null>(null);
  editingId = signal<number | null>(null);
  form: UpsertScanRuleRequest & { isActive: boolean } = this.blank();

  /** Engine of the source's connection — decides how the DB rule runs (T-SQL vs
   *  Oracle SQL) and so which SqlQuery syntax / naming hints apply. SqlServer until
   *  the lookup answers, and when it fails (non-admins, older API): the backend
   *  still validates with the real provider on save. */
  provider = signal<DbProvider>('SqlServer');
  /** Connection lookup, loaded once per drawer lifetime (like the source drawer). */
  private connections: ConnectionInfo[] | null = null;
  private connectionsLoading = false;

  isOracle(): boolean { return this.isDbSource() && this.provider() === 'Oracle'; }

  // ── Column dropdowns (DB table rules) ───────────────────────────────────────
  // The Source Table's columns, read live from the source's own database
  // (`GET /config/scan-sources/{id}/columns`, Admin). null = free-text inputs: SqlQuery
  // rule, no table yet, not an admin, a JOIN / alias in Source Table, or a failed
  // lookup (then `columnsHint` says why). Nothing here ever blocks a save.

  readonly kindCategories   = columnCategoriesForKind;
  readonly watermarkColumns = WATERMARK_COLUMN_CATEGORIES;
  readonly stringColumns: readonly ColumnCategory[] = ['string'];

  columns        = signal<SourceColumnInfo[] | null>(null);
  columnsHint    = signal<string | null>(null);
  columnsLoading = signal(false);
  /** Successful lookups per (sourceId, table) for the drawer's lifetime. Failures are
   *  not cached — a connection that was down may be back on the next open. */
  private columnsCache = new Map<string, SourceColumnInfo[]>();
  private columnsRequests = new Subject<ColumnsRequest>();

  constructor() {
    // switchMap: a newer request (another keystroke, re-open, mode switch) cancels the
    // pending debounce / HTTP call, so a slow answer for an old table can't land last.
    this.columnsRequests.pipe(
      switchMap(r => (r.debounce ? timer(COLUMNS_DEBOUNCE_MS) : of(0)).pipe(switchMap(() => this.fetchColumns(r)))),
      takeUntilDestroyed(),
    ).subscribe(o => {
      this.columns.set(o.columns);
      this.columnsHint.set(o.hint);
      this.columnsLoading.set(false);
    });
  }

  private columnsApply(): boolean {
    return this.isDbSource() && this.form.checkType !== 'SqlQuery' && this.auth.hasAtLeast('Administrator');
  }

  private requestColumns(debounce: boolean): void {
    const table = this.columnsApply() ? this.form.sourceTable?.trim() || null : null;
    const sourceId = this.source()?.scanSourceId ?? 0;
    const cached = table ? this.columnsCache.get(this.cacheKey(sourceId, table)) : undefined;
    // No table / cached → settle now (no flicker, no debounce); else show "loading".
    this.columnsLoading.set(!!table && !cached);
    this.columnsRequests.next({ sourceId, table, debounce: debounce && !!table && !cached });
  }

  private cacheKey(sourceId: number, table: string): string { return `${sourceId}|${table.toLowerCase()}`; }

  private fetchColumns(r: ColumnsRequest): Observable<ColumnsOutcome> {
    if (!r.table) return of({ columns: null, hint: null });
    const key = this.cacheKey(r.sourceId, r.table);
    const cached = this.columnsCache.get(key);
    if (cached) return of({ columns: cached, hint: null });
    return this.svc.getSourceColumns(r.sourceId, r.table).pipe(
      map(cols => { this.columnsCache.set(key, cols); return { columns: cols, hint: null }; }),
      catchError((e: HttpErrorResponse) => of({ columns: null, hint: this.columnsFailureHint(e, r.table!) })),
    );
  }

  private columnsFailureHint(e: HttpErrorResponse, table: string): string {
    if (e?.error?.error === 'TableNotResolvable')
      return 'Not a single table (JOIN or alias) — type the column names.';
    // No { error } body: the request never reached the column lookup. A bare 404 is an
    // API build without the endpoint (not restarted since the update); status 0 = no API.
    if (!e?.error?.error && e?.status === 404)
      return 'The running MAIA API has no column lookup yet (restart / update Maia.API) — type the column names.';
    if (e?.status === 0)
      return 'The MAIA API did not answer — type the column names.';
    const why = e?.error?.message as string | undefined;
    return `Couldn't load the columns of ${table}${why ? `: ${why}` : ''} — type the column names.`;
  }

  onSourceTableChange(value: string | null): void {
    this.form.sourceTable = value;
    this.requestColumns(true);
  }

  colRef(c: SourceColumnInfo): string { return columnRef(c.name, this.provider()); }

  /** A saved column reference the loaded table doesn't have. False in free-text mode. */
  isStaleColumn(ref: string | null | undefined): boolean {
    const cols = this.columns();
    return !!cols && !!ref?.trim() && !findColumn(cols, ref, this.provider());
  }

  /** Every column field's value that isn't in the table (deduped, in form order). */
  staleColumns(): string[] {
    if (!this.columns()) return [];
    const refs = [this.form.targetField, ...this.conditions().map(c => c.targetField),
                  this.form.sourceIdColumn, this.form.referenceIdColumn, this.form.filePathColumn,
                  ...this.detailList(), this.form.watermarkColumn];
    return [...new Set(refs.map(r => r?.trim() ?? '').filter(r => r && this.isStaleColumn(r)))];
  }

  detailList(): string[] { return splitColumnList(this.form.detailColumns); }

  /** Columns not yet in Detail Columns (any type — detail values are rendered as text). */
  detailOptions(): SourceColumnInfo[] {
    const chosen = this.detailList();
    return (this.columns() ?? []).filter(c => !chosen.some(d => findColumn([c], d, this.provider())));
  }

  addDetailColumn(select: HTMLSelectElement): void {
    const ref = select.value;
    select.value = '';                       // back to "+ Add column…"
    if (!ref) return;
    this.form.detailColumns = [...this.detailList(), ref].join(', ');
  }

  removeDetailColumn(index: number): void {
    const list = this.detailList();
    list.splice(index, 1);
    this.form.detailColumns = list.length ? list.join(', ') : null;
  }

  /** Mirrors the backend's 400 OracleQueryNotSelect — warn before the round trip. */
  oracleQueryNotSelect(): boolean {
    return this.isOracle() && /^\s*(EXEC|EXECUTE|CALL|BEGIN|DECLARE)\b/i.test(this.form.sourceTable ?? '');
  }

  sqlQueryPlaceholder(): string {
    return this.isOracle()
      ? 'SELECT o.order_id, o.is_stuck FROM orders o JOIN shipments s ON … WHERE …'
      : 'SELECT OrderId, IsStuck FROM Orders o JOIN Shipments s ON … WHERE …\n— or —\nEXEC sp_CheckStuckOrders @threshold=60';
  }

  sqlQueryTip(): string {
    return this.isOracle()
      ? 'Full Oracle SELECT (or WITH … SELECT), run as-is on the source\'s Oracle connection; a trailing ; is stripped. Procedure calls and PL/SQL blocks are not supported — they return no rows. Every row the query returns becomes a failure — put the condition in your WHERE/JOIN. Result-column names match case-insensitively (ORDER_ID = order_id). Runs under the connection login — use a least-privilege read-only user.'
      : 'Full SQL SELECT or a stored-procedure call (EXEC sp_Name @p=…), run as-is. Every row the query returns becomes a failure — put the condition in your WHERE/JOIN (there\'s no separate predicate). Handles cross-table checks the single-table rules can\'t. Runs under the source\'s connection login — use a least-privilege read-only login.';
  }

  private applyProvider(): void {
    const name = this.source()?.connectionName ?? 'DefaultConnection';
    const p = this.connections?.find(c => c.name === name)?.provider;
    this.provider.set(p === 'Oracle' ? 'Oracle' : 'SqlServer');
  }

  private loadProvider(): void {
    this.applyProvider();
    if (!this.isDbSource() || this.connections !== null || this.connectionsLoading) return;
    this.connectionsLoading = true;
    this.svc.getConnections().subscribe({
      next:  conns => { this.connections = conns; this.connectionsLoading = false; this.applyProvider(); },
      error: ()    => { this.connectionsLoading = false; },
    });
  }

  /** Soft, non-blocking hint for a SqlQuery rule whose query has no row filter:
   *  no WHERE, no HAVING, and not an EXEC (stored procs filter internally). Such a
   *  query flags every returned row as a failure — bounded (500-row cap) but
   *  usually a mistake. Read path, so warn-not-block; no server-side check. */
  sqlQueryNeedsWhereWarning(): boolean {
    const q = this.form.sourceTable ?? '';
    if (!q.trim()) return false;              // empty → handled by required validation
    if (/^\s*EXEC\b/i.test(q)) return false;  // stored proc — filtering lives inside it (Oracle: the not-a-SELECT warning shows instead)
    return !/\bWHERE\b/i.test(q) && !/\bHAVING\b/i.test(q);
  }

  /** "Start scanning from now" — shown on every rule. DB rules need a Watermark
   *  Column (otherwise there is no cursor to seed → disabled with a hint); file
   *  rules seed the owning source's per-file positions. Default: checked on
   *  create AND edit (operator decision 2026-10-12) — safe because the backend is
   *  insert-only: positions that already exist are never moved, so on edit it
   *  only marks files/rows that have never been scanned as already seen. */
  private isDbSource(): boolean { return this.source()?.scanTypeId === 2; }
  seedDisabled(): boolean { return this.isDbSource() && !this.form.watermarkColumn?.trim(); }
  seedScopeLabel(): string {
    const t = this.source()?.scanTypeId;
    return t === 1 ? 'skip existing log content' : t === 4 ? 'skip existing files' : 'skip existing rows';
  }
  /** Short line under the checkbox; the full explanation is the ⓘ tooltip (seedTitle). */
  seedHint(): string {
    const t = this.source()?.scanTypeId;
    if (t === 1) return 'Existing log content is skipped; only lines appended after save are scanned.';
    if (t === 4) return 'Existing files are skipped; only files created or modified after save are scanned.';
    return `Watermark set to the current max ${this.form.watermarkColumn}; only newer rows are reported.`;
  }
  seedWarning(): string {
    const t = this.source()?.scanTypeId;
    if (t === 1) return 'All existing log content will be scanned (a failure per historical error).';
    if (t === 4) return 'All existing files will be processed (a failure per historical match).';
    return 'All existing matching rows will be processed. Slow on large tables.';
  }
  seedTitle(): string {
    const t = this.source()?.scanTypeId;
    if (t === 1) return 'Marks every log file currently in the source folder as already read, so the next scan only picks up lines appended after you save. Files that already have a position are left untouched. Unticked: existing log files without a position are read from the beginning — a failure for every historical error line.';
    if (t === 4) return 'Marks every file currently in the source folder as already processed, so the next scan only picks up files created or modified after you save. Files that already have a position are left untouched. Unticked: existing files without a position are all processed — a failure for every historical match.';
    if (this.seedDisabled()) return 'Needs a Watermark Column: without one the rule has no scan cursor and every tick re-reads all matching rows.';
    return `Sets the watermark to the current highest ${this.form.watermarkColumn} among matching rows, so the next scan only reports rows that arrive after you save. An existing watermark is left untouched. Unticked: the next scan processes all existing matching rows (up to the 500-row cap per tick) and creates a failure for each.`;
  }

  onPredicateTypeChange(next: string | null): void {
    this.form.extractorPredicateType = next;
    if (!next) this.form.extractorPredicateValue = null;
  }

  // ── Composite scan rule — extra AND'd conditions (DB table rules only) ──────

  /** The editable conditions list (always an array; `form.conditions` may be undefined). */
  conditions(): ScanRuleConditionDto[] { return this.form.conditions ?? []; }

  /** DB check type switch. SqlQuery rules cannot carry conditions (backend 400
   *  ConditionsNotSupported), so switching TO SqlQuery drops them — mirrors the fix
   *  drawer clearing steps when switching away from Composite. */
  /** Header "Check Type": derived from the stored checkType. */
  ruleMode(): RuleMode { return this.form.checkType === 'SqlQuery' ? 'SqlQuery' : 'Table'; }

  /** Switching shape. → SqlQuery: checkType = SqlQuery (conditions dropped by
   *  onCheckTypeChange). → Table: back to ValueEquals as the line-1 default unless
   *  the rule already is a table kind (then nothing changes). */
  onRuleModeChange(mode: RuleMode): void {
    if (mode === 'SqlQuery') { this.onCheckTypeChange('SqlQuery'); this.requestColumns(false); return; }
    if (this.form.checkType === 'SqlQuery') { this.onCheckTypeChange('ValueEquals'); this.requestColumns(false); }
  }

  /** Line-1 kind select (any DB check kind) — and the SqlQuery hop from the header. */
  onCheckTypeChange(next: string): void {
    this.form.checkType = next;
    if (next === 'SqlQuery') this.form.conditions = [];
    // Line 1 (the rule's own check) swaps its value fields like any condition row.
    if (usesExpectedValue(next)) { this.form.minValue = null; this.form.maxValue = null; }
    else if (next === 'ColumnRange') { this.form.expectedValue = null; }
  }

  addCondition(): void {
    const list = this.conditions();
    list.push({ conditionOrder: list.length + 1, kind: 'ValueEquals', targetField: '',
                expectedValue: null, minValue: null, maxValue: null, description: null });
    this.form.conditions = list;
  }
  removeCondition(index: number): void {
    const list = this.conditions();
    list.splice(index, 1);
    list.forEach((c, i) => c.conditionOrder = i + 1);
    this.form.conditions = list;
  }
  moveCondition(index: number, delta: number): void {
    const list = this.conditions();
    const target = index + delta;
    if (target < 0 || target >= list.length) return;
    [list[index], list[target]] = [list[target], list[index]];
    list.forEach((c, i) => c.conditionOrder = i + 1);
    this.form.conditions = list;
  }
  /** Kind switch clears the fields the new kind doesn't use, so a stale value can't
   *  ride along (the backend nulls them too — this just keeps the form honest). */
  setConditionKind(c: ScanRuleConditionDto, kind: string): void {
    c.kind = kind;
    if (usesExpectedValue(kind)) { c.minValue = null; c.maxValue = null; }
    else { c.expectedValue = null; }
  }
  /** Client-side guard mirroring the backend's CheckValueRequired / InvalidInList /
   *  ConditionIncomplete 400s. Numbers match the drawer's lines: line 1 is the rule's
   *  own check, so condition order N is shown as line N + 1. */
  private conditionsError(): string | null {
    if (this.isDbSource() && this.form.checkType !== 'SqlQuery') {
      const e = checkValueError(this.form.checkType, this.form.expectedValue, this.form.minValue, this.form.maxValue);
      if (e) return `Line 1 (${this.form.targetField}): ${e}.`;
    }
    for (const c of this.conditions()) {
      const n = c.conditionOrder + 1;
      if (!c.targetField?.trim()) return `Line ${n}: column is required.`;
      const e = checkValueError(c.kind, c.expectedValue, c.minValue, c.maxValue);
      if (e) return `Line ${n} (${c.targetField}): ${e}.`;
    }
    return null;
  }

  open(source: ScanSource, rule: ScanCheckRule | null) {
    this.error.set(null);
    this.source.set(source);
    this.editingId.set(rule?.checkRuleId ?? null);
    this.form = rule ? {
      checkType: rule.checkType, sourceTable: rule.sourceTable, targetField: rule.targetField,
      minValue: rule.minValue, maxValue: rule.maxValue, expectedValue: rule.expectedValue,
      watermarkColumn: rule.watermarkColumn, sourceIdColumn: rule.sourceIdColumn,
      referenceIdColumn: rule.referenceIdColumn,
      filePathColumn: rule.filePathColumn, detailColumns: rule.detailColumns,
      inputPathPattern: rule.inputPathPattern,
      extractorType: rule.extractorType, extractorLocator: rule.extractorLocator,
      identifierLocator: rule.identifierLocator, extractorPredicateType: rule.extractorPredicateType,
      extractorPredicateValue: rule.extractorPredicateValue,
      severity: rule.severity, description: rule.description, isActive: true,
      seedWatermarkFromNow: true,
      // Drop conditionId (write shape has none; ids churn on every save) and copy so
      // edits don't mutate the list row's object before the operator hits Save.
      conditions: (rule.conditions ?? []).map(c => ({
        conditionOrder: c.conditionOrder, kind: c.kind, targetField: c.targetField,
        expectedValue: c.expectedValue, minValue: c.minValue, maxValue: c.maxValue, description: c.description,
      })),
    } : this.blank(source.scanTypeId);
    this.loadProvider();
    // Never show the previous rule's / source's columns while this one loads.
    this.columns.set(null);
    this.columnsHint.set(null);
    this.requestColumns(false);
    this.isOpen.set(true);
  }

  save() {
    if (!this.form.targetField?.trim()) { this.error.set(this.isDbSource() ? 'Line 1: column is required.' : 'Target field / pattern is required.'); return; }
    const condErr = this.conditionsError();
    if (condErr) { this.error.set(condErr); return; }
    this.saving.set(true);
    this.error.set(null);
    const ruleId   = this.editingId();
    const sourceId = this.source()!.scanSourceId;
    const req$: Observable<CreateScanRuleResponse | { warnings?: string[] | null } | null> = ruleId
      ? this.svc.updateScanRule(ruleId, this.form)
      : this.svc.createScanRuleForSource(sourceId, this.form);
    req$.subscribe({
      // 400 FileContent validation (ExtractorTypeRequired / PredicateIncomplete /
      // PredicateRequiresLocator) carries { error, message } — surface the message.
      // Create may return non-blocking warnings (watermark seed couldn't run) —
      // the rule IS saved, so close + reload as normal and toast the warning.
      next: res => {
        this.isOpen.set(false); this.saving.set(false); this.saved.emit();
        for (const w of (res as { warnings?: string[] | null } | null)?.warnings ?? []) this.notify.show(`⚠ ${w}`, 'info', 12000);
      },
      error: e => { this.error.set(e?.error?.message ?? 'Save failed. Check the rule fields and try again.'); this.saving.set(false); },
    });
  }

  private blank(scanTypeId = 2): UpsertScanRuleRequest & { isActive: boolean } {
    const checkType = scanTypeId === 1 ? 'ErrorKeyword'
                    : scanTypeId === 4 ? 'FileContent'
                    : 'ValueEquals';
    return { checkType, sourceTable: null, targetField: '', minValue: null,
             maxValue: null, expectedValue: null, watermarkColumn: null, sourceIdColumn: null,
             referenceIdColumn: null,
             filePathColumn: null, detailColumns: null, inputPathPattern: null,
             extractorType: scanTypeId === 4 ? 'Xml' : null,
             extractorLocator: null, identifierLocator: null,
             extractorPredicateType: null, extractorPredicateValue: null,
             severity: 'Medium', description: null, isActive: true,
             seedWatermarkFromNow: true, conditions: [] };
  }
}
