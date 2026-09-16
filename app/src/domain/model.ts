// The app's core vocabulary, per REQUIREMENTS.md R0: payee, debit, good/bad.
// "Debtor" is plain wrong here — a debtor owes *you* money, while these
// parties take it.
//
// These types are permanent. src/mocks/data.ts only supplies fixture *values*
// for them until the backend exists, and goes away; this file does not.

export type Classification = 'good' | 'bad';

/** Whoever took the money (R3). Payees come from the bank feed, not the user. */
export type Payee = {
  id: string;
  name: string;
  initials: string;
  iban: string;
};

export type PaymentType =
  | 'Direct debit'
  | 'Card payment'
  | 'Subscription'
  | 'Bank transfer';

/**
 * A charge taken from the user's account (R2). Incoming money never becomes a
 * debit (R2a), so `amountEUR` is always positive: the sign would distinguish
 * nothing, and R17a bans it from the UI outright.
 */
export type Debit = {
  id: string;
  payeeId: string;
  amountEUR: number;
  /** ISO 8601. Date-section grouping (Today/Yesterday/…) is derived from this. */
  timestamp: string;
  /**
   * Whether `timestamp` carries a real clock time or only a date.
   *
   * Banks are not obliged to say *when* a charge happened, and the first real
   * dump said it on **none** of its 100 rows: every one was a booking date
   * with no time, and `transaction_date` was null throughout. The backend
   * still sends a full ISO timestamp (midnight UTC) so the date grouping has
   * something to sort on — this flag is what stops the UI reading that
   * midnight back as "02:00" and inventing a precision the bank never gave.
   *
   * `false` is the safe default: a missing field hides the time rather than
   * making one up. Fixtures set it to `true` because they carry deliberate,
   * meaningful times.
   */
  hasTime: boolean;
  paymentType: PaymentType;
  reference: string;
};

/**
 * A payee has at most one rule, and only the user creates or edits it (R4).
 * The *absence* of a rule is meaningful: it means "never reviewed", which
 * classifies bad exactly like an explicit bad rule does. That is why no
 * separate "reviewed" flag exists — having a rule is what reviewed means
 * (R4b), and only onboarding cares about the difference.
 */
export type Rule = {
  payeeId: string;
  classification: Classification;
  /**
   * Only meaningful while `classification` is 'good' (R5). Undefined means no
   * limit — every charge from this payee is good, whatever its size (R4a).
   * A limit set while good is *kept*, not wiped, when the payee is marked bad
   * (R8a/R14), so marking them good again brings the previous limit back.
   */
  amountEUR?: number;
};

/**
 * A rule as the user edits it, before it is attached to a payee. The edit
 * screen commits both fields at once (one Save = one write), so the write path
 * takes the whole rule rather than one setter per field — which is also what
 * keeps a save from ever landing half-applied.
 */
export type RuleDraft = Omit<Rule, 'payeeId'>;
