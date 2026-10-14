import { Component, DestroyRef, ElementRef, NgZone, effect, inject, untracked, viewChild } from '@angular/core';
import { AssistantService } from '../../core/services/assistant.service';
import { AuthService } from '../../core/services/auth.service';

/** The parts of the `<knowledge-assistant>` element MAIA sets after creation (Angular Elements maps them to inputs). */
interface KnowledgeAssistantElement extends HTMLElement {
  prefill: string | null;
}

/**
 * Right-hand drawer hosting the embedded knowledge assistant (`<knowledge-assistant>`, see AssistantService).
 *
 * Non-modal on purpose: it sits below the top bar and above page content (and above the failure drawer), with no
 * backdrop, so the operator keeps reading the failure while asking about it. Opened by the top-bar launcher or by
 * `AssistantService.ask()`; closed by the launcher, Esc (handled by the launcher) or the assistant's own ✕.
 *
 * The element is created on first open and then kept mounted while the shell lives, so an answer that is still
 * streaming and the open conversation survive closing the drawer. It is created with `document.createElement`
 * outside Angular's zone: it is a separate Angular app, and running it inside MAIA's zone would make every streamed
 * token trigger MAIA-wide change detection.
 */
@Component({
  selector: 'app-assistant-drawer',
  standalone: true,
  template: `
    <aside id="ka-drawer" class="ka-drawer" [class.open]="assistant.open()"
           [attr.aria-hidden]="!assistant.open()" aria-label="Knowledge assistant">
      <div #host class="ka-host"></div>
    </aside>
  `,
  styles: [`
    .ka-drawer {
      position: fixed;
      top: var(--topbar-h); right: 0; bottom: 0;
      width: min(440px, 100vw);
      z-index: 60;                       /* above the failure drawer (50) and its backdrop (49) */
      display: flex; flex-direction: column;
      background: var(--surface);
      border-left: 1px solid var(--border);
      box-shadow: -12px 0 32px rgba(16, 24, 40, 0.16);
      transform: translateX(100%);
      /* hidden after the slide-out so the shadow does not peek in and nothing inside is focusable */
      visibility: hidden;
      transition: transform 200ms ease-out, visibility 0s linear 200ms;
    }
    .ka-drawer.open {
      transform: none;
      visibility: visible;
      transition: transform 200ms ease-out;
    }
    .ka-host { flex: 1; min-height: 0; display: flex; flex-direction: column; }
    @media (prefers-reduced-motion: reduce) { .ka-drawer, .ka-drawer.open { transition: none; } }
  `],
})
export class AssistantDrawerComponent {
  readonly assistant = inject(AssistantService);
  private auth = inject(AuthService);
  private zone = inject(NgZone);

  private host = viewChild.required<ElementRef<HTMLDivElement>>('host');
  private element: KnowledgeAssistantElement | null = null;

  constructor() {
    // First open → create the element (once the bundle has defined it).
    effect(() => {
      if (this.assistant.open() && this.assistant.available()) untracked(() => this.ensureElement());
    });

    // Signed-in user → X-User; the assistant switches its conversation list when it changes.
    effect(() => {
      const user = this.auth.currentUser()?.username;
      if (user && this.element) this.element.setAttribute('user', user);
    });

    // "Ask about this failure": put the text in the composer. Reset first so the same text can be applied twice.
    effect(() => {
      const request = this.assistant.prefillRequest();
      if (!request || !this.assistant.available()) return;
      untracked(() => this.ensureElement());
      const el = this.element!;
      this.zone.runOutsideAngular(() => {
        el.prefill = '';
        el.prefill = request.text;
      });
    });

    inject(DestroyRef).onDestroy(() => this.assistant.setOpen(false));
  }

  private ensureElement(): void {
    if (this.element) return;
    const user = this.auth.currentUser()?.username ?? '';
    this.element = this.zone.runOutsideAngular(() => {
      const el = document.createElement('knowledge-assistant') as KnowledgeAssistantElement;
      if (user) el.setAttribute('user', user);
      el.setAttribute('show-close', '');
      const saved = sessionStorage.getItem(this.storageKey(user));
      if (saved) el.setAttribute('conversation-id', saved);
      el.style.flex = '1';
      el.style.minHeight = '0';

      el.addEventListener('closed', () => this.zone.run(() => this.assistant.setOpen(false)));
      el.addEventListener('conversationIdChange', (e: Event) => {
        const id = (e as CustomEvent<string | null>).detail;
        const key = this.storageKey(this.auth.currentUser()?.username ?? '');
        if (id) sessionStorage.setItem(key, id);
        else sessionStorage.removeItem(key);
      });

      this.host().nativeElement.append(el);
      return el;
    });
  }

  /** Last open conversation per MAIA user, for the browser tab's lifetime (reload keeps it, a new tab starts fresh). */
  private storageKey(user: string): string {
    return `maia.assistant.conversation.${user}`;
  }
}
