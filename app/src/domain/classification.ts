// R5: the single place that decides whether something is good or bad.
//
// Nothing here mutates or stores anything. Classification is *derived* from
// the payee's current rule and never written onto a debit (R6) — that is what
// makes R7 work for free: edit a rule, and every debit of that payee is
// re-labelled the next time it is read, with no re-labelling pass over
// history.

import { Classification, Debit, Rule } from './model';
import { formatCurrencyEUR } from '../utils/format';

/**
 * Why a debit came out bad, and what the wording needs to say it (R12a).
 *
 * The limit is carried by the `over-limit` variant *only*, rather than sitting
 * optional across the whole bad branch: it is the one reason that has a limit
 * to quote, and splitting the union that way makes "Over your limit of €0.00."
 * unrepresentable instead of merely unlikely. The compiler now holds the
 * invariant, so `describeBadReason` needs no fallback.
 */
export type DebitClassification =
  | { classification: 'good' }
  | { classification: 'bad'; reason: 'no-rule' | 'marked-bad' }
  | { classification: 'bad'; reason: 'over-limit'; limitEUR: number };

/**
 * A payee with no rule is bad: the trust model is opt-out, so an unknown
 * payee alerts until the user says otherwise (R5, R4b).
 */
export function classifyPayee(rule: Rule | undefined): Classification {
  return rule?.classification ?? 'bad';
}

/**
 * R5's table, in order:
 *
 * | Rule for the payee   | Result                                   |
 * |----------------------|------------------------------------------|
 * | No rule              | bad                                      |
 * | Bad                  | bad — the amount is *not* consulted      |
 * | Good, no amount      | good                                     |
 * | Good, amount set     | good if it does not exceed the amount    |
 *
 * The second row is the one that is easy to get backwards: a bad payee alerts
 * on every charge whatever its size. Consulting a leftover limit there would
 * silence exactly the payees the user flagged on purpose, which is the single
 * failure R1 exists to prevent.
 */
export function classifyDebit(debit: Debit, rule: Rule | undefined): DebitClassification {
  if (!rule) {
    return { classification: 'bad', reason: 'no-rule' };
  }
  if (rule.classification === 'bad') {
    return { classification: 'bad', reason: 'marked-bad' };
  }
  if (rule.amountEUR == null) {
    return { classification: 'good' };
  }
  // "Does not exceed" means less than or equal, so a charge exactly equal to
  // the limit is good (R5a) — strictly `>`, never `>=`.
  return debit.amountEUR > rule.amountEUR
    ? { classification: 'bad', reason: 'over-limit', limitEUR: rule.amountEUR }
    : { classification: 'good' };
}

/**
 * R12a's three bodies, kept next to the reasons that produce them so the two
 * cannot drift apart. The notification dispatcher will word pushes from the
 * same strings once it exists; today only the debit detail screen uses them.
 */
export function describeBadReason(result: DebitClassification): string | undefined {
  if (result.classification === 'good') {
    return undefined;
  }
  switch (result.reason) {
    case 'no-rule':
      return "New payee — you haven't seen this one before.";
    case 'marked-bad':
      return 'You marked this payee as bad.';
    case 'over-limit':
      return `Over your limit of ${formatCurrencyEUR(result.limitEUR)}.`;
  }
}
