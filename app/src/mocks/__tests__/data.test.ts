/**
 * The fixture set is scaffolding, but its *properties* are not: each one
 * keeps a branch of the spec reachable by hand on the device, and every one
 * of them has been lost once already by someone tidying the data. This suite
 * pins them.
 *
 * The auto-flip suites that used to live here are gone with R8: nothing the
 * app does on its own moves a payee's classification any more, so there is no
 * save-time flip, no passive Bad-only flip, and no auto-flip notice left to
 * test. What replaced them is pure and lives in
 * src/domain/__tests__/classification.test.ts.
 *
 * The `addDebit` suite went the same way on 2026-08-19, with the function it
 * covered (the why is recorded at its former home in ../data.ts). The
 * `savePayeeRule` suite that used to live here — the array-reference
 * discipline that regression pinned — moved wholesale to
 * src/data/__tests__/rulesStore.test.ts alongside the function itself.
 */

// jest.resetModules() + a fresh require() isn't load-bearing any more now
// that nothing in this module mutates (payees/debits are frozen consts,
// FIXTURE_SEED_RULES is a plain seed value) — kept anyway so this file stays
// agnostic to that fact and isn't the place a future mutable export gets its
// state leaked across tests by accident.
function loadDataModule() {
  jest.resetModules();
  return require('../data') as typeof import('../data');
}

// The payee deliberately left out of `FIXTURE_SEED_RULES`: never reviewed,
// and therefore bad (R4b). Saving a rule for them is what "reviewing" means.
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
    expect(dataModule.FIXTURE_SEED_RULES.find(r => r.payeeId === UNREVIEWED_PAYEE_ID)).toBeUndefined();
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

    const explicitlyBad = dataModule.FIXTURE_SEED_RULES.filter(r => r.classification === 'bad');
    expect(explicitlyBad.length).toBeGreaterThan(0);
    expect(dataModule.debits.some(d => d.payeeId === explicitlyBad[0].payeeId)).toBe(true);

    const ruled = new Set(dataModule.FIXTURE_SEED_RULES.map(r => r.payeeId));
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

    const limited = dataModule.FIXTURE_SEED_RULES.filter(
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

  /**
   * The type only demands an answer, not this one. Real bank data says
   * `hasTime: false` on every row it has produced so far, so the fixtures are
   * the *only* place the "3 Jul, 09:14" rendering is exercised at all — a
   * tidy-up that flipped them to false would leave that branch dead on the
   * device with every test still green.
   */
  it('gives every fixture debit a real time, since the live feed gives none', () => {
    const dataModule = loadDataModule();

    expect(dataModule.debits.every(d => d.hasTime)).toBe(true);
  });
});
