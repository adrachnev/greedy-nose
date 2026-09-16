// Thin data-access seam. Screens consume payees/debits/rules only through
// these hooks — never by importing app/src/mocks/data.ts or backendFeed.ts
// directly. Which source is behind them is decided once, here, from
// USE_BACKEND in ./config.
//
// Payees and debits come from whichever source that flag names. **Rules do
// not**: the backend deliberately sends no classification (R6 — the client
// derives it from the current rule), so there is nothing on the wire to
// switch, and rules stay in the local store under both flags.

import { useCallback, useMemo, useSyncExternalStore } from 'react';
import { Debit, Payee, Rule, RuleDraft } from '../domain/model';
import { debits as fixtureDebits, payees as fixturePayees, subscribeToDataChanges } from '../mocks/data';
import {
  FeedState,
  getDebits,
  getPayees,
  getState,
  refresh,
  subscribe as subscribeToFeed,
} from './backendFeed';
import { USE_BACKEND } from './config';
import { getRules, saveRule, subscribe as subscribeToRules } from './rulesStore';

// Every collection is read through useSyncExternalStore, so screens re-render
// when one changes elsewhere — e.g. navigating back to the debit list after
// editing a rule, which must re-label that payee's debits immediately (R7).
//
// The source is bound at module level rather than branched inside each hook.
// USE_BACKEND cannot change while the process lives, so this is not a
// conditional hook — and binding it per call would mean subscribing to *both*
// stores, which would start a backend fetch even in fixture mode.

const subscribeToPayees = USE_BACKEND ? subscribeToFeed : subscribeToDataChanges;
const payeesSnapshot = USE_BACKEND ? getPayees : () => fixturePayees;
const subscribeToDebits = USE_BACKEND ? subscribeToFeed : subscribeToDataChanges;
const debitsSnapshot = USE_BACKEND ? getDebits : () => fixtureDebits;

export function usePayees(): Payee[] {
  return useSyncExternalStore(subscribeToPayees, payeesSnapshot);
}

export function usePayee(payeeId: string): Payee | undefined {
  const all = usePayees();
  return useMemo(() => all.find(p => p.id === payeeId), [all, payeeId]);
}

export function useDebits(): Debit[] {
  return useSyncExternalStore(subscribeToDebits, debitsSnapshot);
}

export function useDebit(debitId: string): Debit | undefined {
  const all = useDebits();
  return useMemo(() => all.find(d => d.id === debitId), [all, debitId]);
}

export function useRules(): Rule[] {
  return useSyncExternalStore(subscribeToRules, getRules);
}

export function useRuleForPayee(payeeId: string): Rule | undefined {
  const all = useRules();
  return useMemo(() => all.find(r => r.payeeId === payeeId), [all, payeeId]);
}

/**
 * Rules keyed by payee, for screens that classify a whole list at once (the
 * debit list, the rules list) and would otherwise do a linear scan per row.
 */
export function useRuleByPayeeId(): Map<string, Rule> {
  const all = useRules();
  return useMemo(() => new Map(all.map(rule => [rule.payeeId, rule])), [all]);
}

/**
 * Exposes the single write path for a rule (PayeeEditScreen's Save and Clear).
 * Classification and amount go together because one Save commits both — see
 * saveRule in src/data/rulesStore.ts.
 */
export function useSavePayeeRule(): (payeeId: string, draft: RuleDraft) => void {
  return useCallback((payeeId: string, draft: RuleDraft) => {
    saveRule(payeeId, draft);
  }, []);
}

// --- Where the data came from -----------------------------------------------

export type DataSource = {
  /** `true` when payees/debits are the backend's, `false` when they are fixtures. */
  live: boolean;
  /** Meaningful only while `live`; the fixture source never loads or fails. */
  feed: FeedState;
  /** Re-reads the feed. A no-op against fixtures, so callers need no branch. */
  refresh: () => void;
};

// Nothing loads, nothing fails and nothing is refused on the way in, so both
// counters are 0 by construction rather than by luck.
const FIXTURE_FEED: FeedState = { status: 'ready', dropped: 0, skipped: 0 };
const noopRefresh = () => {};
// Not awaited: refresh() reports failure through the feed's own state, which is
// what `feed` above carries to the badge.
const feedRefresh = () => {
  refresh();
};

/**
 * What the on-device badge reports. It exists because a tracer bullet has to be
 * *seen* working: an empty list means "the backend is unreachable", "the
 * consent expired" and "this account genuinely has no debits" all at once, and
 * the device is the one place where guessing between them is expensive.
 */
export function useDataSource(): DataSource {
  const feed = useSyncExternalStore(
    USE_BACKEND ? subscribeToFeed : subscribeToDataChanges,
    USE_BACKEND ? getState : () => FIXTURE_FEED,
  );
  return {
    live: USE_BACKEND,
    feed,
    refresh: USE_BACKEND ? feedRefresh : noopRefresh,
  };
}
