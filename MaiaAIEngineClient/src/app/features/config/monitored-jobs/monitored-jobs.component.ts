import { Component, OnInit, inject, signal, computed } from '@angular/core';
import { Observable } from 'rxjs';
import { FormsModule } from '@angular/forms';
import {
  ConfigService, JobType, ErrorType, FixPolicyRule, ClassificationRule,
  UpsertJobRequest, UpsertScanRuleRequest,
  UpsertJobClassificationRuleRequest, UpsertFixPolicyRuleRequest,
  UpsertClassificationRuleRequest,
} from '../../../core/services/config.service';
import { MonitoredJob, ScanCheckRule, RuleOverride } from '../../../core/models';

type ActiveTab    = 'scan' | 'class' | 'fix';
type ActiveDrawer = 'job' | 'scan-rule' | 'class-rule' | 'link-class-rule' | 'fix-rule' | null;

const SCAN_TYPES     = [{ id: 1, name: 'FileSystem' }, { id: 2, name: 'Database' }, { id: 3, name: 'ApiEndpoint' }];
const DB_CHECK_TYPES = ['ColumnRange', 'ValueEquals'];
const SEVERITIES     = ['Low', 'Medium', 'High', 'Critical'];
const FIX_CATEGORIES = ['Retry', 'FileRepair', 'DbFix', 'Manual'];
const ACTION_TYPES   = ['Manual', 'ApiCall', 'StoredProcedure', 'Script', 'SqlScript'];

@Component({
  selector: 'app-monitored-jobs',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Monitored Jobs</h1>
          <p class="text-muted text-sm">{{ jobs().length }} job(s) configured</p>
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
                      @if (j.rules.length === 0) {
                        <div class="empty-tab">
                          <span>🏷️</span>
                          <p>No classification rules linked — add one to enable error type detection.</p>
                        </div>
                      } @else {
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
                                <td><span class="badge badge-classified">{{ r.errorTypeCode }}</span></td>
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
      <div class="drawer">
        <div class="drawer-header">
          <h3>{{ editingFixRule() ? 'Edit Fix Option' : 'New Fix Option' }}</h3>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="closeDrawer()">✕</button>
        </div>
        <div class="drawer-body">
          <div class="drawer-context-banner">
            <span>⚙️</span>
            <span>
              Fix policy for <strong>{{ editingFixRuleJob()?.jobTypeName }}</strong> jobs —
              applies to all jobs of this type
            </span>
          </div>
          <div class="form-grid">
            <div class="form-group span2">
              <label>Error Type *</label>
              <select [(ngModel)]="fixRuleForm.errorTypeId">
                <option [ngValue]="0" disabled>Select error type…</option>
                @for (et of errorTypes(); track et.errorTypeId) {
                  <option [ngValue]="et.errorTypeId">
                    {{ et.code }} — {{ et.displayName }}
                  </option>
                }
              </select>
            </div>
            <div class="form-group span2">
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
              <select [(ngModel)]="fixRuleForm.actionType">
                @for (a of actionTypes; track a) { <option [ngValue]="a">{{ a }}</option> }
              </select>
            </div>
            @if (fixRuleForm.actionType !== 'Manual') {
              <div class="form-group span2">
                <label>Action Payload</label>
                @if (fixRuleForm.actionType === 'ApiCall') {
                  <input [(ngModel)]="fixRuleForm.actionPayload"
                         placeholder="http://jobs.internal/api/jobs/{failureId}/retry" />
                  <span class="field-hint">Use {{'{'}}failureId{{'}'}} as a placeholder — replaced at runtime.</span>
                } @else if (fixRuleForm.actionType === 'StoredProcedure') {
                  <input [(ngModel)]="fixRuleForm.actionPayload"
                         placeholder="dbo.sp_RetryJob  or  ConnName|dbo.sp_RetryJob" />
                } @else if (fixRuleForm.actionType === 'SqlScript') {
                  <textarea [(ngModel)]="fixRuleForm.actionPayload" rows="4"
                            placeholder="UPDATE dbo.Files SET StatusCode = 0 WHERE Id = {failureId}"></textarea>
                  <span class="field-hint">Raw SQL executed against the default DB. Use {{'{'}}failureId{{'}'}} as a placeholder.</span>
                } @else {
                  <input [(ngModel)]="fixRuleForm.actionPayload"
                         placeholder="powershell.exe C:\scripts\fix.ps1 {failureId}" />
                }
              </div>
            }
            <div class="form-group" style="padding-top:14px">
              <label>Auto-Heal</label>
              <label class="toggle" style="margin-top:6px">
                <input type="checkbox" [(ngModel)]="fixRuleForm.isAutoHealEligible" />
                <span class="slider"></span>
              </label>
            </div>
            <div class="form-group" style="padding-top:14px">
              <label>Enabled</label>
              <label class="toggle" style="margin-top:6px">
                <input type="checkbox" [(ngModel)]="fixRuleForm.enabled" />
                <span class="slider"></span>
              </label>
            </div>
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

  readonly scanTypes     = SCAN_TYPES;
  readonly dbCheckTypes  = DB_CHECK_TYPES;
  readonly severities    = SEVERITIES;
  readonly fixCategories = FIX_CATEGORIES;
  readonly actionTypes   = ACTION_TYPES;

  jobForm      : UpsertJobRequest                              = this.blankJob();
  ruleForm     : UpsertScanRuleRequest & { isActive: boolean } = this.blankScanRule();
  classRuleForm: UpsertJobClassificationRuleRequest            = this.blankClassRule();
  fixRuleForm  : UpsertFixPolicyRuleRequest                    = this.blankFixRule();

  ngOnInit() {
    this.loading.set(true);
    this.svc.getAllJobs().subscribe({
      next: j => { this.jobs.set(j); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
    this.svc.getJobTypes().subscribe({ next: t => this.jobTypes.set(t) });
    this.svc.getErrorTypes().subscribe({ next: t => this.errorTypes.set(t) });
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
      if (jt) this.loadFixPolicies(jt);
    }
  }

  getJobTypeId(job: MonitoredJob): number {
    return this.jobTypes().find(t => t.name === job.jobTypeName)?.jobTypeId ?? 0;
  }

  scanIcon(id: number): string {
    return ({ 1: '📁', 2: '🗄', 3: '🌐' } as Record<number, string>)[id] ?? '📋';
  }

  private loadFixPolicies(jobTypeId: number) {
    this.loadingFix.set(true);
    this.svc.getFixPolicyRules(jobTypeId).subscribe({
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
    if (!confirm(`Deactivate job "${job.name}"?`)) return;
    this.svc.deleteJob(job.monitoredJobId).subscribe({ next: () => this.reload() });
  }

  // ── Scan Rule CRUD ─────────────────────────────────────────────────────────

  openScanRuleDrawer(job: MonitoredJob, rule: ScanCheckRule | null) {
    this.editingRuleJob.set(job);
    this.editingRule.set(rule);
    this.ruleForm = rule ? {
      checkType: rule.checkType, sourceTable: rule.sourceTable, targetField: rule.targetField,
      minValue: rule.minValue, maxValue: rule.maxValue, expectedValue: rule.expectedValue,
      watermarkColumn: rule.watermarkColumn, sourceIdColumn: rule.sourceIdColumn,
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
    if (rule) {
      this.fixRuleForm = {
        jobTypeId         : rule.jobTypeId,
        errorTypeId       : rule.errorTypeId,
        actionToApply     : rule.actionToApply,
        fixCategory       : rule.fixCategory,
        actionType        : rule.actionType,
        actionPayload     : rule.actionPayload,
        isAutoHealEligible: rule.isAutoHealEligible,
        enabled           : rule.enabled,
      };
    } else {
      this.fixRuleForm = { ...this.blankFixRule(), jobTypeId: this.getJobTypeId(job) };
    }
    this.activeDrawer.set('fix-rule');
  }

  saveFixRule() {
    if (!this.fixRuleForm.actionToApply || !this.fixRuleForm.errorTypeId) return;
    this.saving.set(true);
    const id    = this.editingFixRule()?.ruleId;
    const req$: Observable<any> = id
      ? this.svc.updateFixPolicyRule(id, this.fixRuleForm)
      : this.svc.createFixPolicyRule(this.fixRuleForm);
    req$.subscribe({
      next: () => {
        this.closeDrawer();
        const jt = this.getJobTypeId(this.editingFixRuleJob()!);
        if (jt) this.loadFixPolicies(jt);
        this.saving.set(false);
      },
      error: () => this.saving.set(false),
    });
  }

  deleteFixRule(job: MonitoredJob, rule: FixPolicyRule) {
    if (!confirm(`Delete fix option for "${rule.errorTypeCode}"?`)) return;
    this.svc.deleteFixPolicyRule(rule.ruleId).subscribe({
      next: () => {
        const jt = this.getJobTypeId(job);
        if (jt) this.loadFixPolicies(jt);
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
             searchPatterns: null, connectionName: null, logSourceUrl: null,
             pollingIntervalSeconds: 300, isActive: true, description: null };
  }
  private blankScanRule(scanTypeId = 2): UpsertScanRuleRequest & { isActive: boolean } {
    const checkType = scanTypeId === 1 ? 'ErrorKeyword' : 'ValueEquals';
    return { checkType, sourceTable: null, targetField: '', minValue: null,
             maxValue: null, expectedValue: null, watermarkColumn: null, sourceIdColumn: null,
             severity: 'Medium', description: null, isActive: true };
  }
  private blankClassRule(): UpsertJobClassificationRuleRequest {
    return { errorTypeId: 0, pattern: '', confidence: 0.9, priority: 1, isActive: true };
  }
  private blankFixRule(): UpsertFixPolicyRuleRequest {
    return { jobTypeId: 0, errorTypeId: 0, actionToApply: '', fixCategory: 'Retry',
             actionType: 'Manual', actionPayload: null, isAutoHealEligible: false, enabled: true };
  }
}
