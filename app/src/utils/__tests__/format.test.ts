/**
 * R17 (amounts follow the device locale) and the date grouping the debit list
 * is built on.
 *
 * Both are tested against fixed inputs — an explicit locale, an explicit
 * "now" — because both would otherwise pass or fail depending on the machine
 * running them, which is the one thing a regression test may not do.
 */

import {
  formatCurrencyEUR,
  formatDayShort,
  groupByDateSection,
  formatTime,
  joinMeta,
} from '../format';

describe('formatCurrencyEUR — R17', () => {
  it('renders a German locale the German way', () => {
    // Escaped on purpose: Intl separates the amount from the symbol with a
    // NON-BREAKING space, and a plain space here would fail on every machine
    // while looking correct in the diff.
    expect(formatCurrencyEUR(49, 'de-DE')).toBe('49,00\u00A0€');
  });

  it('renders an English locale the English way', () => {
    expect(formatCurrencyEUR(49, 'en-US')).toBe('€49.00');
  });

  it('always shows two decimals', () => {
    expect(formatCurrencyEUR(9.9, 'en-US')).toBe('€9.90');
    expect(formatCurrencyEUR(1234.5, 'en-US')).toBe('€1,234.50');
  });

  /** R17a: every debit is money going out, so a sign distinguishes nothing. */
  it('never carries a minus sign, whatever it is handed', () => {
    expect(formatCurrencyEUR(-49, 'en-US')).toBe('€49.00');
    expect(formatCurrencyEUR(-49, 'de-DE')).not.toContain('-');
  });

  // Not tested: that the formatter is cached. It is a performance decision
  // with no observable behaviour, and a test asserting "the same string twice"
  // would only look like coverage.
});

describe('formatDayShort / formatTime', () => {
  it('spells the day the way a month-header row needs it', () => {
    expect(formatDayShort(new Date(2026, 6, 3, 9, 14).toISOString())).toBe('3 Jul');
  });

  it('renders 24h time', () => {
    expect(formatTime(new Date(2026, 6, 3, 9, 14).toISOString())).toBe('09:14');
  });
});

type Item = { at: string };

const item = (date: Date): Item => ({ at: date.toISOString() });
const at = (item_: Item) => item_.at;

describe('groupByDateSection', () => {
  // A fixed clock: 17 August 2026, mid-morning.
  const now = new Date(2026, 7, 17, 10, 0, 0);

  it('labels today and yesterday by name and everything older by month', () => {
    const sections = groupByDateSection(
      [
        item(new Date(2026, 7, 17, 9, 14)),
        item(new Date(2026, 7, 16, 18, 2)),
        item(new Date(2026, 7, 2, 7, 30)),
        item(new Date(2026, 6, 3, 9, 14)),
      ],
      at,
      now,
    );

    expect(sections.map(s => [s.kind, s.label])).toEqual([
      ['today', 'Today'],
      ['yesterday', 'Yesterday'],
      ['month', 'August 2026'],
      ['month', 'July 2026'],
    ]);
  });

  /**
   * The reason the label carries the year. A year of history is exactly what
   * this app shows, so two Augusts is the normal case, not an edge one — and
   * merged into one section they would read as a single month's spending.
   */
  it('keeps the same month in different years apart', () => {
    const sections = groupByDateSection(
      [item(new Date(2026, 7, 2, 8, 0)), item(new Date(2025, 7, 2, 8, 0))],
      at,
      now,
    );

    expect(sections.map(s => s.label)).toEqual(['August 2026', 'August 2025']);
  });

  it('sorts newest first regardless of input order', () => {
    const older = item(new Date(2026, 6, 3, 9, 14));
    const newer = item(new Date(2026, 7, 2, 7, 30));

    const sections = groupByDateSection([older, newer], at, now);

    expect(sections[0].data).toEqual([newer]);
    expect(sections[1].data).toEqual([older]);
  });

  /**
   * The stale-header bug, as a test: the same debit is "Today" before midnight
   * and "Yesterday" after it. Nothing about the data changed — only the day —
   * which is why the screen's memo depends on the current date key.
   */
  it('re-labels the same item once the clock crosses midnight', () => {
    const charge = item(new Date(2026, 7, 17, 9, 14));

    const beforeMidnight = groupByDateSection([charge], at, new Date(2026, 7, 17, 23, 59));
    const afterMidnight = groupByDateSection([charge], at, new Date(2026, 7, 18, 0, 1));

    expect(beforeMidnight[0].kind).toBe('today');
    expect(afterMidnight[0].kind).toBe('yesterday');
  });

  it('groups an empty list into no sections at all', () => {
    expect(groupByDateSection([], at, now)).toEqual([]);
  });
});

/**
 * The separator has to leave with its part. Every debit from a real bank so
 * far carries no time (Debit.hasTime), so "Card payment · " with nothing after
 * it is the *common* rendering if this is got wrong — and a trailing middot
 * reads as a value the app failed to load.
 */
describe('joinMeta', () => {
  it('joins the parts that are there with a middot', () => {
    expect(joinMeta('Card payment', '3 Jul, 09:14')).toBe('Card payment · 3 Jul, 09:14');
  });

  it('drops a part the bank did not give, separator included', () => {
    // `false` is what a screen passes for `hasTime && formatTime(...)`.
    expect(joinMeta('Card payment', 'Aug 18, 2026', false)).toBe('Card payment · Aug 18, 2026');
    expect(joinMeta('Card payment', '')).toBe('Card payment');
    expect(joinMeta('Card payment', null, undefined)).toBe('Card payment');
  });

  it('drops a missing part from the middle without doubling the separator', () => {
    expect(joinMeta('Card payment', undefined, '09:14')).toBe('Card payment · 09:14');
  });

  it('returns an empty string when there is nothing to say', () => {
    expect(joinMeta(false, null, undefined, '')).toBe('');
  });
});
