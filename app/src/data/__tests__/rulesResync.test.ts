/**
 * Story 2.1: the app resends every local rule once per start, so a rule whose
 * first POST failed (offline at save time) still reaches the backend. Same
 * isolateModules + mocked ./config approach as rulesStore.test.ts, because
 * USE_BACKEND is bound at module load; the feed is loaded in the same registry
 * so the store sees the payees a real start would.
 */

export {};

const RULES_KEY = '@greedy-nose/rules/v1';
const KNOWN_RULE = { payeeId: 'name:LIDL CONNECT', classification: 'good', amountEUR: 25 };
const UNKNOWN_RULE = { payeeId: 'name:GONE SHOP', classification: 'bad' };

const KNOWN_PAYEE = {
  id: 'name:LIDL CONNECT',
  name: 'LIDL CONNECT',
  initials: 'LC',
  iban: 'DE12 3456 7890 0000 1234 00',
};

type Modules = {
  store: typeof import('../rulesStore');
  AsyncStorage: { setItem: (key: string, value: string) => Promise<void> };
};

function loadWith(useBackend: boolean, apiKey?: string): Modules {
  let modules!: Modules;
  jest.isolateModules(() => {
    if (apiKey !== undefined) {
      require('../backendAuth').__setApiKeyForTests(apiKey);
    }
    jest.doMock('../config', () => ({
      USE_BACKEND: useBackend,
      BACKEND_BASE_URL: 'http://backend.test',
      BACKEND_TIMEOUT_MS: 1000,
    }));
    modules = {
      store: require('../rulesStore'),
      AsyncStorage: require('@react-native-async-storage/async-storage').default,
    };
  });
  return modules;
}

/** A macrotask settles every pending microtask chain, however deep. */
function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, 0));
}

type Emitter = { on(e: string, f: () => void): void; off(e: string, f: () => void): void };
const nodeProcess = (globalThis as unknown as { process: Emitter }).process;

const originalFetch = globalThis.fetch;
let fetchMock: jest.Mock;

/** Answers /debits with the feed body and /rules with 204; `offline` rejects both. */
function installFetch(offline = false, holdRules?: Promise<void>): void {
  fetchMock = jest.fn((url: string) => {
    if (offline) {
      return Promise.reject(new TypeError('Network request failed'));
    }
    if (url.endsWith('/debits')) {
      return Promise.resolve({
        ok: true,
        status: 200,
        json: async () => ({ payees: [KNOWN_PAYEE], debits: [] }),
      });
    }
    const answer = { ok: true, status: 204 };
    // Only the first /rules call is held, so a test can inspect the loop mid-flight.
    const held = holdRules;
    holdRules = undefined;
    return held ? held.then(() => answer) : Promise.resolve(answer);
  });
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
}

function ruleBodies(): Record<string, unknown>[] {
  return fetchMock.mock.calls
    .filter(([url]) => String(url).endsWith('/rules'))
    .map(([, init]) => JSON.parse(init.body));
}

async function seedRules(modules: Modules, rules: unknown[]): Promise<void> {
  await modules.AsyncStorage.setItem(RULES_KEY, JSON.stringify(rules));
}

beforeEach(() => {
  jest.spyOn(console, 'warn').mockImplementation(() => {});
});

afterEach(() => {
  (globalThis as unknown as { fetch: unknown }).fetch = originalFetch;
  jest.dontMock('../config');
  jest.restoreAllMocks();
});

describe('rules resync at start', () => {
  it('sends one POST per rule, with the real name where the feed knows the payee', async () => {
    installFetch();
    const modules = loadWith(true);
    await seedRules(modules, [KNOWN_RULE, UNKNOWN_RULE]);

    modules.store.startRulesResync();
    await flush();

    expect(ruleBodies()).toEqual([
      {
        payeeId: KNOWN_RULE.payeeId,
        classification: 'good',
        amountEUR: 25,
        name: 'LIDL CONNECT',
        initials: 'LC',
        iban: KNOWN_PAYEE.iban,
      },
      // Not in the feed: the placeholder goes out and the backend keeps any real name.
      {
        payeeId: UNKNOWN_RULE.payeeId,
        classification: 'bad',
        name: 'Unknown payee',
        initials: 'UP',
      },
    ]);
  });

  it('waits for the feed, so the real name is not replaced by a race', async () => {
    installFetch();
    const modules = loadWith(true);
    await seedRules(modules, [KNOWN_RULE]);

    modules.store.startRulesResync();
    await flush();

    const urls = fetchMock.mock.calls.map(([url]) => url);
    expect(urls).toEqual(['http://backend.test/debits', 'http://backend.test/rules']);
  });

  it('survives an offline start: no unhandled rejection, local rules unchanged', async () => {
    installFetch(true);
    const unhandled = jest.fn();
    nodeProcess.on('unhandledRejection', unhandled);
    const modules = loadWith(true);
    await seedRules(modules, [KNOWN_RULE, UNKNOWN_RULE]);

    modules.store.startRulesResync();
    await flush();
    await flush();

    nodeProcess.off('unhandledRejection', unhandled);
    // Extra only: Jest's own handling makes this a weak proof; the real one is
    // that startRulesResync() settles cleanly and the rules below are intact.
    expect(unhandled).not.toHaveBeenCalled();
    expect(modules.store.getRules()).toEqual([KNOWN_RULE, UNKNOWN_RULE]);
    // Feed failed too, yet both rules were still attempted (with placeholders).
    expect(ruleBodies()).toHaveLength(2);
  });

  it('does not resend on a second start in the same process', async () => {
    installFetch();
    const modules = loadWith(true);
    await seedRules(modules, [KNOWN_RULE]);

    modules.store.startRulesResync();
    await flush();
    modules.store.startRulesResync();
    await flush();

    expect(ruleBodies()).toHaveLength(1);
  });

  it('sends nothing in fixture mode', async () => {
    installFetch();
    const modules = loadWith(false);
    await seedRules(modules, [KNOWN_RULE]);

    modules.store.startRulesResync();
    await flush();

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('sends nothing without an API key', async () => {
    installFetch();
    const modules = loadWith(true, '');
    await seedRules(modules, [KNOWN_RULE]);

    modules.store.startRulesResync();
    await flush();

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('sends the POSTs one at a time, not in parallel', async () => {
    let release!: () => void;
    installFetch(false, new Promise<void>(resolve => (release = resolve)));
    const modules = loadWith(true);
    await seedRules(modules, [KNOWN_RULE, UNKNOWN_RULE]);

    modules.store.startRulesResync();
    await flush();
    expect(ruleBodies()).toHaveLength(1);

    release();
    await flush();
    expect(ruleBodies()).toHaveLength(2);
  });

  it('does not send a stale copy of a rule edited while the loop is running', async () => {
    let release!: () => void;
    installFetch(false, new Promise<void>(resolve => (release = resolve)));
    const modules = loadWith(true);
    await seedRules(modules, [KNOWN_RULE, UNKNOWN_RULE]);

    modules.store.startRulesResync();
    await flush();
    // First POST is in flight; the user flips the second payee to good meanwhile.
    modules.store.saveRule(
      { id: UNKNOWN_RULE.payeeId, name: 'GONE SHOP', initials: 'GS', iban: '' },
      { classification: 'good' },
    );
    release();
    await flush();

    const forSecond = ruleBodies().filter(b => b.payeeId === UNKNOWN_RULE.payeeId);
    expect(forSecond.length).toBeGreaterThan(0);
    expect(forSecond.every(b => b.classification === 'good')).toBe(true);
  });

  it('stops waiting for a feed that never settles and sends with placeholders', async () => {
    jest.useFakeTimers();
    fetchMock = jest.fn((url: string) =>
      String(url).endsWith('/debits')
        ? new Promise(() => {})
        : Promise.resolve({ ok: true, status: 204 }),
    );
    (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
    const modules = loadWith(true);
    await seedRules(modules, [KNOWN_RULE]);

    modules.store.startRulesResync();
    await jest.advanceTimersByTimeAsync(1000);
    expect(ruleBodies()).toHaveLength(0);

    await jest.advanceTimersByTimeAsync(1500);
    expect(ruleBodies()).toEqual([expect.objectContaining({ name: 'Unknown payee' })]);
    jest.useRealTimers();
  });
});
