// MOCK FIXTURE DATA — not backed by any API. Ported from the static values
// shown in mocks/02-transaction-list.html, mocks/03-transaction-detail.html
// and mocks/04-debitor-rules.html. Replace with real data once the backend
// + Enable Banking integration exist (see app/src/data/hooks.ts, which is
// the seam screens should consume through instead of importing this file
// directly).

export type Debtor = {
  id: string;
  name: string;
  initials: string;
  /** Opt-out trust model: every debtor defaults to "bad" until explicitly trusted. */
  trusted: boolean;
  iban: string;
};

export type PaymentType =
  | 'Direct debit'
  | 'Card payment'
  | 'Subscription'
  | 'Bank transfer';

export type Transaction = {
  id: string;
  debtorId: string;
  /** Euros, negative for an outgoing charge — matches the mock's "−€x.xx" formatting. */
  amountEUR: number;
  /** ISO 8601 timestamp. Date-section grouping (Today/Yesterday/…) is derived from this. */
  timestamp: string;
  paymentType: PaymentType;
  reference: string;
};

export type RuleFrequencyPeriod = 'month';

export type Rule = {
  debtorId: string;
  /** Only alert if a single charge exceeds this amount (EUR). */
  amountThresholdEUR?: number;
  /** Only alert if charged more than N times per period. Combined with amountThresholdEUR via AND. */
  frequencyThreshold?: {
    count: number;
    period: RuleFrequencyPeriod;
  };
};

// --- Debtors -----------------------------------------------------------
// Bad is the default/opt-out state — most debtors here are Bad, matching
// mocks/04-debitor-rules.html ("Bad (3)" / "Trusted (2)").

// `let`, not `const` — setDebtorTrusted() below reassigns this to a new
// array (rather than mutating an element in place) so useSyncExternalStore
// subscribers in hooks.ts see a changed snapshot reference and re-render.
export let debtors: Debtor[] = [
  {
    id: 'debtor-scamyloans',
    name: 'ScamyLoans GmbH',
    initials: 'SC',
    trusted: false,
    iban: 'DE12 3456 7890 0000 1234 00',
  },
  {
    id: 'debtor-spotify',
    name: 'Spotify AB',
    initials: 'SP',
    trusted: false,
    iban: 'SE45 5000 0000 0583 9825 7466',
  },
  {
    id: 'debtor-vodafone',
    name: 'Vodafone GmbH',
    initials: 'VF',
    trusted: false,
    iban: 'DE89 3704 0044 0532 0130 00',
  },
  {
    id: 'debtor-rewe',
    name: 'REWE Markt',
    initials: 'RE',
    trusted: true,
    iban: 'DE02 1009 0000 5573 9829 01',
  },
  {
    id: 'debtor-landlord',
    name: 'Landlord J. Meyer',
    initials: 'LL',
    trusted: true,
    iban: 'DE44 5001 0517 5407 3249 31',
  },
];

// --- Transactions --------------------------------------------------------
// Timestamps are computed relative to "now" (rather than hardcoded dates)
// so the date-section grouping logic in useTransactions()/TransactionListScreen
// runs against real, generic Today/Yesterday/older logic instead of matching
// on fixed label strings.

function relativeTimestamp(daysAgo: number, hours: number, minutes: number): string {
  const d = new Date();
  d.setDate(d.getDate() - daysAgo);
  d.setHours(hours, minutes, 0, 0);
  return d.toISOString();
}

export const transactions: Transaction[] = [
  {
    id: 'tx-spotify-1',
    debtorId: 'debtor-spotify',
    amountEUR: -9.99,
    timestamp: relativeTimestamp(0, 9, 14),
    paymentType: 'Subscription',
    reference: 'Spotify Premium Family',
  },
  {
    id: 'tx-scamyloans-1',
    debtorId: 'debtor-scamyloans',
    amountEUR: -49.0,
    timestamp: relativeTimestamp(1, 18, 2),
    paymentType: 'Direct debit',
    reference: 'Invoice #99213 loan installment',
  },
  {
    id: 'tx-rewe-1',
    debtorId: 'debtor-rewe',
    amountEUR: -34.21,
    timestamp: relativeTimestamp(1, 12, 41),
    paymentType: 'Card payment',
    reference: 'Card purchase REWE Markt #4471',
  },
  {
    id: 'tx-vodafone-1',
    debtorId: 'debtor-vodafone',
    amountEUR: -24.99,
    timestamp: relativeTimestamp(3, 7, 30),
    paymentType: 'Direct debit',
    reference: 'Mobile plan monthly fee',
  },
  {
    id: 'tx-landlord-1',
    debtorId: 'debtor-landlord',
    amountEUR: -850.0,
    timestamp: relativeTimestamp(3, 6, 0),
    paymentType: 'Bank transfer',
    reference: 'Rent August 2026',
  },
];

// --- Rules -----------------------------------------------------------
// None of the Bad debitors currently have thresholds set, matching
// mocks/04-debitor-rules.html ("Alerts on every charge" for all three) —
// leaving both fields unset means every charge alerts.

export const rules: Rule[] = [
  { debtorId: 'debtor-scamyloans' },
  { debtorId: 'debtor-spotify' },
  { debtorId: 'debtor-vodafone' },
];

// --- Mutations ------------------------------------------------------------
// The only write path this fixture layer supports so far: flipping a
// debtor's Trusted/Bad flag (the app's core interaction — see CLAUDE.md's
// opt-out trust model). Replaces the `debtors` array above with a new array
// (see setDebtorTrusted below) and notifies subscribers so hooks.ts's
// useSyncExternalStore can re-render screens that read it.
// A real backend will replace this with an actual mutation call + refetch/
// cache invalidation (React Query et al.) — this listener set is a
// deliberately minimal stand-in, not a state-management pattern to grow.

type Listener = () => void;
const listeners = new Set<Listener>();

export function subscribeToDataChanges(listener: Listener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function notifyDataChanged(): void {
  listeners.forEach(listener => listener());
}

export function setDebtorTrusted(debtorId: string, trusted: boolean): void {
  const debtor = debtors.find(d => d.id === debtorId);
  if (!debtor || debtor.trusted === trusted) {
    return;
  }
  // Reassign to a new array (rather than `debtor.trusted = trusted` in
  // place) so useSyncExternalStore's getSnapshot() returns a different
  // reference and subscribers actually re-render — see comment above.
  debtors = debtors.map(d => (d.id === debtorId ? { ...d, trusted } : d));
  notifyDataChanged();
}
