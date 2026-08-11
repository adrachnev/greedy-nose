// Thin data-access seam. Screens consume Debtors/Transactions/Rules only
// through these hooks — never by importing app/src/mocks/data.ts directly.
// Today they just return the mock fixtures; swapping in real API calls
// (via React Query, context, whatever) later only touches this file.

import { useCallback, useMemo, useSyncExternalStore } from 'react';
import {
  Debtor,
  Rule,
  Transaction,
  debtors,
  rules,
  setDebtorTrusted,
  subscribeToDataChanges,
  transactions,
} from '../mocks/data';

// Debtors are mutable (setDebtorTrusted) — subscribe via useSyncExternalStore
// so screens re-render when the Trusted/Bad flag changes elsewhere (e.g.
// navigating back to the list after toggling on the detail screen).
// Transactions/Rules have no write path yet, so they're returned as-is.

export function useDebtors(): Debtor[] {
  return useSyncExternalStore(subscribeToDataChanges, () => debtors);
}

export function useDebtor(debtorId: string): Debtor | undefined {
  const all = useDebtors();
  return useMemo(() => all.find(d => d.id === debtorId), [all, debtorId]);
}

/** Exposes the Trusted/Bad mutation to screens (e.g. TransactionDetailScreen's toggle). */
export function useSetDebtorTrusted(): (debtorId: string, trusted: boolean) => void {
  return useCallback((debtorId: string, trusted: boolean) => {
    setDebtorTrusted(debtorId, trusted);
  }, []);
}

export function useTransactions(): Transaction[] {
  return transactions;
}

export function useTransaction(transactionId: string): Transaction | undefined {
  return useMemo(() => transactions.find(t => t.id === transactionId), [transactionId]);
}

export function useRules(): Rule[] {
  return rules;
}

export function useRuleForDebtor(debtorId: string): Rule | undefined {
  return useMemo(() => rules.find(r => r.debtorId === debtorId), [debtorId]);
}
