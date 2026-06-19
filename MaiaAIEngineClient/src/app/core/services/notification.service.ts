import { Injectable, signal } from '@angular/core';

export type NoticeKind = 'error' | 'info';

export interface Notice {
  id: number;
  kind: NoticeKind;
  message: string;
}

/**
 * Minimal transient-notice channel. Used by the auth-error interceptor to surface a
 * plain 403 ("you don't have permission") without navigating, and available for other
 * one-off messages. Rendered by ShellComponent as auto-dismissing toasts.
 */
@Injectable({ providedIn: 'root' })
export class NotificationService {
  private _next = 0;
  readonly notices = signal<Notice[]>([]);

  show(message: string, kind: NoticeKind = 'info', autoDismissMs = 5000): void {
    const id = ++this._next;
    this.notices.update(list => [...list, { id, kind, message }]);
    if (autoDismissMs > 0) setTimeout(() => this.dismiss(id), autoDismissMs);
  }

  error(message: string): void { this.show(message, 'error'); }

  dismiss(id: number): void {
    this.notices.update(list => list.filter(n => n.id !== id));
  }
}
