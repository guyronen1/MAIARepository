/**
 * Display-only split of a failure's ErrorMessage into the headline and the
 * Detail Columns fields.
 *
 * DatabaseScanStrategy.BuildRowMessage writes the rule-derived message on line 1
 * and one "Column: value" line per configured Detail Column after it (SQL NULL →
 * "NULL", a column missing from a SqlQuery result → "(column not in result set)",
 * values capped at 2000 chars). The stored text is never changed — the
 * classifier matches on it — this only shapes how the failure drawer shows it.
 */

export interface ErrorMessageField {
  name:  string;
  value: string;
  /** Long or multi-line value — render it across the full width, under its name. */
  wide:  boolean;
  /** "NULL" / "(column not in result set)" — a marker, not data. */
  muted: boolean;
}

export interface ParsedErrorMessage {
  head:   string;
  fields: ErrorMessageField[];
}

/** Values longer than this (or containing a newline) get a full-width row. */
export const WIDE_VALUE_LENGTH = 60;

const MARKERS = new Set(['NULL', '(column not in result set)']);

// Detail Columns are validated server-side (no brackets, semicolons or embedded
// spaces; `alias.Column` allowed), so a field line is "<identifier>: <value>".
const FIELD_LINE = /^([A-Za-z_][\w.]*): (.*)$/;

export function parseErrorMessage(message: string | null | undefined): ParsedErrorMessage {
  const lines = (message ?? '').split(/\r?\n/);
  const headLines = [lines[0]];
  const fields: { name: string; value: string }[] = [];

  for (const line of lines.slice(1)) {
    const m = FIELD_LINE.exec(line);
    if (m) fields.push({ name: m[1], value: m[2] });
    else if (fields.length) fields[fields.length - 1].value += '\n' + line;   // a value that itself spans lines
    else headLines.push(line);                                                // multi-line headline, no fields yet
  }

  return {
    head: headLines.join('\n'),
    fields: fields.map(f => ({
      ...f,
      wide:  f.value.length > WIDE_VALUE_LENGTH || f.value.includes('\n'),
      muted: MARKERS.has(f.value),
    })),
  };
}
