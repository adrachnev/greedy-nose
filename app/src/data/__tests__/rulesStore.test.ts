/**
 * The local rule store: the one piece of user-created state that has to
 * survive an app restart (see rulesStore.ts's own header for why). These
 * tests cover what a persistence layer earns its keep on:
 *
 * - what a truly first launch seeds, under both USE_BACKEND values (the
 *   binding is read at module load, so — same as hooksSource.test.tsx — the
 *   only way to see both branches is jest.isolateModules + a mocked
 *   ./config, loading the module twice);
 * - hydrating from a value the app itself wrote earlier;
 * - the same distrust backendFeed.ts's toPayee/toDebit apply to JSON off a
 *   socket, applied here to JSON off disk — a corrupted or hand-edited value
 *   must not crash the app or lose every rule to one bad entry;
 * - write ordering: two saves back to back must both land on disk, in order,
 *   not just the first (the reason persist() is a promise queue and not a
 *   bare unawaited AsyncStorage.setItem per call);
 * - write failure recovery: one rejected AsyncStorage.setItem must not
 *   permanently wedge the queue and silently drop every save after it.
 *
 * The array-reference-discipline suite below is ported near-verbatim from
 * src/mocks/__tests__/data.test.ts's old `savePayeeRule` describe block —
 * that function, and the regression it was written to pin, both moved here
 * wholesale when rules gained persistence.
 */

import { Rule } from '../../domain/model';

const RULES_KEY = '@greedy-nose/rules/v1';

// jest.setup.js mocks the async-storage package itself; grabbing the default
// export directly (rather than via a fresh require per isolated module) is
// fine for *reading back* what got persisted, since the mock's default export
// is a single in-memory instance per module registry — the same instance
// rulesStore.ts's own `import AsyncStorage from '...'` resolves to within one
// isolate.
type AsyncStorageMock = {
  getItem: (key: string) => Promise<string | null>;
  setItem: (key: string, value: string) => Promise<void>;
};

type Modules = {
  store: typeof import('../rulesStore');
  fixtures: typeof import('../../mocks/data');
  AsyncStorage: AsyncStorageMock;
};

function loadWith(useBackend: boolean): Modules {
  let modules!: Modules;
  jest.isolateModules(() => {
    jest.doMock('../config', () => ({
      USE_BACKEND: useBackend,
      BACKEND_BASE_URL: 'http://backend.test',
      BACKEND_TIMEOUT_MS: 1000,
    }));
    modules = {
      store: require('../rulesStore'),
      fixtures: require('../../mocks/data'),
      AsyncStorage: require('@react-native-async-storage/async-storage').default,
    };
  });
  return modules;
}

/**
 * Flushes every pending microtask, so the fire-and-forget hydrate()/persist()
 * chains rulesStore.ts kicks off have settled before an assertion reads their
 * result. A macrotask (`setTimeout`) is what guarantees this regardless of
 * exactly how many `.then()`s deep a given chain is — counting awaits instead
 * would assert against an implementation detail rather than the outcome.
 */
function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, 0));
}

/** Subscribes (which is what starts hydration) and waits for it to settle. */
async function hydrated(store: Modules['store']): Promise<void> {
  store.subscribe(() => {});
  await flush();
}

beforeEach(() => {
  jest.spyOn(console, 'warn').mockImplementation(() => {});
});

afterEach(() => {
  jest.dontMock('../config');
  jest.restoreAllMocks();
});

describe('seeding on first launch', () => {
  it('seeds the fixture mix in fixture mode', async () => {
    const { store, fixtures } = loadWith(false);

    await hydrated(store);

    expect(store.getRules()).toEqual(fixtures.FIXTURE_SEED_RULES);
  });

  it('seeds empty in backend mode — no demo data pretending to be real (R6)', async () => {
    const { store } = loadWith(true);

    await hydrated(store);

    expect(store.getRules()).toEqual([]);
  });

  it('persists the seed immediately, so a restart with nothing else touching it stays seeded', async () => {
    const { store, fixtures, AsyncStorage } = loadWith(false);

    await hydrated(store);

    const stored = await AsyncStorage.getItem(RULES_KEY);
    expect(JSON.parse(stored!)).toEqual(fixtures.FIXTURE_SEED_RULES);
  });
});

describe('hydration from storage', () => {
  it('loads a previously-stored valid value instead of reseeding', async () => {
    const stored: Rule[] = [{ payeeId: 'payee-example', classification: 'good', amountEUR: 12 }];
    const { store, AsyncStorage } = loadWith(false);
    AsyncStorage.setItem(RULES_KEY, JSON.stringify(stored));

    await hydrated(store);

    expect(store.getRules()).toEqual(stored);
  });

  it('reseeds and warns on malformed top-level JSON', async () => {
    const { store, fixtures, AsyncStorage } = loadWith(false);
    AsyncStorage.setItem(RULES_KEY, 'not json{');

    await hydrated(store);

    expect(store.getRules()).toEqual(fixtures.FIXTURE_SEED_RULES);
    expect(console.warn).toHaveBeenCalled();
  });

  it('reseeds and warns when the stored value parses but is not an array', async () => {
    const { store, fixtures, AsyncStorage } = loadWith(false);
    AsyncStorage.setItem(RULES_KEY, JSON.stringify({ payeeId: 'not-an-array' }));

    await hydrated(store);

    expect(store.getRules()).toEqual(fixtures.FIXTURE_SEED_RULES);
    expect(console.warn).toHaveBeenCalled();
  });

  it('drops individually malformed rows but keeps the rest of an otherwise-valid array', async () => {
    const { store, AsyncStorage } = loadWith(false);
    AsyncStorage.setItem(
      RULES_KEY,
      JSON.stringify([
        { payeeId: 'payee-good', classification: 'good' },
        { payeeId: '', classification: 'bad' }, // empty payeeId
        { classification: 'good' }, // no payeeId at all
        { payeeId: 'payee-bad-classification', classification: 'neutral' },
        { payeeId: 'payee-negative-amount', classification: 'good', amountEUR: -5 },
        { payeeId: 'payee-zero-amount', classification: 'good', amountEUR: 0 },
        { payeeId: 'payee-nan-amount', classification: 'good', amountEUR: 'ten' },
      ]),
    );

    await hydrated(store);

    // Only the one well-formed row survives; each rejected row logs, so a
    // corrupted file is visible in the log rather than silently thinning out.
    expect(store.getRules()).toEqual([{ payeeId: 'payee-good', classification: 'good' }]);
    expect(console.warn).toHaveBeenCalledTimes(6);
  });

  it('keeps a valid amountEUR that happens to sit alongside other malformed rows', async () => {
    const { store, AsyncStorage } = loadWith(false);
    AsyncStorage.setItem(
      RULES_KEY,
      JSON.stringify([
        { payeeId: 'payee-limited', classification: 'good', amountEUR: 42.5 },
        { payeeId: 'payee-no-classification' },
      ]),
    );

    await hydrated(store);

    expect(store.getRules()).toEqual([
      { payeeId: 'payee-limited', classification: 'good', amountEUR: 42.5 },
    ]);
  });
});

describe('saveRule — array-reference discipline (ported from the old savePayeeRule suite)', () => {
  const UNREVIEWED_PAYEE_ID = 'payee-fitline';

  async function loadHydrated(): Promise<Modules['store']> {
    const { store } = loadWith(false);
    await hydrated(store);
    return store;
  }

  it('writes classification and amount together, producing a new `rules` array reference (the exact regression: in-place mutation kept the same reference)', async () => {
    const store = await loadHydrated();
    const before = store.getRules();

    store.saveRule('payee-netflix', { classification: 'good', amountEUR: 25 });

    expect(store.getRules()).not.toBe(before);
    expect(store.getRules().find(r => r.payeeId === 'payee-netflix')).toEqual({
      payeeId: 'payee-netflix',
      classification: 'good',
      amountEUR: 25,
    });
  });

  it('creates a rule for a payee that has none — saving one is what "reviewed" means (R4b)', async () => {
    const store = await loadHydrated();
    const before = store.getRules();

    store.saveRule(UNREVIEWED_PAYEE_ID, { classification: 'good' });

    expect(store.getRules()).not.toBe(before);
    expect(store.getRules().find(r => r.payeeId === UNREVIEWED_PAYEE_ID)).toEqual({
      payeeId: UNREVIEWED_PAYEE_ID,
      classification: 'good',
    });
  });

  it('clears the amount when passed undefined — no limit means every charge is good (R4a)', async () => {
    const store = await loadHydrated();
    // payee-baeckerei is good with a €30 limit in the fixture seed.
    expect(store.getRules().find(r => r.payeeId === 'payee-baeckerei')!.amountEUR).toBe(30);

    store.saveRule('payee-baeckerei', { classification: 'good', amountEUR: undefined });

    expect(store.getRules().find(r => r.payeeId === 'payee-baeckerei')!.amountEUR).toBeUndefined();
  });

  it('keeps an amount handed to it alongside a bad classification — hidden, not wiped (R8a/R14)', async () => {
    const store = await loadHydrated();

    store.saveRule('payee-baeckerei', { classification: 'bad', amountEUR: 30 });
    expect(store.getRules().find(r => r.payeeId === 'payee-baeckerei')).toEqual({
      payeeId: 'payee-baeckerei',
      classification: 'bad',
      amountEUR: 30,
    });

    // …and it comes straight back when the payee is marked good again.
    store.saveRule('payee-baeckerei', { classification: 'good', amountEUR: 30 });
    expect(store.getRules().find(r => r.payeeId === 'payee-baeckerei')).toEqual({
      payeeId: 'payee-baeckerei',
      classification: 'good',
      amountEUR: 30,
    });
  });

  it('notifies subscribers exactly once per save, however many fields changed', async () => {
    const store = await loadHydrated();
    const listener = jest.fn();
    store.subscribe(listener);

    store.saveRule('payee-netflix', { classification: 'good', amountEUR: 25 });

    expect(listener).toHaveBeenCalledTimes(1);
  });

  it('is a no-op (no reassignment, no notification) when the rule is unchanged', async () => {
    const store = await loadHydrated();
    const listener = jest.fn();
    store.subscribe(listener);
    const before = store.getRules();

    store.saveRule('payee-baeckerei', { classification: 'good', amountEUR: 30 });

    expect(store.getRules()).toBe(before);
    expect(listener).not.toHaveBeenCalled();
  });

  it('leaves every other payee’s rule untouched', async () => {
    const store = await loadHydrated();
    const otherBefore = store.getRules().find(r => r.payeeId === 'payee-netflix')!;

    store.saveRule('payee-baeckerei', { classification: 'bad' });

    expect(store.getRules().find(r => r.payeeId === 'payee-netflix')).toBe(otherBefore);
  });
});

describe('persistence write ordering', () => {
  it('reflects both of two rapid consecutive saves — not just the first — in what ends up on disk', async () => {
    const { store, AsyncStorage } = loadWith(false);
    await hydrated(store);

    // Synchronous back-to-back saves, exactly how PayeeEditScreen's Save
    // (and a Save immediately followed by a Clear) call it.
    store.saveRule('payee-netflix', { classification: 'good', amountEUR: 5 });
    store.saveRule('payee-netflix', { classification: 'good', amountEUR: 9 });

    await flush();

    const stored = JSON.parse((await AsyncStorage.getItem(RULES_KEY))!) as Rule[];
    expect(stored.find(r => r.payeeId === 'payee-netflix')).toEqual({
      payeeId: 'payee-netflix',
      classification: 'good',
      amountEUR: 9,
    });
  });
});

describe('persistence write failures', () => {
  /**
   * Pins the exact bug a promise-chain write queue is prone to: once one
   * `.then()` in the chain rejects, every `.then()` chained onto it afterward
   * skips its callback rather than running it, so an unguarded queue would
   * let a single failed write silently and permanently stop every later save
   * from ever reaching disk — with the in-memory store still looking correct
   * the whole time, so nothing on screen would say so.
   */
  it('reaches disk on a later save, even after an earlier AsyncStorage.setItem rejected', async () => {
    const { store, AsyncStorage } = loadWith(false);
    await hydrated(store);

    // spyOn (not a permanent mock) so only this one write fails; the next
    // setItem call falls through to the real in-memory mock.
    jest.spyOn(AsyncStorage, 'setItem').mockRejectedValueOnce(new Error('disk full'));

    store.saveRule('payee-netflix', { classification: 'good', amountEUR: 5 });
    await flush();

    store.saveRule('payee-netflix', { classification: 'good', amountEUR: 9 });
    await flush();

    const stored = JSON.parse((await AsyncStorage.getItem(RULES_KEY))!) as Rule[];
    expect(stored.find(r => r.payeeId === 'payee-netflix')).toEqual({
      payeeId: 'payee-netflix',
      classification: 'good',
      amountEUR: 9,
    });
    // The failed write is reported, not swallowed.
    expect(console.warn).toHaveBeenCalledWith(
      '[rulesStore] failed to persist rules',
      expect.any(Error),
    );
  });
});
