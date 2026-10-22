/**
 * Story 5.9: saving a set of rules. saveRules writes everything with one
 * notification and one persist and sends nothing; saveRulesBatch sends ONE
 * POST /rules/batch and writes locally only after its answer. fetch is a mock.
 */

import { Payee } from '../../domain/model';

const RULES_KEY = '@greedy-nose/rules/v1';
const payee = (id: string, iban = ''): Payee => ({ id, name: `Name ${id}`, initials: 'NX', iban });

type Modules = {
  store: typeof import('../rulesStore');
  AsyncStorage: { setItem: (key: string, value: string) => Promise<void>; getItem: (key: string) => Promise<string | null> };
};

let setItemSpy: jest.SpyInstance;

function loadWith(useBackend: boolean): Modules {
  let modules!: Modules;
  jest.isolateModules(() => {
    require('../backendAuth').__setApiKeyForTests('k'.repeat(40));
    jest.doMock('../config', () => ({
      USE_BACKEND: useBackend,
      BACKEND_BASE_URL: 'http://backend.test',
      BACKEND_TIMEOUT_MS: 1000,
    }));
    modules = {
      store: require('../rulesStore'),
      AsyncStorage: require('@react-native-async-storage/async-storage').default,
    };
    setItemSpy = jest.spyOn(modules.AsyncStorage, 'setItem');
  });
  return modules;
}

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));
const originalFetch = globalThis.fetch;
const fetchMock = jest.fn();

beforeEach(() => {
  fetchMock.mockReset();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
});
afterEach(() => {
  globalThis.fetch = originalFetch;
  jest.restoreAllMocks();
});

const reply = (status: number) =>
  Promise.resolve({ ok: status < 300, status, json: () => Promise.reject(new Error('no body')) });

describe('saveRules', () => {
  it('writes every rule with one notification and one persist, and sends nothing', async () => {
    const { store } = loadWith(true);
    const stopHydration = store.subscribe(() => {});
    await flush();
    setItemSpy.mockClear();
    const listener = jest.fn();
    const stop = store.subscribe(listener);
    store.saveRules([
      { payee: payee('a'), draft: { classification: 'good' } },
      { payee: payee('b'), draft: { classification: 'good' } },
      { payee: payee('c'), draft: { classification: 'good' } },
    ]);
    await flush();
    expect(listener).toHaveBeenCalledTimes(1);
    expect(setItemSpy).toHaveBeenCalledTimes(1);
    expect(JSON.parse(setItemSpy.mock.calls[0][1])).toEqual([
      { payeeId: 'a', classification: 'good' },
      { payeeId: 'b', classification: 'good' },
      { payeeId: 'c', classification: 'good' },
    ]);
    expect(fetchMock).not.toHaveBeenCalled();
    stop();
    stopHydration();
  });

  it('replaces the rule of a payee that has one', () => {
    const { store } = loadWith(false);
    store.saveRule(payee('a'), { classification: 'bad', amountEUR: 5 });
    store.saveRules([{ payee: payee('a'), draft: { classification: 'good' } }]);
    expect(store.getRules()).toEqual([{ payeeId: 'a', classification: 'good' }]);
  });
});

describe('saveRulesBatch', () => {
  it('sends ONE request with the items of the contract, then writes locally', async () => {
    const { store, AsyncStorage } = loadWith(true);
    fetchMock.mockImplementation(() => reply(204));
    const ok = await store.saveRulesBatch([
      { payee: payee('a', 'DE00'), draft: { classification: 'good' } },
      { payee: payee('b'), draft: { classification: 'good' } },
    ]);
    expect(ok).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('http://backend.test/rules/batch');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual({
      rules: [
        { payeeId: 'a', classification: 'good', name: 'Name a', initials: 'NX', iban: 'DE00' },
        { payeeId: 'b', classification: 'good', name: 'Name b', initials: 'NX' },
      ],
    });
    expect(store.getRules().map(r => r.payeeId)).toEqual(['a', 'b']);
    await flush();
    expect(JSON.parse((await AsyncStorage.getItem(RULES_KEY)) ?? '[]')).toHaveLength(2);
  });

  it.each([
    ['a 400', () => reply(400)],
    ['a 503', () => reply(503)],
    ['no network', () => Promise.reject(new Error('network'))],
  ])('writes nothing locally on %s', async (_, answer) => {
    const { store } = loadWith(true);
    fetchMock.mockImplementation(answer);
    const ok = await store.saveRulesBatch([{ payee: payee('a'), draft: { classification: 'good' } }]);
    expect(ok).toBe(false);
    expect(store.getRules()).toEqual([]);
    await flush();
    expect(setItemSpy).not.toHaveBeenCalled();
  });

  it('sends a trimmed name, a placeholder for a blank one, and never blank initials', async () => {
    const { store } = loadWith(true);
    fetchMock.mockImplementation(() => reply(204));
    await store.saveRulesBatch([
      { payee: { id: 'a', name: '  Shop  ', initials: ' ', iban: '' }, draft: { classification: 'good' } },
      { payee: { id: 'b', name: '   ', initials: '', iban: '' }, draft: { classification: 'good' } },
    ]);
    expect(JSON.parse(fetchMock.mock.calls[0][1].body).rules).toEqual([
      { payeeId: 'a', classification: 'good', name: 'Shop', initials: 'S' },
      { payeeId: 'b', classification: 'good', name: 'Unknown payee', initials: 'UP' },
    ]);
  });

  it('refuses a set the backend would refuse whole, without sending', async () => {
    const { store } = loadWith(true);
    const entries = Array.from({ length: store.MAX_BATCH_RULES + 1 }, (_, i) => ({
      payee: payee(`p${i}`),
      draft: { classification: 'good' as const },
    }));
    expect(await store.saveRulesBatch(entries)).toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(store.getRules()).toEqual([]);
  });

  it('in fixture mode writes locally and sends nothing', async () => {
    const { store } = loadWith(false);
    expect(await store.saveRulesBatch([{ payee: payee('a'), draft: { classification: 'good' } }])).toBe(true);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(store.getRules().map(r => r.payeeId)).toContain('a');
  });
});
