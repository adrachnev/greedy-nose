/**
 * Regression coverage for the setDebtorTrusted() array-reference bug: an
 * earlier implementation mutated a debtor object in place without producing
 * a new `debtors` array, so useSyncExternalStore's getSnapshot() in
 * src/data/hooks.ts returned an unchanged reference and subscribers never
 * re-rendered (see TransactionListScreen not picking up a Trusted/Bad toggle
 * made on TransactionDetailScreen). These tests assert on the *reference*
 * changing, not just the end value, since that's the exact thing that broke.
 */

// The fixture module holds mutable module-level state (`debtors`), so each
// test gets a fresh copy via jest.resetModules() + a fresh require().
function loadDataModule() {
  jest.resetModules();
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  return require('../data') as typeof import('../data');
}

describe('setDebtorTrusted', () => {
  it('flips the trusted flag for the matching debtor', () => {
    const dataModule = loadDataModule();
    const target = dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!;
    expect(target.trusted).toBe(false);

    dataModule.setDebtorTrusted('debtor-scamyloans', true);

    // Re-read the live `debtors` binding off the same module instance
    // (not the stale `target`/array captured above).
    const updated = dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!;
    expect(updated.trusted).toBe(true);
  });

  it('produces a new `debtors` array reference (the exact regression: in-place mutation kept the same reference)', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;

    dataModule.setDebtorTrusted('debtor-scamyloans', true);

    // `debtors` is a `let` export reassigned inside data.ts, so re-reading
    // the live binding must reflect a new array object, not the same one
    // mutated in place.
    expect(dataModule.debtors).not.toBe(before);
    expect(dataModule.debtors).not.toEqual(before);
  });

  it('notifies subscribers when the trusted flag actually changes', () => {
    const { setDebtorTrusted, subscribeToDataChanges } = loadDataModule();
    const listener = jest.fn();
    subscribeToDataChanges(listener);

    setDebtorTrusted('debtor-scamyloans', true);

    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('is a no-op (no reassignment, no notification) when setting the already-current value', () => {
    const dataModule = loadDataModule();
    const listener = jest.fn();
    dataModule.subscribeToDataChanges(listener);
    const before = dataModule.debtors;

    // debtor-scamyloans already defaults to trusted: false.
    dataModule.setDebtorTrusted('debtor-scamyloans', false);

    expect(dataModule.debtors).toBe(before);
    expect(listener).not.toHaveBeenCalled();
  });
});
