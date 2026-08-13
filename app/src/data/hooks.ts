// Thin data-access seam. Screens consume Debtors/Transactions/Rules only
// through these hooks — never by importing app/src/mocks/data.ts directly.
// Today they just return the mock fixtures; swapping in real API calls
// (via React Query, context, whatever) later only touches this file.

import { useCallback, useMemo, useSyncExternalStore } from 'react';
import {
  AutoFlipNotice,
  Debtor,
  Rule,
  RuleThresholds,
  Transaction,
  acknowledgeAutoFlipNotice,
  autoFlipNotices,
  debtors,
  rules,
  setDebtorRule,
  setDebtorTrusted,
  subscribeToDataChanges,
  transactions,
} from '../mocks/data';

// Debtors, Rules, and Transactions are all mutable (setDebtorTrusted /
// setDebtorRule / addTransaction) — subscribe via useSyncExternalStore so
// screens re-render when any of them changes elsewhere (e.g. navigating
// back to the list after editing a rule on DebitorEditScreen, or a new
// transaction arriving via addTransaction).

export function useDebtors(): Debtor[] {
  return useSyncExternalStore(subscribeToDataChanges, () => debtors);
}

export function useDebtor(debtorId: string): Debtor | undefined {
  const all = useDebtors();
  return useMemo(() => all.find(d => d.id === debtorId), [all, debtorId]);
}

/** Exposes the Trusted/Bad mutation to screens (e.g. TransactionDetailScreen's toggle). */
export function useSetDebtorTrusted(): (
  debtorId: string,
  trusted: boolean,
) => { clearedAmountThresholdEUR: number | undefined } {
  return useCallback((debtorId: string, trusted: boolean) => {
    return setDebtorTrusted(debtorId, trusted);
  }, []);
}

export function useTransactions(): Transaction[] {
  return useSyncExternalStore(subscribeToDataChanges, () => transactions);
}

export function useTransaction(transactionId: string): Transaction | undefined {
  const all = useTransactions();
  return useMemo(() => all.find(t => t.id === transactionId), [all, transactionId]);
}

export function useRules(): Rule[] {
  return useSyncExternalStore(subscribeToDataChanges, () => rules);
}

export function useRuleForDebtor(debtorId: string): Rule | undefined {
  const all = useRules();
  return useMemo(() => all.find(r => r.debtorId === debtorId), [all, debtorId]);
}

/** Exposes the alert-condition mutation to screens (DebitorEditScreen's Save/Clear). */
export function useSetDebtorRule(): (
  debtorId: string,
  thresholds: RuleThresholds,
) => { autoFlippedTo: 'Bad' | 'Trusted' | null } {
  return useCallback((debtorId: string, thresholds: RuleThresholds) => {
    return setDebtorRule(debtorId, thresholds);
  }, []);
}

/** Pending passive auto-flip-to-Bad markers, surfaced on RulesListScreen
 * until the user opens that debtor's Edit screen — see acknowledgeAutoFlipNotice. */
export function useAutoFlipNotices(): AutoFlipNotice[] {
  return useSyncExternalStore(subscribeToDataChanges, () => autoFlipNotices);
}

/** Exposes clearing a debtor's pending auto-flip notice (DebitorEditScreen, on mount). */
export function useAcknowledgeAutoFlipNotice(): (debtorId: string) => void {
  return useCallback((debtorId: string) => {
    acknowledgeAutoFlipNotice(debtorId);
  }, []);
}
