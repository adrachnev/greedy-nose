// Thin data-access seam. Screens consume payees/debits/rules only through
// these hooks — never by importing app/src/mocks/data.ts directly. Today they
// just return the mock fixtures; swapping in real API calls (via React Query,
// context, whatever) later only touches this file.

import { useCallback, useMemo, useSyncExternalStore } from 'react';
import { Debit, Payee, Rule, RuleDraft } from '../domain/model';
import {
  addDebit,
  debits,
  payees,
  rules,
  savePayeeRule,
  subscribeToDataChanges,
} from '../mocks/data';

// Every collection is read through useSyncExternalStore, so screens re-render
// when one changes elsewhere — e.g. navigating back to the debit list after
// editing a rule, which must re-label that payee's debits immediately (R7).
// Payees happen to be immutable in this fixture layer; they are subscribed the
// same way anyway, so nothing has to be revisited when the bank feed starts
// creating them and no screen has to know which collection is which.

export function usePayees(): Payee[] {
  return useSyncExternalStore(subscribeToDataChanges, () => payees);
}

export function usePayee(payeeId: string): Payee | undefined {
  const all = usePayees();
  return useMemo(() => all.find(p => p.id === payeeId), [all, payeeId]);
}

export function useDebits(): Debit[] {
  return useSyncExternalStore(subscribeToDataChanges, () => debits);
}

export function useDebit(debitId: string): Debit | undefined {
  const all = useDebits();
  return useMemo(() => all.find(d => d.id === debitId), [all, debitId]);
}

export function useRules(): Rule[] {
  return useSyncExternalStore(subscribeToDataChanges, () => rules);
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
 * savePayeeRule in src/mocks/data.ts.
 */
export function useSavePayeeRule(): (payeeId: string, draft: RuleDraft) => void {
  return useCallback((payeeId: string, draft: RuleDraft) => {
    savePayeeRule(payeeId, draft);
  }, []);
}

/** Exposes appending a debit — see addDebit's note on why nothing calls it yet. */
export function useAddDebit(): (debit: Debit) => void {
  return useCallback((debit: Debit) => {
    addDebit(debit);
  }, []);
}
