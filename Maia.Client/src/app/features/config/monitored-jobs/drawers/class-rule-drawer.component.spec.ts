import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { ClassRuleDrawerComponent } from './class-rule-drawer.component';
import type { MonitoredJob, RuleOverride } from '../../../../core/models';
import type { ClassificationRule } from '../../../../core/services/config.service';

describe('ClassRuleDrawerComponent', () => {
  let fixture: ComponentFixture<ClassRuleDrawerComponent>;
  let c: ClassRuleDrawerComponent;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ClassRuleDrawerComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    fixture = TestBed.createComponent(ClassRuleDrawerComponent);
    c = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('errorTypes', [{ errorTypeId: 8, code: 'DbConn', displayName: 'DB' }]);
  });

  it('openNew() starts a blank rule; prefill seeds pattern / errorTypeId', () => {
    c.openNew();
    expect(c.editingRule()).toBeNull();
    expect(c.form.pattern).toBe('');
    expect(c.editOpen()).toBe(true);

    c.openNew({ pattern: 'Timeout*' });
    expect(c.form.pattern).toBe('Timeout*');

    c.openNew({ errorTypeId: 8 });
    expect(c.form.errorTypeId).toBe(8);
  });

  it('openEdit() resolves the errorTypeId from the rule\'s error-type code', () => {
    const rule = { ruleId: 3, pattern: 'foo', errorTypeCode: 'DbConn', confidence: 0.8, priority: 2 } as RuleOverride;
    c.openEdit(rule);
    expect(c.editingRule()).toBe(rule);
    expect(c.form.errorTypeId).toBe(8);          // resolved from 'DbConn'
    expect(c.form.pattern).toBe('foo');
  });

  it('save() blocks on missing pattern / errorTypeId and issues no request', () => {
    c.openNew();                                  // pattern '', errorTypeId 0
    c.save();
    http.expectNone(() => true);
  });

  it('openLink() fetches all rules, filters out already-linked, and re-emits them to the parent', () => {
    fixture.componentRef.setInput('job', { monitoredJobId: 1, rules: [{ ruleId: 1 }] } as unknown as MonitoredJob);
    let refreshed: ClassificationRule[] | null = null;
    c.allClassRulesRefreshed.subscribe(r => (refreshed = r));

    c.openLink();
    const all = [
      { ruleId: 1, pattern: 'AlreadyLinked', errorTypeCode: 'E', jobTypeName: 'T', confidence: 0.9 },
      { ruleId: 2, pattern: 'Available',     errorTypeCode: 'E', jobTypeName: 'T', confidence: 0.9 },
    ];
    http.expectOne(r => r.method === 'GET' && r.url.endsWith('/classification-rules')).flush(all);

    expect(c.linkOpen()).toBe(true);
    expect(c.loadingLink()).toBe(false);
    expect(refreshed).toEqual(all as any);                          // parent's shared list kept in sync
    expect(c.filteredLinkableRules().map(r => r.ruleId)).toEqual([2]); // rule 1 excluded (linked)
    http.verify();
  });
});
