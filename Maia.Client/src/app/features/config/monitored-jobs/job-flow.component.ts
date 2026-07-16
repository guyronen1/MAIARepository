import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { forkJoin, of } from 'rxjs';
import { map, switchMap } from 'rxjs/operators';
import { ConfigService, ClassificationRule, FixPolicyRule } from '../../../core/services/config.service';
import { MonitoredJob, ScanSource, ScanCheckRule } from '../../../core/models';
import {
  EffectiveClassRule, ClassificationLinkState,
  computeEffectiveClassRules, matchScanRuleClassification, effectiveFixForErrorType,
} from '../../../core/util/coverage-match.util';

/** One detection path: a scan rule traced left-to-right through the pipeline. */
interface FlowRow {
  // detect
  sourceIcon:  string;
  sourceLabel: string;
  checkType:   string;
  target:      string;
  detail:      string;
  // classify
  classifyState: ClassificationLinkState;
  errorTypeCode: string | null;
  classPattern:  string | null;
  extraMatches:  number;
  // recommend + fix (both derived from the effective FixPolicyRule)
  fix: FixPolicyRule | null;
  /** complete | gap-classification (dead path) | gap-fix | not-determinable */
  rowKind: 'complete' | 'gap-classification' | 'gap-fix' | 'not-determinable';
}

/**
 * Read-only "flow view" for one monitored job: each scan rule's full
 * detect → classify → recommend → fix path, one row per detection path.
 *
 * A comprehension/audit surface, NOT an editor — no click-through. Reuses the
 * shared coverage-match util so it can never disagree with the config screen's
 * coverage markers. All data comes from existing Operator-gated config reads;
 * there is no dedicated backend endpoint. Detection-order, flat list with the
 * source shown per row. SqlQuery (and broad ErrorKeyword/FileContent) links are
 * shown as "not determinable" rather than guessed.
 */
@Component({
  selector: 'app-job-flow',
  standalone: true,
  template: `
    <div class="flow-panel">
      @if (loading()) {
        <div class="flow-loading"><span class="spinner"></span> Loading flow…</div>
      } @else if (errored()) {
        <div class="flow-empty">Couldn't load the flow for this job.</div>
      } @else {
        <div class="flow-head">
          <span class="flow-title">Process flow</span>
          <span class="text-muted text-sm">read-only · {{ rows().length }} {{ rows().length === 1 ? 'detection path' : 'detection paths' }}</span>
          <span class="flow-legend">
            <span class="lg"><span class="fix-mode auto">⚡</span> auto-heal</span>
            <span class="lg"><span class="fix-mode">✋</span> manual / on approval</span>
            <span class="lg"><span class="undet-chip">❔</span> link not determinable</span>
          </span>
        </div>

        @if (rows().length === 0) {
          <div class="flow-empty">No scan rules to trace on this job yet.</div>
        } @else {
          <div class="flow-scroll">
            <table class="flow-table">
              <thead>
                <tr>
                  <th>Detect</th><th class="arrow-col"></th>
                  <th>Classify</th><th class="arrow-col"></th>
                  <th>Recommend</th><th class="arrow-col"></th>
                  <th>Fix</th>
                </tr>
              </thead>
              <tbody>
                @for (row of rows(); track $index) {
                  <tr [class.row-gap]="row.rowKind === 'gap-classification'" dir="auto">
                    <!-- DETECT -->
                    <td class="cell">
                      <div class="src-line"><span class="src-icon">{{ row.sourceIcon }}</span>{{ row.sourceLabel }}</div>
                      <div><span class="badge badge-info">{{ row.checkType }}</span> <span class="mono">{{ row.target }}</span></div>
                      @if (row.detail) { <div class="detail text-muted">{{ row.detail }}</div> }
                    </td>
                    <td class="arrow-col"><span class="arrow">→</span></td>

                    <!-- CLASSIFY -->
                    <td class="cell">
                      @switch (row.classifyState) {
                        @case ('matched') {
                          <span class="badge badge-classified">{{ row.errorTypeCode }}</span>
                          @if (row.extraMatches > 0) { <span class="text-muted text-sm"> +{{ row.extraMatches }}</span> }
                          @if (row.classPattern) { <div class="detail text-muted mono">{{ row.classPattern }}</div> }
                        }
                        @case ('gap') {
                          <span class="gap-chip">no classification</span>
                        }
                        @case ('not-determinable') {
                          <span class="undet-chip"
                                title="This rule's output is operator-defined (SqlQuery) or a broad keyword (ErrorKeyword / FileContent) — the matching classification can't be computed at config time.">❔ not determinable</span>
                        }
                      }
                    </td>
                    <td class="arrow-col"><span class="arrow" [class.dim]="row.rowKind !== 'complete'">→</span></td>

                    <!-- RECOMMEND (the fix intent / category) -->
                    <td class="cell">
                      @if (row.fix) { <span class="badge badge-muted">{{ row.fix.fixCategory }}</span> }
                      @else { <span class="text-muted">—</span> }
                    </td>
                    <td class="arrow-col"><span class="arrow" [class.dim]="row.rowKind !== 'complete'">→</span></td>

                    <!-- FIX (the action + effective auto-heal/manual mode) -->
                    <td class="cell">
                      @if (row.fix) {
                        <span class="fix-mode" [class.auto]="row.fix.isAutoHealEligible"
                              [title]="fixModeTitle(row.fix)">{{ fixModeIcon(row.fix) }} {{ fixModeLabel(row.fix) }}</span>
                        <div class="detail text-muted">{{ row.fix.actionType }}@if (row.fix.actionType === 'Composite') { · {{ row.fix.steps.length }} steps }</div>
                      } @else if (row.rowKind === 'gap-fix') {
                        <span class="gap-chip">no fix option</span>
                      } @else {
                        <span class="text-muted">—</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      }
    </div>
  `,
  styles: [`
    .flow-panel { border-top: 1px solid var(--border); background: var(--surface-2); padding: 12px 16px 14px; }
    .flow-loading, .flow-empty { color: var(--text-muted); font-size: 13px; padding: 6px 0; }

    .flow-head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 10px; }
    .flow-title { font-weight: 600; font-size: 13px; }
    .flow-legend { display: flex; gap: 14px; margin-left: auto; flex-wrap: wrap; }
    .flow-legend .lg { display: inline-flex; align-items: center; gap: 4px; font-size: 11px; color: var(--text-muted); }

    .flow-scroll { overflow-x: auto; }
    .flow-table { width: 100%; border-collapse: collapse; font-size: 12.5px; }
    .flow-table th { text-align: left; font-size: 11px; text-transform: uppercase; letter-spacing: .03em;
                     color: var(--text-muted); font-weight: 600; padding: 4px 10px; border-bottom: 1px solid var(--border); }
    .flow-table td.cell { padding: 8px 10px; border-bottom: 1px solid var(--border-light); vertical-align: top; min-width: 130px; }
    .flow-table tr.row-gap td { background: var(--warn-bg-2); }

    .arrow-col { width: 22px; text-align: center; padding: 0; border-bottom: 1px solid var(--border-light); }
    .arrow { color: var(--text-muted); }
    .arrow.dim { opacity: .25; }

    .src-line { display: flex; align-items: center; gap: 4px; font-size: 11px; color: var(--text-muted); margin-bottom: 3px; }
    .src-icon { font-size: 13px; }
    .mono     { font-family: 'Consolas', monospace; }
    .detail   { font-size: 11px; margin-top: 3px; max-width: 240px; word-break: break-word; }

    .gap-chip { display: inline-block; background: var(--warn-bg-2); border: 1px solid var(--warn-strong); color: var(--warn-text);
                border-radius: 4px; padding: 1px 7px; font-size: 11px; font-weight: 600; }
    .undet-chip { display: inline-block; color: var(--text-muted); border: 1px dashed var(--border);
                  border-radius: 4px; padding: 1px 7px; font-size: 11px; }

    .fix-mode { display: inline-block; font-weight: 600; font-size: 12px; }
    .fix-mode.auto { color: var(--success); }
  `]
})
export class JobFlowComponent implements OnInit {
  jobId = input.required<number>();

  private svc = inject(ConfigService);

  loading  = signal(true);
  errored  = signal(false);
  private job        = signal<MonitoredJob | null>(null);
  private allJobs    = signal<MonitoredJob[]>([]);
  private allClass   = signal<ClassificationRule[]>([]);
  private jobTypeId  = signal<number>(0);
  private fixes      = signal<FixPolicyRule[]>([]);

  private effective = computed<EffectiveClassRule[]>(() => {
    const j = this.job();
    return j ? computeEffectiveClassRules(j, this.allJobs(), this.allClass(), this.jobTypeId()) : [];
  });

  rows = computed<FlowRow[]>(() => {
    const j = this.job();
    if (!j) return [];
    const eff = this.effective();
    const pols = this.fixes();
    const out: FlowRow[] = [];
    for (const s of j.sources) {
      for (const r of s.scanCheckRules) {
        const m = matchScanRuleClassification(r, eff);
        let errorTypeCode: string | null = null;
        let classPattern:  string | null = null;
        let extraMatches = 0;
        let fix: FixPolicyRule | null = null;
        let rowKind: FlowRow['rowKind'];

        if (m.state === 'matched') {
          errorTypeCode = m.matched[0].errorTypeCode;
          classPattern  = m.matched[0].pattern;
          extraMatches  = m.matched.length - 1;
          fix = effectiveFixForErrorType(errorTypeCode, pols);
          rowKind = fix ? 'complete' : 'gap-fix';
        } else if (m.state === 'gap') {
          rowKind = 'gap-classification';
        } else {
          rowKind = 'not-determinable';
        }

        out.push({
          sourceIcon: this.scanIcon(s.scanTypeName),
          sourceLabel: this.sourceLabel(s),
          checkType: r.checkType,
          target: r.targetField,
          detail: this.ruleDetail(r),
          classifyState: m.state,
          errorTypeCode, classPattern, extraMatches,
          fix, rowKind,
        });
      }
    }
    return out;
  });

  ngOnInit(): void {
    const id = this.jobId();
    forkJoin({
      job:      this.svc.getJob(id),
      allClass: this.svc.getAllClassificationRules(),
      allJobs:  this.svc.getAllJobs(),
      jobTypes: this.svc.getJobTypes(),
    }).pipe(
      switchMap(({ job, allClass, allJobs, jobTypes }) => {
        const jobTypeId = jobTypes.find(t => t.name === job.jobTypeName)?.jobTypeId ?? 0;
        const fixes$ = jobTypeId ? this.svc.getFixPolicyRules(jobTypeId, id) : of([] as FixPolicyRule[]);
        return fixes$.pipe(map(fixes => ({ job, allClass, allJobs, jobTypeId, fixes })));
      }),
    ).subscribe({
      next: r => {
        this.job.set(r.job);
        this.allClass.set(r.allClass);
        this.allJobs.set(r.allJobs);
        this.jobTypeId.set(r.jobTypeId);
        this.fixes.set(r.fixes);
        this.loading.set(false);
      },
      error: () => { this.errored.set(true); this.loading.set(false); },
    });
  }

  // ── presentation helpers ──────────────────────────────────────────────────
  fixModeIcon(p: FixPolicyRule): string { return p.isAutoHealEligible ? '⚡' : '✋'; }
  fixModeLabel(p: FixPolicyRule): string {
    return p.isAutoHealEligible ? 'Auto-heal' : (p.actionType === 'Manual' ? 'Manual' : 'On approval');
  }
  fixModeTitle(p: FixPolicyRule): string {
    return p.isAutoHealEligible
      ? 'Runs automatically when this error is detected (no operator approval).'
      : (p.actionType === 'Manual'
          ? 'No automated executor — operator performs the fix off-system.'
          : 'Runs only after an operator approves the recommendation.');
  }

  private scanIcon(scanTypeName: string): string {
    switch (scanTypeName) {
      case 'FileSystem':  return '📁';
      case 'Database':    return '🔌';
      case 'ApiEndpoint': return '🌐';
      case 'FileContent': return '📄';
      default:            return '📋';
    }
  }
  private sourceLabel(s: ScanSource): string {
    const detail = s.connectionName || s.logFolder || s.logSourceUrl || s.name;
    return detail && detail !== s.scanTypeName ? `${s.scanTypeName} · ${detail}` : s.scanTypeName;
  }
  private ruleDetail(r: ScanCheckRule): string {
    switch (r.checkType) {
      case 'ColumnRange':  return `range ${r.minValue ?? '−∞'}…${r.maxValue ?? '∞'}`;
      case 'ValueEquals':  return r.expectedValue ? `= ${r.expectedValue}` : '';
      case 'ErrorKeyword': return 'log keyword';
      case 'FileContent':  return r.extractorLocator ? `at ${r.extractorLocator}` : 'file content';
      case 'SqlQuery':     return r.description ?? 'custom SQL';
      default:             return '';
    }
  }
}
