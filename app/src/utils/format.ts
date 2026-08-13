/** Formats a signed euro amount like the mocks: "−€9.99" / "€9.99". */
export function formatCurrencyEUR(amountEUR: number): string {
  const abs = Math.abs(amountEUR).toLocaleString('en-US', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  return `${amountEUR < 0 ? '−' : ''}€${abs}`;
}

/** "09:14" style 24h time. */
export function formatTime(isoTimestamp: string): string {
  const d = new Date(isoTimestamp);
  return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
}

/** "Aug 3, 2026" style long date, used on the transaction detail screen. */
export function formatLongDate(isoTimestamp: string): string {
  const d = new Date(isoTimestamp);
  return d.toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  });
}

/** "just now" / "5m ago" / "3h ago", falling back to a short date for
 * anything a day or older. Used for the "Auto-marked Bad · Xm ago" marker
 * on RulesListScreen — a minor UI nicety, so simple buckets are fine. */
export function formatRelativeTime(isoTimestamp: string): string {
  const diffMs = Date.now() - new Date(isoTimestamp).getTime();
  const diffMinutes = Math.floor(diffMs / 60000);
  if (diffMinutes < 1) {
    return 'just now';
  }
  if (diffMinutes < 60) {
    return `${diffMinutes}m ago`;
  }
  const diffHours = Math.floor(diffMinutes / 60);
  if (diffHours < 24) {
    return `${diffHours}h ago`;
  }
  return new Date(isoTimestamp).toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
}

function isSameCalendarDay(a: Date, b: Date): boolean {
  return (
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate()
  );
}

/**
 * Groups items into date sections labelled "Today" / "Yesterday" / a short
 * date (e.g. "Aug 2"), matching mocks/02-transaction-list.html. Generic over
 * the caller's item shape so it isn't coupled to the Transaction type.
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
