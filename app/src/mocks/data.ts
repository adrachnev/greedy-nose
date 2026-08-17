// MOCK FIXTURE DATA — not backed by any API. Replace with real data once the
// backend + Enable Banking integration exist (see app/src/data/hooks.ts, which
// is the seam screens should consume through instead of importing this file
// directly).
//
// Types live in src/domain/model.ts, not here: the vocabulary is permanent,
// these values are scaffolding.
//
// Shaped to exercise the list, not to look pretty: six payees and a year of
// history, so date grouping, month headers and search (R23) all run against
// something that behaves like a real account. Three of them are boring
// subscriptions on purpose — a recurring charge at the same amount every month
// is what a search for one payee is meant to surface, one row per month — and
// the other three exist to make each of R12a's three bad-debit reasons
// reachable by hand (see the note above `rules`).

import { Debit, Payee, Rule, RuleDraft } from '../domain/model';

// --- Payees ---------------------------------------------------------------
// The trust model is opt-out: a payee is bad until the user says otherwise,
// and "has no rule" is itself the unreviewed-and-therefore-bad state (R4b).
// Nothing here carries a good/bad flag — that lives on the rule (R4).

export const payees: Payee[] = [
  {
    id: 'payee-netflix',
    name: 'Netflix',
    initials: 'NE',
    iban: 'NL46 INGB 0006 8890 78',
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
    // The umlaut is load-bearing, not decoration: it is what makes R23a
    // testable by hand on the device. "müller", "muller" and "mueller" all
    // have to find this payee, and the bank's own text for them is
    // "BAECKEREI MUELLER 4711" (see the references below) — so the fold has to
    // work in both directions, on the query and on the data.
    id: 'payee-baeckerei',
    name: 'Bäckerei Müller',
    initials: 'BM',
    iban: 'DE02 1009 0000 5573 9829 01',
  },
  {
    // Deliberately absent from `rules` below: the never-reviewed payee, which
    // is R5's first row (no rule -> bad) and the only source of R12a's "New
    // payee — you haven't seen this one before." Without one in the fixtures
    // that branch is unreachable on device, so the case most likely to be got
    // wrong is also the one nobody ever sees.
    id: 'payee-fitline',
    name: 'FitLine Gym',
    initials: 'FG',
    iban: 'DE21 5001 0517 1234 5678 90',
  },
  {
    // The payee the user reviewed and marked **bad** — an explicit
    // `{ classification: 'bad' }` rule below, not an absent one.
    //
    // It exists because the two look identical on screen but are not the same
    // thing (R4b), and R12a gives them different words: this one says "You
    // marked this payee as bad.", FitLine above says "New payee — you haven't
    // seen this one before." A fixture set with only FitLine can reach one of
    // those strings and not the other. Bäckerei Müller's over-limit charge
    // covers the third.
    id: 'payee-scamyloans',
    name: 'ScamyLoans GmbH',
    initials: 'SC',
    iban: 'DE12 3456 7890 0000 1234 00',
  },
];

// --- Debits ---------------------------------------------------------------
// Amounts are positive: every debit is money going out (R2a), so a sign would
// distinguish nothing and R17a keeps it out of the UI entirely.
//
// Timestamps are computed relative to "now" rather than hardcoded, so the date
// grouping in DebitListScreen runs against real Today/Yesterday/month logic
// instead of matching fixed label strings — and so the fixtures never go stale.
//
// Note: no fixture uses the 'Bank transfer' payment type any more (the rent
// payment went with the old fixture set). The union member stays — it is bank
// vocabulary, not fixture vocabulary, and the real feed will produce it.

function relativeTimestamp(daysAgo: number, hours: number, minutes: number): string {
  const d = new Date();
  d.setDate(d.getDate() - daysAgo);
  d.setHours(hours, minutes, 0, 0);
  // Clamped: "today at 09:14" generated at 07:00 would otherwise be a charge
  // dated in the future, sorted above everything and quietly nonsensical.
  return new Date(Math.min(d.getTime(), Date.now())).toISOString();
}

/**
 * The timestamps of a monthly subscription, newest first.
 *
 * `dayOfMonth` must be <= 28, or February silently shifts the charge into
 * March. If this month's charge day has not arrived yet, the run starts last
 * month — otherwise the newest entry would be dated in the future, and
 * stepping only that one back would collide with the entry behind it.
 */
function monthlyRun(
  count: number,
  dayOfMonth: number,
  hours: number,
  minutes: number,
): string[] {
  const now = new Date();
  const start = new Date(now.getFullYear(), now.getMonth(), dayOfMonth, hours, minutes, 0, 0);
  if (start.getTime() > now.getTime()) {
    start.setMonth(start.getMonth() - 1);
  }
  return Array.from({ length: count }, (_unused, monthsBack) =>
    new Date(
      start.getFullYear(),
      start.getMonth() - monthsBack,
      dayOfMonth,
      hours,
      minutes,
      0,
      0,
    ).toISOString(),
  );
}

const netflixDebits: Debit[] = monthlyRun(12, 4, 6, 12).map((timestamp, index) => ({
  id: `debit-netflix-${index + 1}`,
  payeeId: 'payee-netflix',
  amountEUR: 12.99,
  timestamp,
  paymentType: 'Subscription',
  reference: 'Netflix Standard plan',
}));

// The one thing that changes in a year of subscriptions: a price rise. Index 0
// is this month's charge, so the six most recent are at the new price and the
// oldest of those is the month it went up.
const SPOTIFY_MONTHS_AT_NEW_PRICE = 6;

const spotifyDebits: Debit[] = monthlyRun(12, 9, 9, 14).map((timestamp, index) => ({
  id: `debit-spotify-${index + 1}`,
  payeeId: 'payee-spotify',
  amountEUR: index < SPOTIFY_MONTHS_AT_NEW_PRICE ? 10.99 : 9.99,
  timestamp,
  paymentType: 'Subscription',
  reference:
    index === SPOTIFY_MONTHS_AT_NEW_PRICE - 1
      ? 'Spotify Premium Family (price update)'
      : 'Spotify Premium Family',
}));

const vodafoneDebits: Debit[] = monthlyRun(12, 2, 7, 30).map((timestamp, index) => ({
  id: `debit-vodafone-${index + 1}`,
  payeeId: 'payee-vodafone',
  amountEUR: 39.99,
  timestamp,
  paymentType: 'Direct debit',
  reference: 'Mobile plan monthly fee',
}));

/**
 * The bakery is the payee that makes R5 visible: marked **good** with a €30.00
 * limit, so their avatar stays green while two of these charges come out bad
 * for exceeding it. Everything else is the small, irregular card spending that
 * limit exists to stay quiet about.
 *
 * Spelled out literally rather than generated — these are the interesting
 * values in the whole fixture set, and a generator would hide exactly the two
 * that matter. [daysAgo, hour, minute, amountEUR].
 */
const BAKERY_CHARGES: [number, number, number, number][] = [
  [1, 12, 41, 63.1], // over the limit, and yesterday: bad, and visible without scrolling
  [3, 8, 20, 4.8],
  [6, 17, 55, 7.25],
  [9, 7, 48, 3.4],
  [13, 11, 20, 12.6],
  [17, 8, 5, 5.95],
  [21, 16, 32, 9.1],
  [26, 9, 3, 18.75],
  [31, 8, 20, 6.4],
  [38, 7, 55, 3.2],
  [45, 15, 12, 14.5],
  [52, 8, 41, 8.05],
  [61, 8, 5, 4.75],
  [68, 12, 27, 11.3],
  [75, 7, 39, 6.9],
  [84, 9, 14, 3.6],
  [93, 16, 2, 16.2],
  [102, 8, 33, 5.4],
  [111, 10, 51, 9.85],
  [124, 7, 44, 7.7],
  [137, 13, 6, 12.15],
  [150, 8, 12, 4.3],
  [165, 9, 27, 8.6],
  [180, 11, 58, 41.5], // the second over-limit charge, half a year back
  [210, 8, 9, 6.05],
  [250, 14, 20, 13.95],
  [300, 7, 52, 5.25],
  [365, 9, 5, 10.4], // a full year of history, which is what month headers are for
];

const bakeryDebits: Debit[] = BAKERY_CHARGES.map(
  ([daysAgo, hours, minutes, amountEUR], index) => ({
    id: `debit-baeckerei-${index + 1}`,
    payeeId: 'payee-baeckerei',
    amountEUR,
    timestamp: relativeTimestamp(daysAgo, hours, minutes),
    paymentType: 'Card payment',
    // The bank's transliterated spelling, on purpose: it is what R23a's fold
    // has to cope with in production.
    reference: 'BAECKEREI MUELLER 4711',
  }),
);

/**
 * The payee the user marked bad: every one of these is red wherever it lands,
 * whatever the amount, because a bad rule never consults one (R5). Irregular
 * on purpose — a predatory lender is not a subscription, and the varying
 * amounts make it obvious that no threshold is being applied.
 * [daysAgo, hour, minute, amountEUR].
 */
const SCAMYLOANS_CHARGES: [number, number, number, number][] = [
  [4, 18, 2, 49.0],
  [35, 18, 2, 89.9],
  [66, 17, 41, 42.5],
  [128, 19, 15, 74.25],
  [219, 18, 2, 57.8],
  [305, 18, 30, 63.4],
];

const scamyloansDebits: Debit[] = SCAMYLOANS_CHARGES.map(
  ([daysAgo, hours, minutes, amountEUR], index) => ({
    id: `debit-scamyloans-${index + 1}`,
    payeeId: 'payee-scamyloans',
    amountEUR,
    timestamp: relativeTimestamp(daysAgo, hours, minutes),
    paymentType: 'Direct debit',
    reference: `Invoice #${99213 + index} loan installment`,
  }),
);

// The unreviewed payee's only charge, today: bad for the third reason (no
// rule), which reads differently from "you marked this payee as bad" (R12a).
const fitlineDebits: Debit[] = [
  {
    id: 'debit-fitline-1',
    payeeId: 'payee-fitline',
    amountEUR: 39.9,
    timestamp: relativeTimestamp(0, 7, 3),
    paymentType: 'Direct debit',
    reference: 'Membership monthly fee',
  },
];

// `let`, not `const` — addDebit() below reassigns this to a new array (rather
// than `push`) so useSyncExternalStore subscribers in hooks.ts see a changed
// snapshot reference and re-render. Same reasoning for `rules` below.
//
// Not sorted here: groupByDateSection sorts newest-first itself, so fixture
// order is a readability choice rather than a contract.
export let debits: Debit[] = [
  ...fitlineDebits,
  ...scamyloansDebits,
  ...bakeryDebits,
  ...netflixDebits,
  ...spotifyDebits,
  ...vodafoneDebits,
];

// --- Rules ----------------------------------------------------------------
// Between them these reach all three of R12a's reasons for a bad debit, which
// is the whole point of the set:
//
// - payee-fitline has NO entry below — unreviewed, and therefore bad (R4b).
//   "New payee — you haven't seen this one before."
// - payee-scamyloans is explicitly bad. "You marked this payee as bad."
// - payee-baeckerei is good with a €30.00 limit, and two of their charges
//   exceed it. "Over your limit of €30.00."
//
// Drop any one of the three and a wording branch becomes unreachable on the
// device while still passing its unit test.

export let rules: Rule[] = [
  { payeeId: 'payee-netflix', classification: 'good' },
  { payeeId: 'payee-spotify', classification: 'good' },
  { payeeId: 'payee-vodafone', classification: 'good' },
  { payeeId: 'payee-baeckerei', classification: 'good', amountEUR: 30 },
  { payeeId: 'payee-scamyloans', classification: 'bad' },
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
