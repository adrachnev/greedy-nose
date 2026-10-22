// Which payees the one-time offer after a bank's first sync lists (Story 5.9,
// R26): the payees of the feed that have no rule and were not in the feed before
// the connect attempt started. A payee that was already there belongs to an
// earlier bank or an earlier visit; offering it again would let one tap rewrite
// choices the user made before. Pure: no React, no storage.

import { Debit, PaymentType, Payee, Rule } from './model';

export type OfferRow = {
  payee: Payee;
  charges: number;
  /** The largest amount of the payee's debits. */
  amountEUR: number;
  /** True when the debits differ in amount ("up to"). */
  varies: boolean;
  /** Of the newest debit. */
  paymentType: PaymentType;
  /** Epoch ms of the newest debit; the list is sorted on it. */
  latest: number;
};

export function buildOffer(
  payees: Payee[],
  debits: Debit[],
  rules: Rule[],
  before: ReadonlySet<string>,
): OfferRow[] {
  const ruled = new Set(rules.map(rule => rule.payeeId));
  const byPayee = new Map<string, Debit[]>();
  for (const debit of debits) {
    const list = byPayee.get(debit.payeeId);
    if (list) {
      list.push(debit);
    } else {
      byPayee.set(debit.payeeId, [debit]);
    }
  }

  const rows: OfferRow[] = [];
  const listed = new Set<string>();
  for (const payee of payees) {
    const own = byPayee.get(payee.id);
    // No debit means nothing to show under the name; the payee is not part of what the sync found.
    if (!own || ruled.has(payee.id) || before.has(payee.id) || listed.has(payee.id)) {
      continue; // a payee id twice would make the backend refuse the whole set
    }
    listed.add(payee.id);
    let newest = own[0];
    let max = own[0].amountEUR;
    let min = own[0].amountEUR;
    for (const debit of own) {
      max = Math.max(max, debit.amountEUR);
      min = Math.min(min, debit.amountEUR);
      if (Date.parse(debit.timestamp) > Date.parse(newest.timestamp)) {
        newest = debit;
      }
    }
    rows.push({
      payee,
      charges: own.length,
      amountEUR: max,
      varies: max !== min,
      paymentType: newest.paymentType,
      latest: Date.parse(newest.timestamp),
    });
  }
  return rows.sort((a, b) => b.latest - a.latest || a.payee.name.localeCompare(b.payee.name));
}
