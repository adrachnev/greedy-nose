// MOCK FIXTURE DATA — not backed by any API. Ported from the static values
// shown in mocks/02-debit-list.html, mocks/03-debit-detail.html and
// mocks/04-payee-rules.html. Replace with real data once the backend +
// Enable Banking integration exist (see app/src/data/hooks.ts, which is the
// seam screens should consume through instead of importing this file
// directly).
//
// Types live in src/domain/model.ts, not here: the vocabulary is permanent,
// these values are scaffolding.

import { Debit, Payee, Rule, RuleDraft } from '../domain/model';

// --- Payees ---------------------------------------------------------------
// The trust model is opt-out: a payee is bad until the user says otherwise,
// and "has no rule" is itself the unreviewed-and-therefore-bad state (R4b).
// Nothing here carries a good/bad flag — that lives on the rule (R4).

export const payees: Payee[] = [
  {
    id: 'payee-scamyloans',
    name: 'ScamyLoans GmbH',
    initials: 'SC',
    iban: 'DE12 3456 7890 0000 1234 00',
  },
  {
    id: 'payee-spotify',
    name: 'Spotify AB',
    initials: 'SP',
    iban: 'SE45 5000 0000 0583 9825 7466',
  },
  {
    id: 'payee-vodafone',
    name: 'Vodafone GmbH',
    initials: 'VF',
    iban: 'DE89 3704 0044 0532 0130 00',
  },
  {
    id: 'payee-rewe',
    name: 'REWE Markt',
    initials: 'RE',
    iban: 'DE02 1009 0000 5573 9829 01',
  },
  {
    id: 'payee-landlord',
    name: 'Landlord J. Meyer',
    initials: 'LL',
    iban: 'DE44 5001 0517 5407 3249 31',
  },
  {
    // Deliberately absent from `rules` below: the never-reviewed payee, which
    // is R5's first row (no rule -> bad) and the only source of R12a's "New
    // payee — you haven't seen this one before." Without one in the fixtures
    // that branch is unreachable on device, so the case most likely to be got
    // wrong is also the one nobody ever sees.
    id: 'payee-fitnessclub',
    name: 'Nordic Fitness Club',
    initials: 'NF',
    iban: 'DE21 5001 0517 1234 5678 90',
  },
];

// --- Debits ---------------------------------------------------------------
// Amounts are positive: every debit is money going out (R2a), so a sign would
// distinguish nothing and R17a keeps it out of the UI entirely.
//
// Timestamps are computed relative to "now" (rather than hardcoded dates) so
// the date-section grouping in DebitListScreen runs against real
// Today/Yesterday/older logic instead of matching on fixed label strings.

function relativeTimestamp(daysAgo: number, hours: number, minutes: number): string {
  const d = new Date();
  d.setDate(d.getDate() - daysAgo);
  d.setHours(hours, minutes, 0, 0);
  return d.toISOString();
}

// `let`, not `const` — addDebit() below reassigns this to a new array (rather
// than `push`) so useSyncExternalStore subscribers in hooks.ts see a changed
// snapshot reference and re-render. Same reasoning for `rules` below.
export let debits: Debit[] = [
  {
    id: 'debit-spotify-1',
    payeeId: 'payee-spotify',
    amountEUR: 9.99,
    timestamp: relativeTimestamp(0, 9, 14),
    paymentType: 'Subscription',
    reference: 'Spotify Premium Family',
  },
  {
    id: 'debit-scamyloans-1',
    payeeId: 'payee-scamyloans',
    amountEUR: 49.0,
    timestamp: relativeTimestamp(1, 18, 2),
    paymentType: 'Direct debit',
    reference: 'Invoice #99213 loan installment',
  },
  {
    // The case that makes R5 visible: a *good* payee whose charge is bad
    // anyway, because it exceeds their €30.00 limit. See
    // mocks/03b-debit-detail-over-limit.html.
    id: 'debit-rewe-1',
    payeeId: 'payee-rewe',
    amountEUR: 34.21,
    timestamp: relativeTimestamp(1, 12, 41),
    paymentType: 'Card payment',
    reference: 'Card purchase REWE Markt #4471',
  },
  {
    // The unreviewed payee's charge: bad for the third reason (no rule), which
    // reads differently from "you marked this payee as bad" (R12a).
    id: 'debit-fitnessclub-1',
    payeeId: 'payee-fitnessclub',
    amountEUR: 39.9,
    timestamp: relativeTimestamp(0, 7, 3),
    paymentType: 'Direct debit',
    reference: 'Membership August 2026',
  },
  {
    id: 'debit-vodafone-1',
    payeeId: 'payee-vodafone',
    amountEUR: 24.99,
    timestamp: relativeTimestamp(3, 7, 30),
    paymentType: 'Direct debit',
    reference: 'Mobile plan monthly fee',
  },
  {
    id: 'debit-landlord-1',
    payeeId: 'payee-landlord',
    amountEUR: 850.0,
    timestamp: relativeTimestamp(3, 6, 0),
    paymentType: 'Bank transfer',
    reference: 'Rent August 2026',
  },
  // Extra history so the list has several payees' charges to group across
  // date sections during manual on-device testing.
  {
    id: 'debit-spotify-2',
    payeeId: 'payee-spotify',
    amountEUR: 9.99,
    timestamp: relativeTimestamp(30, 9, 12),
    paymentType: 'Subscription',
    reference: 'Spotify Premium Family',
  },
  {
    id: 'debit-spotify-3',
    payeeId: 'payee-spotify',
    amountEUR: 14.99,
    timestamp: relativeTimestamp(61, 9, 9),
    paymentType: 'Subscription',
    reference: 'Spotify Premium Family (price update)',
  },
  {
    // Under REWE's €30.00 limit, so this one is good while debit-rewe-1 is
    // bad — same payee, different verdict, which is the point of R5.
    id: 'debit-rewe-2',
    payeeId: 'payee-rewe',
    amountEUR: 18.47,
    timestamp: relativeTimestamp(6, 17, 55),
    paymentType: 'Card payment',
    reference: 'Card purchase REWE Markt #4471',
  },
  {
    id: 'debit-rewe-3',
    payeeId: 'payee-rewe',
    amountEUR: 52.03,
    timestamp: relativeTimestamp(13, 11, 20),
    paymentType: 'Card payment',
    reference: 'Card purchase REWE Markt #2210',
  },
  {
    id: 'debit-vodafone-2',
    payeeId: 'payee-vodafone',
    amountEUR: 24.99,
    timestamp: relativeTimestamp(33, 7, 30),
    paymentType: 'Direct debit',
    reference: 'Mobile plan monthly fee',
  },
  {
    id: 'debit-vodafone-3',
    payeeId: 'payee-vodafone',
    amountEUR: 29.98,
    timestamp: relativeTimestamp(64, 8, 5),
    paymentType: 'Direct debit',
    reference: 'Mobile plan + roaming add-on',
  },
];

// --- Rules ----------------------------------------------------------------
// Matches mocks/04-payee-rules.html: three bad payees alerting on every
// charge, REWE good with a €30.00 limit, the landlord good with none.
//
// A payee with no rule here is unreviewed — and therefore also bad (R4b).
// payee-fitnessclub is exactly that, and has no entry below on purpose.

export let rules: Rule[] = [
  { payeeId: 'payee-scamyloans', classification: 'bad' },
  { payeeId: 'payee-spotify', classification: 'bad' },
  { payeeId: 'payee-vodafone', classification: 'bad' },
  { payeeId: 'payee-rewe', classification: 'good', amountEUR: 30 },
  { payeeId: 'payee-landlord', classification: 'good' },
];

// --- Mutations ------------------------------------------------------------
// Every write reassigns the module-level array to a *new* array rather than
// mutating an element in place, so useSyncExternalStore's getSnapshot()
// returns a changed reference and subscribers actually re-render. Mutating in
// place silently broke exactly that once already; the tests assert on
// reference identity, not just on the end value, so it cannot regress quietly.
//
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
 * Upserts a payee's whole rule — classification and amount together, in one
 * reassignment and one notification. PayeeEditScreen is a form with a single
 * commit point (Save), so a per-field setter would only let a save land half
 * applied and make subscribers re-render twice for one user action.
 *
 * This is the *only* thing that moves a payee's classification: nothing the
 * app does on its own ever flips it (R8). Creating a rule where there was none
 * is what "reviewed" means (R4b), and rules are never deleted (R13) — marking
 * a payee bad already expresses everything a delete would.
 *
 * `amountEUR: undefined` means no limit — every charge from that payee is good
 * (R4a). The caller decides what to pass while a payee is bad; the amount is
 * kept rather than wiped there (R8a/R14), it simply has no effect (R5).
 */
export function savePayeeRule(payeeId: string, draft: RuleDraft): void {
  const existing = rules.find(r => r.payeeId === payeeId);
  if (
    existing &&
    existing.classification === draft.classification &&
    existing.amountEUR === draft.amountEUR
  ) {
    return;
  }
  const next: Rule = { payeeId, classification: draft.classification };
  if (draft.amountEUR != null) {
    next.amountEUR = draft.amountEUR;
  }
  rules = existing ? rules.map(r => (r.payeeId === payeeId ? next : r)) : [...rules, next];
  notifyDataChanged();
}

/**
 * Appends a debit (new array reference, not `push` — see above). Unused by
 * any screen today: there is no live feed and no "simulate an incoming
 * charge" UI. It exists so the arrival of a debit is modelled at all, and
 * because the classification it triggers is derived at read time (R6), this
 * is now the whole of it — an arriving charge changes no payee's
 * classification and never can (R8).
 */
export function addDebit(debit: Debit): void {
  debits = [...debits, debit];
  notifyDataChanged();
}
