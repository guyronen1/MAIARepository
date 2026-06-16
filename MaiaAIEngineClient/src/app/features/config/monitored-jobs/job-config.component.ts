import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ConfigService, FixPolicyRule, JobType, ErrorType, ClassificationRule,
  UpsertJobRequest, UpsertScanSourceRequest, UpsertScanRuleRequest,
  UpsertJobClassificationRuleRequest, UpsertFixPolicyRuleRequest, UpsertClassificationRuleRequest,
} from '../../../core/services/config.service';
import { MonitoredJob, ScanSource, ScanCheckRule, RuleOverride } from '../../../core/models';
import { DrawerComponent } from '../../../shared/drawer/drawer.component';

const SCAN_TYPES = [
  { id: 1, name: 'FileSystem' }, { id: 2, name: 'Database' },
  { id: 3, name: 'ApiEndpoint' }, { id: 4, name: 'FileContent' },
];
const DB_CHECK_TYPES  = ['ColumnRange', 'ValueEquals', 'SqlQuery'];
const FILE_FORMATS    = ['Xml'];
const PREDICATE_TYPES = ['Equals', 'NotEquals', 'Contains', 'NotContains'];
const SEVERITIES      = ['Low', 'Medium', 'High', 'Critical'];
const FIX_CATEGORIES  = ['Retry', 'FileRepair', 'DbFix', 'Manual'];
const ACTION_TYPES    = ['Manual', 'ApiCall', 'StoredProcedure', 'Script', 'SqlScript', 'CopyFile', 'Composite'];

/**
 * Tier 2.5 (d2): dedicated per-job configuration screen at
 * /config/monitored-jobs/:id. Sections render as row panels — Scan Sources
 * (each nesting its rules), Classification Rules, Fix Options.
 *
 * (d2b) is the READ-ONLY shell: it loads the full job picture and renders the
 * three panels. Editing (source/rule/class/fix drawers) lands in (d2c)/(d2d),
 * at which point the Monitored Jobs list's "Configure" flips from inline-expand
 * to navigating here. Until then this screen is reachable by URL only — no
 * editing regression in the list.
 */
@Component({
  selector: 'app-job-config',
  standalone: true,
  imports: [RouterLink, FormsModule, DrawerComponent],
  template: `
    <div class="page">
      @if (loading()) {
        <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
      } @else if (job(); as j) {
        <div class="page-header">
          <div class="title-row">
            <a class="btn btn-ghost btn-sm" routerLink="/config/monitored-jobs">← Monitored Jobs</a>
            <div class="title-main">
              <h1>{{ j.displayName ?? j.name }}</h1>
              <p class="text-muted text-sm">
                {{ j.name }} · {{ j.jobTypeName }} · ⏱ {{ j.pollingIntervalSeconds }}s ·
                <span class="badge" [class]="j.isActive ? 'badge-resolved' : 'badge-failed'">
                  {{ j.isActive ? 'Active' : 'Inactive' }}
                </span>
              </p>
            </div>
            <button class="btn btn-ghost btn-sm" (click)="openEditJob(j)">Edit Job</button>
          </div>
        </div>

        <!-- ── Section: Scan Sources ───────────────────────────────────────── -->
        <div class="card section">
          <div class="section-head">
            <h3>Scan Sources <span class="count">{{ j.sources.length }}</span></h3>
            <button class="btn btn-primary btn-sm" (click)="openSourceDrawer(null)">+ Add Source</button>
          </div>
          @if (j.sources.length === 0) {
            <div class="empty-state">
              <span class="empty-icon">📡</span>
              <p>This job has no scan sources yet. Add a source to begin monitoring.</p>
              <button class="btn btn-primary btn-sm" (click)="openSourceDrawer(null)">+ Add Source</button>
            </div>
          } @else {
            @for (s of j.sources; track s.scanSourceId) {
              <div class="source-block">
                <div class="source-head">
                  @if (j.sources.length > 1) {
                    <!-- Collapse toggle: only shown when the job has multiple sources.
                         Single-source jobs always show their rules (collapsing serves
                         no purpose and adds a pointless click). -->
                    <button class="collapse-btn" #colBtn
                            (click)="toggleSource(s.scanSourceId); colBtn.blur()"
                            [title]="isSourceCollapsed(s.scanSourceId) ? 'Expand source rules' : 'Collapse source rules'">
                      {{ isSourceCollapsed(s.scanSourceId) ? '▶' : '▼' }}
                    </button>
                  }
                  <span class="scan-icon">{{ scanIcon(s.scanTypeId) }}</span>
                  <strong>{{ s.name }}</strong>
                  <span class="badge badge-info">{{ s.scanTypeName }}</span>
                  <span class="source-config text-muted text-sm">{{ sourceConfig(s) }}</span>
                  @if (!s.isActive) { <span class="badge badge-failed">Inactive</span> }
                  <!-- Coverage rollup: visible only when collapsed and there are uncovered rules.
                       Lets the operator scan collapsed headers at a glance and expand only
                       what needs attention. -->
                  @if (j.sources.length > 1 && isSourceCollapsed(s.scanSourceId) && scanRulesNeedingClassCount(s) > 0) {
                    <span class="collapsed-gap"
                          title="Some scan rules have no matching classification rule — failures they produce won't be labeled">
                      ⚠ {{ scanRulesNeedingClassCount(s) }} {{ scanRulesNeedingClassCount(s) === 1 ? 'rule needs' : 'rules need' }} classification
                    </span>
                  }
                  <span class="source-tools">
                    @if (s.scanTypeName !== 'ApiEndpoint') {
                      <span class="rule-count">{{ s.scanCheckRules.length }} {{ s.scanCheckRules.length === 1 ? 'rule' : 'rules' }}</span>
                      <button class="btn btn-ghost btn-sm" (click)="openRuleDrawer(s, null)">+ Add Rule</button>
                    }
                    <span class="row-actions">
                      <button class="btn btn-ghost btn-sm" (click)="openSourceDrawer(s)">Edit</button>
                      <button class="btn btn-danger btn-sm" (click)="deleteSource(s)">Delete</button>
                    </span>
                  </span>
                </div>

                @if (!isSourceCollapsed(s.scanSourceId)) {
                  @if (s.scanTypeName === 'ApiEndpoint') {
                    <p class="source-note text-muted text-sm">API endpoint check — no rules needed.</p>
                  } @else if (s.scanCheckRules.length === 0) {
                    <p class="source-note text-muted text-sm">No rules on this source yet.</p>
                  } @else {
                    <table class="data-table compact rule-table">
                      <thead>
                        <tr><th style="width:14%">Check</th><th style="width:24%">Target</th><th style="width:26%">Detail</th><th style="width:12%">Severity</th><th style="width:24%"></th></tr>
                      </thead>
                      <tbody>
                        @for (r of s.scanCheckRules; track r.checkRuleId) {
                          <tr>
                            <td><span class="badge badge-info">{{ r.checkType }}</span></td>
                            <td class="font-mono">{{ r.targetField }}</td>
                            <td class="text-sm">{{ ruleDetail(r) }}</td>
                            <td><span class="badge" [class]="'badge-' + r.severity.toLowerCase()">{{ r.severity }}</span></td>
                            <td class="rule-actions">
                              @if (scanRuleNeedsClassification(r)) {
                                <button class="gap-warn gap-warn-btn"
                                        title="No classification rule covers this check's output — click to add one pre-filled with a matching pattern."
                                        (click)="openClassDrawerForScanRule(r)">⚠ No class rule</button>
                              }
                              <span class="row-actions">
                                <button class="btn btn-ghost btn-sm" (click)="openRuleDrawer(s, r)">Edit</button>
                                <button class="btn btn-danger btn-sm" (click)="deleteRule(r)">✕</button>
                              </span>
                            </td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  }
                }
              </div>
            }
          }
        </div>

        <!-- ── Section: Classification Rules ───────────────────────────────── -->
        <div class="card section">
          <div class="section-head">
            <h3>Classification Rules <span class="count">{{ j.rules.length }}</span></h3>
            <span class="head-actions">
              <button class="btn btn-ghost btn-sm" (click)="openLinkDrawer()">Link Existing</button>
              <button class="btn btn-primary btn-sm" (click)="openClassDrawer(null)">+ New Rule</button>
            </span>
          </div>
          <div class="section-body">
            @if (j.rules.length === 0 && jobTypeGlobalRules().length === 0) {
              <div class="empty-state"><span class="empty-icon">🏷️</span><p>No classification rules apply to this job yet.</p></div>
            } @else {
              @if (j.rules.length > 0) {
                <div class="subsection-label">Linked to this job</div>
                <table class="data-table compact">
                  <thead><tr><th style="width:34%">Pattern</th><th style="width:20%">Error Type</th><th style="width:10%">Conf.</th><th style="width:10%">Pri.</th><th style="width:26%"></th></tr></thead>
                  <tbody>
                    @for (r of j.rules; track r.ruleId) {
                      <tr>
                        <td class="font-mono">{{ r.pattern }}</td>
                        <td><span class="badge badge-classified">{{ r.errorTypeCode }}</span></td>
                        <td class="text-sm">{{ (r.confidence * 100).toFixed(0) }}%</td>
                        <td class="text-sm text-muted">#{{ r.priority }}</td>
                        <td class="rule-actions">
                          @if (classRuleNeedsFix(r)) {
                            <button class="gap-warn gap-warn-btn"
                                    title="No enabled fix option covers this error type — click to add one pre-filled with this error type."
                                    (click)="openFixRuleDrawerForClassRule(r)">⚠ No fix option</button>
                          }
                          <span class="row-actions">
                            <button class="btn btn-ghost btn-sm" (click)="openClassDrawer(r)">Edit</button>
                            <button class="btn btn-danger btn-sm" (click)="deleteClassRule(r)">✕</button>
                          </span>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
              @if (jobTypeGlobalRules().length > 0) {
                <div class="subsection-label">{{ j.jobTypeName }} defaults <span class="text-muted">(apply to all {{ j.jobTypeName }} jobs)</span></div>
                <table class="data-table compact">
                  <thead><tr><th style="width:48%">Pattern</th><th style="width:26%">Error Type</th><th style="width:13%">Conf.</th><th style="width:13%">Pri.</th></tr></thead>
                  <tbody>
                    @for (r of jobTypeGlobalRules(); track r.ruleId) {
                      <tr class="global-row">
                        <td class="font-mono">{{ r.pattern }}</td>
                        <td><span class="badge badge-classified">{{ r.errorTypeCode }}</span></td>
                        <td class="text-sm">{{ (r.confidence * 100).toFixed(0) }}%</td>
                        <td class="text-sm text-muted">#{{ r.priority }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            }
          </div>
        </div>

        <!-- ── Section: Fix Options ────────────────────────────────────────── -->
        <div class="card section">
          <div class="section-head">
            <h3>Fix Options <span class="count">{{ fixPolicies().length }}</span></h3>
            <button class="btn btn-primary btn-sm" (click)="openFixRuleDrawer(null)">+ New Fix Option</button>
          </div>
          <div class="section-body">
            @if (fixPolicies().length === 0) {
              <div class="empty-state"><span class="empty-icon">🛠</span><p>No fix policies for this job yet.</p>
                <button class="btn btn-primary btn-sm" (click)="openFixRuleDrawer(null)">+ New Fix Option</button></div>
            } @else {
              <table class="data-table compact">
                <thead><tr><th style="width:20%">Error Type</th><th style="width:22%">Action</th><th style="width:20%">Scope</th><th style="width:12%">Auto-heal</th><th style="width:14%">Status</th><th style="width:12%"></th></tr></thead>
                <tbody>
                  @for (p of fixPolicies(); track p.ruleId) {
                    <tr [class.shadowed]="isShadowedDefault(p)">
                      <td>
                        <span class="badge badge-classified">{{ p.errorTypeCode }}</span>
                        @if (fixHasNoClassCoverage(p)) {
                          <button class="gap-warn gap-warn-btn"
                                  title="No classification rule produces this error type — this fix won't trigger. Click to add a classification rule pre-filled with this error type."
                                  (click)="openClassDrawerForFixGap(p)">⚠ No class rule</button>
                        }
                      </td>
                      <td class="text-sm">{{ p.actionType }}@if (p.actionType === 'Composite') { · {{ p.steps.length }} steps }</td>
                      <td class="text-sm">
                        @if (p.monitoredJobId) { <span class="badge badge-info">Override</span> }
                        @else { Default @if (isShadowedDefault(p)) { <span class="badge badge-muted">shadowed</span> } }
                      </td>
                      <td>{{ p.isAutoHealEligible ? '✓' : '—' }}</td>
                      <td><span class="badge" [class]="p.enabled ? 'badge-resolved' : 'badge-failed'">{{ p.enabled ? 'Enabled' : 'Disabled' }}</span></td>
                      <td class="rule-actions"><span class="row-actions">
                        <button class="btn btn-ghost btn-sm" (click)="openFixRuleDrawer(p)">Edit</button>
                        <button class="btn btn-danger btn-sm" (click)="deleteFixRule(p)">✕</button>
                      </span></td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </div>
        </div>

        <!-- ── Edit Job drawer (identity only; scan config lives on sources) ── -->
        <app-drawer [open]="editOpen()" [ariaLabel]="'Edit job ' + j.name" (close)="editOpen.set(false)">
          <ng-container drawer-title><span class="text-muted text-sm">Edit Job</span> &nbsp;<strong>{{ j.name }}</strong></ng-container>
          <div class="form-grid">
            <div class="form-group span2">
              <label>Name *</label>
              <input [(ngModel)]="jobForm.name" />
            </div>
            <div class="form-group span2">
              <label>Display Name</label>
              <input [(ngModel)]="jobForm.displayName" placeholder="Optional friendly name" />
            </div>
            <div class="form-group">
              <label>Job Type *</label>
              <select [(ngModel)]="jobForm.jobTypeId">
                <option [ngValue]="0" disabled>Select…</option>
                @for (t of jobTypes(); track t.jobTypeId) { <option [ngValue]="t.jobTypeId">{{ t.name }}</option> }
              </select>
            </div>
            <div class="form-group">
              <label>Poll Interval (seconds)</label>
              <input type="number" [(ngModel)]="jobForm.pollingIntervalSeconds" min="10" />
              <span class="field-hint">Scan cadence at the job level. All sources of this job scan together within each tick, sequentially — sources cannot scan at independent frequencies in this version.</span>
            </div>
            <div class="form-group">
              <label class="toggle-label"><input type="checkbox" [(ngModel)]="jobForm.isActive" /> Active</label>
            </div>
            <div class="form-group span2">
              <label>Description</label>
              <textarea [(ngModel)]="jobForm.description" rows="2" placeholder="Optional notes"></textarea>
            </div>
          </div>
          @if (editError()) { <div class="edit-error">⚠ {{ editError() }}</div> }
          <div class="drawer-foot">
            <button class="btn btn-ghost" (click)="editOpen.set(false)">Cancel</button>
            <button class="btn btn-primary" (click)="saveJobEdit()" [disabled]="saving()">
              @if (saving()) { <span class="spinner"></span> } Save Changes
            </button>
          </div>
        </app-drawer>

        <!-- ── Scan Source drawer (config branches on ScanType; immutable on edit) ── -->
        <app-drawer [open]="sourceDrawerOpen()" [ariaLabel]="'Scan source'" (close)="sourceDrawerOpen.set(false)">
          <ng-container drawer-title><span class="text-muted text-sm">{{ editingSourceId() ? 'Edit' : 'New' }} Scan Source</span></ng-container>
          <div class="form-grid">
            <div class="form-group span2">
              <label>Name *</label>
              <input [(ngModel)]="sourceForm.name" placeholder="e.g. App logs, Orders DB" />
            </div>
            <div class="form-group span2">
              <label>Scan Type *</label>
              @if (editingSourceId()) {
                <input [value]="scanTypeName(sourceForm.scanTypeId)" disabled />
                <span class="field-hint">Scan type can't change after creation — delete and recreate to switch it.</span>
              } @else {
                <select [(ngModel)]="sourceForm.scanTypeId">
                  @for (t of scanTypes; track t.id) { <option [ngValue]="t.id">{{ t.name }}</option> }
                </select>
              }
            </div>

            @if (isFileBased(sourceForm.scanTypeId)) {
              <div class="form-group span2">
                <label>Folder to Scan *</label>
                <input [(ngModel)]="sourceForm.logFolder" placeholder="C:\logs\app" />
              </div>
              @if (sourceForm.scanTypeId === 1) {
                <div class="form-group span2">
                  <label>Search Patterns</label>
                  <input [(ngModel)]="sourceForm.searchPatterns" placeholder="app*.log, error*.log" />
                </div>
                <div class="form-group span2">
                  <label>Input Folder</label>
                  <input [(ngModel)]="sourceForm.inputFolder" placeholder="Optional — base for relative input paths" />
                </div>
              }
              <div class="form-group span2">
                <label class="toggle-label"><input type="checkbox" [(ngModel)]="sourceForm.includeSubfolders" /> Include subfolders (recurse)</label>
              </div>
            }
            @if (sourceForm.scanTypeId === 2) {
              <div class="form-group span2">
                <label>Connection Name *</label>
                <input [(ngModel)]="sourceForm.connectionName" placeholder="appsettings connection key" />
              </div>
            }
            @if (sourceForm.scanTypeId === 3) {
              <div class="form-group span2">
                <label>API URL *</label>
                <input [(ngModel)]="sourceForm.logSourceUrl" placeholder="https://api.example.com/health" />
              </div>
            }
            @if (editingSourceId()) {
              <div class="form-group span2">
                <label class="toggle-label"><input type="checkbox" [(ngModel)]="sourceForm.isActive" /> Active</label>
              </div>
            }
          </div>
          @if (sourceError()) { <div class="edit-error">⚠ {{ sourceError() }}</div> }
          <div class="drawer-foot">
            <button class="btn btn-ghost" (click)="sourceDrawerOpen.set(false)">Cancel</button>
            <button class="btn btn-primary" (click)="saveSource()" [disabled]="savingSource()">
              @if (savingSource()) { <span class="spinner"></span> } Save
            </button>
          </div>
        </app-drawer>

        <!-- ── Scan Rule drawer (config branches on the source's ScanType) ────── -->
        <app-drawer [open]="ruleDrawerOpen()" [ariaLabel]="'Scan rule'" (close)="ruleDrawerOpen.set(false)">
          <ng-container drawer-title>
            <span class="text-muted text-sm">{{ editingRuleId() ? 'Edit' : 'New' }} Scan Rule</span>
            @if (editingRuleSource(); as src) { &nbsp;<strong>{{ src.name }}</strong> }
          </ng-container>
          <div class="form-grid">
            @if (editingRuleSource()?.scanTypeId === 1) {
              <!-- FileSystem: keyword + optional input-path extraction -->
              <div class="form-group span2">
                <label>Keyword / Pattern *</label>
                <input [(ngModel)]="ruleForm.targetField" placeholder="e.g. ERROR|FAILED|Exception" />
                <span class="field-hint">Text searched in each log file line (case-insensitive). Wildcards (*) are ignored — just type the keyword, e.g. File Not Found.</span>
              </div>
              <div class="form-group span2">
                <label>Input File Extraction</label>
                <input [(ngModel)]="ruleForm.inputPathPattern" placeholder="e.g. Processing file: (.+\.txt)" />
                <span class="field-hint">Optional. Regex; capture group #1 must be the input file path. Full regex (<em>not</em> the <code>*</code>-wildcard shorthand). The captured path becomes the <code>{{'{'}}sourceFilePath{{'}'}}</code> placeholder for a fix policy's payload. Leave blank if no fix needs the input file.</span>
              </div>
            } @else if (editingRuleSource()?.scanTypeId === 4) {
              <!-- FileContent: structured extraction from input data files -->
              <div class="form-group span2">
                <label>Filename Pattern *</label>
                <input [(ngModel)]="ruleForm.targetField" placeholder="e.g. *WARNING*.xml  or  *.xml" />
                <span class="field-hint">Files whose name matches are examined. Uses <code>*</code> as a wildcard, case-insensitive — same DSL as classification patterns (<em>not</em> regex).</span>
              </div>
              <div class="form-group span2">
                <label>Format *</label>
                <select [(ngModel)]="ruleForm.extractorType">
                  @for (f of fileFormats; track f) { <option [ngValue]="f">{{ f }}</option> }
                </select>
                <span class="field-hint">Extractor used to read the file. XML only in v1.</span>
              </div>
              <div class="form-group span2">
                <label>Value Locator (XPath)</label>
                <input [(ngModel)]="ruleForm.extractorLocator" placeholder="e.g. /file/status/code" />
                <span class="field-hint"><strong>Leave blank if the filename match alone signals the failure.</strong> Namespaces are ignored — write plain element names, not <code>local-name()</code>.</span>
              </div>
              <div class="form-group">
                <label>Predicate</label>
                <!-- Two-way bind splits into [ngModel]+(ngModelChange) so we can
                     clear the stale predicate value when the operator switches to
                     "None". Without this the invisible value field retains its old
                     string and the backend returns PredicateIncomplete 400. -->
                <select [ngModel]="ruleForm.extractorPredicateType"
                        (ngModelChange)="onPredicateTypeChange($event)">
                  <option [ngValue]="null">None — filename match is the failure</option>
                  @for (p of predicateTypes; track p) { <option [ngValue]="p">{{ p }}</option> }
                </select>
              </div>
              @if (ruleForm.extractorPredicateType) {
                <div class="form-group">
                  <label>Predicate Value *</label>
                  <input [(ngModel)]="ruleForm.extractorPredicateValue" placeholder="e.g. ERROR" />
                </div>
                @if (!ruleForm.extractorLocator) {
                  <div class="soft-warn span2">⚠ A predicate needs a <strong>Value Locator</strong> to extract the value it tests.</div>
                }
              }
              <div class="form-group span2">
                <label>Identifier Locator (XPath)</label>
                <input [(ngModel)]="ruleForm.identifierLocator" placeholder="e.g. /file/header/invoiceId" />
                <span class="field-hint">XPath to the natural key, stored as the failure's <code>{{'{'}}sourceId{{'}'}}</code>. Leave blank to use the filename without extension.</span>
              </div>
            } @else {
              <!-- Database: ColumnRange / ValueEquals -->
              <div class="form-group span2">
                <label>Check Type *</label>
                <select [(ngModel)]="ruleForm.checkType">
                  @for (ct of dbCheckTypes; track ct) { <option [ngValue]="ct">{{ ct }}</option> }
                </select>
              </div>
              @if (ruleForm.checkType === 'SqlQuery') {
                <div class="form-group span2">
                  <label>Source Query *</label>
                  <textarea [(ngModel)]="ruleForm.sourceTable" rows="4" class="sql-area"
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
                  <input [(ngModel)]="ruleForm.targetField" placeholder="IsStuck" />
                  <span class="field-hint">Result-set column whose value is shown on each failure.</span>
                </div>
                <div class="form-group">
                  <label>Source ID Column <span class="text-muted">(row identity)</span></label>
                  <input [(ngModel)]="ruleForm.sourceIdColumn" placeholder="OrderId" />
                  <span class="field-hint">
                    Result-set column used as the failure's <code>{{'{'}}sourceId{{'}'}}</code>. Blank → row number.
                    <strong>Set this</strong> so a new problem row is detected even while an earlier row's failure is still open
                    (per-row dedup, case-insensitive).
                  </span>
                </div>
                <div class="form-group span2">
                  <label>Watermark Column <span class="text-muted">(scan cursor — optional)</span></label>
                  <input [(ngModel)]="ruleForm.watermarkColumn" placeholder="UpdateDate" />
                  <span class="field-hint">
                    Incremental scanning, same as the single-table rules: each scan only processes rows whose value
                    here exceeds the last one seen. <strong>The column must be in your <code>SELECT</code></strong>
                    (e.g. add <code>UpdateDate</code> to the query). Blank → the whole query re-runs every tick and
                    dedup relies on Source ID / open-failure state. For large result sets add
                    <code>ORDER BY {{ ruleForm.watermarkColumn || 'UpdateDate' }} ASC</code> so the 500-row cap reads oldest-first.
                  </span>
                </div>
              } @else {
                <div class="form-group">
                  <label>Source Table *</label>
                  <input [(ngModel)]="ruleForm.sourceTable" placeholder="dbo.Files" />
                </div>
                <div class="form-group">
                  <label>Target Field *</label>
                  <input [(ngModel)]="ruleForm.targetField" placeholder="FileStatusCode" />
                </div>
                @if (ruleForm.checkType === 'ValueEquals') {
                  <div class="form-group span2">
                    <label>Expected Value (triggers failure)</label>
                    <input [(ngModel)]="ruleForm.expectedValue" placeholder="5" />
                  </div>
                }
                @if (ruleForm.checkType === 'ColumnRange') {
                  <div class="form-group">
                    <label>Min Value</label>
                    <input type="number" [(ngModel)]="ruleForm.minValue" placeholder="blank = −∞" />
                  </div>
                  <div class="form-group">
                    <label>Max Value</label>
                    <input type="number" [(ngModel)]="ruleForm.maxValue" placeholder="blank = +∞" />
                  </div>
                }
                <div class="form-group">
                  <label>Watermark Column <span class="text-muted">(scan cursor)</span></label>
                  <input [(ngModel)]="ruleForm.watermarkColumn" placeholder="UpdateDate" />
                </div>
                <div class="form-group">
                  <label>Source ID Column <span class="text-muted">(row identity)</span></label>
                  <input [(ngModel)]="ruleForm.sourceIdColumn" placeholder="Id" />
                </div>
                <div class="form-group span2">
                  <label>File Path Column</label>
                  <input [(ngModel)]="ruleForm.filePathColumn" placeholder="e.g. FilePath  or  j.FilePath" />
                  <span class="field-hint">Optional. Column on the source row holding the input file path → the <code>{{'{'}}sourceFilePath{{'}'}}</code> placeholder. No auto-JOIN — put any JOIN into Source Table and use <code>alias.Column</code> here.</span>
                </div>
              }
            }
            <div class="form-group">
              <label>Severity</label>
              <select [(ngModel)]="ruleForm.severity">
                @for (sv of severities; track sv) { <option [ngValue]="sv">{{ sv }}</option> }
              </select>
            </div>
            <div class="form-group">
              <label class="toggle-label"><input type="checkbox" [(ngModel)]="ruleForm.isActive" /> Active</label>
            </div>
            <div class="form-group span2">
              <label>Description</label>
              <input [(ngModel)]="ruleForm.description" placeholder="Optional notes" />
            </div>
          </div>
          @if (ruleError()) { <div class="edit-error">⚠ {{ ruleError() }}</div> }
          <div class="drawer-foot">
            <button class="btn btn-ghost" (click)="ruleDrawerOpen.set(false)">Cancel</button>
            <button class="btn btn-primary" (click)="saveRule()" [disabled]="savingRule()">
              @if (savingRule()) { <span class="spinner"></span> } {{ editingRuleId() ? 'Save Changes' : 'Add Rule' }}
            </button>
          </div>
        </app-drawer>

        <!-- ── Classification Rule drawer ────────────────────────────────────── -->
        <app-drawer [open]="classDrawerOpen()" [ariaLabel]="'Classification rule'" (close)="classDrawerOpen.set(false)">
          <ng-container drawer-title><span class="text-muted text-sm">{{ editingClassRule() ? 'Edit' : 'New' }} Classification Rule</span></ng-container>
          <div class="form-grid">
            <div class="form-group span2">
              <label>Match Pattern *</label>
              <input [(ngModel)]="classRuleForm.pattern" placeholder="e.g. FileNotFoundException  or  Error code * occurred" />
              <span class="field-hint">Case-insensitive substring of the error message. Use <code>*</code> as a wildcard for any text; other characters are literal.</span>
            </div>
            <div class="form-group span2">
              <label>Error Type *</label>
              <select [(ngModel)]="classRuleForm.errorTypeId">
                <option [ngValue]="0" disabled>Select error type…</option>
                @for (et of errorTypes(); track et.errorTypeId) { <option [ngValue]="et.errorTypeId">{{ et.code }} — {{ et.displayName }}</option> }
              </select>
            </div>
            <div class="form-group">
              <label>Confidence (0 – 1)</label>
              <input type="number" [(ngModel)]="classRuleForm.confidence" min="0" max="1" step="0.05" />
            </div>
            <div class="form-group">
              <label>Priority</label>
              <input type="number" [(ngModel)]="classRuleForm.priority" min="1" />
              <span class="field-hint">Lower = evaluated first.</span>
            </div>
            <div class="form-group">
              <label class="toggle-label"><input type="checkbox" [(ngModel)]="classRuleForm.isActive" /> Active</label>
            </div>
          </div>
          <div class="drawer-foot">
            <button class="btn btn-ghost" (click)="classDrawerOpen.set(false)">Cancel</button>
            <button class="btn btn-primary" (click)="saveClassRule()" [disabled]="savingClass()">
              @if (savingClass()) { <span class="spinner"></span> } {{ editingClassRule() ? 'Save Changes' : 'Add Rule' }}
            </button>
          </div>
        </app-drawer>

        <!-- ── Link Existing Classification Rule drawer ──────────────────────── -->
        <app-drawer [open]="linkDrawerOpen()" [ariaLabel]="'Link classification rule'" (close)="linkDrawerOpen.set(false)">
          <ng-container drawer-title><span class="text-muted text-sm">Link Existing Classification Rule</span></ng-container>
          <div class="form-group" style="margin-bottom:12px">
            <input [ngModel]="linkRuleSearch()" (ngModelChange)="linkRuleSearch.set($event)" placeholder="Search by pattern or error type…" />
          </div>
          @if (loadingLinkRules()) {
            <div class="loading-overlay" style="padding:20px 0"><span class="spinner"></span> Loading…</div>
          } @else if (filteredLinkableRules().length === 0) {
            <div class="empty-state"><span class="empty-icon">🏷️</span><p>No unlinked rules found{{ linkRuleSearch() ? ' matching "' + linkRuleSearch() + '"' : '' }}.</p></div>
          } @else {
            <div class="link-rule-list">
              @for (r of filteredLinkableRules(); track r.ruleId) {
                <div class="link-rule-item" (click)="confirmLinkRule(r)">
                  <div class="link-rule-pattern font-mono">{{ r.pattern }}</div>
                  <div class="link-rule-meta">
                    <span class="badge badge-classified">{{ r.errorTypeCode }}</span>
                    <span class="text-muted text-sm">{{ r.jobTypeName }}</span>
                    <span class="text-muted text-sm">conf {{ (r.confidence * 100).toFixed(0) }}%</span>
                  </div>
                </div>
              }
            </div>
          }
          <div class="drawer-foot">
            <button class="btn btn-ghost" (click)="linkDrawerOpen.set(false)">Cancel</button>
          </div>
        </app-drawer>

        <!-- ── Fix Option drawer ─────────────────────────────────────────────── -->
        <app-drawer [open]="fixDrawerOpen()" [ariaLabel]="'Fix option'" (close)="fixDrawerOpen.set(false)">
          <ng-container drawer-title><span class="text-muted text-sm">{{ editingFixRule() ? 'Edit' : 'New' }} Fix Option</span></ng-container>
          <div class="drawer-context-banner">
            <span class="banner-icon" aria-hidden="true">i</span>
            <span>
              @if (fixRuleForm.monitoredJobId !== null) {
                Fix policy for <strong>{{ j.displayName ?? j.name }}</strong> — applies to this job only.
              } @else {
                Fix policy for <strong>all {{ j.jobTypeName }} jobs</strong> (JobType-wide default) — a per-job policy overrides it.
              }
            </span>
          </div>
          <div class="form-grid">
            <div class="span2 scope-line">
              @if (fixRuleForm.monitoredJobId !== null) {
                <span class="scope-current">Scope: <strong>This job</strong></span>
                <button type="button" class="link-btn" (click)="setFixRuleScope(null)">Apply to all {{ j.jobTypeName }} jobs instead</button>
              } @else {
                <span class="scope-current">Scope: <strong>All {{ j.jobTypeName }} jobs</strong> (default)</span>
                <button type="button" class="link-btn" (click)="setFixRuleScope(j.monitoredJobId)">Scope to just this job instead</button>
              }
            </div>
            @if (effectiveClassRules().length > 0) {
              <div class="form-group span2">
                <label>Target a classification rule <span class="text-muted">(shortcut)</span></label>
                <select [ngModel]="shortcutRuleId" (ngModelChange)="pickClassificationRuleById($event)">
                  <option [ngValue]="null" disabled>Pick a symptom to target…</option>
                  @for (cr of effectiveClassRules(); track cr.ruleId) { <option [ngValue]="cr.ruleId">{{ cr.pattern }} → {{ cr.errorTypeCode }}</option> }
                </select>
                <span class="field-hint">Sets the Error Type below from the rule's type.</span>
              </div>
            }
            <div class="form-group span2">
              <label>Error Type *</label>
              <select [(ngModel)]="fixRuleForm.errorTypeId" (ngModelChange)="shortcutRuleId = null; syncFixRuleSignal()">
                <option [ngValue]="0" disabled>Select error type…</option>
                @for (et of errorTypes(); track et.errorTypeId) { <option [ngValue]="et.errorTypeId">{{ et.code }} — {{ et.displayName }}</option> }
              </select>
              @if (fixRuleDuplicateConflict(); as conflict) {
                <div class="dup-warn">
                  ⚠ An active fix policy already exists for this Error Type at the
                  <strong>{{ fixRuleForm.monitoredJobId === null ? 'default (all jobs)' : 'override (this job)' }}</strong> scope.
                  Existing: <strong>{{ conflict.fixCategory }} / {{ conflict.actionType }}</strong>.
                  <button type="button" class="link-btn" (click)="openConflictingPolicy(conflict)">Edit existing policy instead?</button>
                </div>
              } @else if (fixRuleSaveConflict(); as conflict) {
                <div class="dup-warn">⚠ {{ conflict.message }}
                  <button type="button" class="link-btn" (click)="openConflictingPolicyById(conflict.conflictingPolicyId)">Open existing policy</button>
                </div>
              }
              @if (selectedErrorTypeCode(); as code) {
                @if (classRulesForSelectedErrorType().length > 0) {
                  <span class="field-hint covers-hint">
                    Covers {{ classRulesForSelectedErrorType().length }} classification {{ classRulesForSelectedErrorType().length === 1 ? 'rule' : 'rules' }} on this job:
                    @for (cr of classRulesForSelectedErrorType(); track cr.ruleId; let last = $last) { <code>{{ cr.pattern }}</code>{{ last ? '' : ', ' }} }
                  </span>
                } @else {
                  <div class="dup-warn reachability-warn">
                    ⚠ No classification rule on this job maps to <strong>{{ code }}</strong> — this fix won't trigger until one exists. Add a matching rule in <strong>Classification Rules</strong> above.
                  </div>
                }
              }
            </div>
            <div class="form-group">
              <label>Action Description *</label>
              <input [(ngModel)]="fixRuleForm.actionToApply" placeholder="e.g. Retry DTSX job via management API" />
            </div>
            <div class="form-group">
              <label>Execution Type *</label>
              <!-- Primary choice — pick this first; Fix Category derives automatically.
                   Disabled only when Fix Category is locked to Manual (operator
                   chose Manual from Fix Category, which back-locks this to Manual). -->
              <select [ngModel]="fixRuleForm.actionType" (ngModelChange)="setFixRuleActionType($event)"
                      [disabled]="fixRuleForm.fixCategory === 'Manual'">
                <option [ngValue]="''" disabled>Select execution type…</option>
                @for (a of orderedActionTypes(); track a) { <option [ngValue]="a">{{ a }}</option> }
              </select>
            </div>
            <div class="form-group">
              <label>Fix Category</label>
              <!-- Derives automatically when Execution Type is picked.
                   Manual ↔ Manual is a hard coupling — picking Manual here
                   locks Execution Type to Manual too (and vice versa). -->
              <select [ngModel]="fixRuleForm.fixCategory" (ngModelChange)="setFixRuleCategory($event)">
                <option [ngValue]="''" disabled>Derived from execution type…</option>
                @for (c of fixCategories; track c) { <option [ngValue]="c">{{ c }}</option> }
              </select>
            </div>
            <div class="form-group">
              <label>Behaviour</label>
              <div class="toggles-row">
                <label class="toggle-pair">
                  <span class="toggle"><input type="checkbox" [(ngModel)]="fixRuleForm.isAutoHealEligible" /><span class="slider"></span></span>
                  <span class="toggle-text">Auto-Heal</span>
                </label>
                <label class="toggle-pair">
                  <span class="toggle"><input type="checkbox" [(ngModel)]="fixRuleForm.enabled" (ngModelChange)="syncFixRuleSignal()" /><span class="slider"></span></span>
                  <span class="toggle-text">Enabled</span>
                </label>
              </div>
            </div>
            @if (fixRuleForm.actionType && fixRuleForm.actionType !== 'Manual' && fixRuleForm.actionType !== 'Composite') {
              <div class="form-group span2">
                <label>Action Payload</label>
                @if (fixRuleForm.actionType === 'ApiCall') {
                  <input [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()" placeholder="http://jobs.internal/api/jobs/{failureId}/retry" />
                  <span class="field-hint">Use {{'{'}}failureId{{'}'}} as a placeholder — replaced at runtime.</span>
                } @else if (fixRuleForm.actionType === 'StoredProcedure') {
                  <input [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()" placeholder="dbo.sp_RetryJob  or  ConnName|dbo.sp_RetryJob" />
                } @else if (fixRuleForm.actionType === 'SqlScript') {
                  <textarea [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()" rows="4" placeholder="UPDATE dbo.Files SET FileStatusCode = 0 WHERE Id = '{sourceId}'"></textarea>
                  <span class="field-hint">Runs against the job's configured connection. Key the source row on <code>'{{'{'}}sourceId{{'}'}}'</code> (quoted) — <em>not</em> <code>{{'{'}}failureId{{'}'}}</code> (MAIA's internal id). A fix must be scoped to the failing row or it won't save.</span>
                  @if (sqlFixNeedsScopeShortcut(fixRuleForm.actionPayload)) {
                    <button type="button" class="link-btn scope-shortcut" (click)="scopeFixPayloadToSourceId()">+ scope to the failing row — add <code>{{ scopeClauseFor(fixRuleForm.actionPayload) }} {{ fixScopeColumn }} = '{{'{'}}sourceId{{'}'}}'</code></button>
                  }
                } @else if (fixRuleForm.actionType === 'CopyFile') {
                  <input [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()" placeholder="{sourceFilePath}|{inputFolder}\reprocess\{sourceFileName}" />
                  <span class="field-hint">Format <code>SOURCE|DEST</code>. Atomic copy, overwrite by default. <code>{{'{'}}sourceFilePath{{'}'}}</code> needs Input File Extraction (FS) or File Path Column (DB) on a scan rule.</span>
                } @else {
                  <input [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()" placeholder="powershell.exe C:\scripts\fix.ps1 {failureId}" />
                }
              </div>
            }
            @if (fixRuleForm.actionType === 'Composite') {
              <div class="form-group span2">
                <label>Steps *</label>
                <div class="steps-editor">
                  @for (step of fixRuleForm.steps ?? []; track $index; let i = $index) {
                    <div class="step-block">
                      <div class="step-row">
                        <span class="step-order">{{ i + 1 }}.</span>
                        <select [(ngModel)]="step.actionType" class="step-type">
                          <option value="SqlScript">SqlScript</option>
                          <option value="Script">Script</option>
                          <option value="CopyFile">CopyFile</option>
                          <option value="ApiCall">ApiCall</option>
                          <option value="StoredProcedure">StoredProcedure</option>
                        </select>
                        @if (step.actionType === 'SqlScript') {
                          <textarea [ngModel]="step.actionPayload" (ngModelChange)="step.actionPayload = $event; syncFixRuleSignal()" rows="2" class="step-payload step-payload-sql" [placeholder]="payloadPlaceholderFor(step.actionType)"></textarea>
                        } @else {
                          <input [ngModel]="step.actionPayload" (ngModelChange)="step.actionPayload = $event; syncFixRuleSignal()" class="step-payload" [placeholder]="payloadPlaceholderFor(step.actionType)" />
                        }
                        <div class="step-controls">
                          <button type="button" class="btn btn-ghost btn-icon" title="Move up" (click)="moveStep(i, -1)" [disabled]="i === 0">↑</button>
                          <button type="button" class="btn btn-ghost btn-icon" title="Move down" (click)="moveStep(i, +1)" [disabled]="i === (fixRuleForm.steps?.length ?? 0) - 1">↓</button>
                          <button type="button" class="btn btn-ghost btn-icon" title="Remove step" (click)="removeStep(i)">✕</button>
                        </div>
                      </div>
                      @if (step.actionType === 'SqlScript' && sqlFixNeedsScopeShortcut(step.actionPayload)) {
                        <button type="button" class="link-btn step-scope" (click)="scopeStepToSourceId(step)">+ scope to the failing row — add <code>{{ scopeClauseFor(step.actionPayload) }} {{ fixScopeColumn }} = '{{'{'}}sourceId{{'}'}}'</code></button>
                      }
                      <input [(ngModel)]="step.description" class="step-desc" placeholder="Description (optional)" />
                    </div>
                  }
                  <button type="button" class="btn btn-ghost btn-sm step-add" (click)="addStep()">+ Add Step</button>
                </div>
                <span class="field-hint">Steps run in order. Any step failure routes the failure to <strong>ManualRequired</strong>; subsequent steps still run (best-effort). One log row per step.</span>
              </div>
            }
            @if (fixRuleSourcePathWarning()) {
              <div class="form-group span2">
                <div class="dup-warn">⚠ This payload uses <code>{{'{'}}sourceFilePath{{'}'}}</code>, but no scan rule on <strong>{{ j.displayName ?? j.name }}</strong> captures a file path. Set <strong>Input File Extraction</strong> (FS) or <strong>File Path Column</strong> (DB) on a scan rule, or the fix fails at runtime with an empty source path.</div>
              </div>
            }
            @if (fixRuleForm.actionType && fixRuleForm.actionType !== 'Manual') {
              <div class="form-group span2">
                <details class="token-legend">
                  <summary>Available placeholders</summary>
                  <dl>
                    <dt><code>{{'{'}}failureId{{'}'}}</code></dt><dd>This failure's numeric id.</dd>
                    <dt><code>{{'{'}}sourceId{{'}'}}</code></dt><dd>Source row's natural key (DB scan) or matched id.</dd>
                    <dt><code>{{'{'}}sourceLogPath{{'}'}}</code></dt><dd>Log file/source where the error was detected.</dd>
                    <dt><code>{{'{'}}sourceFilePath{{'}'}}</code></dt><dd>Input file path — needs Input File Extraction (FS) or File Path Column (DB).</dd>
                    <dt><code>{{'{'}}sourceFileName{{'}'}}</code></dt><dd>Filename only, sliced from {{'{'}}sourceFilePath{{'}'}}.</dd>
                    <dt><code>{{'{'}}jobFolder{{'}'}}</code></dt><dd>The source's scanned folder.</dd>
                    <dt><code>{{'{'}}inputFolder{{'}'}}</code></dt><dd>The source's input folder.</dd>
                  </dl>
                  <span class="token-note">Unknown tokens are left as-is. Matching is case-insensitive.</span>
                </details>
              </div>
            }
            @if (!fixRuleForm.enabled && fixRuleForm.monitoredJobId === null && editingFixRule() !== null && editingFixRule()!.enabled) {
              <div class="dup-warn span2">⚠ Disabling this default — jobs of this JobType without their own override for this error type fall back to the built-in catalogue. Overrides on other jobs are unaffected.</div>
            }
          </div>
          @if (fixRuleForm.isAutoHealEligible) {
            <div class="auto-heal-banner"><span>⚡</span><span>Auto-heal is ON — this fix executes <strong>automatically</strong> without operator approval whenever this error type is detected.</span></div>
          }
          @if (fixRuleSaveError(); as msg) {
            <div class="dup-warn save-error" role="alert">⚠ Save failed: {{ msg }}</div>
          }
          <div class="drawer-foot">
            <button class="btn btn-ghost" (click)="fixDrawerOpen.set(false)">Cancel</button>
            <button class="btn btn-primary" (click)="saveFixRule()"
                    [disabled]="savingFix() || !fixRuleForm.actionType"
                    [title]="!fixRuleForm.actionType ? 'Select an execution type first' : ''">
              @if (savingFix()) { <span class="spinner"></span> } {{ editingFixRule() ? 'Save Changes' : 'Add Fix Option' }}
            </button>
          </div>
        </app-drawer>
      } @else {
        <div class="card"><div class="empty-state"><span class="empty-icon">❓</span><p>Job not found.</p>
          <a class="btn btn-primary btn-sm" routerLink="/config/monitored-jobs">Back to Monitored Jobs</a></div></div>
      }
    </div>
  `,
  styles: [`
    /* Config screen is a focused, form-like surface — cap its width so content
       reads as a tidy column instead of stretching across an ultra-wide page. */
    .page { max-width: 1080px; }
    h1 { font-size: 22px; font-weight: 700; }
    .title-row { display: flex; align-items: flex-start; gap: 14px; }
    .title-main { flex: 1; min-width: 0; }
    .section { padding: 0; overflow: hidden; margin-bottom: 14px; }
    .section-head { display: flex; align-items: center; justify-content: space-between; padding: 12px 16px; border-bottom: 1px solid var(--border-light); background: var(--surface-2); }
    .section-head h3 { font-size: 14px; font-weight: 600; }
    .count { display: inline-block; min-width: 20px; padding: 0 6px; margin-left: 6px; border-radius: 10px; background: var(--surface-3, #e2e8f0); color: var(--text-muted); font-size: 11px; text-align: center; }
    .source-block { padding: 14px 16px; border-bottom: 1px solid var(--border); }
    .source-block:last-child { border-bottom: none; }
    /* Source header: identity (icon · name · type) flush-left, the config path
       takes the middle and truncates, count + actions flush-right. Left and
       right edges line up across every source; only the middle path varies. */
    .source-head { display: flex; align-items: center; gap: 8px; }
    .scan-icon { font-size: 16px; flex-shrink: 0; }
    .source-head strong { flex-shrink: 0; }
    .source-config { margin-left: 4px; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    /* Inline rule-count chip on the source header — muted meta styling matching
       the page's other meta chips. */
    .rule-count { font-size: 11px; color: var(--text-muted); background: var(--surface-2);
                  border: 1px solid var(--border-light); border-radius: 4px; padding: 1px 7px; white-space: nowrap; }
    /* Right-aligned tools cluster: primary "+ Add Rule" (always visible) +
       hover-reveal Edit/Delete. */
    .source-tools { margin-left: auto; display: flex; align-items: center; gap: 6px; }
    .source-note { margin: 6px 0 0 24px; }
    /* Fixed layout + width:100% → tables are the same width on every job and
       columns don't shift with the data (long XPaths/patterns wrap instead of
       widening their column). Per-table column widths set on the <th>s below. */
    .data-table.compact { margin-top: 6px; width: 100%; table-layout: fixed; }
    .data-table.compact th, .data-table.compact td { padding: 6px 10px; vertical-align: top; word-break: break-word; }
    /* Change 3 — breathing room on scan-rule rows only (class/fix tables keep
       their density). */
    .rule-table td { padding-top: 10px; padding-bottom: 10px; }
    .rule-actions { display: flex; align-items: center; gap: 6px; white-space: nowrap; }
    /* Push Edit/Delete to the right edge of the cell regardless of whether
       a gap badge is also present — badge floats left, buttons pin right. */
    .rule-actions .row-actions { margin-left: auto; }
    /* Change 1 — secondary actions reveal on hover / keyboard focus; layout is
       reserved (opacity, not display) so rows don't shift. */
    .row-actions { display: inline-flex; gap: 4px; opacity: 0; transition: opacity 150ms ease; }
    .source-head:hover .row-actions, .source-head:focus-within .row-actions,
    tr:hover .row-actions, tr:focus-within .row-actions { opacity: 1; }
    .soft-warn { padding: 7px 10px; border-radius: var(--radius-sm); background: #fffbeb; border: 1px solid #fde68a; color: #92400e; font-size: 12px; }
    .empty-state { padding: 24px; text-align: center; color: var(--text-muted); }
    .empty-icon { font-size: 28px; display: block; margin-bottom: 6px; }
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
    .scope-shortcut { align-self: flex-start; margin-top: 6px; font-size: 12px; }
    .step-scope { margin: 4px 0 0 32px; font-size: 11px; }
    .field-hint { font-size: 11px; color: var(--text-dim); }
    .drawer-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; }
    .edit-error { margin-top: 10px; padding: 8px 10px; border-radius: var(--radius-sm); background: #fef2f2; border: 1px solid #fecaca; color: #991b1b; font-size: 12px; }

    /* Section heads + bodies (class/fix panels) */
    .head-actions { display: flex; gap: 6px; }
    .section-body { padding: 12px 16px; }
    .subsection-label { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-muted); margin: 14px 0 6px; }
    .subsection-label:first-child { margin-top: 0; }
    .global-row td { opacity: 0.82; }
    .data-table.compact tr.shadowed td { opacity: 0.6; }
    .badge-muted { background: #e2e8f0; color: #475569; border: 1px solid #cbd5e1; }

    /* Fix Option drawer */
    .drawer-context-banner { display: flex; align-items: center; gap: 8px; background: var(--primary-light); border: 1px solid var(--primary); border-radius: var(--radius-sm); padding: 8px 12px; font-size: 12px; color: var(--primary-dark); margin-bottom: 14px; }
    .banner-icon { display: inline-flex; align-items: center; justify-content: center; width: 18px; height: 18px; flex-shrink: 0; border-radius: 50%; background: var(--primary); color: #fff; font-style: italic; font-weight: 700; font-size: 12px; font-family: Georgia, serif; }
    .auto-heal-banner { display: flex; align-items: flex-start; gap: 8px; background: #fff7ed; border: 1px solid #fed7aa; border-radius: var(--radius-sm); padding: 10px 12px; font-size: 12px; color: #92400e; line-height: 1.5; margin-top: 12px; }
    .auto-heal-banner span:first-child { font-size: 16px; }
    .scope-line { display: flex; align-items: baseline; flex-wrap: wrap; gap: 4px 10px; font-size: 12px; color: var(--text-muted); }
    .scope-current strong { color: var(--text); }
    .link-btn { background: transparent; border: none; padding: 0; color: var(--primary); font-weight: 600; cursor: pointer; text-decoration: underline; font-size: inherit; }
    .link-btn:hover { color: var(--primary-dark); }
    .toggles-row { display: flex; gap: 24px; align-items: center; padding-top: 4px; }
    .toggle-pair { display: inline-flex; align-items: center; gap: 8px; cursor: pointer; }
    .toggle-pair .toggle-text { font-size: 13px; color: var(--text); }
    .covers-hint { display: block; }
    .dup-warn { display: block; margin-top: 6px; padding: 8px 10px; border-radius: var(--radius-sm); background: #fef3c7; border: 1px solid #fde68a; font-size: 12px; color: #78350f; line-height: 1.4; }
    .dup-warn .link-btn { color: #b45309; margin-left: 4px; }
    .dup-warn .link-btn:hover { color: #92400e; }
    .dup-warn.save-error { background: #fef2f2; border-color: #fecaca; color: #991b1b; margin-top: 12px; }
    .dup-warn.reachability-warn { margin-top: 6px; }

    /* Composite step editor */
    .steps-editor { display: flex; flex-direction: column; gap: 10px; margin-top: 4px; }
    .step-block { display: flex; flex-direction: column; gap: 4px; padding: 8px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); }
    .step-row { display: grid; grid-template-columns: 24px 110px 1fr auto; gap: 6px; align-items: start; }
    .step-row .step-order { font-weight: 600; color: var(--text-dim); text-align: right; align-self: center; }
    .step-row select.step-type, .step-row input.step-payload { font-size: 12px; padding: 4px 6px; min-width: 0; }
    .step-row textarea.step-payload { font-size: 12px; padding: 4px 6px; min-width: 0; font-family: ui-monospace, Menlo, Consolas, monospace; resize: vertical; }
    .step-block input.step-desc { font-size: 12px; padding: 4px 6px; margin-left: 32px; }
    .step-controls { display: flex; gap: 2px; align-self: center; }
    .step-row .btn-icon { padding: 2px 6px; font-size: 13px; line-height: 1.2; }
    .step-add { align-self: flex-start; margin-top: 2px; }

    /* Token legend */
    .token-legend { margin-top: 6px; font-size: 12px; border: 1px solid var(--border-light); border-radius: var(--radius-sm); background: var(--surface-2); }
    .token-legend > summary { cursor: pointer; padding: 6px 10px; font-weight: 600; color: var(--text-muted); user-select: none; }
    .token-legend[open] > summary { border-bottom: 1px solid var(--border-light); }
    .token-legend dl { margin: 0; padding: 8px 10px; display: grid; grid-template-columns: auto 1fr; gap: 4px 12px; }
    .token-legend dt, .token-legend dd { margin: 0; }
    .token-legend dd { color: var(--text-muted); }
    .token-legend code { font-size: 11px; }
    .token-note { display: block; padding: 0 10px 8px; font-size: 11px; color: var(--text-dim); }

    /* Link-rule picker */
    .link-rule-list { display: flex; flex-direction: column; gap: 6px; }
    .link-rule-item { padding: 10px 12px; border: 1px solid var(--border); border-radius: var(--radius-sm); cursor: pointer; transition: all var(--transition); }
    .link-rule-item:hover { border-color: var(--primary); background: var(--primary-light); }
    .link-rule-pattern { font-size: 12px; margin-bottom: 4px; word-break: break-all; }
    .link-rule-meta { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }

    /* Coverage gap indicators — amber, noticeable but never alarming.
       A gap may be intentional (operator plans to configure downstream later).
       .gap-warn is the shared pill style; .gap-warn-btn makes it a clickable
       button (browser resets need to be undone: background/border/cursor). */
    .gap-warn, .gap-warn-btn {
      display: inline-flex; align-items: center; gap: 3px;
      color: #92400e; background: #fef3c7; border: 1px solid #f59e0b;
      border-radius: 4px; padding: 2px 7px;
      font-size: 12px; font-weight: 600; margin-left: 6px;
      line-height: 1.4; white-space: nowrap;
    }
    .gap-warn-btn { cursor: pointer; font-family: inherit; }
    .gap-warn-btn:hover { background: #fde68a; border-color: #d97706; }
    /* Rollup chip on a collapsed source header — matches the inline pill. */
    .collapsed-gap { display: inline-flex; align-items: center; gap: 3px;
                     color: #92400e; background: #fef3c7; border: 1px solid #f59e0b;
                     border-radius: 4px; padding: 2px 8px; font-size: 12px; font-weight: 600;
                     white-space: nowrap; margin-left: 6px; }

    /* Per-source collapse chevron — only rendered for multi-source jobs. */
    .collapse-btn { background: none; border: none; padding: 0 4px 0 0; cursor: pointer;
                    color: var(--text-muted); font-size: 11px; flex-shrink: 0; line-height: 1; }
    .collapse-btn:hover { color: var(--text); }
  `]
})
export class JobConfigComponent implements OnInit {
  private svc    = inject(ConfigService);
  private route  = inject(ActivatedRoute);
  private router = inject(Router);

  job           = signal<MonitoredJob | null>(null);
  fixPolicies   = signal<FixPolicyRule[]>([]);
  jobTypes      = signal<JobType[]>([]);
  errorTypes    = signal<ErrorType[]>([]);
  allClassRules = signal<ClassificationRule[]>([]);
  allJobs       = signal<MonitoredJob[]>([]);
  loading       = signal(true);

  // Edit-job drawer (identity only — scan config lives on sources in Tier 2.5).
  editOpen  = signal(false);
  saving    = signal(false);
  editError = signal<string | null>(null);
  jobForm: UpsertJobRequest = this.blankJob();

  // Scan Source drawer.
  readonly scanTypes = SCAN_TYPES;
  sourceDrawerOpen = signal(false);
  savingSource     = signal(false);
  sourceError      = signal<string | null>(null);
  editingSourceId  = signal<number | null>(null);
  sourceForm: UpsertScanSourceRequest = this.blankSource();

  // Scan Rule drawer (config branches on the rule's owning source ScanType).
  readonly dbCheckTypes   = DB_CHECK_TYPES;
  readonly fileFormats    = FILE_FORMATS;
  readonly predicateTypes = PREDICATE_TYPES;
  readonly severities     = SEVERITIES;
  ruleDrawerOpen    = signal(false);
  savingRule        = signal(false);
  ruleError         = signal<string | null>(null);
  editingRuleSource = signal<ScanSource | null>(null);
  editingRuleId     = signal<number | null>(null);
  ruleForm: UpsertScanRuleRequest & { isActive: boolean } = this.blankScanRule();

  // ── Classification Rule drawers ───────────────────────────────────────────
  classDrawerOpen  = signal(false);
  savingClass      = signal(false);
  editingClassRule = signal<RuleOverride | null>(null);
  classRuleForm: UpsertJobClassificationRuleRequest = this.blankClassRule();
  // Link-existing drawer.
  linkDrawerOpen   = signal(false);
  loadingLinkRules = signal(false);
  linkRuleSearch   = signal('');
  filteredLinkableRules = computed(() => {
    const linked = new Set(this.job()?.rules.map(r => r.ruleId) ?? []);
    const q = this.linkRuleSearch().toLowerCase();
    return this.allClassRules().filter(r =>
      !linked.has(r.ruleId) &&
      (!q || r.pattern.toLowerCase().includes(q) || r.errorTypeCode.toLowerCase().includes(q)));
  });

  // ── Fix Option drawer ─────────────────────────────────────────────────────
  readonly fixCategories = FIX_CATEGORIES;

  // Part 3 — soft guidance: order ActionType options with the most natural
  // choices for the current FixCategory first, all types still available
  // (DbFix+ApiCall can be legitimate — see CLAUDE.md decision).
  // Exception: FixCategory=Manual → only 'Manual' shown (hard coupling).
  orderedActionTypes = computed((): string[] => {
    const cat = this.fixRuleFormSignal().fixCategory;
    if (cat === 'Manual') return ['Manual'];
    // No category yet (blank new form) — neutral order, nothing pre-biased.
    if (!cat) return ['SqlScript', 'ApiCall', 'CopyFile', 'Script', 'StoredProcedure', 'Composite', 'Manual'];
    const orderMap: Record<string, string[]> = {
      'DbFix':      ['SqlScript', 'StoredProcedure', 'Composite', 'ApiCall', 'Script', 'CopyFile', 'Manual'],
      'FileRepair': ['CopyFile', 'Script', 'Composite', 'ApiCall', 'SqlScript', 'StoredProcedure', 'Manual'],
      'Retry':      ['ApiCall', 'Script', 'Composite', 'SqlScript', 'StoredProcedure', 'CopyFile', 'Manual'],
    };
    return orderMap[cat] ?? ['SqlScript', 'ApiCall', 'CopyFile', 'Script', 'StoredProcedure', 'Composite', 'Manual'];
  });
  fixDrawerOpen   = signal(false);
  savingFix       = signal(false);
  editingFixRule  = signal<FixPolicyRule | null>(null);
  fixRuleForm: UpsertFixPolicyRuleRequest = this.blankFixRule();
  shortcutRuleId: number | null = null;
  /** Mirror of fixRuleForm so the warning/dup computeds re-evaluate on change. */
  private fixRuleFormSignal = signal<UpsertFixPolicyRuleRequest>(this.blankFixRule());
  fixRuleSaveConflict = signal<{ message: string; conflictingPolicyId: number } | null>(null);
  fixRuleSaveError    = signal<string | null>(null);

  /** Two-pronged duplicate detection — same key shape as the backend 409. */
  fixRuleDuplicateConflict = computed<FixPolicyRule | null>(() => {
    const form = this.fixRuleFormSignal();
    if (!form.enabled || !form.errorTypeId || !form.jobTypeId) return null;
    const editingId = this.editingFixRule()?.ruleId;
    return this.fixPolicies().find(p => {
      if (!p.enabled || p.ruleId === editingId) return false;
      if (p.errorTypeId !== form.errorTypeId)   return false;
      return form.monitoredJobId !== null
        ? p.monitoredJobId === form.monitoredJobId
        : p.monitoredJobId === null && p.jobTypeId === form.jobTypeId;
    }) ?? null;
  });

  /** Soft config-time warning: payload references {sourceFilePath} but no scan
   *  rule on the job captures one (no InputPathPattern / FilePathColumn). */
  fixRuleSourcePathWarning = computed<boolean>(() => {
    const form = this.fixRuleFormSignal();
    const usesToken = (s: string | null | undefined) => !!s && /\{sourceFilePath\}/i.test(s);
    const referenced = usesToken(form.actionPayload) || (form.steps ?? []).some(s => usesToken(s.actionPayload));
    if (!referenced) return false;
    const captures = (this.job()?.sources ?? []).flatMap(s => s.scanCheckRules)
      .some(r => !!r.inputPathPattern?.trim() || !!r.filePathColumn?.trim());
    return !captures;
  });

  /** Effective classifier rules for this job: linked rules ∪ JobType-global
   *  defaults (rules of this JobType linked to no job). Mirrors GetEffectiveRulesAsync. */
  effectiveClassRules = computed<{ ruleId: number; pattern: string; errorTypeCode: string }[]>(() => {
    const job = this.job();
    if (!job) return [];
    const jt = this.getJobTypeId(job);
    const linked = job.rules ?? [];
    const linkedAnywhere = new Set(this.allJobs().flatMap(j => j.rules.map(r => r.ruleId)));
    const defaults = this.allClassRules().filter(r => r.jobTypeId === jt && r.isActive && !linkedAnywhere.has(r.ruleId));
    return [
      ...linked.map(r => ({ ruleId: r.ruleId, pattern: r.pattern, errorTypeCode: r.errorTypeCode })),
      ...defaults.map(r => ({ ruleId: r.ruleId, pattern: r.pattern, errorTypeCode: r.errorTypeCode })),
    ];
  });

  selectedErrorTypeCode = computed<string | null>(() => {
    const id = this.fixRuleFormSignal().errorTypeId;
    if (!id) return null;
    return this.errorTypes().find(e => e.errorTypeId === id)?.code ?? null;
  });

  classRulesForSelectedErrorType = computed(() => {
    const code = this.selectedErrorTypeCode();
    if (!code) return [];
    return this.effectiveClassRules().filter(r => r.errorTypeCode === code);
  });

  /** JobType-global rules that ALSO classify this job (active, same JobType,
   *  linked to no job) — shown read-only under the job's linked rules. */
  jobTypeGlobalRules = computed<ClassificationRule[]>(() => {
    const job = this.job();
    if (!job) return [];
    const jt = this.getJobTypeId(job);
    const linkedAnywhere = new Set(this.allJobs().flatMap(j => j.rules.map(r => r.ruleId)));
    return this.allClassRules()
      .filter(r => r.jobTypeId === jt && r.isActive && !linkedAnywhere.has(r.ruleId))
      .sort((a, b) => a.priority - b.priority);
  });

  private jobId = 0;

  ngOnInit() {
    this.jobId = Number(this.route.snapshot.paramMap.get('id'));
    // Lookups for the editors — loaded once, independent of the render-critical
    // job fetch. ErrorTypes feed the class/fix ErrorType selects; allClassRules
    // + allJobs drive the effective-classifier ("covers"/reachability) logic and
    // the link drawer; mirror the list component's union semantics.
    this.svc.getErrorTypes().subscribe(t => this.errorTypes.set(t));
    this.svc.getAllClassificationRules().subscribe(r => this.allClassRules.set(r));
    this.svc.getAllJobs().subscribe(j => this.allJobs.set(j));
    this.reload(true);
  }

  /** (Re)load the job + its fix policies. Called on init and after any edit. */
  private reload(initial = false) {
    if (initial) this.loading.set(true);
    this.svc.getJob(this.jobId).subscribe({
      next: j => {
        this.job.set(j);
        this.loading.set(false);
        // Fix policies are fetched separately (keyed on JobType + this job for overrides).
        this.svc.getJobTypes().subscribe(types => {
          this.jobTypes.set(types);
          const jobTypeId = types.find(t => t.name === j.jobTypeName)?.jobTypeId;
          if (jobTypeId) this.svc.getFixPolicyRules(jobTypeId, j.monitoredJobId)
            .subscribe(p => this.fixPolicies.set(p));
          if (initial) this.applyFixDeepLink(j);
        });
      },
      error: () => this.loading.set(false),
    });
  }

  /** Case-B deep-link from /unconfigured: ?errorTypeId=<id> on this screen pops
   *  a pre-filled new-fix drawer (per-job scope + that ErrorType). Param is
   *  cleared afterward so a refresh/back doesn't re-trigger. */
  private applyFixDeepLink(job: MonitoredJob) {
    const etId = Number(this.route.snapshot.queryParamMap.get('errorTypeId') ?? 0);
    if (!etId) return;
    this.openFixRuleDrawer(null);
    this.fixRuleForm.errorTypeId = etId;
    this.syncFixRuleSignal();
    this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }

  openEditJob(j: MonitoredJob) {
    this.editError.set(null);
    const jt = this.jobTypes().find(t => t.name === j.jobTypeName);
    this.jobForm = {
      name: j.name, displayName: j.displayName, jobTypeId: jt?.jobTypeId ?? 0,
      // Scan config now lives on sources; preserve the job's legacy columns
      // unchanged — this form only edits identity + cadence.
      scanTypeId: j.scanTypeId, logFolder: j.logFolder, searchPatterns: j.searchPatterns,
      inputFolder: j.inputFolder, includeSubfolders: j.includeSubfolders,
      connectionName: j.connectionName, logSourceUrl: j.logSourceUrl,
      pollingIntervalSeconds: j.pollingIntervalSeconds, isActive: j.isActive,
      description: j.description,
    };
    this.editOpen.set(true);
  }

  saveJobEdit() {
    if (!this.jobForm.name || !this.jobForm.jobTypeId) {
      this.editError.set('Name and Job Type are required.');
      return;
    }
    this.saving.set(true);
    this.editError.set(null);
    this.svc.updateJob(this.jobId, this.jobForm).subscribe({
      next: () => { this.editOpen.set(false); this.saving.set(false); this.reload(); },
      error: e => { this.editError.set(e?.error?.message ?? 'Save failed.'); this.saving.set(false); },
    });
  }

  private blankJob(): UpsertJobRequest {
    return { name: '', displayName: null, jobTypeId: 0, scanTypeId: 1, logFolder: null,
             searchPatterns: null, inputFolder: null, includeSubfolders: false,
             connectionName: null, logSourceUrl: null, pollingIntervalSeconds: 300,
             isActive: true, description: null };
  }

  // ── Scan Source CRUD ──────────────────────────────────────────────────────
  /** Soft, non-blocking hint for a SqlQuery rule whose query has no row filter:
   *  no WHERE, no HAVING, and not an EXEC (stored procs filter internally). Such a
   *  query flags every returned row as a failure — bounded (500-row cap) but
   *  usually a mistake. Read path, so warn-not-block; no server-side check. */
  sqlQueryNeedsWhereWarning(): boolean {
    const q = this.ruleForm.sourceTable ?? '';
    if (!q.trim()) return false;              // empty → handled by required validation
    if (/^\s*EXEC\b/i.test(q)) return false;  // stored proc — filtering lives inside it
    return !/\bWHERE\b/i.test(q) && !/\bHAVING\b/i.test(q);
  }

  isFileBased(scanTypeId: number): boolean { return scanTypeId === 1 || scanTypeId === 4; }
  scanTypeName(id: number): string { return this.scanTypes.find(t => t.id === id)?.name ?? String(id); }

  openSourceDrawer(s: ScanSource | null) {
    this.sourceError.set(null);
    this.editingSourceId.set(s?.scanSourceId ?? null);
    this.sourceForm = s
      ? { name: s.name, scanTypeId: s.scanTypeId, logFolder: s.logFolder, searchPatterns: s.searchPatterns,
          inputFolder: s.inputFolder, includeSubfolders: s.includeSubfolders,
          connectionName: s.connectionName, logSourceUrl: s.logSourceUrl, isActive: s.isActive }
      : this.blankSource();
    this.sourceDrawerOpen.set(true);
  }

  saveSource() {
    if (!this.sourceForm.name?.trim()) { this.sourceError.set('Name is required.'); return; }
    this.savingSource.set(true);
    this.sourceError.set(null);
    const id = this.editingSourceId();
    const req$: Observable<unknown> = id
      ? this.svc.updateScanSource(id, this.sourceForm)
      : this.svc.createScanSource(this.jobId, this.sourceForm);
    req$.subscribe({
      next: () => { this.sourceDrawerOpen.set(false); this.savingSource.set(false); this.reload(); },
      // 400 from the validation matrix (SourceFolderConflict, LogFolderRequired, …)
      // carries { error, message } — surface the message in the drawer footer.
      error: e => { this.sourceError.set(e?.error?.message ?? 'Save failed.'); this.savingSource.set(false); },
    });
  }

  deleteSource(s: ScanSource) {
    if (!confirm(`Delete source "${s.name}"? Its scan rules will be deactivated.`)) return;
    this.svc.deleteScanSource(s.scanSourceId).subscribe({ next: () => this.reload() });
  }

  private blankSource(): UpsertScanSourceRequest {
    return { name: '', scanTypeId: 1, logFolder: null, searchPatterns: null, inputFolder: null,
             includeSubfolders: false, connectionName: null, logSourceUrl: null, isActive: true };
  }

  // ── Scan Rule CRUD (per source) ───────────────────────────────────────────
  openRuleDrawer(source: ScanSource, rule: ScanCheckRule | null) {
    this.ruleError.set(null);
    this.editingRuleSource.set(source);
    this.editingRuleId.set(rule?.checkRuleId ?? null);
    this.ruleForm = rule ? {
      checkType: rule.checkType, sourceTable: rule.sourceTable, targetField: rule.targetField,
      minValue: rule.minValue, maxValue: rule.maxValue, expectedValue: rule.expectedValue,
      watermarkColumn: rule.watermarkColumn, sourceIdColumn: rule.sourceIdColumn,
      filePathColumn: rule.filePathColumn, inputPathPattern: rule.inputPathPattern,
      extractorType: rule.extractorType, extractorLocator: rule.extractorLocator,
      identifierLocator: rule.identifierLocator, extractorPredicateType: rule.extractorPredicateType,
      extractorPredicateValue: rule.extractorPredicateValue,
      severity: rule.severity, description: rule.description, isActive: true,
    } : this.blankScanRule(source.scanTypeId);
    this.ruleDrawerOpen.set(true);
  }

  saveRule() {
    if (!this.ruleForm.targetField?.trim()) { this.ruleError.set('Target field / pattern is required.'); return; }
    this.savingRule.set(true);
    this.ruleError.set(null);
    const ruleId   = this.editingRuleId();
    const sourceId = this.editingRuleSource()!.scanSourceId;
    const req$: Observable<unknown> = ruleId
      ? this.svc.updateScanRule(ruleId, this.ruleForm)
      : this.svc.createScanRuleForSource(sourceId, this.ruleForm);
    req$.subscribe({
      // 400 FileContent validation (ExtractorTypeRequired / PredicateIncomplete /
      // PredicateRequiresLocator) carries { error, message } — surface the message.
      next: () => { this.ruleDrawerOpen.set(false); this.savingRule.set(false); this.reload(); },
      error: e => { this.ruleError.set(e?.error?.message ?? 'Save failed. Check the rule fields and try again.'); this.savingRule.set(false); },
    });
  }

  deleteRule(rule: ScanCheckRule) {
    if (!confirm(`Delete scan rule "${rule.targetField}"?`)) return;
    this.svc.deleteScanRule(rule.checkRuleId).subscribe({ next: () => this.reload() });
  }

  private blankScanRule(scanTypeId = 2): UpsertScanRuleRequest & { isActive: boolean } {
    const checkType = scanTypeId === 1 ? 'ErrorKeyword'
                    : scanTypeId === 4 ? 'FileContent'
                    : 'ValueEquals';
    return { checkType, sourceTable: null, targetField: '', minValue: null,
             maxValue: null, expectedValue: null, watermarkColumn: null, sourceIdColumn: null,
             filePathColumn: null, inputPathPattern: null,
             extractorType: scanTypeId === 4 ? 'Xml' : null,
             extractorLocator: null, identifierLocator: null,
             extractorPredicateType: null, extractorPredicateValue: null,
             severity: 'Medium', description: null, isActive: true };
  }

  scanIcon(scanTypeId: number): string {
    return scanTypeId === 1 ? '📄' : scanTypeId === 2 ? '🗄️' : scanTypeId === 3 ? '🌐' : '📦';
  }

  sourceConfig(s: { logFolder: string | null; connectionName: string | null; logSourceUrl: string | null; includeSubfolders: boolean }): string {
    if (s.logFolder)      return '📁 ' + s.logFolder + (s.includeSubfolders ? ' (recursive)' : '');
    if (s.connectionName) return '🔌 ' + s.connectionName;
    if (s.logSourceUrl)   return '🌐 ' + s.logSourceUrl;
    return '';
  }

  ruleDetail(r: { checkType: string; expectedValue: string | null; minValue: number | null; maxValue: number | null; extractorLocator: string | null; extractorPredicateType: string | null; extractorPredicateValue: string | null }): string {
    switch (r.checkType) {
      case 'ValueEquals':  return '= ' + (r.expectedValue ?? '');
      case 'ColumnRange':  return `${r.minValue ?? '−∞'} – ${r.maxValue ?? '+∞'}`;
      case 'FileContent':  return r.extractorPredicateType
        ? `${r.extractorLocator} ${r.extractorPredicateType} ${r.extractorPredicateValue}`
        : (r.extractorLocator ?? 'filename match');
      default: return '—';
    }
  }

  getJobTypeId(job: MonitoredJob): number {
    return this.jobTypes().find(t => t.name === job.jobTypeName)?.jobTypeId ?? 0;
  }

  // ── Classification Rule CRUD ──────────────────────────────────────────────
  openClassDrawer(rule: RuleOverride | null) {
    this.editingClassRule.set(rule);
    if (rule) {
      const et = this.errorTypes().find(e => e.code === rule.errorTypeCode);
      this.classRuleForm = { errorTypeId: et?.errorTypeId ?? 0, pattern: rule.pattern,
                             confidence: rule.confidence, priority: rule.priority, isActive: true };
    } else {
      this.classRuleForm = this.blankClassRule();
    }
    this.classDrawerOpen.set(true);
  }

  saveClassRule() {
    if (!this.classRuleForm.pattern?.trim() || !this.classRuleForm.errorTypeId) return;
    this.savingClass.set(true);
    const job = this.job()!;
    const ruleId = this.editingClassRule()?.ruleId;
    const req$: Observable<unknown> = ruleId
      ? this.svc.updateClassificationRule(ruleId, {
          jobTypeId: this.getJobTypeId(job), errorTypeId: this.classRuleForm.errorTypeId,
          pattern: this.classRuleForm.pattern, confidence: this.classRuleForm.confidence,
          priority: this.classRuleForm.priority, isActive: this.classRuleForm.isActive,
        } as UpsertClassificationRuleRequest)
      : this.svc.createJobClassificationRule(job.monitoredJobId, this.classRuleForm);
    req$.subscribe({
      next: () => { this.classDrawerOpen.set(false); this.savingClass.set(false); this.reload(); },
      error: () => this.savingClass.set(false),
    });
  }

  deleteClassRule(rule: RuleOverride) {
    if (!confirm(`Remove pattern "${rule.pattern}" from this job?`)) return;
    this.svc.deleteJobClassificationRule(this.job()!.monitoredJobId, rule.ruleId)
      .subscribe({ next: () => this.reload() });
  }

  openLinkDrawer() {
    this.linkRuleSearch.set('');
    this.loadingLinkRules.set(true);
    this.linkDrawerOpen.set(true);
    this.svc.getAllClassificationRules().subscribe({
      next: r => { this.allClassRules.set(r); this.loadingLinkRules.set(false); },
      error: () => this.loadingLinkRules.set(false),
    });
  }

  confirmLinkRule(rule: ClassificationRule) {
    this.savingClass.set(true);
    this.svc.linkJobClassificationRule(this.job()!.monitoredJobId, rule.ruleId).subscribe({
      next: () => { this.linkDrawerOpen.set(false); this.savingClass.set(false); this.reload(); },
      error: () => this.savingClass.set(false),
    });
  }

  private blankClassRule(): UpsertJobClassificationRuleRequest {
    return { errorTypeId: 0, pattern: '', confidence: 0.9, priority: 1, isActive: true };
  }

  // ── Fix Option CRUD ───────────────────────────────────────────────────────
  openFixRuleDrawer(rule: FixPolicyRule | null) {
    const job = this.job()!;
    this.editingFixRule.set(rule);
    this.shortcutRuleId = null;
    this.fixRuleSaveConflict.set(null);
    this.fixRuleSaveError.set(null);
    if (rule) {
      // Normalize legacy mismatches: only Manual↔Manual is a valid pair.
      // If an existing rule has fixCategory=Manual but actionType≠Manual (or vice versa),
      // enforce the coupling silently so the operator doesn't see an illegal state.
      let fixCategory = rule.fixCategory;
      let actionType  = rule.actionType;
      if (fixCategory === 'Manual' && actionType !== 'Manual') actionType  = 'Manual';
      if (actionType  === 'Manual' && fixCategory !== 'Manual') fixCategory = 'Manual';
      this.fixRuleForm = {
        jobTypeId: rule.jobTypeId, errorTypeId: rule.errorTypeId, monitoredJobId: rule.monitoredJobId,
        actionToApply: rule.actionToApply, fixCategory, actionType,
        actionPayload: rule.actionPayload, isAutoHealEligible: rule.isAutoHealEligible, enabled: rule.enabled,
        steps: (rule.steps ?? []).map(s => ({ stepOrder: s.stepOrder, actionType: s.actionType,
                                              actionPayload: s.actionPayload, description: s.description })),
      };
    } else {
      // New rule defaults to THIS job (per-job override) — the common case.
      this.fixRuleForm = { ...this.blankFixRule(), jobTypeId: this.getJobTypeId(job), monitoredJobId: job.monitoredJobId };
    }
    this.fixRuleFormSignal.set({ ...this.fixRuleForm });
    this.fixDrawerOpen.set(true);
  }

  saveFixRule() {
    if (!this.fixRuleForm.actionType || !this.fixRuleForm.actionToApply || !this.fixRuleForm.errorTypeId) return;
    this.savingFix.set(true);
    this.fixRuleSaveConflict.set(null);
    this.fixRuleSaveError.set(null);
    const id = this.editingFixRule()?.ruleId;
    const req$: Observable<unknown> = id
      ? this.svc.updateFixPolicyRule(id, this.fixRuleForm)
      : this.svc.createFixPolicyRule(this.fixRuleForm);
    req$.subscribe({
      next: () => { this.fixDrawerOpen.set(false); this.savingFix.set(false); this.reload(); },
      error: (err: { status?: number; error?: { error?: string; message?: string; conflictingPolicyId?: number } | string; message?: string }) => {
        const body = err?.error;
        if (err?.status === 409 && typeof body === 'object' && body?.error === 'DuplicateFixPolicy' && body?.conflictingPolicyId) {
          this.fixRuleSaveConflict.set({ message: body.message ?? 'A duplicate active policy exists.', conflictingPolicyId: body.conflictingPolicyId });
        } else if (err?.status === 400 && typeof body === 'object' && body?.message) {
          this.fixRuleSaveError.set(body.message);
        } else if (err?.status === 400 && typeof body === 'string') {
          this.fixRuleSaveError.set(body);
        } else {
          this.fixRuleSaveError.set(err?.message || 'Save failed. Check the server logs and try again.');
        }
        this.savingFix.set(false);
      },
    });
  }

  deleteFixRule(rule: FixPolicyRule) {
    if (!confirm(`Delete fix option for "${rule.errorTypeCode}"?`)) return;
    this.svc.deleteFixPolicyRule(rule.ruleId).subscribe({ next: () => this.reload() });
  }

  /** A JobType default is shadowed when an enabled per-job override for the
   *  same ErrorType exists in the effective list — the override is what runs. */
  isShadowedDefault(r: FixPolicyRule): boolean {
    if (r.monitoredJobId !== null) return false;
    return this.fixPolicies().some(p => p.monitoredJobId !== null && p.enabled && p.errorTypeCode === r.errorTypeCode);
  }

  // ── Coverage gap indicators ───────────────────────────────────────────────
  // Quiet config-time hints — a gap may be intentional (operator plans the
  // downstream piece later). Inform, don't block or error-style.

  /** ⚠ on scan rule rows: fires when there are ZERO effective class rules for this
   *  job. That's the reliable proxy — pattern matching against future log lines
   *  isn't computable at config time. Never shown for SqlQuery (arbitrary result
   *  shape whose output can't be predicted). */
  onPredicateTypeChange(next: string | null): void {
    this.ruleForm.extractorPredicateType = next;
    if (!next) this.ruleForm.extractorPredicateValue = null;
  }

  scanRuleNeedsClassification(rule: ScanCheckRule): boolean {
    if (rule.checkType === 'SqlQuery') return false;
    const effective = this.effectiveClassRules();
    if (effective.length === 0) return true;
    // Keyword-overlap heuristic: strip `*` from the scan rule's targetField and
    // from each class rule's pattern, then check substring containment in either
    // direction. If no class rule's literal overlaps with the rule's keyword,
    // there's a likely classification gap.
    const keyword = (rule.targetField ?? '').replace(/\*/g, '').trim().toLowerCase();
    if (!keyword) return false;
    return !effective.some(cr => {
      const literal = cr.pattern.replace(/\*/g, '').trim().toLowerCase();
      return literal && (literal.includes(keyword) || keyword.includes(literal));
    });
  }

  /** Count of scan rules in a source that would show the ⚠ classification gap.
   *  Used for the rollup chip on collapsed source headers. */
  scanRulesNeedingClassCount(source: ScanSource): number {
    return source.scanCheckRules.filter(r => this.scanRuleNeedsClassification(r)).length;
  }

  /** ⚠ on classification rule rows: no enabled fix policy covers this errorTypeCode. */
  classRuleNeedsFix(rule: RuleOverride): boolean {
    return !this.fixPolicies().some(p => p.errorTypeCode === rule.errorTypeCode && p.enabled);
  }

  /** ⚠ on fix policy rows: no effective class rule produces this errorTypeCode,
   *  so the fix won't trigger automatically (mirrors the reachability warning
   *  already built into the fix drawer). */
  fixHasNoClassCoverage(policy: FixPolicyRule): boolean {
    return !this.effectiveClassRules().some(r => r.errorTypeCode === policy.errorTypeCode);
  }

  // ── Gap-marker click-throughs ─────────────────────────────────────────────

  /** Scan-rule ⚠ click: open the classification drawer pre-filled with a
   *  pattern derived from what the rule's ErrorMessage will look like.
   *  - ErrorKeyword → pattern is the keyword (strip *)
   *  - ValueEquals / ColumnRange → pattern like "TargetField=ExpectedValue"
   *  - FileContent → pattern is the filename keyword (strip *)
   *  Leaves ErrorType blank so the operator picks one explicitly. */
  openClassDrawerForScanRule(rule: ScanCheckRule): void {
    const pattern = this.classPatternForScanRule(rule);
    this.editingClassRule.set(null);
    this.classRuleForm = { ...this.blankClassRule(), pattern };
    this.classDrawerOpen.set(true);
  }

  private classPatternForScanRule(rule: ScanCheckRule): string {
    const target = (rule.targetField ?? '').replace(/\*/g, '').trim();
    switch (rule.checkType) {
      case 'ErrorKeyword':  return target;                                    // the keyword itself
      case 'FileContent':   return target;                                    // filename keyword
      case 'ValueEquals':   return target && rule.expectedValue
                              ? `${target}=${rule.expectedValue}` : target;   // e.g. "FileStatusCode=5"
      case 'ColumnRange':   return target;                                    // just the column name
      default:              return target;
    }
  }

  /** Classification-rule ⚠ click: open the fix drawer pre-filled with the
   *  error type that has no fix option, scoped to this job (override). */
  openFixRuleDrawerForClassRule(rule: RuleOverride): void {
    const job = this.job()!;
    const et  = this.errorTypes().find(e => e.code === rule.errorTypeCode);
    this.openFixRuleDrawer(null);                 // resets form to per-job blank
    if (et) {
      this.fixRuleForm.errorTypeId = et.errorTypeId;
      this.syncFixRuleSignal();
    }
  }

  /** Fix-options ⚠ click: open the classification drawer pre-filled with the
   *  error type that has no class rule coverage — operator adds the upstream
   *  classification rule to complete the pipeline. */
  openClassDrawerForFixGap(policy: FixPolicyRule): void {
    const et = this.errorTypes().find(e => e.code === policy.errorTypeCode);
    this.editingClassRule.set(null);
    this.classRuleForm = {
      ...this.blankClassRule(),
      errorTypeId: et?.errorTypeId ?? 0,
      pattern: '',
    };
    this.classDrawerOpen.set(true);
  }

  // ── Per-source collapse (only for jobs with 2+ sources) ──────────────────
  // Default: all sources expanded (show the full picture on load).
  // Single-source jobs: no collapse affordance at all.

  private _collapsedSources = signal<Set<number>>(new Set<number>());

  isSourceCollapsed(sourceId: number): boolean {
    return this._collapsedSources().has(sourceId);
  }

  toggleSource(sourceId: number): void {
    const next = new Set(this._collapsedSources());
    next.has(sourceId) ? next.delete(sourceId) : next.add(sourceId);
    this._collapsedSources.set(next);
  }

  // Hard coupling: Manual ↔ Manual enforced both directions.
  // No-default: category derives from execution type on first pick; unlocking
  // Manual resets actionType to '' (operator must re-choose execution type
  // explicitly — safer than auto-picking when any default could be wrong).
  setFixRuleCategory(next: string) {
    const prevActionType = this.fixRuleForm.actionType;
    this.fixRuleForm.fixCategory = next;
    if (next === 'Manual') {
      this.fixRuleForm.actionType    = 'Manual';
      this.fixRuleForm.actionPayload = null;
      this.fixRuleForm.steps         = [];
    } else if (prevActionType === 'Manual') {
      // Unlocking — clear actionType so the operator explicitly re-chooses.
      this.fixRuleForm.actionType = '';
    }
    this.fixRuleSaveError.set(null);
    this.syncFixRuleSignal();
  }

  setFixRuleActionType(next: string) {
    const prev = this.fixRuleForm.actionType;
    this.fixRuleForm.actionType = next;
    if (next === 'Manual') {
      this.fixRuleForm.fixCategory   = 'Manual';
      this.fixRuleForm.actionPayload = null;
      this.fixRuleForm.steps         = [];
    } else {
      if (next === 'Composite') {
        this.fixRuleForm.actionPayload = null;
      } else if (prev === 'Composite') {
        this.fixRuleForm.steps = [];
      }
      // Derive category when it is not yet set or was locked to Manual.
      if (!this.fixRuleForm.fixCategory || this.fixRuleForm.fixCategory === 'Manual') {
        this.fixRuleForm.fixCategory = this.defaultCategoryFor(next);
      }
    }
    this.fixRuleSaveError.set(null);
    this.syncFixRuleSignal();
  }

  // Reverse of the order-map above: execution type → most natural fix category.
  private defaultCategoryFor(actionType: string): string {
    const map: Record<string, string> = {
      'SqlScript': 'DbFix', 'StoredProcedure': 'DbFix',
      'CopyFile':  'FileRepair',
      'Manual':    'Manual',
      'ApiCall': 'Retry', 'Script': 'Retry', 'Composite': 'Retry',
    };
    return map[actionType] ?? 'Retry';
  }

  /** @deprecated — kept for the orderedActionTypes category-hint; category
   *  no longer drives actionType default for new rules. */
  private defaultActionTypeFor(category: string): string {
    const defaults: Record<string, string> = {
      'DbFix': 'SqlScript', 'FileRepair': 'CopyFile', 'Retry': 'ApiCall',
    };
    return defaults[category] ?? 'ApiCall';
  }

  syncFixRuleSignal() { this.fixRuleFormSignal.set({ ...this.fixRuleForm }); }

  pickClassificationRuleById(ruleId: number | null) {
    if (ruleId == null) return;
    const cr = this.effectiveClassRules().find(r => r.ruleId === ruleId);
    const et = cr ? this.errorTypes().find(e => e.code === cr.errorTypeCode) : null;
    if (et) { this.shortcutRuleId = ruleId; this.fixRuleForm.errorTypeId = et.errorTypeId; this.syncFixRuleSignal(); }
  }

  setFixRuleScope(monitoredJobId: number | null) {
    this.fixRuleForm.monitoredJobId = monitoredJobId;
    this.fixRuleSaveConflict.set(null);
    this.syncFixRuleSignal();
  }

  openConflictingPolicy(conflict: FixPolicyRule) { this.openFixRuleDrawer(conflict); }
  openConflictingPolicyById(id: number) {
    const rule = this.fixPolicies().find(p => p.ruleId === id);
    if (rule) this.openFixRuleDrawer(rule);
  }

  addStep() {
    const steps = this.fixRuleForm.steps ?? [];
    steps.push({ stepOrder: steps.length + 1, actionType: 'SqlScript', actionPayload: '', description: null });
    this.fixRuleForm.steps = steps;
    this.syncFixRuleSignal();
  }
  removeStep(index: number) {
    const steps = this.fixRuleForm.steps ?? [];
    steps.splice(index, 1);
    steps.forEach((s, i) => s.stepOrder = i + 1);
    this.fixRuleForm.steps = steps;
    this.syncFixRuleSignal();
  }
  moveStep(index: number, delta: number) {
    const steps = this.fixRuleForm.steps ?? [];
    const target = index + delta;
    if (target < 0 || target >= steps.length) return;
    [steps[index], steps[target]] = [steps[target], steps[index]];
    steps.forEach((s, i) => s.stepOrder = i + 1);
    this.fixRuleForm.steps = steps;
    this.syncFixRuleSignal();
  }
  payloadPlaceholderFor(actionType: string): string {
    switch (actionType) {
      case 'SqlScript':       return 'UPDATE dbo.Files SET FileStatusCode = 0 WHERE Id = {sourceId}';
      case 'Script':          return 'powershell.exe C:\\scripts\\fix.ps1 {failureId}';
      case 'ApiCall':         return 'http://jobs.internal/api/jobs/{failureId}/retry';
      case 'StoredProcedure': return 'dbo.sp_RetryJob  or  ConnName|dbo.sp_RetryJob';
      case 'CopyFile':        return '{sourceFilePath}|{inputFolder}\\reprocess\\{sourceFileName}';
      default:                return 'payload';
    }
  }

  private blankFixRule(): UpsertFixPolicyRuleRequest {
    return { jobTypeId: 0, errorTypeId: 0, monitoredJobId: null, actionToApply: '', fixCategory: '',
             actionType: '', actionPayload: null, isAutoHealEligible: false, enabled: true, steps: [] };
  }

  // ── SqlScript "scope to failing row" shortcut ─────────────────────────────
  // The server-side write-guard rejects a SqlScript fix unless it references
  // {sourceId} in its WHERE. This shortcut appends a starter scope clause so the
  // operator doesn't have to hand-type it. Shown only when missing (and not an
  // EXEC — those scope via a parameter). Operator edits the key column name.
  sqlFixNeedsScopeShortcut(payload: string | null | undefined): boolean {
    const q = (payload ?? '').trim();
    if (!q) return false;
    if (/^\s*EXEC\b/i.test(q)) return false;   // EXEC: add {sourceId} as a parameter by hand
    return !/\{sourceId\}/i.test(q);            // already scoped → hide
  }

  /** WHERE when the payload has none yet, else AND — so the snippet appends cleanly. */
  scopeClauseFor(payload: string | null | undefined): string {
    return /\bWHERE\b/i.test(payload ?? '') ? 'AND' : 'WHERE';
  }

  /** Placeholder key column for the scope clause. Deliberately NOT derived from
   *  the scan rule's SourceIdColumn — a fix may update a DIFFERENT table where
   *  {sourceId} is an FK under another name, so any guess would be misleading.
   *  An obvious bracketed placeholder forces the operator to fill in the real
   *  target column; the guard only requires {sourceId} in the WHERE, not a
   *  specific column, so this still saves and fails safe (ManualRequired) if
   *  left unedited. */
  readonly fixScopeColumn = '[KeyColumn]';

  private appendSourceIdScope(payload: string | null | undefined): string {
    const base = (payload ?? '').replace(/;\s*$/, '').trimEnd();   // drop a trailing ';'
    return `${base} ${this.scopeClauseFor(base)} ${this.fixScopeColumn} = '{sourceId}'`;
  }

  scopeFixPayloadToSourceId() {
    this.fixRuleForm.actionPayload = this.appendSourceIdScope(this.fixRuleForm.actionPayload);
    this.syncFixRuleSignal();
  }

  scopeStepToSourceId(step: { actionPayload: string }) {
    step.actionPayload = this.appendSourceIdScope(step.actionPayload);
    this.syncFixRuleSignal();
  }
}
