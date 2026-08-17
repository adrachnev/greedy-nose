/**
 * Regression coverage for the array-reference bug this fixture layer hit
 * once: an earlier mutation changed an object in place without producing a
 * new array, so useSyncExternalStore's getSnapshot() in src/data/hooks.ts
 * returned an unchanged reference and subscribers never re-rendered. These
 * tests assert on the *reference* changing, not just the end value, since
 * that is the exact thing that broke.
 *
 * The auto-flip suites that used to live here are gone with R8: nothing the
 * app does on its own moves a payee's classification any more, so there is no
 * save-time flip, no passive Bad-only flip, and no auto-flip notice left to
 * test. What replaced them is pure and lives in
 * src/domain/__tests__/classification.test.ts.
 */

// The fixture module holds mutable module-level state (`rules`, `debits`), so
// each test gets a fresh copy via jest.resetModules() + a fresh require().
function loadDataModule() {
  jest.resetModules();
  return require('../data') as typeof import('../data');
}

// The payee deliberately left out of the `rules` fixture: never reviewed, and
// therefore bad (R4b). Saving a rule for them is what "reviewing" means.
const UNREVIEWED_PAYEE_ID = 'payee-fitline';

/**
 * The fixture set is scaffolding, but its *properties* are not: each one keeps
 * a branch of the spec reachable by hand on the device, and every one of them
 * has been lost once already by someone tidying the data. These tests say out
 * loud what the values are for.
 */
describe('fixtures', () => {
  it('ships one payee with no rule, so R5’s "no rule -> bad" row is reachable on device', () => {
    const dataModule = loadDataModule();

    expect(dataModule.payees.find(p => p.id === UNREVIEWED_PAYEE_ID)).toBeDefined();
    expect(dataModule.rules.find(r => r.payeeId === UNREVIEWED_PAYEE_ID)).toBeUndefined();
    expect(dataModule.debits.some(d => d.payeeId === UNREVIEWED_PAYEE_ID)).toBe(true);
  });

  /**
   * The two bad states look identical on screen but are not the same thing
   * (R4b), and R12a gives them different words. This pins both, because the
   * set has already lost the explicit one once: a fixture tidy-up left only
   * the unreviewed payee behind, and "You marked this payee as bad." quietly
   * became unreachable on the device while its unit test kept passing.
   */
  it('ships both kinds of bad payee — one explicitly marked, one never reviewed', () => {
    const dataModule = loadDataModule();

    const explicitlyBad = dataModule.rules.filter(r => r.classification === 'bad');
    expect(explicitlyBad.length).toBeGreaterThan(0);
    expect(dataModule.debits.some(d => d.payeeId === explicitlyBad[0].payeeId)).toBe(true);

    const ruled = new Set(dataModule.rules.map(r => r.payeeId));
    expect(dataModule.payees.some(p => !ruled.has(p.id))).toBe(true);
  });

  /**
   * R5's subtlest row, and the only one no single screen can show on its own:
   * a payee who is good while one of their charges is bad. Without it in the
   * fixtures, the list and the detail screen agree everywhere and the case
   * most likely to be implemented backwards is never seen.
   */
  it('ships a good payee with a limit and at least one charge above it', () => {
    const dataModule = loadDataModule();

    const limited = dataModule.rules.filter(
      r => r.classification === 'good' && r.amountEUR != null,
    );
    expect(limited.length).toBeGreaterThan(0);

    const overLimit = dataModule.debits.filter(debit => {
      const rule = limited.find(r => r.payeeId === debit.payeeId);
      return rule?.amountEUR != null && debit.amountEUR > rule.amountEUR;
    });
    expect(overLimit.length).toBeGreaterThan(0);
  });

  /** R23a is only testable by hand if a name actually carries an umlaut. */
  it('ships a payee whose name carries an umlaut', () => {
    const dataModule = loadDataModule();

    expect(dataModule.payees.some(p => /[äöüßÄÖÜ]/.test(p.name))).toBe(true);
  });

  /**
   * Month headers, and the "one row per month" reading of a subscription that
   * R23 asks search to preserve, only mean anything with a year of history
   * behind them.
   */
  it('ships at least twelve months of history', () => {
    const dataModule = loadDataModule();

    const months = new Set(
      dataModule.debits.map(d => {
        const date = new Date(d.timestamp);
        return `${date.getFullYear()}-${date.getMonth()}`;
      }),
    );
    expect(months.size).toBeGreaterThanOrEqual(12);
  });

  it('dates no charge in the future — a fixture clock is still a clock', () => {
    const dataModule = loadDataModule();
    const now = Date.now();

    expect(dataModule.debits.every(d => new Date(d.timestamp).getTime() <= now)).toBe(true);
  });
});

describe('savePayeeRule', () => {
  it('writes classification and amount together, producing a new `rules` array reference (the exact regression: in-place mutation kept the same reference)', () => {
    const dataModule = loadDataModule();
    const before = dataModule.rules;

    dataModule.savePayeeRule('payee-netflix', { classification: 'good', amountEUR: 25 });

    expect(dataModule.rules).not.toBe(before);
    expect(dataModule.rules.find(r => r.payeeId === 'payee-netflix')).toEqual({
      payeeId: 'payee-netflix',
      classification: 'good',
      amountEUR: 25,
    });
  });

  it('creates a rule for a payee that has none — saving one is what "reviewed" means (R4b)', () => {
    const dataModule = loadDataModule();
    const before = dataModule.rules;

    dataModule.savePayeeRule(UNREVIEWED_PAYEE_ID, { classification: 'good' });

    expect(dataModule.rules).not.toBe(before);
    expect(dataModule.rules.find(r => r.payeeId === UNREVIEWED_PAYEE_ID)).toEqual({
      payeeId: UNREVIEWED_PAYEE_ID,
      classification: 'good',
    });
  });

  it('clears the amount when passed undefined — no limit means every charge is good (R4a)', () => {
    const dataModule = loadDataModule();
    // payee-baeckerei is good with a €30 limit in the fixtures.
    expect(dataModule.rules.find(r => r.payeeId === 'payee-baeckerei')!.amountEUR).toBe(30);

    dataModule.savePayeeRule('payee-baeckerei', { classification: 'good', amountEUR: undefined });

    expect(dataModule.rules.find(r => r.payeeId === 'payee-baeckerei')!.amountEUR).toBeUndefined();
  });

  it('keeps an amount handed to it alongside a bad classification — hidden, not wiped (R8a/R14)', () => {
    const dataModule = loadDataModule();

    dataModule.savePayeeRule('payee-baeckerei', { classification: 'bad', amountEUR: 30 });
    const stored = dataModule.rules.find(r => r.payeeId === 'payee-baeckerei')!;
    expect(stored).toEqual({ payeeId: 'payee-baeckerei', classification: 'bad', amountEUR: 30 });

    // …and it comes straight back when the payee is marked good again.
    dataModule.savePayeeRule('payee-baeckerei', { classification: 'good', amountEUR: 30 });
    expect(dataModule.rules.find(r => r.payeeId === 'payee-baeckerei')).toEqual({
      payeeId: 'payee-baeckerei',
      classification: 'good',
      amountEUR: 30,
    });
  });

  it('notifies subscribers exactly once per save, however many fields changed', () => {
    const { savePayeeRule, subscribeToDataChanges } = loadDataModule();
    const listener = jest.fn();
    subscribeToDataChanges(listener);

    savePayeeRule('payee-netflix', { classification: 'good', amountEUR: 25 });

    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('is a no-op (no reassignment, no notification) when the rule is unchanged', () => {
    const dataModule = loadDataModule();
    const listener = jest.fn();
    dataModule.subscribeToDataChanges(listener);
    const before = dataModule.rules;

    dataModule.savePayeeRule('payee-baeckerei', { classification: 'good', amountEUR: 30 });

    expect(dataModule.rules).toBe(before);
    expect(listener).not.toHaveBeenCalled();
  });

  it('leaves every other payee’s rule untouched', () => {
    const dataModule = loadDataModule();
    const otherBefore = dataModule.rules.find(r => r.payeeId === 'payee-netflix')!;

    dataModule.savePayeeRule('payee-baeckerei', { classification: 'bad' });

    expect(dataModule.rules.find(r => r.payeeId === 'payee-netflix')).toBe(otherBefore);
  });
});

describe('addDebit', () => {
  it('appends a debit, producing a new `debits` array reference', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debits;

    dataModule.addDebit({
      id: 'debit-test-1',
      payeeId: 'payee-spotify',
      amountEUR: 9.99,
      timestamp: new Date().toISOString(),
      paymentType: 'Subscription',
      reference: 'test',
    });

    expect(dataModule.debits).not.toBe(before);
    expect(dataModule.debits.find(d => d.id === 'debit-test-1')).toBeDefined();
  });

  it('notifies subscribers', () => {
    const dataModule = loadDataModule();
    const listener = jest.fn();
    dataModule.subscribeToDataChanges(listener);

    dataModule.addDebit({
      id: 'debit-test-2',
      payeeId: 'payee-spotify',
      amountEUR: 9.99,
      timestamp: new Date().toISOString(),
      paymentType: 'Subscription',
      reference: 'test',
    });

    expect(listener).toHaveBeenCalled();
  });

  /**
   * The key R8 guard: an arriving charge used to be able to flip a payee to
   * Bad on its own. It cannot any more, in either direction — however large
   * the charge, and whatever rule the payee carries.
   */
  it('never changes any payee’s classification, however large the charge', () => {
    const dataModule = loadDataModule();
    const rulesBefore = dataModule.rules;

    dataModule.addDebit({
      id: 'debit-netflix-huge',
      payeeId: 'payee-netflix',
      amountEUR: 99_999,
      timestamp: new Date().toISOString(),
      paymentType: 'Bank transfer',
      reference: 'Wildly over anything',
    });

    expect(dataModule.rules).toBe(rulesBefore);
    expect(dataModule.rules.find(r => r.payeeId === 'payee-netflix')!.classification).toBe(
      'good',
    );
  });
});
