// The payee the app cannot name, and how a screen always gets *a* payee.
//
// This exists because of R1's last mile: a debit is money that left the user's
// account, and the one thing the app may never do is make it disappear. Every
// layer between the wire and the pixel used to have its own quiet way of
// dropping a charge whose payee it could not find — the feed dropped the row,
// the list's renderItem returned null, the detail screen said "Debit not
// found." All three are the same failure, so all three now resolve to the same
// placeholder instead.
//
// The wording is the backend's ("Unknown payee", see
// TransactionMapper.BuildPayee): the user meets both in one list, and two
// spellings of one idea read as two different problems. That duplication
// across the seam is unavoidable in two languages and is recorded in TODO.md;
// what is avoidable is duplicating it three more times inside this app, which
// is why nothing outside this file spells it.

import { Payee } from './model';

export const UNKNOWN_PAYEE_NAME = 'Unknown payee';

/**
 * The bank's own rule, mirrored from the backend's `ToInitials`: the first
 * letter of up to two words, uppercased, `?` when there is no letter at all.
 *
 * Derived rather than written out, so the avatar cannot end up disagreeing
 * with the name above it. The initials for UNKNOWN_PAYEE_NAME happened to
 * match the backend's by luck before this existed; luck is not a contract.
 */
export function toInitials(name: string): string {
  const letters = name
    .split(/\s+/)
    .map(word => [...word].find(isLetter))
    .filter((character): character is string => character != null)
    .slice(0, 2);
  return letters.length > 0 ? letters.join('').toUpperCase() : '?';
}

/**
 * Deliberately not a Unicode property escape (`/\p{L}/u`). Those lean on
 * the engine's Unicode tables, and Hermes' Unicode surface is a build option
 * that has been trimmed before — src/utils/search.ts refuses
 * `normalize('NFD')` for the same reason. An unsupported escape is a *syntax*
 * error rather than a wrong answer, so the failure would be a crash — on a
 * device this code has never run on yet.
 *
 * Latin-1 Supplement plus Latin Extended-A/B is what European payee names are
 * written in (R22: any EU bank, not only German ones), and it is the same
 * scope src/utils/search.ts folds. Anything outside it falls through to the
 * '?' avatar — a plain row, not a broken one.
 *
 * The two holes in the range are not typos: U+00D7 (×) and U+00F7 (÷) sit
 * inside Latin-1 and are **not** letters. .NET's char.IsLetter — the rule the
 * backend's ToInitials uses — excludes them, so a flat À-ɏ range would make
 * the two sides disagree in the one file whose whole purpose is that they
 * agree. Pinned by a test, because that is where this bug already lived once.
 */
function isLetter(character: string): boolean {
  return /[a-zA-ZÀ-ÖØ-öø-ɏ]/.test(character);
}

/**
 * A stand-in for a payee the app has a charge for but no record of.
 *
 * It keeps the debit's own payee id rather than pooling every unknown payee
 * under one shared id: the id is what a rule is keyed on, so merging two of
 * them would let one inherit the other's "good" — the merge direction R3b
 * calls the dangerous one. Two rows both reading "Unknown payee" is the
 * split-rather-than-merge side of that trade, and each can be given its own
 * rule like any other payee (it classifies bad until then — R4b/R5).
 *
 * **The backend does the opposite with the same words, and both are right.**
 * TransactionMapper pools *every* creditor-less charge under one shared
 * UnknownPayeeKey (decided 2026-08-18, see TRACER-01-BANK-DATA.md) because there the
 * charges have no creditor at all — no name, no IBAN, no remittance — so one
 * payee each would flood the Rules list with un-reviewable one-offs, and one
 * shared payee is one review. Here the situation is not the same: the debit
 * *has* a payee id, a real and distinct key, and only the record describing it
 * is missing. Pooling those would merge two payees the bank told us apart,
 * which is the thing R3b forbids. Same wording on screen, different question
 * underneath.
 */
export function unknownPayee(payeeId: string): Payee {
  return {
    id: payeeId,
    name: UNKNOWN_PAYEE_NAME,
    initials: toInitials(UNKNOWN_PAYEE_NAME),
    iban: '',
  };
}

/**
 * Builds "give me the payee for this id, always" over a list of payees.
 *
 * Two properties the callers depend on, both easy to lose in an inline
 * `map.get(id) ?? unknownPayee(id)`:
 *
 * - it **never returns undefined**, so no caller has a branch that can quietly
 *   render nothing;
 * - the placeholder for one id is **the same object every time**, so a
 *   memoized list row does not see a new `payee` prop on every keystroke.
 *
 * `onUnknown` is called once per id that had to be invented — the developer's
 * trace for a payload where payees and debits disagree. The user's trace is
 * the row itself, which is the point.
 */
export function createPayeeLookup(
  payees: Payee[],
  onUnknown?: (payeeId: string) => void,
): (payeeId: string) => Payee {
  const known = new Map(payees.map(payee => [payee.id, payee]));
  const invented = new Map<string, Payee>();

  return (payeeId: string): Payee => {
    const match = known.get(payeeId);
    if (match) {
      return match;
    }
    const cached = invented.get(payeeId);
    if (cached) {
      return cached;
    }
    const placeholder = unknownPayee(payeeId);
    invented.set(payeeId, placeholder);
    onUnknown?.(payeeId);
    return placeholder;
  };
}
