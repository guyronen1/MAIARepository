import { Injectable, NgZone, computed, inject, signal } from '@angular/core';
import { environment } from '../../../environments/environment';
import { AuthService } from './auth.service';

declare global {
  interface Window {
    /** Read once by the knowledge-assistant bundle when it loads (docs/embedding.md in KnowledgeAgent). */
    knowledgeAssistant?: { apiBaseUrl?: string };
  }
}

/**
 * Host side of the embedded knowledge assistant (KnowledgeAgent's `<knowledge-assistant>` custom element).
 *
 * The assistant is a separate Angular app shipped as one script + one stylesheet by the KnowledgeAgent Api
 * (`{knowledgeAssistantUrl}/assistant/main.js|styles.css`). MAIA loads that bundle once, shows the
 * `<knowledge-assistant-launcher>` button in the top bar and hosts `<knowledge-assistant>` in a right-hand drawer
 * (`AssistantDrawerComponent`). This service is the shared state between the three: open/closed, the "ask about
 * this failure" prefill, and whether the bundle is available at all.
 *
 * The assistant calls its own Api directly (fetch, SSE); MAIA's HttpClient and interceptors are not involved.
 * Identity is the pilot `X-User` header — the drawer passes the signed-in MAIA username.
 */
@Injectable({ providedIn: 'root' })
export class AssistantService {
  private zone = inject(NgZone);
  private auth = inject(AuthService);

  private readonly base = (environment.knowledgeAssistantUrl ?? '').replace(/\/+$/, '');
  /** On only when the build has a URL (environment.knowledgeAssistantUrl) AND the server switches it on
   *  (appsettings `KnowledgeAssistant:Enabled`, surfaced on /api/auth/me; missing = off). */
  readonly enabled = computed(() => this.base !== '' && !!this.auth.currentUser()?.knowledgeAssistantEnabled);
  private readonly loaded = signal(false);
  /** The bundle loaded and the custom elements are defined — gate any UI that opens the drawer on this. */
  readonly available = computed(() => this.enabled() && this.loaded());
  /** Drawer state; the launcher and the drawer both read and write it. */
  readonly open = signal(false);
  /** Display name from the assistant's own branding (`GET /api/branding`), e.g. for "Ask {name}" buttons. */
  readonly assistantName = signal('assistant');
  /** Latest "put this text in the composer" request; a new object each time so the same text can be re-sent. */
  readonly prefillRequest = signal<{ text: string } | null>(null);

  private loading: Promise<void> | null = null;

  /** Loads the bundle once (idempotent). Resolves when `<knowledge-assistant>` is defined (stays pending if the bundle cannot load). */
  load(): Promise<void> {
    if (!this.enabled()) return Promise.resolve();
    // Outside Angular: the bundle bootstraps its own (zoneless) Angular app; its timers and streams must not
    // drive MAIA's change detection.
    this.loading ??= this.zone.runOutsideAngular(() => this.insertBundle());
    return this.loading;
  }

  setOpen(open: boolean): void {
    this.open.set(open);
  }

  /** Opens the drawer with `text` in the composer for the operator to edit and send. */
  ask(text: string): void {
    this.prefillRequest.set({ text });
    this.open.set(true);
  }

  private insertBundle(): Promise<void> {
    window.knowledgeAssistant = { apiBaseUrl: this.base };

    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = `${this.base}/assistant/styles.css`;
    document.head.append(link);

    const script = document.createElement('script');
    script.type = 'module';
    script.src = `${this.base}/assistant/main.js`;
    script.onerror = () => console.warn(`Knowledge assistant unavailable: ${script.src} did not load.`);
    document.head.append(script);

    void fetch(`${this.base}/api/branding`)
      .then(r => (r.ok ? r.json() : null))
      .then((b: { assistantName?: string } | null) => {
        if (b?.assistantName) this.zone.run(() => this.assistantName.set(b.assistantName!));
      })
      .catch(() => { /* the launcher shows its own label; the name is cosmetic here */ });

    return customElements.whenDefined('knowledge-assistant').then(() => {
      this.zone.run(() => this.loaded.set(true));
    });
  }
}
