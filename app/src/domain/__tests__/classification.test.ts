/**
 * R5's classification table, exhaustively. This is the logic the whole
 * product rests on: get a row wrong and the app either alerts on everything
 * or — far worse — goes quiet about a payee the user flagged.
 *
 * The architecture calls out two ways to get it backwards (see
 * ARCHITECTURE.md's "Rule engine"), and both have a test here: the amount
 * belonging to the *good* branch only, and equality counting as good.
 */

import { classifyDebit, classifyPayee, describeBadReason } from '../classification';
import { Debit, Rule } from '../model';

function debitOf(amountEUR: number): Debit {
  return {
    id: 'debit-test',
    payeeId: 'payee-test',
    amountEUR,
    timestamp: new Date().toISOString(),
    paymentType: 'Direct debit',
    reference: 'test',
  };
}

const goodNoLimit: Rule = { payeeId: 'payee-test', classification: 'good' };
const goodWithLimit: Rule = { payeeId: 'payee-test', classification: 'good', amountEUR: 30 };
const bad: Rule = { payeeId: 'payee-test', classification: 'bad' };

describe('classifyPayee', () => {
  it('classifies a payee with no rule as bad (unreviewed is bad — R4b/R5)', () => {
    expect(classifyPayee(undefined)).toBe('bad');
  });

  it('reads the classification straight off the rule', () => {
    expect(classifyPayee(goodNoLimit)).toBe('good');
    expect(classifyPayee(bad)).toBe('bad');
  });
});

describe('classifyDebit — R5 table', () => {
  it('no rule -> bad, reason "no-rule"', () => {
    expect(classifyDebit(debitOf(9.99), undefined)).toEqual({
      classification: 'bad',
      reason: 'no-rule',
    });
  });

  it('bad rule -> bad, reason "marked-bad"', () => {
    expect(classifyDebit(debitOf(9.99), bad)).toEqual({
      classification: 'bad',
      reason: 'marked-bad',
    });
  });

  it('good rule with no amount -> good, whatever the size (R4a)', () => {
    expect(classifyDebit(debitOf(9.99), goodNoLimit)).toEqual({ classification: 'good' });
    expect(classifyDebit(debitOf(9_999_999), goodNoLimit)).toEqual({ classification: 'good' });
  });

  it('good rule, charge under the limit -> good', () => {
    expect(classifyDebit(debitOf(18.47), goodWithLimit)).toEqual({ classification: 'good' });
  });

  it('good rule, charge over the limit -> bad, carrying the limit for the wording', () => {
    expect(classifyDebit(debitOf(34.21), goodWithLimit)).toEqual({
      classification: 'bad',
      reason: 'over-limit',
      limitEUR: 30,
    });
  });
});

describe('classifyDebit — the two easy-to-invert rules', () => {
  /**
   * The `A1` bug, as a test: the architecture had the rule engine evaluating
   * the amount for *bad* payees. Built that way, a payee marked bad while
   * carrying an old limit falls silent for every charge under it — the exact
   * failure R1 exists to prevent.
   */
  it('ignores a leftover limit on a bad payee — a bad payee alerts on every charge', () => {
    const badWithLeftoverLimit: Rule = {
      payeeId: 'payee-test',
      classification: 'bad',
      amountEUR: 100,
    };

    // Well under the leftover limit, and still bad.
    expect(classifyDebit(debitOf(5), badWithLeftoverLimit)).toEqual({
      classification: 'bad',
      reason: 'marked-bad',
    });
  });

  /** R5a: "does not exceed" is <=, so the boundary itself is good. */
  it('treats a charge exactly equal to the limit as good, not bad', () => {
    expect(classifyDebit(debitOf(30), goodWithLimit)).toEqual({ classification: 'good' });
    // One cent over is the first bad amount.
    expect(classifyDebit(debitOf(30.01), goodWithLimit)).toMatchObject({
      classification: 'bad',
      reason: 'over-limit',
    });
  });
});

describe('describeBadReason — R12a wording', () => {
  it('has no reason to give for a good debit', () => {
    expect(describeBadReason({ classification: 'good' })).toBeUndefined();
  });

  it('words each reason the way R12a specifies', () => {
    expect(describeBadReason(classifyDebit(debitOf(9.99), undefined))).toBe(
      "New payee — you haven't seen this one before.",
    );
    expect(describeBadReason(classifyDebit(debitOf(9.99), bad))).toBe(
      'You marked this payee as bad.',
    );
    expect(describeBadReason(classifyDebit(debitOf(34.21), goodWithLimit))).toBe(
      'Over your limit of €30.00.',
    );
  });

  it('never puts a minus sign on an amount (R17a)', () => {
    const text = describeBadReason(classifyDebit(debitOf(34.21), goodWithLimit))!;
    expect(text).not.toContain('−');
    expect(text).not.toContain('-');
  });
});
