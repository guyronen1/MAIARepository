import { Component, OnInit, inject, signal, computed, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  ConfigService, FixPolicyRule, JobType, ErrorType, ClassificationRule,
} from '../../../core/services/config.service';
import { MonitoredJob, ScanSource, ScanCheckRule, RuleOverride } from '../../../core/models';
import { NotificationService } from '../../../core/services/notification.service';
import { EditJobDrawerComponent } from './drawers/edit-job-drawer.component';
import { ScanSourceDrawerComponent } from './drawers/scan-source-drawer.component';
import { ScanRuleDrawerComponent } from './drawers/scan-rule-drawer.component';
import { ClassRuleDrawerComponent } from './drawers/class-rule-drawer.component';
import { FixOptionDrawerComponent } from './drawers/fix-option-drawer.component';
import { computeEffectiveClassRules, scanRulePredictedPattern,
  scanRuleNeedsClassification as coverageNeedsClassification } from '../../../core/util/coverage-match.util';

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
  imports: [RouterLink, FormsModule, EditJobDrawerComponent, ScanSourceDrawerComponent,
            ScanRuleDrawerComponent, ClassRuleDrawerComponent, FixOptionDrawerComponent],
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
                <div class="subsection-label">
                  {{ j.jobTypeName }} defaults
                  <span class="text-muted">(apply to all {{ j.jobTypeName }} jobs)</span>
                  @if (relevantDefaultClassRules().length < jobTypeGlobalRules().length) {
                    <span class="defaults-filter-note">
                      — showing {{ relevantDefaultClassRules().length }} of {{ jobTypeGlobalRules().length }} relevant to your scan rules
                    </span>
                  }
                </div>
                @if (displayedDefaultClassRules().length > 0) {
                  <table class="data-table compact">
                    <thead><tr><th style="width:48%">Pattern</th><th style="width:26%">Error Type</th><th style="width:13%">Conf.</th><th style="width:13%">Pri.</th></tr></thead>
                    <tbody>
                      @for (r of displayedDefaultClassRules(); track r.ruleId) {
                        <tr class="global-row">
                          <td class="font-mono">{{ r.pattern }}</td>
                          <td><span class="badge badge-classified">{{ r.errorTypeCode }}</span></td>
                          <td class="text-sm">{{ (r.confidence * 100).toFixed(0) }}%</td>
                          <td class="text-sm text-muted">#{{ r.priority }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                } @else {
                  <div class="defaults-empty">No {{ j.jobTypeName }} defaults match your current scan rules.</div>
                }
                @if (relevantDefaultClassRules().length < jobTypeGlobalRules().length) {
                  <button class="link-btn defaults-toggle" (click)="showAllDefaults.set(!showAllDefaults())">
                    {{ showAllDefaults() ? 'Show relevant only' : 'Show all ' + jobTypeGlobalRules().length + ' defaults' }}
                  </button>
                }
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
        <app-edit-job-drawer [jobTypes]="jobTypes()" [jobId]="jobId" (saved)="reload()" />

        <!-- ── Editor drawers (extracted to child components) ───────────────── -->
        <app-scan-source-drawer [jobId]="jobId" (saved)="reload()" />
        <app-scan-rule-drawer (saved)="reload()" />
        <app-class-rule-drawer [job]="j" [jobTypeId]="getJobTypeId(j)" [errorTypes]="errorTypes()"
                               (saved)="reload()" (allClassRulesRefreshed)="allClassRules.set($event)" />
        <app-fix-option-drawer [job]="j" [jobTypeId]="getJobTypeId(j)" [errorTypes]="errorTypes()"
                               [fixPolicies]="fixPolicies()" [effectiveClassRules]="effectiveClassRules()"
                               (saved)="reload()" />
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
    .soft-warn { padding: 7px 10px; border-radius: var(--radius-sm); background: var(--warn-bg); border: 1px solid var(--warn-border); color: var(--warn-text); font-size: 12px; }
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
    .form-group input[readonly] { background: var(--surface-2, #f8fafc); color: var(--text-muted); cursor: default; border-style: dashed; }
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
    .defaults-filter-note { font-weight: 400; text-transform: none; letter-spacing: 0; }
    .defaults-empty { font-size: 12px; color: var(--text-dim); padding: 6px 0; }
    .defaults-toggle { font-size: 11px; margin-top: 6px; display: block; }
    .global-row td { opacity: 0.82; }
    .data-table.compact tr.shadowed td { opacity: 0.6; }
    .badge-muted { background: var(--surface-3); color: var(--text-muted); border: 1px solid var(--border); }

    /* Fix Option drawer */
    .drawer-context-banner { display: flex; align-items: center; gap: 8px; background: var(--primary-light); border: 1px solid var(--primary); border-radius: var(--radius-sm); padding: 8px 12px; font-size: 12px; color: var(--primary-dark); margin-bottom: 14px; }
    .banner-icon { display: inline-flex; align-items: center; justify-content: center; width: 18px; height: 18px; flex-shrink: 0; border-radius: 50%; background: var(--primary); color: #fff; font-style: italic; font-weight: 700; font-size: 12px; font-family: Georgia, serif; }
    .auto-heal-banner { display: flex; align-items: flex-start; gap: 8px; background: var(--warn-bg); border: 1px solid var(--warn-border); border-radius: var(--radius-sm); padding: 10px 12px; font-size: 12px; color: var(--warn-text); line-height: 1.5; margin-top: 12px; }
    .auto-heal-banner span:first-child { font-size: 16px; }
    .scope-line { display: flex; align-items: baseline; flex-wrap: wrap; gap: 4px 10px; font-size: 12px; color: var(--text-muted); }
    .scope-current strong { color: var(--text); }
    .link-btn { background: transparent; border: none; padding: 0; color: var(--primary); font-weight: 600; cursor: pointer; text-decoration: underline; font-size: inherit; }
    .link-btn:hover { color: var(--primary-dark); }
    .toggles-row { display: flex; gap: 24px; align-items: center; padding-top: 4px; }
    .toggle-pair { display: inline-flex; align-items: center; gap: 8px; cursor: pointer; }
    .toggle-pair .toggle-text { font-size: 13px; color: var(--text); }
    .covers-hint { display: block; }
    .dup-warn { display: block; margin-top: 6px; padding: 8px 10px; border-radius: var(--radius-sm); background: var(--warn-bg-2); border: 1px solid var(--warn-border); font-size: 12px; color: var(--warn-text); line-height: 1.4; }
    .dup-warn .link-btn { color: var(--warn-text); font-weight: 600; text-decoration: underline; margin-left: 4px; }
    .dup-warn .link-btn:hover { color: var(--warn-strong); }
    .dup-warn.save-error { background: var(--danger-bg); border-color: var(--danger); color: var(--danger); margin-top: 12px; }
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
      color: var(--warn-text); background: var(--warn-bg-2); border: 1px solid var(--warn-strong);
      border-radius: 4px; padding: 2px 7px;
      font-size: 12px; font-weight: 600; margin-left: 6px;
      line-height: 1.4; white-space: nowrap;
    }
    .gap-warn-btn { cursor: pointer; font-family: inherit; }
    .gap-warn-btn:hover { background: var(--warn-border); border-color: var(--warn-strong); }
    /* Rollup chip on a collapsed source header — matches the inline pill. */
    .collapsed-gap { display: inline-flex; align-items: center; gap: 3px;
                     color: var(--warn-text); background: var(--warn-bg-2); border: 1px solid var(--warn-strong);
                     border-radius: 4px; padding: 2px 8px; font-size: 12px; font-weight: 600;
                     white-space: nowrap; margin-left: 6px; }

    /* Per-source collapse chevron — only rendered for multi-source jobs. */
    .collapse-btn { background: none; border: none; padding: 0 4px 0 0; cursor: pointer;
                    color: var(--text-muted); font-size: 11px; flex-shrink: 0; line-height: 1; }
    .collapse-btn:hover { color: var(--text); }
    /* Secondary entity name in drawer headers — slightly muted, normal weight,
       so the drawer type label reads as the primary identifier. */
    .drawer-title-sub { font-weight: 400; color: var(--text-muted); font-size: 13px; }
  `]
})
export class JobConfigComponent implements OnInit {
  private svc    = inject(ConfigService);
  private route  = inject(ActivatedRoute);
  private router = inject(Router);
  private notify = inject(NotificationService);

  job           = signal<MonitoredJob | null>(null);
  fixPolicies   = signal<FixPolicyRule[]>([]);
  jobTypes      = signal<JobType[]>([]);
  errorTypes    = signal<ErrorType[]>([]);
  allClassRules = signal<ClassificationRule[]>([]);
  allJobs       = signal<MonitoredJob[]>([]);
  loading       = signal(true);

  // The five editor drawers were extracted to child components; the parent opens
  // each via its viewChild ref and reloads on the child's (saved) output.
  private editJobDrawer    = viewChild(EditJobDrawerComponent);
  private scanSourceDrawer = viewChild(ScanSourceDrawerComponent);
  private scanRuleDrawer   = viewChild(ScanRuleDrawerComponent);
  private classRuleDrawer  = viewChild(ClassRuleDrawerComponent);
  private fixOptionDrawer  = viewChild(FixOptionDrawerComponent);

  /** Effective classifier rules for this job: linked rules ∪ JobType-global
   *  defaults (rules of this JobType linked to no job). Mirrors GetEffectiveRulesAsync.
   *  Shared with the read-only flow view via coverage-match.util, and passed into
   *  the fix-option drawer (its reachability/shortcut logic). */
  effectiveClassRules = computed<{ ruleId: number; pattern: string; errorTypeCode: string }[]>(() => {
    const job = this.job();
    if (!job) return [];
    return computeEffectiveClassRules(job, this.allJobs(), this.allClassRules(), this.getJobTypeId(job));
  });

  /** JobType-global rules that ALSO classify this job (active, same JobType,
   *  linked to no job) — shown read-only under the job's linked rules. */
  jobTypeGlobalRules = computed<ClassificationRule[]>(() => {
    const job = this.job();
    if (!job) return [];
    const jt = this.getJobTypeId(job);
    const linkedToThisJob = new Set((job.rules ?? []).map(r => r.ruleId));
    const linkedAnywhere = new Set(this.allJobs().flatMap(j => j.rules.map(r => r.ruleId)));
    return this.allClassRules()
      .filter(r => r.jobTypeId === jt && r.isActive && !linkedToThisJob.has(r.ruleId) && !linkedAnywhere.has(r.ruleId))
      .sort((a, b) => a.priority - b.priority);
  });

  /** Subset of jobTypeGlobalRules whose pattern overlaps with at least one scan
   *  rule on this job — irrelevant defaults are hidden by default. Both sides are
   *  compared after stripping wildcards; Field=Value patterns require exact match. */
  relevantDefaultClassRules = computed<ClassificationRule[]>(() => {
    const allScanRules = (this.job()?.sources ?? []).flatMap(s => s.scanCheckRules);
    return this.jobTypeGlobalRules().filter(cr => {
      const crLiteral = cr.pattern.replace(/\*/g, '').trim().toLowerCase();
      if (!crLiteral) return true; // wildcard-only pattern matches anything
      return allScanRules.some(rule => {
        const keyword = this.classPatternForScanRule(rule).toLowerCase();
        if (!keyword) return false;
        if (keyword.includes('=') && crLiteral.includes('=')) return keyword === crLiteral;
        return keyword.includes(crLiteral) || crLiteral.includes(keyword);
      });
    });
  });

  showAllDefaults = signal(false);

  displayedDefaultClassRules = computed<ClassificationRule[]>(() =>
    this.showAllDefaults() ? this.jobTypeGlobalRules() : this.relevantDefaultClassRules()
  );

  // Public (not private) so the template can bind it to child drawers, e.g.
  // <app-edit-job-drawer [jobId]="jobId">.
  jobId = 0;

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

  /** (Re)load the job + its fix policies. Called on init, after any edit, and
   *  from child drawers' (saved) outputs — so it can't be private. */
  reload(initial = false) {
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
          if (initial) this.applyFixDeepLink();
        });
      },
      error: () => { this.loading.set(false); this.notify.error('Could not load this job\'s configuration.'); },
    });
  }

  /** Case-B deep-link from /unconfigured: ?errorTypeId=<id> pops a pre-filled
   *  new-fix drawer (per-job scope + that ErrorType). Deferred one tick so the
   *  drawer child (inside @if (job())) is rendered and its viewChild ref resolved.
   *  Param cleared afterward so a refresh/back doesn't re-trigger. */
  private applyFixDeepLink() {
    const etId = Number(this.route.snapshot.queryParamMap.get('errorTypeId') ?? 0);
    if (!etId) return;
    setTimeout(() => this.fixOptionDrawer()?.openFor(null, { errorTypeId: etId }));
    this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }

  openEditJob(j: MonitoredJob) { this.editJobDrawer()?.open(j); }

  // ── Scan Source ───────────────────────────────────────────────────────────
  openSourceDrawer(s: ScanSource | null) { this.scanSourceDrawer()?.open(s); }

  deleteSource(s: ScanSource) {
    if (!confirm(`Delete source "${s.name}"? Its scan rules will be deactivated.`)) return;
    this.svc.deleteScanSource(s.scanSourceId).subscribe({ next: () => this.reload() });
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

  // ── Scan Rule ─────────────────────────────────────────────────────────────
  openRuleDrawer(source: ScanSource, rule: ScanCheckRule | null) { this.scanRuleDrawer()?.open(source, rule); }

  deleteRule(rule: ScanCheckRule) {
    if (!confirm(`Delete scan rule "${rule.targetField}"?`)) return;
    this.svc.deleteScanRule(rule.checkRuleId).subscribe({ next: () => this.reload() });
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

  // ── Classification Rule ───────────────────────────────────────────────────
  openClassDrawer(rule: RuleOverride | null) {
    if (rule) this.classRuleDrawer()?.openEdit(rule);
    else      this.classRuleDrawer()?.openNew();
  }

  openLinkDrawer() { this.classRuleDrawer()?.openLink(); }

  deleteClassRule(rule: RuleOverride) {
    if (!confirm(`Remove pattern "${rule.pattern}" from this job?`)) return;
    this.svc.deleteJobClassificationRule(this.job()!.monitoredJobId, rule.ruleId)
      .subscribe({ next: () => this.reload() });
  }

  // ── Fix Option ────────────────────────────────────────────────────────────
  openFixRuleDrawer(rule: FixPolicyRule | null) { this.fixOptionDrawer()?.openFor(rule); }

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

  scanRuleNeedsClassification(rule: ScanCheckRule): boolean {
    // Delegates to the shared coverage matcher so this marker and the flow view
    // can never disagree (gap === matcher state 'gap').
    return coverageNeedsClassification(rule, this.effectiveClassRules());
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

  /** ⚠ on fix policy rows: no effective class rule produces this errorTypeCode, so
   *  the fix won't trigger automatically. */
  fixHasNoClassCoverage(policy: FixPolicyRule): boolean {
    return !this.effectiveClassRules().some(r => r.errorTypeCode === policy.errorTypeCode);
  }

  // ── Gap-marker click-throughs (open the relevant extracted drawer, pre-filled) ──

  /** Scan-rule ⚠ click: open the classification drawer pre-filled with a pattern
   *  derived from what the rule's ErrorMessage will look like. */
  openClassDrawerForScanRule(rule: ScanCheckRule): void {
    this.classRuleDrawer()?.openNew({ pattern: this.classPatternForScanRule(rule) });
  }

  private classPatternForScanRule(rule: ScanCheckRule): string {
    return scanRulePredictedPattern(rule);   // shared with the flow view
  }

  /** Classification-rule ⚠ click: open the fix drawer pre-filled with the error
   *  type that has no fix option, scoped to this job (override). */
  openFixRuleDrawerForClassRule(rule: RuleOverride): void {
    const et = this.errorTypes().find(e => e.code === rule.errorTypeCode);
    this.fixOptionDrawer()?.openFor(null, et ? { errorTypeId: et.errorTypeId } : undefined);
  }

  /** Fix-options ⚠ click: open the classification drawer pre-filled with the error
   *  type that has no class rule coverage. */
  openClassDrawerForFixGap(policy: FixPolicyRule): void {
    const et = this.errorTypes().find(e => e.code === policy.errorTypeCode);
    this.classRuleDrawer()?.openNew({ errorTypeId: et?.errorTypeId ?? 0 });
  }

  // ── Per-source collapse (only for jobs with 2+ sources) ──────────────────
  // Default: all sources expanded. Single-source jobs: no collapse affordance.
  private _collapsedSources = signal<Set<number>>(new Set<number>());

  isSourceCollapsed(sourceId: number): boolean {
    return this._collapsedSources().has(sourceId);
  }

  toggleSource(sourceId: number): void {
    const next = new Set(this._collapsedSources());
    next.has(sourceId) ? next.delete(sourceId) : next.add(sourceId);
    this._collapsedSources.set(next);
  }
}
