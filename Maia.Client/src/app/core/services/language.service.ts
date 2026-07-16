import { Injectable, computed, effect, signal } from '@angular/core';

export type LangCode = 'en' | 'he';

export interface LangOption {
  code:        LangCode;
  label:       string;   // English name (for tooltips/aria)
  nativeLabel: string;   // shown in the switcher
  dir:         'ltr' | 'rtl';
  enabled:     boolean;  // false = listed but not selectable yet (translations pending)
}

const STORAGE_KEY = 'maia-lang';

/**
 * UI language selection — scaffolding only for now. English is the sole enabled
 * language; Hebrew is listed as "coming soon" and its RTL + translations land
 * with the deferred i18n/RTL work (FOLLOWUPS item 11). The service persists the
 * choice and stamps `lang`/`dir` on <html>; when Hebrew is enabled and a
 * translation layer is wired in, only this file + the option's `enabled` flag
 * change — the switcher UI stays put. Mirrors ThemeService's shape.
 */
@Injectable({ providedIn: 'root' })
export class LanguageService {
  readonly languages: LangOption[] = [
    { code: 'en', label: 'English', nativeLabel: 'English', dir: 'ltr', enabled: true },
    // Enable when translations + RTL layout ship (item 11).
    { code: 'he', label: 'Hebrew',  nativeLabel: 'עברית',   dir: 'rtl', enabled: false },
  ];

  readonly current = signal<LangCode>(this.load());
  readonly currentOption = computed(() =>
    this.languages.find(l => l.code === this.current()) ?? this.languages[0]);

  constructor() {
    effect(() => this.apply(this.current()));
  }

  /** Switch language. No-ops for unknown or not-yet-enabled languages. */
  set(code: LangCode): void {
    const opt = this.languages.find(l => l.code === code);
    if (!opt || !opt.enabled) return;
    this.current.set(code);
    this.persist(code);
  }

  private apply(code: LangCode): void {
    if (typeof document === 'undefined') return;
    const opt = this.languages.find(l => l.code === code) ?? this.languages[0];
    const root = document.documentElement;
    root.lang = opt.code;
    root.dir  = opt.dir;   // 'ltr' today; 'rtl' takes effect once Hebrew is enabled
  }

  private load(): LangCode {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      const opt = this.languages.find(l => l.code === v);
      if (opt?.enabled) return opt.code;
    } catch { /* localStorage unavailable */ }
    return 'en';
  }

  private persist(code: LangCode): void {
    try { localStorage.setItem(STORAGE_KEY, code); } catch { /* ignore */ }
  }
}
