import { useEffect, useState } from 'react';
import { AppState } from 'react-native';

/**
 * The current **local** day, as a stable "2026-08-17" string.
 *
 * It exists so a memo that groups by date can depend on the *day* rather than
 * on the clock. Without it, the debit list's sections memo pinned "Today" and
 * "Yesterday" to whatever they meant when the debits last changed, and a
 * session left open across midnight kept showing yesterday's charge under
 * "Today" — the one label in the app a user reads as a fact.
 */

/** Local, never UTC: toISOString() would flip the day at the wrong moment. */
export function currentDateKey(now: Date = new Date()): string {
  const month = `${now.getMonth() + 1}`.padStart(2, '0');
  const day = `${now.getDate()}`.padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}

/** Local midnight of the given key — the "now" date grouping actually needs. */
export function dateFromDateKey(key: string): Date {
  const [year, month, day] = key.split('-').map(Number);
  return new Date(year, month - 1, day);
}

function msUntilNextLocalMidnight(now: Date): number {
  const next = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1, 0, 0, 0, 0);
  return next.getTime() - now.getTime();
}

export function useCurrentDateKey(): string {
  const [key, setKey] = useState(() => currentDateKey());

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;

    const tick = () => {
      // Same string = React bails out, so a spurious tick costs nothing.
      setKey(currentDateKey());
      schedule();
    };

    const schedule = () => {
      if (timer) {
        clearTimeout(timer);
      }
      // A second past midnight, so an early-firing timer cannot recompute the
      // same day and then wait another 24h for the next one.
      timer = setTimeout(tick, msUntilNextLocalMidnight(new Date()) + 1000);
    };

    schedule();

    // JS timers do not fire while Android has the app dozing, and "a session
    // open across midnight" almost always means the phone was in the user's
    // pocket for those hours. Re-checking on resume is what actually closes
    // the stale-header case; the timer alone only covers a screen left awake.
    const subscription = AppState.addEventListener('change', state => {
      if (state === 'active') {
        tick();
      }
    });

    return () => {
      if (timer) {
        clearTimeout(timer);
      }
      subscription.remove();
    };
  }, []);

  return key;
}
