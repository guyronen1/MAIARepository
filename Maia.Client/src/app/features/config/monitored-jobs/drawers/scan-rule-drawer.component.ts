import { Component, inject, output, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { ConfigService, UpsertScanRuleRequest } from '../../../../core/services/config.service';
import { ScanSource, ScanCheckRule } from '../../../../core/models';
import { DrawerComponent } from '../../../../shared/drawer/drawer.component';

const DB_CHECK_TYPES  = ['ColumnRange', 'ValueEquals', 'SqlQuery'];
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
  imports: [FormsModule, DrawerComponent],
  template: `
    <app-drawer [open]="isOpen()" [ariaLabel]="'Scan rule'" (close)="isOpen.set(false)">
      <ng-container drawer-title>
        {{ editingId() ? 'Edit' : 'New' }} Scan Rule
        @if (source(); as src) { &nbsp;<span class="drawer-title-sub">{{ src.name }}</span> }
      </ng-container>
      <div class="form-grid">
        @if (source()?.scanTypeId === 1) {
          <!-- FileSystem: keyword + optional input-path extraction -->
          <div class="form-group span2">
            <label>Keyword / Pattern *</label>
            <input [(ngModel)]="form.targetField" placeholder="e.g. ERROR|FAILED|Exception" />
            <span class="field-hint">Text searched in each log file line (case-insensitive). Wildcards (*) are ignored — just type the keyword, e.g. File Not Found.</span>
          </div>
          <div class="form-group span2">
            <label>Input File Extraction</label>
            <input [(ngModel)]="form.inputPathPattern" placeholder="e.g. Processing file: (.+\\.txt)" />
            <span class="field-hint">Optional. Regex; capture group #1 must be the input file path. Full regex (<em>not</em> the <code>*</code>-wildcard shorthand). The captured path becomes the <code>{{'{'}}sourceFilePath{{'}'}}</code> placeholder for a fix policy's payload. Leave blank if no fix needs the input file.</span>
          </div>
        } @else if (source()?.scanTypeId === 4) {
          <!-- FileContent: structured extraction from input data files -->
          <div class="form-group span2">
            <label>Filename Pattern *</label>
            <input [(ngModel)]="form.targetField" placeholder="e.g. *WARNING*.xml  or  *.xml" />
            <span class="field-hint">Files whose name matches are examined. Uses <code>*</code> as a wildcard, case-insensitive — same DSL as classification patterns (<em>not</em> regex).</span>
          </div>
          <div class="form-group span2">
            <label>Format *</label>
            <select [(ngModel)]="form.extractorType">
              @for (f of fileFormats; track f) { <option [ngValue]="f">{{ f }}</option> }
            </select>
            <span class="field-hint">Extractor used to read the file. XML only in v1.</span>
          </div>
          <div class="form-group span2">
            <label>Value Locator (XPath)</label>
            <input [(ngModel)]="form.extractorLocator" placeholder="e.g. /file/status/code" />
            <span class="field-hint"><strong>Leave blank if the filename match alone signals the failure.</strong> Namespaces are ignored — write plain element names, not <code>local-name()</code>.</span>
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
            <label>Identifier Locator (XPath)</label>
            <input [(ngModel)]="form.identifierLocator" placeholder="e.g. /file/header/invoiceId" />
            <span class="field-hint">XPath to the natural key, stored as the failure's <code>{{'{'}}sourceId{{'}'}}</code>. Leave blank to use the filename without extension.</span>
          </div>
        } @else {
          <!-- Database: ColumnRange / ValueEquals -->
          <div class="form-group span2">
            <label>Check Type *</label>
            <select [(ngModel)]="form.checkType">
              @for (ct of dbCheckTypes; track ct) { <option [ngValue]="ct">{{ ct }}</option> }
            </select>
          </div>
          @if (form.checkType === 'SqlQuery') {
            <div class="form-group span2">
              <label>Source Query *</label>
              <textarea [(ngModel)]="form.sourceTable" rows="4" class="sql-area"
                        placeholder="SELECT OrderId, IsStuck FROM Orders o JOIN Shipments s ON … WHERE …&#10;— or —&#10;EXEC sp_CheckStuckOrders @threshold=60"></textarea>
              <span class="field-hint">
                Full SQL <code>SELECT</code> or a stored-procedure call (<code>EXEC sp_Name @p=…</code>), run as-is.
                <strong>Every row the query returns becomes a failure</strong> — put the condition in your
                <code>WHERE</code>/<code>JOIN</code> (there's no separate predicate). Handles cross-table checks the
                single-table rules can't. Runs under the source's connection login — use a least-privilege read-only login.
              </span>
            </div>
            @if (sqlQueryNeedsWhereWarning()) {
              <div class="soft-warn span2">⚠️ This query has no <code>WHERE</code> clause — it will flag <strong>every</strong> returned row as a failure (up to 500). Add a <code>WHERE</code> to target specific problem rows, or confirm this is intentional (e.g., a pre-filtered view or aggregation).</div>
            }
            <div class="form-group">
              <label>Result Column *</label>
              <input [(ngModel)]="form.targetField" placeholder="IsStuck" />
              <span class="field-hint">Result-set column whose value is shown on each failure.</span>
            </div>
            <div class="form-group">
              <label>Source ID Column <span class="text-muted">(row identity)</span></label>
              <input [(ngModel)]="form.sourceIdColumn" placeholder="OrderId" />
              <span class="field-hint">
                Result-set column used as the failure's <code>{{'{'}}sourceId{{'}'}}</code>. Blank → row number.
                <strong>Set this</strong> so a new problem row is detected even while an earlier row's failure is still open
                (per-row dedup, case-insensitive).
              </span>
            </div>
            <div class="form-group">
              <label>Reference ID Column <span class="text-muted">(parent / FK key — optional)</span></label>
              <input [(ngModel)]="form.referenceIdColumn" placeholder="OrderId" />
              <span class="field-hint">
                A related row's identity — e.g. the parent order id when fixing all line items.
                Stored as the failure's <code>{{'{'}}referenceId{{'}'}}</code> placeholder.
                Must be in your <code>SELECT</code>. Blank → <code>{{'{'}}referenceId{{'}'}}</code> resolves to empty (safe no-op, not a bulk write).
              </span>
            </div>
            <div class="form-group span2">
              <label>Watermark Column <span class="text-muted">(scan cursor — optional)</span></label>
              <input [(ngModel)]="form.watermarkColumn" placeholder="UpdateDate" />
              <span class="field-hint">
                Incremental scanning, same as the single-table rules: each scan only processes rows whose value
                here exceeds the last one seen. <strong>The column must be in your <code>SELECT</code></strong>
                (e.g. add <code>UpdateDate</code> to the query). Blank → the whole query re-runs every tick and
                dedup relies on Source ID / open-failure state. For large result sets add
                <code>ORDER BY {{ form.watermarkColumn || 'UpdateDate' }} ASC</code> so the 500-row cap reads oldest-first.
              </span>
            </div>
          } @else {
            <div class="form-group">
              <label>Source Table *</label>
              <input [(ngModel)]="form.sourceTable" placeholder="dbo.Files" />
            </div>
            <div class="form-group">
              <label>Target Field *</label>
              <input [(ngModel)]="form.targetField" placeholder="FileStatusCode" />
            </div>
            @if (form.checkType === 'ValueEquals') {
              <div class="form-group span2">
                <label>Expected Value (triggers failure)</label>
                <input [(ngModel)]="form.expectedValue" placeholder="5" />
              </div>
            }
            @if (form.checkType === 'ColumnRange') {
              <div class="form-group">
                <label>Min Value</label>
                <input type="number" [(ngModel)]="form.minValue" placeholder="blank = −∞" />
              </div>
              <div class="form-group">
                <label>Max Value</label>
                <input type="number" [(ngModel)]="form.maxValue" placeholder="blank = +∞" />
              </div>
            }
            <div class="form-group">
              <label>Watermark Column <span class="text-muted">(scan cursor)</span></label>
              <input [(ngModel)]="form.watermarkColumn" placeholder="UpdateDate" />
            </div>
            <div class="form-group">
              <label>Source ID Column <span class="text-muted">(row identity)</span></label>
              <input [(ngModel)]="form.sourceIdColumn" placeholder="Id" />
            </div>
            <div class="form-group">
              <label>Reference ID Column <span class="text-muted">(parent / FK key)</span></label>
              <input [(ngModel)]="form.referenceIdColumn" placeholder="OrderId" />
            </div>
            <div class="form-group span2">
              <label>File Path Column</label>
              <input [(ngModel)]="form.filePathColumn" placeholder="e.g. FilePath  or  j.FilePath" />
              <span class="field-hint">Optional. Column on the source row holding the input file path → the <code>{{'{'}}sourceFilePath{{'}'}}</code> placeholder. No auto-JOIN — put any JOIN into Source Table and use <code>alias.Column</code> here.</span>
            </div>
          }
        }
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
    .soft-warn { padding: 7px 10px; border-radius: var(--radius-sm); background: var(--warn-bg); border: 1px solid var(--warn-border); color: var(--warn-text); font-size: 12px; }
    .drawer-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
    .edit-error { margin-top: 10px; padding: 8px 10px; border-radius: var(--radius-sm); background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; font-size: 12px; }
  `],
})
export class ScanRuleDrawerComponent {
  private svc = inject(ConfigService);

  readonly dbCheckTypes   = DB_CHECK_TYPES;
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

  /** Soft, non-blocking hint for a SqlQuery rule whose query has no row filter:
   *  no WHERE, no HAVING, and not an EXEC (stored procs filter internally). Such a
   *  query flags every returned row as a failure — bounded (500-row cap) but
   *  usually a mistake. Read path, so warn-not-block; no server-side check. */
  sqlQueryNeedsWhereWarning(): boolean {
    const q = this.form.sourceTable ?? '';
    if (!q.trim()) return false;              // empty → handled by required validation
    if (/^\s*EXEC\b/i.test(q)) return false;  // stored proc — filtering lives inside it
    return !/\bWHERE\b/i.test(q) && !/\bHAVING\b/i.test(q);
  }

  onPredicateTypeChange(next: string | null): void {
    this.form.extractorPredicateType = next;
    if (!next) this.form.extractorPredicateValue = null;
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
      filePathColumn: rule.filePathColumn, inputPathPattern: rule.inputPathPattern,
      extractorType: rule.extractorType, extractorLocator: rule.extractorLocator,
      identifierLocator: rule.identifierLocator, extractorPredicateType: rule.extractorPredicateType,
      extractorPredicateValue: rule.extractorPredicateValue,
      severity: rule.severity, description: rule.description, isActive: true,
    } : this.blank(source.scanTypeId);
    this.isOpen.set(true);
  }

  save() {
    if (!this.form.targetField?.trim()) { this.error.set('Target field / pattern is required.'); return; }
    this.saving.set(true);
    this.error.set(null);
    const ruleId   = this.editingId();
    const sourceId = this.source()!.scanSourceId;
    const req$: Observable<unknown> = ruleId
      ? this.svc.updateScanRule(ruleId, this.form)
      : this.svc.createScanRuleForSource(sourceId, this.form);
    req$.subscribe({
      // 400 FileContent validation (ExtractorTypeRequired / PredicateIncomplete /
      // PredicateRequiresLocator) carries { error, message } — surface the message.
      next: () => { this.isOpen.set(false); this.saving.set(false); this.saved.emit(); },
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
             filePathColumn: null, inputPathPattern: null,
             extractorType: scanTypeId === 4 ? 'Xml' : null,
             extractorLocator: null, identifierLocator: null,
             extractorPredicateType: null, extractorPredicateValue: null,
             severity: 'Medium', description: null, isActive: true };
  }
}
