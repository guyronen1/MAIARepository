import { TestBed, ComponentFixture } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { FailureDetailComponent } from './failure-detail.component';

/**
 * The stage-pipeline derivation — a documented load-bearing invariant. After
 * "Recommended" the failure branches into ONE of two alternative middle stages:
 * Acknowledged (AwaitingManualAction) vs Manual (ManualRequired). The pipeline
 * must render only the reached middle, and isStageCompleted() must derive its
 * order from the RENDERED stages (never the missing alternative — that was the
 * original -1/indexOf bug). Tested without detectChanges so the polling effect
 * (which reads the required failureId input + fires HTTP) never runs.
 */
describe('FailureDetailComponent — stage derivation', () => {
  let fixture: ComponentFixture<FailureDetailComponent>;
  let c: FailureDetailComponent;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [FailureDetailComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    fixture = TestBed.createComponent(FailureDetailComponent);
    c = fixture.componentInstance;
    // Satisfy the required input without triggering CD (we never detectChanges).
    fixture.componentRef.setInput('failureId', 1);
  });

  const setFailure = (status: string, stage: string) =>
    c.failure.set({ status, stage } as any);

  it('renders the Acknowledged middle stage for a non-ManualRequired failure', () => {
    setFailure('AwaitingManualAction', 'Acknowledged');
    expect(c.stages().map(s => s.key)).toEqual(
      ['Failed', 'Classified', 'Recommended', 'Acknowledged', 'Fixed']);
  });

  it('renders the Manual middle stage (never Acknowledged) for a ManualRequired failure', () => {
    setFailure('ManualRequired', 'Manual');
    const keys = c.stages().map(s => s.key);
    expect(keys).toEqual(['Failed', 'Classified', 'Recommended', 'Manual', 'Fixed']);
    expect(keys).not.toContain('Acknowledged');   // the two are mutually exclusive
  });

  it('isStageCompleted: only stages strictly before the current stage are "done"', () => {
    setFailure('Resolved', 'Fixed');              // current = Fixed (last)
    expect(c.isStageCompleted('Failed')).toBe(true);
    expect(c.isStageCompleted('Recommended')).toBe(true);
    expect(c.isStageCompleted('Acknowledged')).toBe(true);   // the rendered middle
    expect(c.isStageCompleted('Fixed')).toBe(false);         // current, not past
  });

  it('isStageCompleted derives order from the RENDERED middle (Manual), not the missing one', () => {
    setFailure('ManualRequired', 'Manual');       // current = Manual (index 3)
    expect(c.isStageCompleted('Recommended')).toBe(true);    // 2 < 3
    expect(c.isStageCompleted('Manual')).toBe(false);        // current
    expect(c.isStageCompleted('Fixed')).toBe(false);         // 4 < 3 is false (later)
  });
});
