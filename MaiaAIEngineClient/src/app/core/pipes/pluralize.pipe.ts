import { Pipe, PipeTransform } from '@angular/core';

/**
 * Format an English count + noun with correct singular/plural. Default plural
 * is the singular with 's' appended; pass an explicit form for irregulars.
 *
 *   pluralize(1, 'suggestion')               → "1 suggestion"
 *   pluralize(3, 'suggestion')               → "3 suggestions"
 *   pluralize(2, 'index', 'indices')         → "2 indices"
 *
 * Out of scope: non-English plurals (Hebrew/Arabic dual forms, etc.) — the
 * "(s)" pattern this replaces was English-only to begin with.
 */
export function pluralize(count: number, singular: string, plural?: string): string {
  const noun = count === 1 ? singular : (plural ?? `${singular}s`);
  return `${count} ${noun}`;
}

/** Template pipe form. Use in component code via the `pluralize()` function instead. */
@Pipe({ name: 'pluralize', standalone: true, pure: true })
export class PluralizePipe implements PipeTransform {
  transform(count: number, singular: string, plural?: string): string {
    return pluralize(count, singular, plural);
  }
}
