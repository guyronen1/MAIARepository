import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import {
  ConfigService, JobType, ErrorType, FixPolicyRule, ClassificationRule,
  UpsertJobRequest, UpsertScanRuleRequest,
  UpsertJobClassificationRuleRequest, UpsertFixPolicyRuleRequest,
  UpsertClassificationRuleRequest,
} from '../../../core/services/config.service';
import { MonitoredJob, ScanCheckRule, RuleOverride } from '../../../core/models';
import { PluralizePipe } from '../../../core/pipes/pluralize.pipe';

type ActiveTab    = 'scan' | 'class' | 'fix';
type ActiveDrawer = 'job' | 'scan-rule' | 'class-rule' | 'link-class-rule' | 'fix-rule' | null;

const SCAN_TYPES     = [{ id: 1, name: 'FileSystem' }, { id: 2, name: 'Database' }, { id: 3, name: 'ApiEndpoint' }];
const DB_CHECK_TYPES = ['ColumnRange', 'ValueEquals'];
const SEVERITIES     = ['Low', 'Medium', 'High', 'Critical'];
const FIX_CATEGORIES = ['Retry', 'FileRepair', 'DbFix', 'Manual'];
const ACTION_TYPES   = ['Manual', 'ApiCall', 'StoredProcedure', 'Script', 'SqlScript', 'CopyFile', 'Composite'];

@Component({
  selector: 'app-monitored-jobs',
  standalone: true,
  imports: [FormsModule, PluralizePipe],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Monitored Jobs</h1>
          <p class="text-muted text-sm">{{ jobs().length | pluralize:'job' }} configured</p>
        </div>
        <button class="btn btn-primary" (click)="openJobDrawer(null)">+ Add Job</button>
      </div>

      @if (loading()) {
        <div class="loading-overlay"><span class="spinner"></span> Loading…</div>
      } @else if (jobs().length === 0) {
        <div class="card"><div class="empty-state"><span class="empty-icon">📋</span><p>No jobs configured yet</p></div></div>
      } @else {
        <div class="jobs-list">
          @for (j of jobs(); track j.monitoredJobId) {
            <div class="job-card">
              <!-- Job card header -->
              <div class="job-card-header">
                <div class="job-lead">
                  <span class="scan-icon">{{ scanIcon(j.scanTypeId) }}</span>
                  <div>
                    <div class="job-name">{{ j.displayName ?? j.name }}</div>
                    <div class="text-muted text-sm">{{ j.name }} · {{ j.jobTypeName }} · {{ j.scanTypeName }}</div>
                  </div>
                </div>
                <div class="job-meta">
                  @if (j.logFolder) { <span class="meta-chip">📁 {{ j.logFolder }}</span> }
                  @if (j.connectionName) { <span class="meta-chip">🔌 {{ j.connectionName }}</span> }
                  <span class="meta-chip">⏱ {{ j.pollingIntervalSeconds }}s</span>
                </div>
                <div class="job-actions">
                  <span class="badge" [class]="j.isActive ? 'badge-resolved' : 'badge-failed'">
                    {{ j.isActive ? 'Active' : 'Inactive' }}
                  </span>
                  <button class="btn btn-ghost btn-sm" (click)="toggle(j)">
                    {{ expanded() === j.monitoredJobId ? '▲ Collapse' : '▼ Configure' }}
                  </button>
                  <button class="btn btn-ghost btn-sm" (click)="openJobDrawer(j)">Edit</button>
                  <button class="btn btn-danger btn-sm" (click)="deleteJob(j)">Delete</button>
                </div>
              </div>

              <!-- Expanded config panel with tabs -->
              @if (expanded() === j.monitoredJobId) {
                <div class="config-panel">

                  <!-- Tab bar -->
                  <div class="tab-bar">
                    <button class="tab-btn" [class.active]="activeTab() === 'scan'"
                            (click)="setTab('scan')">
                      Scan Rules
                      <span class="tab-badge">{{ j.scanCheckRules.length }}</span>
                    </button>
                    <button class="tab-btn" [class.active]="activeTab() === 'class'"
                            (click)="setTab('class')">
                      Classification Rules
                      <span class="tab-badge">{{ j.rules.length }}</span>
                    </button>
                    <button class="tab-btn" [class.active]="activeTab() === 'fix'"
                            (click)="setTab('fix', j)">
                      Fix Options
                      @if (activeTab() === 'fix') {
                        <span class="tab-badge">{{ fixPolicies().length }}</span>
                      }
                    </button>
                  </div>

                  <!-- ── TAB: Scan Rules ────────────────────────────────── -->
                  @if (activeTab() === 'scan') {
                    <div class="tab-content">
                      @if (j.scanTypeId === 3) {
                        <div class="scan-info-notice">
                          <span>🌐</span>
                          <div>
                            <strong>API endpoint jobs detect failures automatically.</strong>
                            <p>Any non-2xx response, or a body containing "error", "exception", or "failed" triggers a failure. No scan rules are needed.</p>
                          </div>
                        </div>
                      } @else {
                        <div class="section-header">
                          <span class="section-desc">
                            @if (j.scanTypeId === 1) {
                              Keywords matched against log file lines — a matching file triggers a JobFailure
                            } @else {
                              Database conditions that trigger a JobFailure (column out of range, value mismatch)
                            }
                          </span>
                          <button class="btn btn-primary btn-sm" (click)="openScanRuleDrawer(j, null)">
                            + {{ j.scanTypeId === 1 ? 'Add Keyword' : 'Add Scan Rule' }}
                          </button>
                        </div>
                        @if (j.scanCheckRules.length === 0) {
                          <div class="empty-tab">
                            <span>🔍</span>
                            <p>
                              @if (j.scanTypeId === 1) {
                                No keyword rules — without these, all log lines are passed through the full classification pipeline.
                              } @else {
                                No scan rules yet — add one to start detecting database condition failures.
                              }
                            </p>
                          </div>
                        } @else if (j.scanTypeId === 1) {
                          <table class="data-table">
                            <thead>
                              <tr><th>Keyword / Pattern</th><th>Severity</th><th></th></tr>
                            </thead>
                            <tbody>
                              @for (r of j.scanCheckRules; track r.checkRuleId) {
                                <tr>
                                  <td class="font-mono">{{ r.targetField }}</td>
                                  <td>
                                    <span class="badge" [class]="'badge-' + r.severity.toLowerCase()">
                                      {{ r.severity }}
                                    </span>
                                  </td>
                                  <td>
                                    <div style="display:flex;gap:4px">
                                      <button class="btn btn-ghost btn-sm" (click)="openScanRuleDrawer(j, r)">Edit</button>
                                      <button class="btn btn-danger btn-sm" (click)="deleteScanRule(j, r)">✕</button>
                                    </div>
                                  </td>
                                </tr>
                              }
                            </tbody>
                          </table>
                        } @else {
                          <table class="data-table">
                            <thead>
                              <tr>
                                <th>Type</th><th>Table</th><th>Field</th>
                                <th>Condition</th><th>Watermark</th><th>Src ID</th>
                                <th>Severity</th><th></th>
                              </tr>
                            </thead>
                            <tbody>
                              @for (r of j.scanCheckRules; track r.checkRuleId) {
                                <tr>
                                  <td><span class="badge badge-info">{{ r.checkType }}</span></td>
                                  <td class="font-mono">{{ r.sourceTable ?? '—' }}</td>
                                  <td class="font-mono">{{ r.targetField }}</td>
                                  <td class="text-sm">
                                    @if (r.checkType === 'ValueEquals')  { = {{ r.expectedValue }} }
                                    @else if (r.checkType === 'ColumnRange') { {{ r.minValue ?? '−∞' }} – {{ r.maxValue ?? '+∞' }} }
                                    @else { — }
                                  </td>
                                  <td class="font-mono text-sm">{{ r.watermarkColumn ?? '—' }}</td>
                                  <td class="font-mono text-sm">{{ r.sourceIdColumn ?? '—' }}</td>
                                  <td>
                                    <span class="badge" [class]="'badge-' + r.severity.toLowerCase()">
                                      {{ r.severity }}
                                    </span>
                                  </td>
                                  <td>
                                    <div style="display:flex;gap:4px">
                                      <button class="btn btn-ghost btn-sm" (click)="openScanRuleDrawer(j, r)">Edit</button>
                                      <button class="btn btn-danger btn-sm" (click)="deleteScanRule(j, r)">✕</button>
                                    </div>
                                  </td>
                                </tr>
                              }
                            </tbody>
                          </table>
                        }
                      }
                    </div>
                  }

                  <!-- ── TAB: Classification Rules ─────────────────────── -->
                  @if (activeTab() === 'class') {
                    <div class="tab-content">
                      <div class="section-header">
                        <span class="section-desc">
                          Substring patterns (with optional <code>*</code> wildcards) that map detected errors to known error types for this job
                        </span>
                        <div style="display:flex;gap:6px">
                          <button class="btn btn-ghost btn-sm" (click)="openLinkClassRuleDrawer(j)">
                            🔗 Link Existing
                          </button>
                          <button class="btn btn-primary btn-sm" (click)="openClassRuleDrawer(j, null)">
                            + New Rule
                          </button>
                        </div>
                      </div>
                      @if (j.rules.length === 0 && jobTypeGlobalRules(j).length === 0) {
                        <div class="empty-tab">
                          <span>🏷️</span>
                          <p>No classification rules apply to this job yet — add one to enable error type detection.</p>
                        </div>
                      }

                      <!-- Job-linked rules (specific to this job). -->
                      @if (j.rules.length > 0) {
                        <div class="subsection-label">Linked to this job</div>
                        <table class="data-table">
                          <thead>
                            <tr>
                              <th>Pattern</th><th>Error Type</th>
                              <th>Confidence</th><th>Priority</th><th></th>
                            </tr>
                          </thead>
                          <tbody>
                            @for (r of j.rules; track r.ruleId) {
                              <tr>
                                <td class="font-mono">{{ r.pattern }}</td>
                                <td><span class="badge badge-classified">{{ r.errorTypeCode }}</span></td>
                                <td>
                                  <div class="confidence-bar">
                                    <div class="bar-track">
                                      <div class="bar-fill" [style.width.%]="r.confidence * 100"></div>
                                    </div>
                                    <span class="bar-value">{{ (r.confidence * 100).toFixed(0) }}%</span>
                                  </div>
                                </td>
                                <td class="text-muted text-sm">#{{ r.priority }}</td>
                                <td>
                                  <div style="display:flex;gap:4px">
                                    <button class="btn btn-ghost btn-sm" (click)="openClassRuleDrawer(j, r)">Edit</button>
                                    <button class="btn btn-danger btn-sm" (click)="deleteClassRule(j, r)">✕</button>
                                  </div>
                                </td>
                              </tr>
                            }
                          </tbody>
                        </table>
                      }

                      <!-- JobType-global rules that ALSO classify this job (union
                           semantics). Read-only here — managed on the
                           Classification Rules screen — but surfaced so the
                           operator sees the full effective set, not just links. -->
                      @if (jobTypeGlobalRules(j).length > 0) {
                        <div class="subsection-label">
                          Also applies — <strong>{{ j.jobTypeName }}</strong> defaults
                          <span class="text-muted text-sm">· JobType-level rules that classify every {{ j.jobTypeName }} job</span>
                        </div>
                        <table class="data-table">
                          <thead>
                            <tr><th>Pattern</th><th>Error Type</th><th>Confidence</th><th>Priority</th><th>Scope</th></tr>
                          </thead>
                          <tbody>
                            @for (r of jobTypeGlobalRules(j); track r.ruleId) {
                              <tr class="global-row">
                                <td class="font-mono">{{ r.pattern }}</td>
                                <td><span class="badge badge-classified">{{ r.errorTypeCode }}</span></td>
                                <td>
                                  <div class="confidence-bar">
                                    <div class="bar-track">
                                      <div class="bar-fill" [style.width.%]="r.confidence * 100"></div>
                                    </div>
                                    <span class="bar-value">{{ (r.confidence * 100).toFixed(0) }}%</span>
                                  </div>
                                </td>
                                <td class="text-muted text-sm">#{{ r.priority }}</td>
                                <td><span class="badge badge-muted" title="JobType-level rule — edit on the Classification Rules screen">Global</span></td>
                              </tr>
                            }
                          </tbody>
                        </table>
                        <span class="field-hint">Job-linked rules win over these on a match. Edit globals on the Classification Rules screen.</span>
                      }
                    </div>
                  }

                  <!-- ── TAB: Fix Options ──────────────────────────────── -->
                  @if (activeTab() === 'fix') {
                    <div class="tab-content">
                      <div class="section-header">
                        <span class="section-desc">
                          Automated fix actions for each error type on <strong>{{ j.jobTypeName }}</strong> jobs
                        </span>
                        <button class="btn btn-primary btn-sm" (click)="openFixRuleDrawer(j, null)">
                          + Add Fix
                        </button>
                      </div>
                      @if (loadingFix()) {
                        <div class="loading-overlay" style="padding:20px 0">
                          <span class="spinner"></span> Loading fix options…
                        </div>
                      } @else if (fixPolicies().length === 0) {
                        <div class="empty-tab">
                          <span>⚙️</span>
                          <p>No fix options yet — add one to enable automated or guided resolution.</p>
                        </div>
                      } @else {
                        <table class="data-table">
                          <thead>
                            <tr>
                              <th>Error Type</th><th>Action Description</th>
                              <th>Category</th><th>Execution</th><th>Auto-Heal</th><th></th>
                            </tr>
                          </thead>
                          <tbody>
                            @for (r of fixPolicies(); track r.ruleId) {
                              <tr>
                                <td>
                                  <span class="badge badge-classified">{{ r.errorTypeCode }}</span>
                                  <!-- Scope badge — distinguishes a JobType-level default from
                                       a per-MonitoredJob override at a glance. Override wins
                                       at evaluation time. -->
                                  @if (r.monitoredJobId !== null) {
                                    <span class="badge badge-info scope-override-badge"
                                          title="Overrides the JobType default for this job — this is the one that runs.">
                                      ⤷ Override · active
                                    </span>
                                  } @else if (isShadowedDefault(r)) {
                                    <span class="badge badge-muted scope-default-badge"
                                          title="A per-job override exists for this Error Type — that override runs instead of this default.">
                                      Default · shadowed
                                    </span>
                                  } @else {
                                    <span class="badge badge-muted scope-default-badge"
                                          title="Applies to every job of this JobType unless an override exists.">
                                      Default
                                    </span>
                                  }
                                </td>
                                <td class="text-sm">{{ r.actionToApply }}</td>
                                <td><span class="badge badge-info">{{ r.fixCategory }}</span></td>
                                <td class="text-sm text-muted">{{ r.actionType }}</td>
                                <td>
                                  @if (r.isAutoHealEligible) {
                                    <span class="badge badge-resolved">⚡ Auto</span>
                                  } @else {
                                    <span class="badge badge-manual">Manual</span>
                                  }
                                </td>
                                <td>
                                  <div style="display:flex;gap:4px">
                                    <button class="btn btn-ghost btn-sm" (click)="openFixRuleDrawer(j, r)">Edit</button>
                                    <button class="btn btn-danger btn-sm" (click)="deleteFixRule(j, r)">✕</button>
                                  </div>
                                </td>
                              </tr>
                            }
                          </tbody>
                        </table>
                      }
                    </div>
                  }

                </div>
              }
            </div>
          }
        </div>
      }
    </div>

    <!-- ── Drawer: Add / Edit Job ──────────────────────────────────────────── -->
    @if (activeDrawer() === 'job') {
      <div class="drawer-overlay" (click)="closeDrawer()"></div>
      <div class="drawer">
        <div class="drawer-header">
          <h3>{{ editingJob()?.monitoredJobId ? 'Edit Job' : 'New Monitored Job' }}</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          <div class="form-grid">
            <div class="form-group">
              <label>Name *</label>
              <input [(ngModel)]="jobForm.name" placeholder="e.g. B2BFilesProcess" />
            </div>
            <div class="form-group">
              <label>Display Name</label>
              <input [(ngModel)]="jobForm.displayName" placeholder="Friendly label" />
            </div>
            <div class="form-group">
              <label>Job Type *</label>
              <select [(ngModel)]="jobForm.jobTypeId">
                <option [ngValue]="0" disabled>Select type…</option>
                @for (t of jobTypes(); track t.jobTypeId) {
                  <option [ngValue]="t.jobTypeId">{{ t.name }}</option>
                }
              </select>
            </div>
            <div class="form-group">
              <label>Scan Type *</label>
              <select [(ngModel)]="jobForm.scanTypeId">
                @for (s of scanTypes; track s.id) {
                  <option [ngValue]="s.id">{{ s.name }}</option>
                }
              </select>
            </div>

            @if (jobForm.scanTypeId === 1) {
              <div class="form-group span2">
                <label>Log Folder</label>
                <input [(ngModel)]="jobForm.logFolder" placeholder="C:\logs\myapp" />
              </div>
              <div class="form-group span2">
                <label>Search Patterns</label>
                <input [(ngModel)]="jobForm.searchPatterns" placeholder="app*.log, error*.log" />
              </div>
              <div class="form-group span2">
                <label>Input Folder</label>
                <input [(ngModel)]="jobForm.inputFolder" placeholder="C:\input\deposits" />
                <span class="field-hint">
                  Optional. Base folder for input file paths captured via a rule's
                  <strong>Input File Extraction</strong> regex when the regex captures
                  a relative filename only. Absolute captures ignore this. Distinct
                  from Log Folder (where the log files live).
                </span>
              </div>
            }
            @if (jobForm.scanTypeId === 2) {
              <div class="form-group span2">
                <label>Connection Name</label>
                <input [(ngModel)]="jobForm.connectionName" placeholder="B2BTest (key from appsettings)" />
              </div>
            }
            @if (jobForm.scanTypeId === 3) {
              <div class="form-group span2">
                <label>API URL</label>
                <input [(ngModel)]="jobForm.logSourceUrl" placeholder="https://api.example.com/health" />
              </div>
            }

            <div class="form-group">
              <label>Poll Interval (seconds)</label>
              <input type="number" [(ngModel)]="jobForm.pollingIntervalSeconds" min="10" />
            </div>
            <div class="form-group" style="justify-content:flex-end;padding-top:18px">
              <label class="toggle-label">
                <label class="toggle">
                  <input type="checkbox" [(ngModel)]="jobForm.isActive" />
                  <span class="slider"></span>
                </label>
                <span class="text-sm">Active</span>
              </label>
            </div>
            <div class="form-group span2">
              <label>Description</label>
              <textarea [(ngModel)]="jobForm.description" rows="2" placeholder="Optional notes"></textarea>
            </div>
          </div>
        </div>
        <div class="drawer-footer">
          <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
          <button class="btn btn-primary" (click)="saveJob()" [disabled]="saving()">
            @if (saving()) { <span class="spinner"></span> }
            {{ editingJob()?.monitoredJobId ? 'Save Changes' : 'Create Job' }}
          </button>
        </div>
      </div>
    }

    <!-- ── Drawer: Add / Edit Scan Rule ───────────────────────────────────── -->
    @if (activeDrawer() === 'scan-rule') {
      <div class="drawer-overlay" (click)="closeDrawer()"></div>
      <div class="drawer">
        <div class="drawer-header">
          <h3>{{ editingRule()?.checkRuleId ? 'Edit Scan Rule' : 'New Scan Rule' }}</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          <div class="form-grid">
            @if (editingRuleJob()?.scanTypeId === 1) {
              <!-- FileSystem: keyword only -->
              <div class="form-group span2">
                <label>Keyword / Pattern *</label>
                <input [(ngModel)]="ruleForm.targetField"
                       placeholder="e.g. ERROR|FAILED|Exception" />
                <span class="field-hint">Text searched in each log file line (case-insensitive). Wildcards (*) are ignored — just type the keyword, e.g. File Not Found</span>
              </div>
              <!-- Optional: extract the INPUT file path from the matching log
                   line. Distinct from the keyword (used for matching) — this
                   regex's capture group #1 is the input file path that gets
                   stored in JobFailure.SourceFilePath for {sourceFilePath}
                   placeholders in fix policies. -->
              <div class="form-group span2">
                <label>Input File Extraction</label>
                <input [(ngModel)]="ruleForm.inputPathPattern"
                       placeholder="e.g. Processing file: (.+\.txt)" />
                <span class="field-hint">
                  Optional. Regex; capture group <code>(...)</code> #1 must be the
                  input file path. Differs from classification patterns — full
                  regex applies here, <em>not</em> the <code>*</code>-wildcard
                  shorthand. The captured path becomes the
                  <code>{{'{'}}sourceFilePath{{'}'}}</code> placeholder you can use in
                  a fix policy's payload (e.g. a <strong>CopyFile</strong> or SQL fix).
                  Leave blank if no fix on this job needs the input file.
                </span>
              </div>
            } @else {
              <!-- Database: ColumnRange / ValueEquals -->
              <div class="form-group span2">
                <label>Check Type *</label>
                <select [(ngModel)]="ruleForm.checkType">
                  @for (ct of dbCheckTypes; track ct) { <option [ngValue]="ct">{{ ct }}</option> }
                </select>
              </div>
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
              <!-- Optional: column on the source row that holds the input file
                   path. Read alongside the rule's check and stored in
                   JobFailure.SourceFilePath. v1: no auto-JOIN — if the column
                   lives on a related table, put the JOIN into Source Table
                   directly and use "alias.Column" syntax here. -->
              <div class="form-group span2">
                <label>File Path Column</label>
                <input [(ngModel)]="ruleForm.filePathColumn"
                       placeholder="e.g. FilePath  or  j.FilePath" />
                <span class="field-hint">
                  Optional. Column on the source row holding the input file
                  path. The captured path becomes the
                  <code>{{'{'}}sourceFilePath{{'}'}}</code> placeholder you can use in
                  a fix policy's payload (e.g. a <strong>CopyFile</strong> or SQL fix).
                  Leave blank if no fix on this job needs the input file.
                </span>
              </div>
            }
            <div class="form-group">
              <label>Severity</label>
              <select [(ngModel)]="ruleForm.severity">
                @for (s of severities; track s) { <option [ngValue]="s">{{ s }}</option> }
              </select>
            </div>
            <div class="form-group" style="padding-top:18px">
              <label>Active</label>
              <label class="toggle" style="margin-top:6px">
                <input type="checkbox" [(ngModel)]="ruleForm.isActive" />
                <span class="slider"></span>
              </label>
            </div>
            <div class="form-group span2">
              <label>Description</label>
              <input [(ngModel)]="ruleForm.description" placeholder="Optional notes" />
            </div>
          </div>
        </div>
        <div class="drawer-footer">
          <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
          <button class="btn btn-primary" (click)="saveScanRule()" [disabled]="saving()">
            @if (saving()) { <span class="spinner"></span> }
            {{ editingRule()?.checkRuleId ? 'Save Changes' : 'Add Rule' }}
          </button>
        </div>
      </div>
    }

    <!-- ── Drawer: Add / Edit Classification Rule ─────────────────────────── -->
    @if (activeDrawer() === 'class-rule') {
      <div class="drawer-overlay" (click)="closeDrawer()"></div>
      <div class="drawer">
        <div class="drawer-header">
          <h3>{{ editingClassRule() ? 'Edit Classification Rule' : 'New Classification Rule' }}</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          <div class="drawer-context-banner">
            <span>🏷️</span>
            <span>
              For job <strong>{{ editingClassRuleJob()?.displayName ?? editingClassRuleJob()?.name }}</strong>
              ({{ editingClassRuleJob()?.jobTypeName }})
            </span>
          </div>
          <div class="form-grid">
            <div class="form-group span2">
              <label>Match Pattern *</label>
              <input [(ngModel)]="classRuleForm.pattern"
                     placeholder="e.g. FileNotFoundException  or  Error code * occurred" />
              <span class="field-hint">Case-insensitive substring of the error message. Use <code>*</code> as a wildcard for any text. Other regex characters are matched literally.</span>
            </div>
            <div class="form-group span2">
              <label>Error Type *</label>
              <select [(ngModel)]="classRuleForm.errorTypeId">
                <option [ngValue]="0" disabled>Select error type…</option>
                @for (et of errorTypes(); track et.errorTypeId) {
                  <option [ngValue]="et.errorTypeId">
                    {{ et.code }} — {{ et.displayName }}
                  </option>
                }
              </select>
            </div>
            <div class="form-group">
              <label>Confidence (0 – 1)</label>
              <input type="number" [(ngModel)]="classRuleForm.confidence"
                     min="0" max="1" step="0.05" />
              <span class="field-hint">How certain this pattern identifies the error type.</span>
            </div>
            <div class="form-group">
              <label>Priority</label>
              <input type="number" [(ngModel)]="classRuleForm.priority" min="1" />
              <span class="field-hint">Lower number = evaluated first.</span>
            </div>
            <div class="form-group" style="padding-top:14px">
              <label>Active</label>
              <label class="toggle" style="margin-top:6px">
                <input type="checkbox" [(ngModel)]="classRuleForm.isActive" />
                <span class="slider"></span>
              </label>
            </div>
          </div>
        </div>
        <div class="drawer-footer">
          <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
          <button class="btn btn-primary" (click)="saveClassRule()" [disabled]="saving()">
            @if (saving()) { <span class="spinner"></span> }
            {{ editingClassRule() ? 'Save Changes' : 'Add Rule' }}
          </button>
        </div>
      </div>
    }

    <!-- ── Drawer: Link Existing Classification Rule ─────────────────────── -->
    @if (activeDrawer() === 'link-class-rule') {
      <div class="drawer-overlay" (click)="closeDrawer()"></div>
      <div class="drawer">
        <div class="drawer-header">
          <h3>Link Existing Classification Rule</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          <div class="drawer-context-banner">
            <span>🔗</span>
            <span>
              Linking to <strong>{{ editingClassRuleJob()?.displayName ?? editingClassRuleJob()?.name }}</strong>
            </span>
          </div>
          <div class="form-group" style="margin-bottom:12px">
            <input [ngModel]="linkRuleSearch()"
                   (ngModelChange)="linkRuleSearch.set($event)"
                   placeholder="Search by pattern or error type…"
                   style="width:100%" />
          </div>
          @if (loadingLinkRules()) {
            <div class="loading-overlay" style="padding:20px 0"><span class="spinner"></span> Loading…</div>
          } @else if (filteredLinkableRules().length === 0) {
            <div class="empty-tab">
              <span>🏷️</span>
              <p>No unlinked rules found{{ linkRuleSearch() ? ' matching "' + linkRuleSearch() + '"' : '' }}.</p>
            </div>
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
        </div>
        <div class="drawer-footer">
          <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
        </div>
      </div>
    }

    <!-- ── Drawer: Add / Edit Fix Option ──────────────────────────────────── -->
    @if (activeDrawer() === 'fix-rule') {
      <div class="drawer-overlay" (click)="closeDrawer()"></div>
      <div class="drawer drawer-wide">
        <div class="drawer-header">
          <h3>{{ editingFixRule() ? 'Edit Fix Option' : 'New Fix Option' }}</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          <div class="drawer-context-banner">
            <span class="banner-icon" aria-hidden="true">i</span>
            <span>
              @if (fixRuleForm.monitoredJobId !== null) {
                Fix policy for <strong>{{ editingFixRuleJob()?.displayName ?? editingFixRuleJob()?.name }}</strong>
                — applies to this job only.
              } @else {
                Fix policy for <strong>all {{ editingFixRuleJob()?.jobTypeName }} jobs</strong>
                (JobType-wide default) — a per-job policy overrides it.
              }
            </span>
          </div>
          <div class="form-grid">
            <!-- Scope is per-job by default (the common case). The JobType-wide
                 default layer is still reachable via the inline link below for
                 the rare "all jobs of this type, same fix" case, and existing
                 defaults open here showing the "all jobs" state. Flipping the
                 link re-runs the duplicate check (setFixRuleScope syncs the
                 form-mirror signal). -->
            <div class="span2 scope-line">
              @if (fixRuleForm.monitoredJobId !== null) {
                <span class="scope-current">Scope: <strong>This job</strong></span>
                <button type="button" class="link-btn"
                        (click)="setFixRuleScope(null)">
                  Apply to all {{ editingFixRuleJob()?.jobTypeName }} jobs instead
                </button>
              } @else {
                <span class="scope-current">Scope: <strong>All {{ editingFixRuleJob()?.jobTypeName }} jobs</strong> (default)</span>
                <button type="button" class="link-btn"
                        (click)="setFixRuleScope(editingFixRuleJob()?.monitoredJobId ?? null)">
                  Scope to just this job instead
                </button>
              }
            </div>
            <!-- Shortcut: pick a classification rule (symptom) and the Error Type
                 below is set from it. Fixes are keyed to ErrorType, so this also
                 covers any sibling rule with the same type (shown under the
                 select). Listed rules are the job's effective classifier set. -->
            @if (effectiveClassRules().length > 0) {
              <div class="form-group span2">
                <label>Target a classification rule <span class="text-muted">(shortcut)</span></label>
                <select [ngModel]="shortcutRuleId" (ngModelChange)="pickClassificationRuleById($event)">
                  <option [ngValue]="null" disabled>Pick a symptom to target…</option>
                  @for (cr of effectiveClassRules(); track cr.ruleId) {
                    <option [ngValue]="cr.ruleId">{{ cr.pattern }} → {{ cr.errorTypeCode }}</option>
                  }
                </select>
                <span class="field-hint">Sets the Error Type below from the rule's type.</span>
              </div>
            }
            <div class="form-group span2">
              <label>Error Type *</label>
              <select [(ngModel)]="fixRuleForm.errorTypeId"
                      (ngModelChange)="shortcutRuleId = null; syncFixRuleSignal()">
                <option [ngValue]="0" disabled>Select error type…</option>
                @for (et of errorTypes(); track et.errorTypeId) {
                  <option [ngValue]="et.errorTypeId">
                    {{ et.code }} — {{ et.displayName }}
                  </option>
                }
              </select>
              <!-- Single duplicate-policy warning. Renders either the
                   client-side detected conflict (computed from the in-memory
                   policy list) or the server-side 409 — whichever fires.
                   Client-side takes priority since it's instant; 409 catches
                   the rare race where the operator's policy list is stale. -->
              @if (fixRuleDuplicateConflict(); as conflict) {
                <div class="dup-warn">
                  ⚠ An active fix policy already exists for this Error Type at the
                  <strong>{{ fixRuleForm.monitoredJobId === null ? 'default (all jobs)' : 'override (this job)' }}</strong>
                  scope. Existing policy:
                  <strong>{{ conflict.fixCategory }} / {{ conflict.actionType }}</strong>.
                  Only one enabled policy per Error Type is allowed at each scope.
                  <button type="button" class="link-btn"
                          (click)="openConflictingPolicy(conflict)">
                    Edit existing policy instead?
                  </button>
                </div>
              } @else if (fixRuleSaveConflict(); as conflict) {
                <div class="dup-warn">
                  ⚠ {{ conflict.message }}
                  <button type="button" class="link-btn"
                          (click)="openConflictingPolicyById(conflict.conflictingPolicyId)">
                    Open existing policy
                  </button>
                </div>
              }
              <!-- Reachability + fan-in clarity. A fix is keyed to an ErrorType,
                   which this job only reaches via its classification rules. -->
              @if (selectedErrorTypeCode(); as code) {
                @if (classRulesForSelectedErrorType().length > 0) {
                  <span class="field-hint covers-hint">
                    Covers {{ classRulesForSelectedErrorType().length }} classification
                    {{ classRulesForSelectedErrorType().length === 1 ? 'rule' : 'rules' }} on this job:
                    @for (cr of classRulesForSelectedErrorType(); track cr.ruleId; let last = $last) {
                      <code>{{ cr.pattern }}</code>{{ last ? '' : ', ' }}
                    }
                  </span>
                } @else {
                  <div class="dup-warn reachability-warn">
                    ⚠ No classification rule on this job maps to <strong>{{ code }}</strong> —
                    this fix won't trigger until one exists. Add a matching rule in the
                    <strong>Classification Rules</strong> tab.
                  </div>
                }
              }
            </div>
            <div class="form-group">
              <label>Action Description *</label>
              <input [(ngModel)]="fixRuleForm.actionToApply"
                     placeholder="e.g. Retry DTSX job via management API" />
            </div>
            <div class="form-group">
              <label>Fix Category</label>
              <select [(ngModel)]="fixRuleForm.fixCategory">
                @for (c of fixCategories; track c) { <option [ngValue]="c">{{ c }}</option> }
              </select>
            </div>
            <div class="form-group">
              <label>Execution Type</label>
              <select [ngModel]="fixRuleForm.actionType"
                      (ngModelChange)="setFixRuleActionType($event)">
                @for (a of actionTypes; track a) { <option [ngValue]="a">{{ a }}</option> }
              </select>
            </div>
            <!-- Auto-Heal + Enabled share Execution Type's row (half width) —
                 keeps these behaviour toggles on the same line instead of a
                 dedicated full-width row at the bottom. Wrapped in a real
                 form-group (label + inner row) so it aligns with the
                 Execution Type select beside it. -->
            <div class="form-group">
              <label>Behaviour</label>
              <div class="toggles-row">
                <label class="toggle-pair">
                  <span class="toggle">
                    <input type="checkbox" [(ngModel)]="fixRuleForm.isAutoHealEligible" />
                    <span class="slider"></span>
                  </span>
                  <span class="toggle-text">Auto-Heal</span>
                </label>
                <label class="toggle-pair">
                  <span class="toggle">
                    <input type="checkbox" [(ngModel)]="fixRuleForm.enabled"
                           (ngModelChange)="syncFixRuleSignal()" />
                    <span class="slider"></span>
                  </span>
                  <span class="toggle-text">Enabled</span>
                </label>
              </div>
            </div>
            @if (fixRuleForm.actionType !== 'Manual' && fixRuleForm.actionType !== 'Composite') {
              <div class="form-group span2">
                <label>Action Payload</label>
                @if (fixRuleForm.actionType === 'ApiCall') {
                  <input [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()"
                         placeholder="http://jobs.internal/api/jobs/{failureId}/retry" />
                  <span class="field-hint">Use {{'{'}}failureId{{'}'}} as a placeholder — replaced at runtime.</span>
                } @else if (fixRuleForm.actionType === 'StoredProcedure') {
                  <input [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()"
                         placeholder="dbo.sp_RetryJob  or  ConnName|dbo.sp_RetryJob" />
                } @else if (fixRuleForm.actionType === 'SqlScript') {
                  <textarea [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()" rows="4"
                            placeholder="UPDATE dbo.Files SET FileStatusCode = 0 WHERE Id = '{sourceId}'"></textarea>
                  <span class="field-hint">
                    Runs against the job's configured connection (DB-scan jobs).
                    To update the <strong>source row</strong>, key on
                    <code>'{{'{'}}sourceId{{'}'}}'</code> (the captured source key, quoted) —
                    <em>not</em> <code>{{'{'}}failureId{{'}'}}</code>, which is MAIA's internal
                    failure id, not a column in your table.
                  </span>
                } @else if (fixRuleForm.actionType === 'CopyFile') {
                  <input [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()"
                         placeholder="{sourceFilePath}|{inputFolder}\reprocess\{sourceFileName}" />
                  <span class="field-hint">
                    Format <code>SOURCE|DEST</code>. Atomic copy (.tmp + rename), overwrite by default.
                    <code>{{'{'}}sourceFilePath{{'}'}}</code> requires InputPathPattern (FS) or FilePathColumn (DB) on the scan rule.
                  </span>
                } @else {
                  <input [ngModel]="fixRuleForm.actionPayload" (ngModelChange)="fixRuleForm.actionPayload = $event; syncFixRuleSignal()"
                         placeholder="powershell.exe C:\scripts\fix.ps1 {failureId}" />
                }
              </div>
            }
            @if (fixRuleForm.actionType === 'Composite') {
              <!-- Composite step editor — payload moves off the header onto
                   each ordered step. Backend rejects (CompositePayloadConflict)
                   if the header carries a payload alongside steps. -->
              <div class="form-group span2">
                <label>Steps *</label>
                <div class="steps-editor">
                  @for (step of fixRuleForm.steps ?? []; track $index; let i = $index) {
                    <!-- Two-line per-step layout: payload gets the full width
                         of the first row (the most-used + most-content-heavy
                         field), description is an optional second line below.
                         Earlier single-row layout squeezed payload to ~40px. -->
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
                          <textarea [ngModel]="step.actionPayload" (ngModelChange)="step.actionPayload = $event; syncFixRuleSignal()"
                                    rows="2"
                                    class="step-payload step-payload-sql"
                                    [placeholder]="payloadPlaceholderFor(step.actionType)"></textarea>
                        } @else {
                          <input [ngModel]="step.actionPayload" (ngModelChange)="step.actionPayload = $event; syncFixRuleSignal()"
                                 class="step-payload"
                                 [placeholder]="payloadPlaceholderFor(step.actionType)" />
                        }
                        <div class="step-controls">
                          <button type="button" class="btn btn-ghost btn-icon"
                                  title="Move up"
                                  (click)="moveStep(i, -1)" [disabled]="i === 0">↑</button>
                          <button type="button" class="btn btn-ghost btn-icon"
                                  title="Move down"
                                  (click)="moveStep(i, +1)"
                                  [disabled]="i === (fixRuleForm.steps?.length ?? 0) - 1">↓</button>
                          <button type="button" class="btn btn-ghost btn-icon"
                                  title="Remove step"
                                  (click)="removeStep(i)">✕</button>
                        </div>
                      </div>
                      <input [(ngModel)]="step.description"
                             class="step-desc"
                             placeholder="Description (optional)" />
                    </div>
                  }
                  <button type="button" class="btn btn-ghost btn-sm step-add" (click)="addStep()">
                    + Add Step
                  </button>
                </div>
                <span class="field-hint">
                  Steps run in order. Any step failure routes the failure to
                  <strong>ManualRequired</strong>; subsequent steps still run
                  (best-effort). One <code>FixExecutionLog</code> row per step.
                </span>
              </div>
            }
            <!-- Soft config-time warning: the payload references
                 {sourceFilePath} but no scan rule on this job captures one.
                 Without a capture the token resolves empty and a CopyFile step
                 fails at execution time — surface it here, not in the logs. -->
            @if (fixRuleSourcePathWarning()) {
              <div class="form-group span2">
                <div class="dup-warn">
                  ⚠ This payload uses <code>{{'{'}}sourceFilePath{{'}'}}</code>, but no
                  scan rule on <strong>{{ editingFixRuleJob()?.displayName ?? editingFixRuleJob()?.name }}</strong>
                  captures a file path. Set <strong>Input File Extraction</strong> (FS) or
                  <strong>File Path Column</strong> (DB) on a scan rule for this job,
                  or the fix will fail at runtime with an empty source path.
                </div>
              </div>
            }
            <!-- Always-visible token reference for any non-Manual fix. Keeps
                 the six payload placeholders documented in one place instead
                 of scattered per-type hints. -->
            @if (fixRuleForm.actionType !== 'Manual') {
              <div class="form-group span2">
                <details class="token-legend">
                  <summary>Available placeholders</summary>
                  <dl>
                    <dt><code>{{'{'}}failureId{{'}'}}</code></dt><dd>This failure's numeric id.</dd>
                    <dt><code>{{'{'}}sourceId{{'}'}}</code></dt><dd>Source row's natural key (DB scan) or matched id.</dd>
                    <dt><code>{{'{'}}sourceLogPath{{'}'}}</code></dt><dd>Log file/source where the error was detected.</dd>
                    <dt><code>{{'{'}}sourceFilePath{{'}'}}</code></dt><dd>Input file path — needs Input File Extraction (FS) or File Path Column (DB).</dd>
                    <dt><code>{{'{'}}sourceFileName{{'}'}}</code></dt><dd>Filename only, sliced from {{'{'}}sourceFilePath{{'}'}} (e.g. <code>deposit.txt</code>).</dd>
                    <dt><code>{{'{'}}jobFolder{{'}'}}</code></dt><dd>The job's Log Folder.</dd>
                    <dt><code>{{'{'}}inputFolder{{'}'}}</code></dt><dd>The job's Input Folder.</dd>
                  </dl>
                  <span class="token-note">Unknown tokens are left as-is. Matching is case-insensitive.</span>
                </details>
              </div>
            }
            <!-- Step 7 disclosure: about to disable an enabled default rule.
                 Overrides on other jobs of the same JobType still apply;
                 jobs WITHOUT overrides for this errorType will fall back to
                 the built-in catalogue. Soft hint, no data fetch. -->
            @if (!fixRuleForm.enabled
                 && fixRuleForm.monitoredJobId === null
                 && editingFixRule() !== null
                 && editingFixRule()!.enabled) {
              <div class="dup-warn span2">
                ⚠ Disabling this default — jobs of this JobType that don't have
                their own override for this error type will fall back to the
                built-in catalogue. Overrides on other jobs are unaffected.
              </div>
            }
          </div>
          @if (fixRuleForm.isAutoHealEligible) {
            <div class="auto-heal-banner">
              <span>⚡</span>
              <span>
                Auto-heal is ON — this fix will execute <strong>automatically</strong> without
                operator approval whenever this error type is detected.
              </span>
            </div>
          }
          @if (fixRuleSaveError(); as msg) {
            <!-- Surfaces 400 validation errors from the backend (composite
                 shape rules, missing payload, etc.) — without this banner,
                 the Save button just resets and the operator sees nothing. -->
            <div class="dup-warn save-error" role="alert">
              ⚠ Save failed: {{ msg }}
            </div>
          }
        </div>
        <div class="drawer-footer">
          <button class="btn btn-ghost" (click)="closeDrawer()">Cancel</button>
          <button class="btn btn-primary" (click)="saveFixRule()" [disabled]="saving()">
            @if (saving()) { <span class="spinner"></span> }
            {{ editingFixRule() ? 'Save Changes' : 'Add Fix Option' }}
          </button>
        </div>
      </div>
    }
  `,
  styles: [`
    h1 { font-size: 22px; font-weight: 700; }
    .page-header { display: flex; justify-content: space-between; align-items: flex-start; }

    /* Job list */
    .jobs-list  { display: flex; flex-direction: column; gap: 10px; }
    .job-card   { background: var(--surface); border: 1px solid var(--border); border-radius: var(--radius); overflow: hidden; }

    .job-card-header {
      display: flex; align-items: center; justify-content: space-between;
      padding: 14px 16px; gap: 12px; flex-wrap: wrap;
    }
    .job-lead    { display: flex; align-items: center; gap: 12px; flex: 1; min-width: 0; }
    .scan-icon   { font-size: 20px; flex-shrink: 0; }
    .job-name    { font-size: 14px; font-weight: 600; }
    .job-meta    { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
    .meta-chip   {
      background: var(--surface-2); border: 1px solid var(--border-light);
      border-radius: 4px; padding: 2px 8px; font-size: 11px; color: var(--text-muted);
      font-family: 'Consolas', monospace; max-width: 200px;
      overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
    }
    .job-actions { display: flex; align-items: center; gap: 6px; flex-shrink: 0; }

    /* Config panel with tabs */
    .config-panel { border-top: 1px solid var(--border); }

    .tab-bar {
      display: flex; gap: 0; padding: 0 16px;
      background: var(--surface-2); border-bottom: 1px solid var(--border);
    }
    .tab-btn {
      display: inline-flex; align-items: center; gap: 6px;
      padding: 10px 16px; border: none; border-bottom: 2px solid transparent;
      background: transparent; cursor: pointer; font-size: 12px; font-weight: 500;
      color: var(--text-muted); margin-bottom: -1px; transition: all var(--transition);
      font-family: inherit;
      &:hover { color: var(--text); background: var(--surface); }
      &.active { color: var(--primary); border-bottom-color: var(--primary); background: var(--surface); }
    }
    .tab-badge {
      background: var(--surface-3); border-radius: 10px;
      padding: 1px 6px; font-size: 10px; font-weight: 700; color: var(--text-muted);
    }
    .tab-btn.active .tab-badge { background: var(--primary-light); color: var(--primary); }

    .tab-content { padding: 14px 16px; }

    .section-header {
      display: flex; align-items: center; justify-content: space-between;
      gap: 12px; margin-bottom: 12px;
    }
    .section-desc { font-size: 12px; color: var(--text-muted); }

    /* Subsection headers in the Classification tab (linked vs JobType globals). */
    .subsection-label {
      font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em;
      color: var(--text-muted); margin: 16px 0 6px;
    }
    .global-row td { opacity: 0.82; }

    .empty-tab {
      display: flex; align-items: center; gap: 10px; padding: 20px 0;
      color: var(--text-muted); font-size: 12px;
      span { font-size: 20px; opacity: 0.5; }
    }

    /* Drawer */
    .drawer-overlay {
      position: fixed; inset: 0; background: rgba(0,0,0,0.25); z-index: 200;
    }
    .drawer {
      position: fixed; top: 0; right: 0; height: 100vh; width: 500px;
      background: var(--surface); border-left: 1px solid var(--border);
      box-shadow: -4px 0 24px rgba(0,0,0,0.12); z-index: 201;
      display: flex; flex-direction: column; animation: slideIn 0.2s ease;
    }
    /* The Fix Options drawer carries more fields (scope, payload/steps,
       toggles, warnings) than the others. Widening it lets the 2-column
       grid actually pair fields side-by-side and gives the payload /
       SQL textarea / warnings room to breathe — trading width for height
       so the form stops scrolling. Capped at 94vw for small screens. */
    .drawer.drawer-wide { width: min(720px, 94vw); }
    @keyframes slideIn { from { transform: translateX(100%); } to { transform: translateX(0); } }

    .drawer-header {
      display: flex; align-items: center; justify-content: space-between;
      padding: 16px 20px; border-bottom: 1px solid var(--border);
      h3 { font-size: 15px; font-weight: 600; }
    }
    .drawer-body   { flex: 1; overflow-y: auto; padding: 20px; display: flex; flex-direction: column; gap: 16px; }
    .drawer-footer {
      display: flex; justify-content: flex-end; gap: 8px;
      padding: 14px 20px; border-top: 1px solid var(--border);
    }

    .drawer-context-banner {
      display: flex; align-items: center; gap: 8px;
      background: var(--primary-light); border: 1px solid var(--primary);
      border-radius: var(--radius-sm); padding: 8px 12px;
      font-size: 12px; color: var(--primary-dark);
    }

    .auto-heal-banner {
      display: flex; align-items: flex-start; gap: 8px;
      background: #fff7ed; border: 1px solid #fed7aa;
      border-radius: var(--radius-sm); padding: 10px 12px;
      font-size: 12px; color: #92400e; line-height: 1.5;
      span:first-child { font-size: 16px; }
    }

    .form-grid {
      display: grid; grid-template-columns: 1fr 1fr; gap: 14px;
      .span2 { grid-column: span 2; }
    }
    .toggle-label { display: flex; align-items: center; gap: 8px; cursor: pointer; }
    .field-hint   { font-size: 11px; color: var(--text-dim); margin-top: 2px; }

    /* Compact scope line in the Fix Options drawer. Replaces the old two-row
       scope radio: per-job is now the default, so the common case needs zero
       interaction. The current scope reads as plain text; the JobType-wide
       default is one inline link away for the rare shared-fix case. */
    .scope-line {
      display: flex; align-items: baseline; flex-wrap: wrap;
      gap: 4px 10px; font-size: 12px; color: var(--text-muted);
    }
    .scope-current strong { color: var(--text); }
    .link-btn {
      background: transparent; border: none; padding: 0;
      color: var(--primary); font-weight: 600; cursor: pointer;
      text-decoration: underline; font-size: inherit;
    }
    .link-btn:hover { color: var(--primary-dark); }

    /* Inline info icon for drawer-context-banner. Static, non-spinning —
       previously used a gear emoji that looked spinner-ish at this size. */
    .banner-icon {
      display: inline-flex; align-items: center; justify-content: center;
      width: 18px; height: 18px; flex-shrink: 0;
      border-radius: 50%; background: var(--primary); color: #fff;
      font-style: italic; font-weight: 700; font-size: 12px;
      font-family: Georgia, serif;
    }

    /* Compact two-toggle row for Auto-Heal + Enabled — single line, inline
       labels right of each switch, replaces the previous two-column slot
       layout that left the toggles feeling tacked on. */
    .toggles-row {
      display: flex; gap: 24px; align-items: center;
      padding-top: 4px;
    }
    .toggle-pair {
      display: inline-flex; align-items: center; gap: 8px; cursor: pointer;
    }
    .toggle-pair .toggle-text { font-size: 13px; color: var(--text); }

    /* Composite step editor — each step row is a horizontal mini-form with
       order badge + type select + payload + optional description + move /
       remove controls. Keep the row dense; payload is the widest column
       since it does the most. */
    /* Composite step editor — two-line per-step layout:
         Row A: [order] [type select] [payload — grows] [↑ ↓ ✕]
         Row B: [description — full width, indented]
       Payload is the field operators interact with most and that holds the
       most content, so it gets the full first-row width. SqlScript payloads
       upgrade to a 2-row textarea so multi-statement SQL stays readable. */
    .steps-editor { display: flex; flex-direction: column; gap: 10px; margin-top: 4px; }
    .step-block {
      display: flex; flex-direction: column; gap: 4px;
      padding: 8px; border: 1px solid var(--border); border-radius: var(--radius-sm);
      background: var(--surface);
    }
    .step-row {
      display: grid;
      grid-template-columns: 24px 110px 1fr auto;
      gap: 6px; align-items: start;
    }
    .step-row .step-order {
      font-weight: 600; color: var(--text-dim); text-align: right;
      align-self: center;
    }
    .step-row select.step-type        { font-size: 12px; padding: 4px 6px; min-width: 0; }
    .step-row input.step-payload      { font-size: 12px; padding: 4px 6px; min-width: 0; }
    .step-row textarea.step-payload   {
      font-size: 12px; padding: 4px 6px; min-width: 0;
      font-family: ui-monospace, Menlo, Consolas, monospace;
      resize: vertical;
    }
    .step-block input.step-desc {
      font-size: 12px; padding: 4px 6px;
      margin-left: 32px; /* indent under the order+type columns */
    }
    .step-controls { display: flex; gap: 2px; align-self: center; }
    .step-row .btn-icon { padding: 2px 6px; font-size: 13px; line-height: 1.2; }
    .step-add { align-self: flex-start; margin-top: 2px; }

    /* Scope badges in the per-row list — small visual difference between
       a default and an override so operators can scan the list quickly. */
    .scope-override-badge { margin-left: 4px; font-size: 10px; }
    .scope-default-badge  { margin-left: 4px; font-size: 10px; }

    .dup-warn {
      display: block; margin-top: 6px;
      padding: 8px 10px; border-radius: var(--radius-sm);
      background: #fef3c7; border: 1px solid #fde68a;
      font-size: 12px; color: #78350f; line-height: 1.4;
    }
    .dup-warn .link-btn {
      background: transparent; border: none; padding: 0; margin-left: 4px;
      color: #b45309; font-weight: 600; cursor: pointer;
      text-decoration: underline; font-size: inherit;
    }
    .dup-warn .link-btn:hover { color: #92400e; }

    /* Red variant for save-error (vs the amber soft-warning dup-warn). Same
       layout, different colour family — operator distinguishes "soft hint
       at the field" from "the save just got rejected". */
    .dup-warn.save-error {
      background: #fef2f2; border-color: #fecaca; color: #991b1b;
      margin-top: 12px;
    }

    /* Collapsible placeholder reference under the payload field. Closed by
       default so it doesn't crowd the form; one click reveals all six tokens. */
    .token-legend {
      margin-top: 6px; font-size: 12px;
      border: 1px solid var(--border-light); border-radius: var(--radius-sm);
      background: var(--surface-2);
    }
    .token-legend > summary {
      cursor: pointer; padding: 6px 10px; font-weight: 600;
      color: var(--text-muted); user-select: none;
    }
    .token-legend[open] > summary { border-bottom: 1px solid var(--border-light); }
    .token-legend dl {
      margin: 0; padding: 8px 10px;
      display: grid; grid-template-columns: auto 1fr; gap: 4px 12px;
    }
    .token-legend dt { margin: 0; }
    .token-legend dd { margin: 0; color: var(--text-muted); }
    .token-legend code { font-size: 11px; }
    .token-note {
      display: block; padding: 0 10px 8px; font-size: 11px; color: var(--text-dim);
    }

    .scan-info-notice {
      display: flex; align-items: flex-start; gap: 10px; padding: 14px 16px;
      background: var(--surface-2); border: 1px solid var(--border-light);
      border-radius: var(--radius-sm); font-size: 12px; color: var(--text-muted);
      span:first-child { font-size: 20px; flex-shrink: 0; }
      strong { color: var(--text); }
      p { margin: 4px 0 0; }
    }

    .link-rule-list { display: flex; flex-direction: column; gap: 6px; }
    .link-rule-item {
      padding: 10px 12px; border: 1px solid var(--border);
      border-radius: var(--radius-sm); cursor: pointer; transition: all var(--transition);
      &:hover { border-color: var(--primary); background: var(--primary-light); }
    }
    .link-rule-pattern { font-size: 12px; margin-bottom: 4px; word-break: break-all; }
    .link-rule-meta    { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
  `]
})
export class MonitoredJobsComponent implements OnInit {
  private svc = inject(ConfigService);
  private route  = inject(ActivatedRoute);
  private router = inject(Router);

  loading      = signal(false);
  saving       = signal(false);
  jobs         = signal<MonitoredJob[]>([]);
  jobTypes     = signal<JobType[]>([]);
  errorTypes   = signal<ErrorType[]>([]);
  expanded     = signal<number | null>(null);
  activeTab    = signal<ActiveTab>('scan');
  activeDrawer = signal<ActiveDrawer>(null);
  fixPolicies  = signal<FixPolicyRule[]>([]);
  loadingFix   = signal(false);

  allClassRules     = signal<ClassificationRule[]>([]);
  loadingLinkRules  = signal(false);
  linkRuleSearch    = signal('');

  filteredLinkableRules = computed(() => {
    const job    = this.editingClassRuleJob();
    const linked = new Set(job?.rules.map(r => r.ruleId) ?? []);
    const q      = this.linkRuleSearch().toLowerCase();
    return this.allClassRules().filter(r =>
      !linked.has(r.ruleId) &&
      (!q || r.pattern.toLowerCase().includes(q) || r.errorTypeCode.toLowerCase().includes(q))
    );
  });

  // Editing context
  editingJob           = signal<MonitoredJob | null>(null);
  editingRule          = signal<ScanCheckRule | null>(null);
  editingRuleJob       = signal<MonitoredJob | null>(null);
  editingClassRule     = signal<RuleOverride | null>(null);
  editingClassRuleJob  = signal<MonitoredJob | null>(null);
  editingFixRule       = signal<FixPolicyRule | null>(null);
  editingFixRuleJob    = signal<MonitoredJob | null>(null);

  /** Two-pronged duplicate detection — same key shape as the backend's
   *  409 check. Switches between layers based on the form's MonitoredJobId:
   *    • monitoredJobId set → override layer; collide on (monitoredJobId, errorTypeId)
   *    • monitoredJobId null → default  layer; collide on (jobTypeId, errorTypeId, monitoredJobId IS NULL)
   *  A default and an override for the same (JobType, ErrorType) are NOT
   *  duplicates — they're complementary. Returns the conflicting rule when
   *  found so the inline warning + "Open existing" can target it. */
  fixRuleDuplicateConflict = computed<FixPolicyRule | null>(() => {
    const form = this.fixRuleFormSignal();
    if (!form.enabled || !form.errorTypeId || !form.jobTypeId) return null;
    const editingId = this.editingFixRule()?.ruleId;
    return this.fixPolicies().find(p => {
      if (!p.enabled || p.ruleId === editingId) return false;
      if (p.errorTypeId !== form.errorTypeId)   return false;
      return form.monitoredJobId !== null
        ? p.monitoredJobId === form.monitoredJobId          // override layer match
        : p.monitoredJobId === null && p.jobTypeId === form.jobTypeId; // default layer match
    }) ?? null;
  });

  /** Soft config-time warning: a fix payload (single-action or any composite
   *  step) references {sourceFilePath}, but no scan rule on the host job
   *  captures one — i.e. no InputPathPattern (FS) and no FilePathColumn (DB)
   *  is set on any of the job's scan rules. Without a capture, the token
   *  resolves to empty at fix time and a CopyFile step fails. Surfacing it
   *  here turns a runtime failure into a config-time hint. */
  fixRuleSourcePathWarning = computed<boolean>(() => {
    const form = this.fixRuleFormSignal();
    const usesToken = (s: string | null | undefined) => !!s && /\{sourceFilePath\}/i.test(s);
    const referenced = usesToken(form.actionPayload)
      || (form.steps ?? []).some(s => usesToken(s.actionPayload));
    if (!referenced) return false;
    const job = this.editingFixRuleJob();
    const captures = (job?.scanCheckRules ?? []).some(r =>
      !!r.inputPathPattern?.trim() || !!r.filePathColumn?.trim());
    return !captures;
  });

  /** The classification rules the classifier would actually use for the
   *  drawer's job: UNION of this job's linked rules (overrides) + the JobType's
   *  UNLINKED defaults — mirrors SqlMonitoredJobRepository.GetEffectiveRulesAsync.
   *  Drives the rule picker + the reachability / "covers" clarity, so a fix for
   *  an ErrorType produced by a JobType default (e.g. ProcessingError) is
   *  correctly seen as reachable even when the job also has its own links. */
  effectiveClassRules = computed<{ ruleId: number; pattern: string; errorTypeCode: string }[]>(() => {
    const job = this.editingFixRuleJob();
    if (!job) return [];
    const jt = this.getJobTypeId(job);
    const linked = job.rules ?? [];
    // JobType defaults = active rules of this JobType linked to NO job (a rule
    // linked to another job is that job's override, not a default here).
    const linkedAnywhere = new Set(this.jobs().flatMap(j => j.rules.map(r => r.ruleId)));
    const defaults = this.allClassRules()
      .filter(r => r.jobTypeId === jt && r.isActive && !linkedAnywhere.has(r.ruleId));
    return [
      ...linked.map(r => ({ ruleId: r.ruleId, pattern: r.pattern, errorTypeCode: r.errorTypeCode })),
      ...defaults.map(r => ({ ruleId: r.ruleId, pattern: r.pattern, errorTypeCode: r.errorTypeCode })),
    ];
  });

  /** Code of the currently-selected ErrorType in the fix form (null = none). */
  selectedErrorTypeCode = computed<string | null>(() => {
    const id = this.fixRuleFormSignal().errorTypeId;
    if (!id) return null;
    return this.errorTypes().find(e => e.errorTypeId === id)?.code ?? null;
  });

  /** Effective classification rules on this job that produce the selected
   *  ErrorType. Empty while an ErrorType is chosen ⇒ the fix is unreachable. */
  classRulesForSelectedErrorType = computed(() => {
    const code = this.selectedErrorTypeCode();
    if (!code) return [];
    return this.effectiveClassRules().filter(r => r.errorTypeCode === code);
  });

  /** Mirrors fixRuleForm into a signal so the computed above re-evaluates
   *  on every form change. ngModel binds to fixRuleForm directly; we sync
   *  via setFixRuleFormField helpers below to keep the signal current. */
  private fixRuleFormSignal = signal<UpsertFixPolicyRuleRequest>(this.blankFixRule());

  /** Last 409 response surfaced after a failed save — keeps the
   *  "Open existing policy" affordance live in the drawer footer. */
  fixRuleSaveConflict = signal<{ message: string; conflictingPolicyId: number } | null>(null);

  /** Last non-409 save error — composite validation (400) lands here so the
   *  operator sees WHY save failed instead of just watching the button reset
   *  silently. Cleared on every save attempt and on drawer reopen. */
  fixRuleSaveError = signal<string | null>(null);

  readonly scanTypes     = SCAN_TYPES;
  readonly dbCheckTypes  = DB_CHECK_TYPES;
  readonly severities    = SEVERITIES;
  readonly fixCategories = FIX_CATEGORIES;
  readonly actionTypes   = ACTION_TYPES;

  jobForm      : UpsertJobRequest                              = this.blankJob();
  ruleForm     : UpsertScanRuleRequest & { isActive: boolean } = this.blankScanRule();
  classRuleForm: UpsertJobClassificationRuleRequest            = this.blankClassRule();
  fixRuleForm  : UpsertFixPolicyRuleRequest                    = this.blankFixRule();
  /** Rule chosen in the "Target a classification rule" shortcut — kept so the
   *  select shows the pick (cleared on drawer open and on manual Error Type change). */
  shortcutRuleId: number | null = null;

  ngOnInit() {
    this.loading.set(true);
    this.svc.getAllJobs().subscribe({
      next: j => { this.jobs.set(j); this.loading.set(false); this.applyFixDeepLink(); },
      error: () => this.loading.set(false),
    });
    this.svc.getJobTypes().subscribe({ next: t => this.jobTypes.set(t) });
    this.svc.getErrorTypes().subscribe({ next: t => this.errorTypes.set(t) });
  }

  /** Deep-link from the /unconfigured Case-B "Configure fix" button:
   *  ?fixForJob=<id>&errorTypeId=<id> → expand the job, open its Fix Options
   *  tab, and pop a new-fix drawer pre-filled (per-job scope + that ErrorType).
   *  Params are cleared afterward so a refresh/back doesn't re-trigger. */
  private applyFixDeepLink() {
    const p = this.route.snapshot.queryParamMap;
    const jobIdStr = p.get('fixForJob');
    if (!jobIdStr) return;
    const job = this.jobs().find(x => x.monitoredJobId === Number(jobIdStr));
    if (!job) return;

    this.expanded.set(job.monitoredJobId);
    this.setTab('fix', job);            // loads the effective fix policies
    this.openFixRuleDrawer(job, null);  // new rule — defaults to per-job scope
    const etId = Number(p.get('errorTypeId') ?? 0);
    if (etId) { this.fixRuleForm.errorTypeId = etId; this.syncFixRuleSignal(); }

    this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }

  // ── Panel & tab control ────────────────────────────────────────────────────

  toggle(job: MonitoredJob) {
    const next = this.expanded() === job.monitoredJobId ? null : job.monitoredJobId;
    this.expanded.set(next);
    if (next !== null) {
      this.activeTab.set('scan');
      this.fixPolicies.set([]);
    }
  }

  setTab(tab: ActiveTab, job?: MonitoredJob) {
    this.activeTab.set(tab);
    if (tab === 'fix' && job) {
      const jt = this.getJobTypeId(job);
      // Pass the host job's MonitoredJobId so the tab shows defaults + any
      // override scoped to this specific job (the operator's effective config).
      if (jt) this.loadFixPolicies(jt, job.monitoredJobId);
    } else if (tab === 'class' && this.allClassRules().length === 0) {
      // Needed to show the JobType-global rules that ALSO classify this job
      // under the union semantics, alongside its job-linked rules.
      this.svc.getAllClassificationRules().subscribe({ next: r => this.allClassRules.set(r) });
    }
  }

  getJobTypeId(job: MonitoredJob): number {
    return this.jobTypes().find(t => t.name === job.jobTypeName)?.jobTypeId ?? 0;
  }

  /** JobType-global classification rules that ALSO classify this job under the
   *  union semantics — active, same JobType, not already linked to the job.
   *  Shown read-only on the job's Classification Rules tab so the operator can
   *  see the full effective set (not just job-linked rules). */
  jobTypeGlobalRules(job: MonitoredJob): ClassificationRule[] {
    const jt = this.getJobTypeId(job);
    // True JobType defaults = rules linked to NO job. A rule linked to a
    // specific job is that job's override and must not show as a default here.
    const linkedAnywhere = new Set(this.jobs().flatMap(j => j.rules.map(r => r.ruleId)));
    return this.allClassRules()
      .filter(r => r.jobTypeId === jt && r.isActive && !linkedAnywhere.has(r.ruleId))
      .sort((a, b) => a.priority - b.priority);
  }

  scanIcon(id: number): string {
    return ({ 1: '📁', 2: '🗄', 3: '🌐' } as Record<number, string>)[id] ?? '📋';
  }

  private loadFixPolicies(jobTypeId: number, monitoredJobId?: number) {
    this.loadingFix.set(true);
    // When a MonitoredJob is in context, ask the backend to include both
    // the JobType-level defaults AND any override scoped to that job, so the
    // "Fix Options" tab shows the effective config the operator sees for THIS
    // specific job (including its overrides). Without monitoredJobId the
    // call returns every default + every override under the JobType.
    this.svc.getFixPolicyRules(jobTypeId, monitoredJobId).subscribe({
      next: r => { this.fixPolicies.set(r); this.loadingFix.set(false); },
      error: () => this.loadingFix.set(false),
    });
  }

  // ── Job CRUD ───────────────────────────────────────────────────────────────

  openJobDrawer(job: MonitoredJob | null) {
    this.editingJob.set(job);
    if (job) {
      const jt = this.jobTypes().find(t => t.name === job.jobTypeName);
      this.jobForm = {
        name: job.name, displayName: job.displayName, jobTypeId: jt?.jobTypeId ?? 0,
        scanTypeId: job.scanTypeId, logFolder: job.logFolder, searchPatterns: job.searchPatterns,
        inputFolder: job.inputFolder,
        connectionName: job.connectionName, logSourceUrl: job.logSourceUrl,
        pollingIntervalSeconds: job.pollingIntervalSeconds, isActive: job.isActive,
        description: job.description,
      };
    } else {
      this.jobForm = this.blankJob();
    }
    this.activeDrawer.set('job');
  }

  saveJob() {
    if (!this.jobForm.name || !this.jobForm.jobTypeId) return;
    this.saving.set(true);
    const id    = this.editingJob()?.monitoredJobId;
    const req$: Observable<any> = id ? this.svc.updateJob(id, this.jobForm) : this.svc.createJob(this.jobForm);
    req$.subscribe({
      next: () => { this.closeDrawer(); this.reload(); },
      error: () => this.saving.set(false),
    });
  }

  deleteJob(job: MonitoredJob) {
    // Soft-delete disclosure: surface override count BEFORE the operator
    // confirms, so they know overrides will become dormant alongside the
    // job (and reactivate if the job is reactivated later). One DB call,
    // no new UI surface. Defers to a single confirm dialog for simplicity.
    const jobTypeId = this.getJobTypeId(job);
    if (!jobTypeId) {
      if (!confirm(`Deactivate job "${job.name}"?`)) return;
      this.svc.deleteJob(job.monitoredJobId).subscribe({ next: () => this.reload() });
      return;
    }
    this.svc.getFixPolicyRules(jobTypeId, job.monitoredJobId).subscribe({
      next: rules => {
        const overrides = rules.filter(r => r.monitoredJobId === job.monitoredJobId && r.enabled);
        const suffix = overrides.length > 0
          ? `\n\nThis job has ${overrides.length} active fix override(s). They'll become inactive with the job and reactivate if you reactivate it later.`
          : '';
        if (!confirm(`Deactivate job "${job.name}"?${suffix}`)) return;
        this.svc.deleteJob(job.monitoredJobId).subscribe({ next: () => this.reload() });
      },
      // If override count fails to load, don't block deletion — fall back to
      // the bare confirm. Surfacing a generic "couldn't check overrides" toast
      // here would be more noise than value.
      error: () => {
        if (!confirm(`Deactivate job "${job.name}"?`)) return;
        this.svc.deleteJob(job.monitoredJobId).subscribe({ next: () => this.reload() });
      },
    });
  }

  // ── Scan Rule CRUD ─────────────────────────────────────────────────────────

  openScanRuleDrawer(job: MonitoredJob, rule: ScanCheckRule | null) {
    this.editingRuleJob.set(job);
    this.editingRule.set(rule);
    this.ruleForm = rule ? {
      checkType: rule.checkType, sourceTable: rule.sourceTable, targetField: rule.targetField,
      minValue: rule.minValue, maxValue: rule.maxValue, expectedValue: rule.expectedValue,
      watermarkColumn: rule.watermarkColumn, sourceIdColumn: rule.sourceIdColumn,
      filePathColumn: rule.filePathColumn, inputPathPattern: rule.inputPathPattern,
      severity: rule.severity, description: rule.description, isActive: true,
    } : this.blankScanRule(job.scanTypeId);
    this.activeDrawer.set('scan-rule');
  }

  saveScanRule() {
    if (!this.ruleForm.targetField) return;
    this.saving.set(true);
    const ruleId  = this.editingRule()?.checkRuleId;
    const jobId   = this.editingRuleJob()!.monitoredJobId;
    const req$: Observable<any> = ruleId
      ? this.svc.updateScanRule(ruleId, this.ruleForm)
      : this.svc.createScanRule(jobId, this.ruleForm);
    req$.subscribe({
      next: () => { this.closeDrawer(); this.reload(); },
      error: () => this.saving.set(false),
    });
  }

  deleteScanRule(job: MonitoredJob, rule: ScanCheckRule) {
    if (!confirm(`Delete scan rule for [${rule.sourceTable}].[${rule.targetField}]?`)) return;
    this.svc.deleteScanRule(rule.checkRuleId).subscribe({ next: () => this.reload() });
  }

  // ── Classification Rule CRUD ───────────────────────────────────────────────

  openClassRuleDrawer(job: MonitoredJob, rule: RuleOverride | null) {
    this.editingClassRuleJob.set(job);
    this.editingClassRule.set(rule);
    if (rule) {
      const et = this.errorTypes().find(e => e.code === rule.errorTypeCode);
      this.classRuleForm = {
        errorTypeId: et?.errorTypeId ?? 0,
        pattern    : rule.pattern,
        confidence : rule.confidence,
        priority   : rule.priority,
        isActive   : true,
      };
    } else {
      this.classRuleForm = this.blankClassRule();
    }
    this.activeDrawer.set('class-rule');
  }

  saveClassRule() {
    if (!this.classRuleForm.pattern || !this.classRuleForm.errorTypeId) return;
    this.saving.set(true);
    const ruleId    = this.editingClassRule()?.ruleId;
    const job       = this.editingClassRuleJob()!;
    const jobTypeId = this.getJobTypeId(job);

    const req$: Observable<any> = ruleId
      ? this.svc.updateClassificationRule(ruleId, {
          jobTypeId,
          errorTypeId: this.classRuleForm.errorTypeId,
          pattern    : this.classRuleForm.pattern,
          confidence : this.classRuleForm.confidence,
          priority   : this.classRuleForm.priority,
          isActive   : this.classRuleForm.isActive,
        } as UpsertClassificationRuleRequest)
      : this.svc.createJobClassificationRule(job.monitoredJobId, this.classRuleForm);

    req$.subscribe({
      next: () => { this.closeDrawer(); this.reload(); },
      error: () => this.saving.set(false),
    });
  }

  deleteClassRule(job: MonitoredJob, rule: RuleOverride) {
    if (!confirm(`Remove pattern "${rule.pattern}" from this job?`)) return;
    this.svc.deleteJobClassificationRule(job.monitoredJobId, rule.ruleId)
      .subscribe({ next: () => this.reload() });
  }

  openLinkClassRuleDrawer(job: MonitoredJob) {
    this.editingClassRuleJob.set(job);
    this.linkRuleSearch.set('');
    this.loadingLinkRules.set(true);
    this.activeDrawer.set('link-class-rule');
    this.svc.getAllClassificationRules().subscribe({
      next: rules => { this.allClassRules.set(rules); this.loadingLinkRules.set(false); },
      error: ()    => this.loadingLinkRules.set(false),
    });
  }

  confirmLinkRule(rule: ClassificationRule) {
    const job = this.editingClassRuleJob();
    if (!job) return;
    this.saving.set(true);
    this.svc.linkJobClassificationRule(job.monitoredJobId, rule.ruleId).subscribe({
      next: () => { this.closeDrawer(); this.reload(); },
      error: () => this.saving.set(false),
    });
  }

  // ── Fix Policy Rule CRUD ───────────────────────────────────────────────────

  openFixRuleDrawer(job: MonitoredJob, rule: FixPolicyRule | null) {
    this.editingFixRuleJob.set(job);
    this.editingFixRule.set(rule);
    this.shortcutRuleId = null;   // fresh shortcut state per drawer open
    // Needed for the rule picker / reachability fallback when this job has no
    // linked classification rules (classifier then uses JobType globals).
    if (this.allClassRules().length === 0) {
      this.svc.getAllClassificationRules().subscribe({ next: r => this.allClassRules.set(r) });
    }
    this.fixRuleSaveConflict.set(null);  // clear stale 409 from a prior session
    this.fixRuleSaveError.set(null);     // and any stale 400 message too
    if (rule) {
      this.fixRuleForm = {
        jobTypeId         : rule.jobTypeId,
        errorTypeId       : rule.errorTypeId,
        monitoredJobId    : rule.monitoredJobId,        // preserve scope
        actionToApply     : rule.actionToApply,
        fixCategory       : rule.fixCategory,
        actionType        : rule.actionType,
        actionPayload     : rule.actionPayload,
        isAutoHealEligible: rule.isAutoHealEligible,
        enabled           : rule.enabled,
        // Steps round-trip via the list endpoint (now includes them) — clone
        // so the editor's add/remove/reorder doesn't mutate the source.
        steps             : (rule.steps ?? []).map(s => ({
          stepOrder:     s.stepOrder,
          actionType:    s.actionType,
          actionPayload: s.actionPayload,
          description:   s.description,
        })),
      };
    } else {
      // New rule defaults to THIS job (per-job override) — the common case:
      // each configured job typically has its own classification + fix.
      // Operator can widen to a JobType-wide default via the inline scope
      // link in the drawer for the rare "all jobs of this type" case.
      this.fixRuleForm = {
        ...this.blankFixRule(),
        jobTypeId      : this.getJobTypeId(job),
        monitoredJobId : job.monitoredJobId,
      };
    }
    // Seed the form-mirror signal so the inline-warning computed evaluates
    // correctly on first open (without requiring the operator to touch a field).
    this.fixRuleFormSignal.set({ ...this.fixRuleForm });
    this.activeDrawer.set('fix-rule');
  }

  saveFixRule() {
    if (!this.fixRuleForm.actionToApply || !this.fixRuleForm.errorTypeId) return;
    this.saving.set(true);
    this.fixRuleSaveConflict.set(null);
    this.fixRuleSaveError.set(null);
    const id    = this.editingFixRule()?.ruleId;
    const req$: Observable<any> = id
      ? this.svc.updateFixPolicyRule(id, this.fixRuleForm)
      : this.svc.createFixPolicyRule(this.fixRuleForm);
    req$.subscribe({
      next: () => {
        this.closeDrawer();
        const hostJob = this.editingFixRuleJob()!;
        const jt = this.getJobTypeId(hostJob);
        if (jt) this.loadFixPolicies(jt, hostJob.monitoredJobId);
        this.saving.set(false);
      },
      error: (err) => {
        // 409 DuplicateFixPolicy surfaces via fixRuleSaveConflict — has its
        // own "Open existing policy" affordance.
        // 400 validation errors (composite shape, missing payload, etc.) get
        // surfaced as plain text in the footer so the operator sees the WHY.
        // Anything else (500, network) gets a generic fallback.
        const body = err?.error;
        if (err?.status === 409 && body?.error === 'DuplicateFixPolicy' && body?.conflictingPolicyId) {
          this.fixRuleSaveConflict.set({
            message: body.message ?? 'A duplicate active policy exists.',
            conflictingPolicyId: body.conflictingPolicyId,
          });
        } else if (err?.status === 400 && body?.message) {
          this.fixRuleSaveError.set(body.message);
        } else if (err?.status === 400 && typeof body === 'string') {
          this.fixRuleSaveError.set(body);
        } else {
          this.fixRuleSaveError.set(
            err?.message || 'Save failed. Check the server logs and try again.');
        }
        this.saving.set(false);
      },
    });
  }

  /** Action-type switch handler. Clears the inappropriate-for-the-new-type
   *  field so a save doesn't trip backend validation with stale data:
   *    - Switching TO Composite     → null out actionPayload (header has none).
   *    - Switching FROM Composite   → empty out steps (single-action rules
   *                                   can't carry steps; backend rejects with
   *                                   NonCompositeWithSteps).
   *    - Switching FROM Manual      → leave payload alone (operator may want
   *                                   to type one in next).
   *  The form-mirror signal sync keeps the inline duplicate warning current. */
  setFixRuleActionType(next: string) {
    const prev = this.fixRuleForm.actionType;
    this.fixRuleForm.actionType = next;
    if (next === 'Composite') {
      this.fixRuleForm.actionPayload = null;
    } else if (prev === 'Composite') {
      this.fixRuleForm.steps = [];
    }
    this.fixRuleSaveError.set(null);
    this.syncFixRuleSignal();
  }

  /** Sync the form-mirror signal whenever a field that affects duplicate
   *  detection (errorTypeId / enabled / monitoredJobId) changes. JobTypeId
   *  is locked per drawer so doesn't need a separate hook; openFixRuleDrawer
   *  captures it in the initial seed. */
  syncFixRuleSignal() {
    this.fixRuleFormSignal.set({ ...this.fixRuleForm });
  }

  /** Rule-picker shortcut: set the form's ErrorType from the chosen
   *  classification rule (fixes are keyed to ErrorType, not the rule). */
  pickClassificationRuleById(ruleId: number | null) {
    if (ruleId == null) return;
    const cr = this.effectiveClassRules().find(r => r.ruleId === ruleId);
    const et = cr ? this.errorTypes().find(e => e.code === cr.errorTypeCode) : null;
    if (et) {
      this.shortcutRuleId = ruleId;   // keep the picked rule visible in the shortcut select
      this.fixRuleForm.errorTypeId = et.errorTypeId;
      this.syncFixRuleSignal();
    }
  }

  /** A JobType default (MonitoredJobId null) is shadowed when an enabled
   *  per-job override for the same ErrorType exists in the effective list —
   *  the override is what actually runs. */
  isShadowedDefault(r: FixPolicyRule): boolean {
    if (r.monitoredJobId !== null) return false;
    return this.fixPolicies().some(p =>
      p.monitoredJobId !== null && p.enabled && p.errorTypeCode === r.errorTypeCode);
  }

  /** Scope radio handler — flips fixRuleForm.monitoredJobId between
   *  null (default layer) and the host MonitoredJobId (override layer)
   *  and re-syncs the form-mirror signal so the duplicate-detection
   *  computed re-runs against the right layer immediately. */
  setFixRuleScope(monitoredJobId: number | null) {
    this.fixRuleForm.monitoredJobId = monitoredJobId;
    // Also clear any stale 409 from a prior layer's save attempt.
    this.fixRuleSaveConflict.set(null);
    this.syncFixRuleSignal();
  }

  /** "Edit existing policy instead?" — close the current drawer and re-open
   *  it loaded with the conflicting policy. Same drawer, same job context,
   *  just swapped to edit mode. */
  openConflictingPolicy(conflict: FixPolicyRule) {
    const job = this.editingFixRuleJob();
    if (!job) return;
    this.openFixRuleDrawer(job, conflict);
  }

  /** Same as openConflictingPolicy but resolves by id — used by the post-
   *  save 409 banner which only carries the conflictingPolicyId from the
   *  backend response. */
  openConflictingPolicyById(id: number) {
    const job  = this.editingFixRuleJob();
    const rule = this.fixPolicies().find(p => p.ruleId === id);
    if (job && rule) this.openFixRuleDrawer(job, rule);
  }

  deleteFixRule(job: MonitoredJob, rule: FixPolicyRule) {
    if (!confirm(`Delete fix option for "${rule.errorTypeCode}"?`)) return;
    this.svc.deleteFixPolicyRule(rule.ruleId).subscribe({
      next: () => {
        const jt = this.getJobTypeId(job);
        if (jt) this.loadFixPolicies(jt, job.monitoredJobId);
      },
    });
  }

  // ── Shared ─────────────────────────────────────────────────────────────────

  closeDrawer() { this.activeDrawer.set(null); this.saving.set(false); }

  private reload() {
    this.svc.getAllJobs().subscribe({ next: j => { this.jobs.set(j); this.saving.set(false); } });
  }

  private blankJob(): UpsertJobRequest {
    return { name: '', displayName: null, jobTypeId: 0, scanTypeId: 1, logFolder: null,
             searchPatterns: null, inputFolder: null, connectionName: null, logSourceUrl: null,
             pollingIntervalSeconds: 300, isActive: true, description: null };
  }
  private blankScanRule(scanTypeId = 2): UpsertScanRuleRequest & { isActive: boolean } {
    const checkType = scanTypeId === 1 ? 'ErrorKeyword' : 'ValueEquals';
    return { checkType, sourceTable: null, targetField: '', minValue: null,
             maxValue: null, expectedValue: null, watermarkColumn: null, sourceIdColumn: null,
             filePathColumn: null, inputPathPattern: null,
             severity: 'Medium', description: null, isActive: true };
  }
  private blankClassRule(): UpsertJobClassificationRuleRequest {
    return { errorTypeId: 0, pattern: '', confidence: 0.9, priority: 1, isActive: true };
  }
  private blankFixRule(): UpsertFixPolicyRuleRequest {
    // monitoredJobId starts null (default scope). The drawer's scope radio
    // lets the operator flip it to a specific MonitoredJobId when needed.
    return { jobTypeId: 0, errorTypeId: 0, monitoredJobId: null,
             actionToApply: '', fixCategory: 'Retry',
             actionType: 'Manual', actionPayload: null, isAutoHealEligible: false, enabled: true,
             steps: [] };
  }

  // ── Composite step editor helpers ──────────────────────────────────────────

  /** Add a new blank step to the composite editor — defaults to SqlScript
   *  since that's the most common building block. Operator changes the type
   *  from the row's dropdown. Order is auto-assigned (length + 1). */
  addStep() {
    const steps = this.fixRuleForm.steps ?? [];
    steps.push({
      stepOrder:     steps.length + 1,
      actionType:    'SqlScript',
      actionPayload: '',
      description:   null,
    });
    this.fixRuleForm.steps = steps;
    this.syncFixRuleSignal();
  }

  removeStep(index: number) {
    const steps = this.fixRuleForm.steps ?? [];
    steps.splice(index, 1);
    // Re-pack orders 1..N so the visible numbering stays contiguous —
    // backend re-packs too on save but the editor should reflect it now.
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

  /** Per-step ActionType payload examples — mirror the single-action drawer's
   *  per-type placeholders so operators see the same examples in both spots. */
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
}
