/**
 * Formats a euro amount like the mocks: "€9.99". Never carries a minus sign —
 * every debit is money going out (R2a), so the sign distinguishes nothing and
 * R17a keeps it out of the UI everywhere, notifications included.
 */
export function formatCurrencyEUR(amountEUR: number): string {
  const amount = Math.abs(amountEUR).toLocaleString('en-US', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  return `€${amount}`;
}

/** "09:14" style 24h time. */
export function formatTime(isoTimestamp: string): string {
  const d = new Date(isoTimestamp);
  return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
}

/** "Aug 3, 2026" style long date, used on the debit detail screen. */
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
 * Groups items into date sections labelled "Today" / "Yesterday" / a short
 * date (e.g. "Aug 2"), matching mocks/02-debit-list.html. Generic over the
 * caller's item shape so it isn't coupled to the Debit type.
 */
export function groupByDateSection<T>(
  items: T[],
  getTimestamp: (item: T) => string,
): { label: string; data: T[] }[] {
  const now = new Date();
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);

  const sections = new Map<string, T[]>();

  // Items already come newest-first in the fixtures; sort defensively so
  // this holds regardless of fixture ordering.
  const sorted = [...items].sort(
    (a, b) => new Date(getTimestamp(b)).getTime() - new Date(getTimestamp(a)).getTime(),
  );

  for (const item of sorted) {
    const d = new Date(getTimestamp(item));
    let label: string;
    if (isSameCalendarDay(d, now)) {
      label = 'Today';
    } else if (isSameCalendarDay(d, yesterday)) {
      label = 'Yesterday';
    } else {
      label = d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    }
    const existing = sections.get(label);
    if (existing) {
      existing.push(item);
    } else {
      sections.set(label, [item]);
    }
  }

  return Array.from(sections.entries()).map(([label, data]) => ({ label, data }));
}
