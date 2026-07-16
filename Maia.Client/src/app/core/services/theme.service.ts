import { Injectable, computed, effect, signal } from '@angular/core';

export type ThemeMode = 'system' | 'light' | 'dark';

const STORAGE_KEY = 'maia-theme';

/**
 * Light/dark theme control. Two inputs, one output:
 *   - `mode` is the operator's CHOICE: 'system' (follow the OS), 'light', or 'dark'.
 *   - `resolved` is what's actually SHOWN ('light' | 'dark') — equal to `mode`
 *     unless it's 'system', in which case it tracks `prefers-color-scheme`.
 *
 * The only side effect is stamping `data-theme` on <html>:
 *   - 'system' → attribute removed → the `@media (prefers-color-scheme)` rules in
 *     styles.scss take over.
 *   - 'light' / 'dark' → attribute set → the `:root[data-theme=...]` override wins.
 *
 * An inline script in index.html applies the stored choice BEFORE Angular boots
 * (anti-FOUC); this service keeps it in sync at runtime and persists changes.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly media =
    typeof window !== 'undefined' && window.matchMedia
      ? window.matchMedia('(prefers-color-scheme: dark)')
      : null;

  /** The operator's explicit choice (default: follow the OS). */
  readonly mode = signal<ThemeMode>(this.load());

  /** Live OS preference, kept reactive so 'system' mode updates on OS change. */
  private readonly systemDark = signal(this.media?.matches ?? false);

  /** What is actually rendered — 'light' | 'dark'. Drives the toggle icon. */
  readonly resolved = computed<'light' | 'dark'>(() => {
    const m = this.mode();
    if (m === 'system') return this.systemDark() ? 'dark' : 'light';
    return m;
  });

  constructor() {
    this.media?.addEventListener('change', e => this.systemDark.set(e.matches));
    // Reflect every mode change onto <html> + persist it.
    effect(() => this.apply(this.mode()));
  }

  /** Flip between light and dark based on what's currently shown. Always lands
   *  on an explicit mode (leaves 'system' behind — the expected toggle UX). */
  toggle(): void {
    this.set(this.resolved() === 'dark' ? 'light' : 'dark');
  }

  set(mode: ThemeMode): void {
    this.mode.set(mode);
    this.persist(mode);
  }

  private apply(mode: ThemeMode): void {
    if (typeof document === 'undefined') return;
    const root = document.documentElement;
    if (mode === 'system') root.removeAttribute('data-theme');
    else root.setAttribute('data-theme', mode);
  }

  private load(): ThemeMode {
    try {
      const v = localStorage.getItem(STORAGE_KEY);
      if (v === 'light' || v === 'dark' || v === 'system') return v;
    } catch { /* localStorage unavailable — fall back to system */ }
    return 'system';
  }

  private persist(mode: ThemeMode): void {
    try { localStorage.setItem(STORAGE_KEY, mode); } catch { /* ignore */ }
  }
}
