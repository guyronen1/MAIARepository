import { Component, computed, input } from '@angular/core';
import { statusMeta } from '../../core/util/status.util';

/**
 * Shared JobStatus badge — icon + canonical label + semantic colour, from the
 * single statusMeta() map. Use everywhere a failure/job status is shown so the
 * wording, glyph and colour never drift between screens.
 */
@Component({
  selector: 'app-status-badge',
  standalone: true,
  template: `
    <span class="badge" [class]="meta().badgeClass" [title]="meta().label">
      <span class="status-ico" aria-hidden="true">{{ meta().icon }}</span>{{ meta().label }}
    </span>
  `,
  styles: [`
    :host { display: inline-flex; }
    .status-ico { font-size: 10px; line-height: 1; }
  `],
})
export class StatusBadgeComponent {
  status = input.required<string>();
  meta = computed(() => statusMeta(this.status()));
}
