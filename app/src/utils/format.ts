/**
 * Rendering values for the UI.
 *
 * Split by locale on purpose: **amounts follow the device**, **dates stay
 * English**. R17 is about money — "49,00 €" on a German phone is what the user
 * expects from every other banking app — but the date headers carry literal
 * words we ship in English ("Today", "Yesterday"), and a French month name
 * sitting under an English header would read worse than plain consistency.
 * When the app is localized properly, the dates follow the copy, not the
 * device.
 */

/**
 * One formatter per locale, built once. `Intl.NumberFormat` is expensive to
 * construct and a 60-row list would otherwise build 60 of them per render.
 */
const currencyFormatters = new Map<string, Intl.NumberFormat>();

function currencyFormatter(locale: string | undefined): Intl.NumberFormat {
  // '' stands for "whatever the device is" — the key has to be a string, and
  // `undefined` is exactly what tells Intl to use the runtime default.
  const key = locale ?? '';
  const cached = currencyFormatters.get(key);
  if (cached) {
    return cached;
  }
  const formatter = new Intl.NumberFormat(locale, { style: 'currency', currency: 'EUR' });
  currencyFormatters.set(key, formatter);
  return formatter;
}

/**
 * A euro amount in the device's locale (R17): "€49.00" in en-US, "49,00 €" in
 * de-DE. Never carries a minus sign — every debit is money going out (R2a), so
 * the sign distinguishes nothing and R17a keeps it out of the UI everywhere,
 * notifications included.
 *
 * `locale` exists so tests can be deterministic on any machine. App code never
 * passes it; passing one would be a bug, since it would pin the UI to a
 * language the user did not choose.
 */
export function formatCurrencyEUR(amountEUR: number, locale?: string): string {
  return currencyFormatter(locale).format(Math.abs(amountEUR));
}

/** English, deliberately — see the note at the top of this file. */
const DATE_LOCALE = 'en-GB';

/** "09:14" style 24h time. */
export function formatTime(isoTimestamp: string): string {
  const d = new Date(isoTimestamp);
  return d.toLocaleTimeString(DATE_LOCALE, { hour: '2-digit', minute: '2-digit' });
}

/** "3 Jul" — the day a debit row needs once it sits under a month header. */
export function formatDayShort(isoTimestamp: string): string {
  const d = new Date(isoTimestamp);
  return d.toLocaleDateString(DATE_LOCALE, { day: 'numeric', month: 'short' });
}

/**
 * "Aug 3, 2026" style long date, used on the debit detail screen. Spelled
 * month-first because that is how mocks/03-debit-detail.html spells it, where
 * the list's shorter form is day-first — the mocks differ, so the code does.
 */
export function formatLongDate(isoTimestamp: string): string {
  const d = new Date(isoTimestamp);
  return d.toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  });
}

// formatRelativeTime() lived here for the "Auto-marked Bad · Xm ago" marker.
// R8 removed the automatic flip that produced it, and with it the only caller.

function isSameCalendarDay(a: Date, b: Date): boolean {
  return (
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate()
  );
}

/**
 * Which kind of section a debit fell into. The list needs this, not just the
 * label: rows under a month header carry the day ("3 Jul, 09:14"), rows under
 * Today/Yesterday need only the time — and comparing against the *label* to
 * decide that would be a string comparison against copy.
 */
export type DateSectionKind = 'today' | 'yesterday' | 'month';

export type DateSection<T> = {
  kind: DateSectionKind;
  label: string;
  data: T[];
};

/**
 * Groups items into "Today" / "Yesterday" / month sections, matching
 * mocks/02-debit-list.html.
 *
 * Only the two days a user thinks of by name get their own header; everything
 * older groups by month, because what is worth seeing in history is the rhythm
 * of a recurring charge, not which Tuesday it landed on (R23).
 *
 * The month label carries the **year** ("August 2026", not "August"). Without
 * it, August 2025 and August 2026 collapse into one section as soon as the
 * history is a year deep — which is exactly how deep it is.
 *
 * `now` is injectable so tests can stand on a fixed clock; callers in the app
 * pass the current day (see useCurrentDateKey), which is what makes the
 * Today/Yesterday labels re-derive across midnight instead of freezing.
 */
export function groupByDateSection<T>(
  items: T[],
  getTimestamp: (item: T) => string,
  now: Date = new Date(),
): DateSection<T>[] {
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);

  const sections = new Map<string, DateSection<T>>();

  // Items already come newest-first in the fixtures; sort defensively so this
  // holds regardless of the caller's ordering.
  const sorted = [...items].sort(
    (a, b) => new Date(getTimestamp(b)).getTime() - new Date(getTimestamp(a)).getTime(),
  );

  for (const item of sorted) {
    const d = new Date(getTimestamp(item));
    let kind: DateSectionKind;
    let label: string;
    if (isSameCalendarDay(d, now)) {
      kind = 'today';
      label = 'Today';
    } else if (isSameCalendarDay(d, yesterday)) {
      kind = 'yesterday';
      label = 'Yesterday';
    } else {
      kind = 'month';
      label = d.toLocaleDateString(DATE_LOCALE, { month: 'long', year: 'numeric' });
    }
    // The label is the key, and it is unique per section precisely because the
    // year is in it.
    const existing = sections.get(label);
    if (existing) {
      existing.data.push(item);
    } else {
      sections.set(label, { kind, label, data: [item] });
    }
  }

  return Array.from(sections.values());
}
