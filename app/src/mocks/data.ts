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

export type Rule = {
  debtorId: string;
  /** Only alert if a single charge exceeds this amount (EUR). */
  amountThresholdEUR?: number;
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

// `let`, not `const` — addTransaction() below reassigns this to a new
// array (rather than `push`) for the same useSyncExternalStore reasoning
// as `debtors`/`rules` above.
export let transactions: Transaction[] = [
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
  // Extra history for debtor-spotify (Bad) and debtor-rewe (Trusted) so
  // "most recent transaction" logic (evaluateAutoFlip in this file) has
  // more than one candidate to pick from during manual on-device testing,
  // not just in unit tests.
  {
    id: 'tx-spotify-2',
    debtorId: 'debtor-spotify',
    amountEUR: -9.99,
    timestamp: relativeTimestamp(30, 9, 12),
    paymentType: 'Subscription',
    reference: 'Spotify Premium Family',
  },
  {
    id: 'tx-spotify-3',
    debtorId: 'debtor-spotify',
    amountEUR: -14.99,
    timestamp: relativeTimestamp(61, 9, 9),
    paymentType: 'Subscription',
    reference: 'Spotify Premium Family (price update)',
  },
  {
    id: 'tx-rewe-2',
    debtorId: 'debtor-rewe',
    amountEUR: -18.47,
    timestamp: relativeTimestamp(6, 17, 55),
    paymentType: 'Card payment',
    reference: 'Card purchase REWE Markt #4471',
  },
  {
    id: 'tx-rewe-3',
    debtorId: 'debtor-rewe',
    amountEUR: -52.03,
    timestamp: relativeTimestamp(13, 11, 20),
    paymentType: 'Card payment',
    reference: 'Card purchase REWE Markt #2210',
  },
  {
    id: 'tx-vodafone-2',
    debtorId: 'debtor-vodafone',
    amountEUR: -24.99,
    timestamp: relativeTimestamp(33, 7, 30),
    paymentType: 'Direct debit',
    reference: 'Mobile plan monthly fee',
  },
  {
    id: 'tx-vodafone-3',
    debtorId: 'debtor-vodafone',
    amountEUR: -29.98,
    timestamp: relativeTimestamp(64, 8, 5),
    paymentType: 'Direct debit',
    reference: 'Mobile plan + roaming add-on',
  },
];

// --- Rules -----------------------------------------------------------
// None of the Bad debitors currently have thresholds set, matching
// mocks/04-debitor-rules.html ("Alerts on every charge" for all three) —
// leaving the amount field unset means every charge alerts.

// `let`, not `const` — same reasoning as `debtors` above: setDebtorRule()
// reassigns this to a new array so useSyncExternalStore subscribers
// (useRuleForDebtor) see a changed snapshot reference and re-render.
export let rules: Rule[] = [
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

/**
 * Manually sets a debtor's Trusted/Bad flag (e.g. DebitorEditScreen's
 * toggle). A manual override always wins over any existing rule: if the
 * debtor has an amount-threshold rule, it's cleared as part of the same
 * mutation, so a stale threshold can't silently fight the user's explicit
 * choice later (see evaluateAutoFlip's passive path, which would otherwise
 * flip a manually-set Bad debtor back to Trusted the next time a
 * transaction arrives). Returns the cleared threshold, if any, so callers
 * can mention it in a confirmation message.
 */
export function setDebtorTrusted(
  debtorId: string,
  trusted: boolean,
): { clearedAmountThresholdEUR: number | undefined } {
  const debtor = debtors.find(d => d.id === debtorId);
  if (!debtor || debtor.trusted === trusted) {
    return { clearedAmountThresholdEUR: undefined };
  }
  // Reassign to a new array (rather than `debtor.trusted = trusted` in
  // place) so useSyncExternalStore's getSnapshot() returns a different
  // reference and subscribers actually re-render — see comment above.
  debtors = debtors.map(d => (d.id === debtorId ? { ...d, trusted } : d));

  const existingRule = rules.find(r => r.debtorId === debtorId);
  const clearedAmountThresholdEUR = existingRule?.amountThresholdEUR;
  if (existingRule) {
    // Same removal semantics as setDebtorRule's "no thresholds" branch —
    // don't leave a stub `{ debtorId }` entry behind.
    rules = rules.filter(r => r.debtorId !== debtorId);
  }

  notifyDataChanged();
  return { clearedAmountThresholdEUR };
}

export type RuleThresholds = {
  amountThresholdEUR?: number;
};

/**
 * Sets (or clears) a debtor's alert-condition threshold. Passing an empty
 * `thresholds` (amountThresholdEUR undefined) removes the rule entry
 * entirely rather than leaving a `{ debtorId }` stub behind — kept
 * consistent with "no rule" so useRuleForDebtor()'s "no rule / rule with no
 * threshold both mean alert on every charge" contract holds either way.
 *
 * Returns `{ autoFlippedTo: 'Bad' | 'Trusted' | null }` — see
 * evaluateAutoFlip() below: saving a threshold re-evaluates the debtor's
 * most recent transaction against it and flips Trusted/Bad in whichever
 * direction the new threshold implies (`null` means no flip occurred). This
 * is the one place the flip is allowed to go both directions — it's an
 * explicit, already-toasted action the user is looking at (see
 * evaluateAutoFlip's `allowFlipToTrusted` param).
 */
export function setDebtorRule(
  debtorId: string,
  thresholds: RuleThresholds,
): { autoFlippedTo: 'Bad' | 'Trusted' | null } {
  const hasThresholds = thresholds.amountThresholdEUR != null;
  const existingIndex = rules.findIndex(r => r.debtorId === debtorId);

  if (!hasThresholds) {
    if (existingIndex === -1) {
      return { autoFlippedTo: null };
    }
    // Reassign to a new array (not `Array#splice`) — see comment above.
    rules = rules.filter(r => r.debtorId !== debtorId);
    notifyDataChanged();
    return { autoFlippedTo: null };
  }

  const updatedRule: Rule = { debtorId, ...thresholds };
  const existingRule = existingIndex === -1 ? undefined : rules[existingIndex];
  const ruleUnchanged =
    existingRule && existingRule.amountThresholdEUR === updatedRule.amountThresholdEUR;
  if (!ruleUnchanged) {
    rules =
      existingIndex === -1
        ? [...rules, updatedRule]
        : rules.map(r => (r.debtorId === debtorId ? updatedRule : r));
    notifyDataChanged();
  }

  return evaluateAutoFlip(debtorId, thresholds.amountThresholdEUR, { allowFlipToTrusted: true });
}

/**
 * Shared auto-flip check, run both when a rule is saved (setDebtorRule) and
 * whenever a new transaction for a debtor arrives (addTransaction) — see
 * the comment above the toast in mocks/04b-debitor-edit.html. Finds the
 * debtor's most recent transaction (by `timestamp`) and derives the state
 * the amount threshold implies — Bad if it strictly exceeds the threshold,
 * Trusted if it's under or equal — then flips the debtor to that state via
 * the same immutable-reassignment pattern as setDebtorTrusted, if (and only
 * if) they aren't already in it.
 *
 * `options.allowFlipToTrusted` distinguishes the two call sites:
 * setDebtorRule (Save) passes `true` — it's an explicit, already-toasted
 * user action, so the flip is fully bidirectional. addTransaction (a
 * passively-arriving transaction, no guaranteed mounted screen) passes
 * `false` — it may only ever push a debtor towards Bad (the core safety
 * mission: never miss a bad charge), never silently move them back to
 * Trusted without the user having done anything. When that Bad-only path
 * does flip, it also records an AutoFlipNotice (see below) as its only way
 * to surface, since there's no toast to rely on.
 */
function evaluateAutoFlip(
  debtorId: string,
  amountThresholdEUR: number | undefined,
  options: { allowFlipToTrusted: boolean },
): { autoFlippedTo: 'Bad' | 'Trusted' | null } {
  if (amountThresholdEUR == null) {
    return { autoFlippedTo: null };
  }
  const debtor = debtors.find(d => d.id === debtorId);
  if (!debtor) {
    return { autoFlippedTo: null };
  }
  const debtorTransactions = transactions.filter(t => t.debtorId === debtorId);
  if (debtorTransactions.length === 0) {
    return { autoFlippedTo: null };
  }
  const mostRecent = debtorTransactions.reduce((latest, t) =>
    new Date(t.timestamp) > new Date(latest.timestamp) ? t : latest,
  );
  const exceeds = Math.abs(mostRecent.amountEUR) > amountThresholdEUR;
  const desiredTrusted = !exceeds;
  if (debtor.trusted === desiredTrusted) {
    return { autoFlippedTo: null };
  }
  if (desiredTrusted && !options.allowFlipToTrusted) {
    return { autoFlippedTo: null };
  }

  debtors = debtors.map(d => (d.id === debtorId ? { ...d, trusted: desiredTrusted } : d));
  if (!desiredTrusted && !options.allowFlipToTrusted) {
    recordAutoFlipNotice(debtorId, amountThresholdEUR);
  }
  notifyDataChanged();
  return { autoFlippedTo: desiredTrusted ? 'Trusted' : 'Bad' };
}

// --- Auto-flip notices ----------------------------------------------------
// The passive Bad-flip in evaluateAutoFlip (addTransaction's call site) has
// no toast to rely on, since there's no guaranteed mounted screen when a
// transaction arrives. Instead it leaves a persistent marker here, surfaced
// as "Auto-marked Bad · Xm ago" on that debtor's RulesListScreen row until
// the user opens DebitorEditScreen for them (which acknowledges/clears it).

export type AutoFlipNotice = {
  debtorId: string;
  amountThresholdEUR: number;
  timestamp: string; // ISO — when the passive flip happened
};

// `let`, not `const` — same useSyncExternalStore reasoning as debtors/rules/
// transactions above.
export let autoFlipNotices: AutoFlipNotice[] = [];

function recordAutoFlipNotice(debtorId: string, amountThresholdEUR: number): void {
  autoFlipNotices = [
    ...autoFlipNotices.filter(n => n.debtorId !== debtorId),
    { debtorId, amountThresholdEUR, timestamp: new Date().toISOString() },
  ];
}

/** Clears a debtor's pending auto-flip notice once the user has seen it
 * (DebitorEditScreen acknowledges on mount). No-op if there isn't one. */
export function acknowledgeAutoFlipNotice(debtorId: string): void {
  if (!autoFlipNotices.some(n => n.debtorId === debtorId)) {
    return;
  }
  autoFlipNotices = autoFlipNotices.filter(n => n.debtorId !== debtorId);
  notifyDataChanged();
}

/**
 * Appends a new transaction (new array reference, not `push` — see comment
 * above `transactions`) and re-runs the auto-flip check using the debtor's
 * existing rule, if any — Bad-only (`allowFlipToTrusted: false`), since this
 * is the passive path with no mounted screen/toast guaranteed (see
 * evaluateAutoFlip). Unused by any screen today — there's no live
 * transaction feed or "simulate incoming transaction" UI — it exists to
 * satisfy the architecture requirement that the same auto-flip check
 * re-run on new transactions, not just on rule save. Covered by a direct
 * unit test (see mocks/__tests__/data.test.ts).
 */
export function addTransaction(transaction: Transaction): void {
  transactions = [...transactions, transaction];

  // evaluateAutoFlip() calls notifyDataChanged() itself, but only when it
  // actually flips the debtor — in that case its notify already covers both
  // the transaction append and the flip, so skip our own to avoid firing
  // subscribers twice for one logical update. Only notify here ourselves
  // when it didn't flip, since then nothing else will.
  const rule = rules.find(r => r.debtorId === transaction.debtorId);
  const { autoFlippedTo } = evaluateAutoFlip(transaction.debtorId, rule?.amountThresholdEUR, {
    allowFlipToTrusted: false,
  });
  if (autoFlippedTo === null) {
    notifyDataChanged();
  }
}
