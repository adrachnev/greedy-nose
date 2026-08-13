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
    const result = dataModule.setDebtorTrusted('debtor-scamyloans', false);

    expect(dataModule.debtors).toBe(before);
    expect(listener).not.toHaveBeenCalled();
    expect(result).toEqual({ clearedAmountThresholdEUR: undefined });
  });

  it('returns clearedAmountThresholdEUR: undefined and leaves `rules` untouched when the debtor has no rule to begin with', () => {
    const dataModule = loadDataModule();
    // debtor-landlord has no rule entry (see fixtures).
    expect(dataModule.rules.find(r => r.debtorId === 'debtor-landlord')).toBeUndefined();
    const rulesBefore = dataModule.rules;

    const result = dataModule.setDebtorTrusted('debtor-landlord', false);

    expect(result).toEqual({ clearedAmountThresholdEUR: undefined });
    expect(dataModule.rules).toBe(rulesBefore);
  });

  it('clears an existing rule when manually flipped Trusted -> Bad, returning the cleared amount', () => {
    const dataModule = loadDataModule();
    dataModule.setDebtorRule('debtor-landlord', { amountThresholdEUR: 900 });
    expect(dataModule.rules.find(r => r.debtorId === 'debtor-landlord')).toEqual({
      debtorId: 'debtor-landlord',
      amountThresholdEUR: 900,
    });
    expect(dataModule.debtors.find(d => d.id === 'debtor-landlord')!.trusted).toBe(true);

    const result = dataModule.setDebtorTrusted('debtor-landlord', false);

    expect(result).toEqual({ clearedAmountThresholdEUR: 900 });
    expect(dataModule.rules.find(r => r.debtorId === 'debtor-landlord')).toBeUndefined();
    expect(dataModule.debtors.find(d => d.id === 'debtor-landlord')!.trusted).toBe(false);
  });

  it('clears an existing rule when manually flipped Bad -> Trusted, returning the cleared amount', () => {
    const dataModule = loadDataModule();
    dataModule.setDebtorRule('debtor-scamyloans', { amountThresholdEUR: 10 });
    expect(dataModule.rules.find(r => r.debtorId === 'debtor-scamyloans')).toEqual({
      debtorId: 'debtor-scamyloans',
      amountThresholdEUR: 10,
    });
    expect(dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!.trusted).toBe(false);

    const result = dataModule.setDebtorTrusted('debtor-scamyloans', true);

    expect(result).toEqual({ clearedAmountThresholdEUR: 10 });
    expect(dataModule.rules.find(r => r.debtorId === 'debtor-scamyloans')).toBeUndefined();
    expect(dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!.trusted).toBe(true);
  });
});

/**
 * Same reference-identity reasoning as setDebtorTrusted above, applied to
 * `rules` (also `useSyncExternalStore`-backed, via useRules()/
 * useRuleForDebtor() in src/data/hooks.ts).
 */
describe('setDebtorRule', () => {
  it('sets a new threshold on a debtor with no existing rule entry, producing a new `rules` array reference', () => {
    const dataModule = loadDataModule();
    const before = dataModule.rules;
    expect(before.find(r => r.debtorId === 'debtor-rewe')).toBeUndefined();

    const result = dataModule.setDebtorRule('debtor-rewe', { amountThresholdEUR: 30 });

    expect(dataModule.rules).not.toBe(before);
    const updated = dataModule.rules.find(r => r.debtorId === 'debtor-rewe');
    expect(updated).toEqual({ debtorId: 'debtor-rewe', amountThresholdEUR: 30 });
    // debtor-rewe's most recent transaction (-34.21) exceeds 30, and
    // debtor-rewe defaults to Trusted — so this also exercises the
    // auto-flip-to-Bad path.
    expect(result).toEqual({ autoFlippedTo: 'Bad' });
  });

  it('updates the threshold on a debtor that already has a rule entry, producing a new `rules` array reference', () => {
    const dataModule = loadDataModule();
    const before = dataModule.rules;
    expect(before.find(r => r.debtorId === 'debtor-spotify')).toEqual({
      debtorId: 'debtor-spotify',
    });

    const result = dataModule.setDebtorRule('debtor-spotify', { amountThresholdEUR: 5 });

    expect(dataModule.rules).not.toBe(before);
    const updated = dataModule.rules.find(r => r.debtorId === 'debtor-spotify');
    expect(updated).toEqual({
      debtorId: 'debtor-spotify',
      amountThresholdEUR: 5,
    });
    // debtor-spotify's most recent transaction (-9.99) exceeds 5, and
    // debtor-spotify is already Bad — already in the desired state, so no
    // auto-flip.
    expect(result).toEqual({ autoFlippedTo: null });
  });

  it('clears an existing rule entry entirely when the threshold is omitted, producing a new `rules` array reference', () => {
    const dataModule = loadDataModule();
    expect(dataModule.rules.find(r => r.debtorId === 'debtor-spotify')).toBeDefined();
    const before = dataModule.rules;

    dataModule.setDebtorRule('debtor-spotify', {});

    expect(dataModule.rules).not.toBe(before);
    expect(dataModule.rules.find(r => r.debtorId === 'debtor-spotify')).toBeUndefined();
  });

  it('is a no-op (no reassignment, no notification) clearing a debtor that has no rule entry', () => {
    const dataModule = loadDataModule();
    const listener = jest.fn();
    dataModule.subscribeToDataChanges(listener);
    const before = dataModule.rules;

    dataModule.setDebtorRule('debtor-landlord', {});

    expect(dataModule.rules).toBe(before);
    expect(listener).not.toHaveBeenCalled();
  });

  it('notifies subscribers when a rule actually changes', () => {
    const { setDebtorRule, subscribeToDataChanges } = loadDataModule();
    const listener = jest.fn();
    subscribeToDataChanges(listener);

    setDebtorRule('debtor-spotify', { amountThresholdEUR: 5 });

    expect(listener).toHaveBeenCalledTimes(1);
  });
});

/**
 * Auto-flip: saving an amount threshold re-evaluates a debtor's most recent
 * transaction against it and flips Trusted/Bad in whichever direction the
 * threshold implies — Trusted -> Bad when the charge exceeds it, and (the
 * previously-missing direction) Bad -> Trusted when it doesn't (see
 * mocks/04b-debitor-edit.html's toast comment and CLAUDE.md). Uses the same
 * reference-identity testing style as the rest of this file.
 */
describe('setDebtorRule auto-flip', () => {
  it('flips a Trusted debtor to Bad when the new threshold is exceeded by their last transaction', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;
    const target = before.find(d => d.id === 'debtor-rewe')!;
    expect(target.trusted).toBe(true);
    // debtor-rewe's most recent transaction (tx-rewe-1) is -34.21.

    const result = dataModule.setDebtorRule('debtor-rewe', { amountThresholdEUR: 30 });

    expect(result).toEqual({ autoFlippedTo: 'Bad' });
    expect(dataModule.debtors).not.toBe(before);
    const updated = dataModule.debtors.find(d => d.id === 'debtor-rewe')!;
    expect(updated.trusted).toBe(false);
  });

  it('does not flip a Trusted debtor when the threshold is not exceeded (stays Trusted)', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;

    // debtor-rewe's most recent transaction (tx-rewe-1) is -34.21.
    const result = dataModule.setDebtorRule('debtor-rewe', { amountThresholdEUR: 100 });

    expect(result).toEqual({ autoFlippedTo: null });
    expect(dataModule.debtors).toBe(before);
    expect(dataModule.debtors.find(d => d.id === 'debtor-rewe')!.trusted).toBe(true);
  });

  it('does not flip a Bad debtor when the threshold is exceeded (stays Bad)', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;

    // debtor-scamyloans is Bad by default, last transaction is -49.00.
    const result = dataModule.setDebtorRule('debtor-scamyloans', { amountThresholdEUR: 10 });

    expect(result).toEqual({ autoFlippedTo: null });
    expect(dataModule.debtors).toBe(before);
    expect(dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!.trusted).toBe(false);
  });

  it('flips a Bad debtor to Trusted when their last charge does not exceed the new threshold', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;
    const target = before.find(d => d.id === 'debtor-scamyloans')!;
    expect(target.trusted).toBe(false);
    // debtor-scamyloans's only transaction is -49.00.

    const result = dataModule.setDebtorRule('debtor-scamyloans', { amountThresholdEUR: 60 });

    expect(result).toEqual({ autoFlippedTo: 'Trusted' });
    expect(dataModule.debtors).not.toBe(before);
    const updated = dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!;
    expect(updated.trusted).toBe(true);
  });

  it('treats an amount exactly equal to the threshold as "under" (Trusted-eligible, not Bad)', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;
    // debtor-scamyloans's only transaction is -49.00 (abs 49).

    const result = dataModule.setDebtorRule('debtor-scamyloans', { amountThresholdEUR: 49 });

    expect(result).toEqual({ autoFlippedTo: 'Trusted' });
    expect(dataModule.debtors).not.toBe(before);
    expect(dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!.trusted).toBe(true);
  });

  it('does not evaluate (returns null) when no threshold is set', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;

    const result = dataModule.setDebtorRule('debtor-scamyloans', {});

    expect(result).toEqual({ autoFlippedTo: null });
    expect(dataModule.debtors).toBe(before);
  });

  it('does not evaluate (returns null) when the debtor has no transactions at all', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;

    const result = dataModule.setDebtorRule('debtor-with-no-transactions', {
      amountThresholdEUR: 1,
    });

    expect(result).toEqual({ autoFlippedTo: null });
    expect(dataModule.debtors).toBe(before);
  });

  it('does not flip an unknown debtor id (no matching debtor, so no transactions either)', () => {
    const dataModule = loadDataModule();
    const before = dataModule.debtors;

    const result = dataModule.setDebtorRule('debtor-does-not-exist', { amountThresholdEUR: 1 });

    expect(result).toEqual({ autoFlippedTo: null });
    expect(dataModule.debtors).toBe(before);
  });

  it('picks the actual most-recent-by-timestamp transaction, not the last one in the array', () => {
    const dataModule = loadDataModule();
    // Append a stale, tiny, out-of-order-looking charge (older than
    // debtor-rewe's existing most-recent charge, tx-rewe-1 at -34.21) via
    // addTransaction (the only supported write path — see comment above
    // `transactions` in data.ts), then append a fresh, large one last. Both
    // land after tx-rewe-1 in array order, but only the fresh one is
    // actually most recent by `timestamp`, which is what the reduce-by-
    // timestamp logic (not array order) must key off.
    dataModule.addTransaction({
      id: 'tx-rewe-stale',
      debtorId: 'debtor-rewe',
      amountEUR: -1.0,
      timestamp: new Date(Date.now() - 90 * 24 * 60 * 60 * 1000).toISOString(),
      paymentType: 'Card payment',
      reference: 'old, small, out of order',
    });
    dataModule.addTransaction({
      id: 'tx-rewe-fresh',
      debtorId: 'debtor-rewe',
      amountEUR: -500.0,
      timestamp: new Date().toISOString(),
      paymentType: 'Card payment',
      reference: 'newest, large charge',
    });

    // A threshold of 100 would keep debtor-rewe Trusted based on the stale
    // -1.00 charge or the existing -34.21 charge, but must flip to Bad
    // based on the actual most recent charge (-500.00).
    const result = dataModule.setDebtorRule('debtor-rewe', { amountThresholdEUR: 100 });

    expect(result).toEqual({ autoFlippedTo: 'Bad' });
    expect(dataModule.debtors.find(d => d.id === 'debtor-rewe')!.trusted).toBe(false);
  });
});

describe('addTransaction', () => {
  it('appends a new transaction, producing a new `transactions` array reference', () => {
    const dataModule = loadDataModule();
    const before = dataModule.transactions;

    dataModule.addTransaction({
      id: 'tx-test-1',
      debtorId: 'debtor-spotify',
      amountEUR: -9.99,
      timestamp: new Date().toISOString(),
      paymentType: 'Subscription',
      reference: 'test',
    });

    expect(dataModule.transactions).not.toBe(before);
    expect(dataModule.transactions.find(t => t.id === 'tx-test-1')).toBeDefined();
  });

  it('notifies subscribers', () => {
    const dataModule = loadDataModule();
    const listener = jest.fn();
    dataModule.subscribeToDataChanges(listener);

    dataModule.addTransaction({
      id: 'tx-test-2',
      debtorId: 'debtor-spotify',
      amountEUR: -9.99,
      timestamp: new Date().toISOString(),
      paymentType: 'Subscription',
      reference: 'test',
    });

    expect(listener).toHaveBeenCalled();
  });

  it('re-runs the auto-flip evaluation for a Trusted debtor with a rule when a new transaction exceeds it (passive Bad-flip still works)', () => {
    const dataModule = loadDataModule();
    dataModule.setDebtorRule('debtor-landlord', { amountThresholdEUR: 900 });
    const target = dataModule.debtors.find(d => d.id === 'debtor-landlord')!;
    expect(target.trusted).toBe(true);
    const before = dataModule.debtors;

    dataModule.addTransaction({
      id: 'tx-landlord-2',
      debtorId: 'debtor-landlord',
      amountEUR: -950,
      timestamp: new Date().toISOString(),
      paymentType: 'Bank transfer',
      reference: 'Rent September 2026',
    });

    expect(dataModule.debtors).not.toBe(before);
    const updated = dataModule.debtors.find(d => d.id === 'debtor-landlord')!;
    expect(updated.trusted).toBe(false);
  });

  it('does NOT flip a Bad debtor with a rule back to Trusted when a new transaction no longer exceeds it — the passive path is Bad-only (key regression guard)', () => {
    const dataModule = loadDataModule();
    dataModule.setDebtorRule('debtor-scamyloans', { amountThresholdEUR: 10 });
    const target = dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!;
    expect(target.trusted).toBe(false);
    // debtor-scamyloans's existing charge (-49.00) still exceeds 10.
    const before = dataModule.debtors;

    dataModule.addTransaction({
      id: 'tx-scamyloans-2',
      debtorId: 'debtor-scamyloans',
      amountEUR: -5,
      timestamp: new Date().toISOString(),
      paymentType: 'Direct debit',
      reference: 'Small refund adjustment',
    });

    // Under the (removed) old bidirectional passive behavior this would
    // flip to Trusted. The new contract: only rule-Save may flip to
    // Trusted; a passively-arriving transaction never does.
    expect(dataModule.debtors).toBe(before);
    const updated = dataModule.debtors.find(d => d.id === 'debtor-scamyloans')!;
    expect(updated.trusted).toBe(false);
  });
});

/**
 * AutoFlipNotice: the passive Bad-only flip in addTransaction has no toast
 * to rely on (no guaranteed mounted screen), so it leaves a persistent
 * marker instead — surfaced on RulesListScreen, cleared via
 * acknowledgeAutoFlipNotice() once the user opens DebitorEditScreen for
 * that debtor. Uses the same reference-identity testing style as the rest
 * of this file, since useAutoFlipNotices() is useSyncExternalStore-backed.
 */
describe('AutoFlipNotice', () => {
  it('is recorded when the passive path (addTransaction) flips a debtor to Bad', () => {
    const dataModule = loadDataModule();
    dataModule.setDebtorRule('debtor-landlord', { amountThresholdEUR: 900 });
    expect(dataModule.autoFlipNotices).toHaveLength(0);

    dataModule.addTransaction({
      id: 'tx-landlord-2',
      debtorId: 'debtor-landlord',
      amountEUR: -950,
      timestamp: new Date().toISOString(),
      paymentType: 'Bank transfer',
      reference: 'Rent September 2026',
    });

    expect(dataModule.autoFlipNotices).toEqual([
      expect.objectContaining({ debtorId: 'debtor-landlord', amountThresholdEUR: 900 }),
    ]);
  });

  it('is NOT recorded when nothing flips', () => {
    const dataModule = loadDataModule();
    dataModule.setDebtorRule('debtor-scamyloans', { amountThresholdEUR: 10 });
    // debtor-scamyloans's charge (-49.00) still exceeds 10, so it's already Bad — no flip.

    dataModule.addTransaction({
      id: 'tx-scamyloans-2',
      debtorId: 'debtor-scamyloans',
      amountEUR: -60,
      timestamp: new Date().toISOString(),
      paymentType: 'Direct debit',
      reference: 'Another large charge',
    });

    expect(dataModule.autoFlipNotices).toHaveLength(0);
  });

  it('is NOT recorded when the flip happens via setDebtorRule (Save) instead of the passive path', () => {
    const dataModule = loadDataModule();
    // debtor-rewe is Trusted, most recent charge -34.21.
    const result = dataModule.setDebtorRule('debtor-rewe', { amountThresholdEUR: 30 });

    expect(result).toEqual({ autoFlippedTo: 'Bad' });
    expect(dataModule.autoFlipNotices).toHaveLength(0);
  });

  it('replaces (does not duplicate) an existing unacknowledged notice for the same debtor on a second passive flip', () => {
    const dataModule = loadDataModule();
    dataModule.setDebtorRule('debtor-landlord', { amountThresholdEUR: 900 });
    dataModule.addTransaction({
      id: 'tx-landlord-2',
      debtorId: 'debtor-landlord',
      amountEUR: -950,
      timestamp: new Date().toISOString(),
      paymentType: 'Bank transfer',
      reference: 'Rent September 2026',
    });
    expect(dataModule.autoFlipNotices).toHaveLength(1);

    // Manually re-trust + re-set a rule (with a threshold high enough that
    // the existing -950 charge doesn't re-trigger a Save-time flip) so a
    // second passive flip can occur for the same debtor. setDebtorTrusted
    // clears the rule, so re-add it; and setDebtorTrusted doesn't itself
    // touch autoFlipNotices — only opening DebitorEditScreen
    // (acknowledgeAutoFlipNotice) does — so acknowledge it explicitly here
    // to isolate the "replaces" assertion below.
    dataModule.setDebtorTrusted('debtor-landlord', true);
    const saveResult = dataModule.setDebtorRule('debtor-landlord', { amountThresholdEUR: 3000 });
    expect(saveResult).toEqual({ autoFlippedTo: null });
    dataModule.acknowledgeAutoFlipNotice('debtor-landlord');
    expect(dataModule.autoFlipNotices).toHaveLength(0);

    dataModule.addTransaction({
      id: 'tx-landlord-3',
      debtorId: 'debtor-landlord',
      // Slightly in the future relative to tx-landlord-2 above, so it's
      // unambiguously the most-recent-by-timestamp even if both run within
      // the same millisecond of test execution.
      amountEUR: -3500,
      timestamp: new Date(Date.now() + 1000).toISOString(),
      paymentType: 'Bank transfer',
      reference: 'Unusual large charge',
    });

    // The key assertion: still exactly one notice for this debtor (replaced,
    // not appended) — not asserting on the timestamp itself, since both
    // flips can land in the same test-execution millisecond.
    expect(dataModule.autoFlipNotices).toHaveLength(1);
    expect(dataModule.autoFlipNotices[0].debtorId).toBe('debtor-landlord');
    expect(dataModule.autoFlipNotices[0].amountThresholdEUR).toBe(3000);
  });
});

describe('acknowledgeAutoFlipNotice', () => {
  it('removes the matching notice and notifies subscribers', () => {
    const dataModule = loadDataModule();
    dataModule.setDebtorRule('debtor-landlord', { amountThresholdEUR: 900 });
    dataModule.addTransaction({
      id: 'tx-landlord-2',
      debtorId: 'debtor-landlord',
      amountEUR: -950,
      timestamp: new Date().toISOString(),
      paymentType: 'Bank transfer',
      reference: 'Rent September 2026',
    });
    expect(dataModule.autoFlipNotices).toHaveLength(1);
    const listener = jest.fn();
    dataModule.subscribeToDataChanges(listener);

    dataModule.acknowledgeAutoFlipNotice('debtor-landlord');

    expect(dataModule.autoFlipNotices).toHaveLength(0);
    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('is a no-op (no notification) when there is no notice for that debtor', () => {
    const dataModule = loadDataModule();
    const listener = jest.fn();
    dataModule.subscribeToDataChanges(listener);
    const before = dataModule.autoFlipNotices;

    dataModule.acknowledgeAutoFlipNotice('debtor-landlord');

    expect(dataModule.autoFlipNotices).toBe(before);
    expect(listener).not.toHaveBeenCalled();
  });
});
