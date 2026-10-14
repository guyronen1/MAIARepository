import { WIDE_VALUE_LENGTH, parseErrorMessage } from './error-message.util';

describe('parseErrorMessage', () => {
  it('a single-line message is all headline, no fields', () => {
    const p = parseErrorMessage('2026-10-14 ERROR: file not found');
    expect(p.head).toBe('2026-10-14 ERROR: file not found');
    expect(p.fields).toEqual([]);
  });

  it('null / empty messages parse to an empty headline', () => {
    expect(parseErrorMessage(null)).toEqual({ head: '', fields: [] });
    expect(parseErrorMessage('')).toEqual({ head: '', fields: [] });
  });

  it('splits Detail Columns lines into name/value fields after the headline', () => {
    const p = parseErrorMessage(
      '[dbo].[Files].[FileStatusCode] = 7 matches error value 7 (Id=42) [FileStatusCode=7]\n' +
      'UpdateDate: 2026-10-14 09:15:00.000\n' +
      'f.Details: Timeout');
    expect(p.head).toBe('[dbo].[Files].[FileStatusCode] = 7 matches error value 7 (Id=42) [FileStatusCode=7]');
    expect(p.fields.map(f => [f.name, f.value])).toEqual([
      ['UpdateDate', '2026-10-14 09:15:00.000'],
      ['f.Details', 'Timeout'],
    ]);
    expect(p.fields.every(f => !f.wide && !f.muted)).toBe(true);
  });

  it('keeps everything after the first ": " in the value', () => {
    const p = parseErrorMessage('head\nDetails: Error: code: 5');
    expect(p.fields[0].value).toBe('Error: code: 5');
  });

  it('marks long and multi-line values wide, and folds continuation lines into the value', () => {
    const long = 'x'.repeat(WIDE_VALUE_LENGTH + 1);
    const p = parseErrorMessage(`head\nDetails: ${long}\nStack: line 1\n   at Foo()\nCode: 5`);
    expect(p.fields.map(f => f.name)).toEqual(['Details', 'Stack', 'Code']);
    expect(p.fields[0].wide).toBe(true);
    expect(p.fields[1].value).toBe('line 1\n   at Foo()');
    expect(p.fields[1].wide).toBe(true);
    expect(p.fields[2].wide).toBe(false);
  });

  it('marks NULL and the missing-column marker as muted', () => {
    const p = parseErrorMessage('head\nA: NULL\nB: (column not in result set)\nC: NULLABLE');
    expect(p.fields.map(f => f.muted)).toEqual([true, true, false]);
  });

  it('lines before the first field stay in the headline', () => {
    const p = parseErrorMessage('first line\nsecond line\nCode: 5');
    expect(p.head).toBe('first line\nsecond line');
    expect(p.fields.map(f => f.name)).toEqual(['Code']);
  });
});
