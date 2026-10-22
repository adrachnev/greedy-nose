/**
 * Story 5.6: the saved banks and the bank search. The network is a mocked
 * fetch; timers are fake so the polling interval is exact.
 */

import {
  __resetMyBanksStoreForTests,
  canRetryFirstSync,
  findMyBank,
  getMyBanks,
  parseBankHitsBody,
  parseMyBanksBody,
  refreshMyBanks,
  retryFirstSync,
  searchBanks,
  startMyBanksPolling,
  SYNC_POLL_MS,
  takeFirstStartNoBank,
  watchMyBanks,
} from '../myBanksStore';

jest.mock('../config', () => ({
  USE_BACKEND: true,
  BACKEND_BASE_URL: 'http://backend.test',
  BACKEND_TIMEOUT_MS: 1000,
}));
jest.mock('../backendAuth', () => ({
  hasApiKey: () => true,
  backendHeaders: (extra: Record<string, string> = {}) => ({ Authorization: 'Bearer test', ...extra }),
}));

const fetchMock = jest.fn();
let warn: jest.SpyInstance;

const bank = (over: Record<string, unknown> = {}) => ({
  name: 'Example Bank',
  country: 'DE',
  account: 'Main account',
  validUntil: '2027-01-19T12:00:00',
  state: 'connected',
  ...over,
});

function answer(body: unknown, ok = true) {
  fetchMock.mockResolvedValueOnce({ ok, status: ok ? 200 : 500, json: () => Promise.resolve(body) });
}

beforeEach(() => {
  __resetMyBanksStoreForTests();
  fetchMock.mockReset();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  warn = jest.spyOn(console, 'warn').mockImplementation(() => {});
});
afterEach(() => {
  jest.useRealTimers();
  jest.restoreAllMocks();
});

describe('parsing', () => {
  it('keeps the four states, nulls the missing parts and drops what it cannot place', () => {
    const rows = parseMyBanksBody({
      banks: [
        bank(),
        bank({ name: 'Example Direct', country: null, account: null, state: 'syncing' }),
        bank({ state: 'dead' }),
        bank({ name: '' }),
        'junk',
      ],
    });
    expect(rows?.map(r => [r.name, r.country, r.account, r.state])).toEqual([
      ['Example Bank', 'DE', 'Main account', 'connected'],
      ['Example Direct', null, null, 'syncing'],
    ]);
  });

  it('says "not that shape" for a body without a banks array', () => {
    expect(parseMyBanksBody({})).toBeNull();
    expect(parseMyBanksBody(null)).toBeNull();
    expect(parseBankHitsBody([])).toBeNull();
  });

  it('reads search hits with their connected flag', () => {
    expect(
      parseBankHitsBody({
        banks: [
          { name: 'A', country: 'DE', connected: true },
          { name: 'B', country: 'AT' },
          { name: 'no country' },
        ],
      }),
    ).toEqual([
      { name: 'A', country: 'DE', connected: true },
      { name: 'B', country: 'AT', connected: false },
    ]);
  });
});

describe('refreshMyBanks', () => {
  it('goes loading -> ready and sends the key', async () => {
    expect(getMyBanks().status).toBe('loading');
    answer({ banks: [bank()] });
    await refreshMyBanks();
    expect(getMyBanks().status).toBe('ready');
    expect(getMyBanks().banks).toHaveLength(1);
    expect(fetchMock.mock.calls[0][0]).toBe('http://backend.test/my-banks');
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBe('Bearer test');
  });

  it('says failed when nothing good ever arrived, and never claims an empty list', async () => {
    answer({}, false);
    await refreshMyBanks();
    expect(getMyBanks()).toEqual({ status: 'failed', banks: [] });
    expect(takeFirstStartNoBank()).toBe(false);
  });

  it('keeps the last good list when a later read fails', async () => {
    answer({ banks: [bank()] });
    await refreshMyBanks();
    fetchMock.mockRejectedValueOnce(new Error('offline'));
    await refreshMyBanks();
    expect(getMyBanks().status).toBe('ready');
    expect(getMyBanks().banks).toHaveLength(1);
  });

  it('does not publish an unchanged answer', async () => {
    answer({ banks: [bank()] });
    await refreshMyBanks();
    const before = getMyBanks();
    answer({ banks: [bank()] });
    await refreshMyBanks();
    expect(getMyBanks()).toBe(before);
  });

  it('shares one request between concurrent callers', async () => {
    answer({ banks: [] });
    await Promise.all([refreshMyBanks(), refreshMyBanks()]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('reports the first-start jump once, only when the first good answer is empty', async () => {
    answer({ banks: [] });
    await refreshMyBanks();
    expect(takeFirstStartNoBank()).toBe(true);
    expect(takeFirstStartNoBank()).toBe(false);
  });

  it('does not jump when the first good answer has a bank, even if a later one is empty', async () => {
    answer({ banks: [bank()] });
    await refreshMyBanks();
    answer({ banks: [] });
    await refreshMyBanks();
    expect(takeFirstStartNoBank()).toBe(false);
  });

  it('logs no bank name', async () => {
    answer({ banks: [bank()] });
    await refreshMyBanks();
    fetchMock.mockRejectedValueOnce(new Error('http://x/Example Bank'));
    await refreshMyBanks();
    expect(JSON.stringify(warn.mock.calls)).not.toMatch(/Example|Main account/);
  });
});

describe('polling', () => {
  it('reads at once, then every few seconds only while a bank syncs, and stops when released', async () => {
    jest.useFakeTimers();
    answer({ banks: [bank({ state: 'syncing' })] });
    const stop = startMyBanksPolling();
    await jest.advanceTimersByTimeAsync(0);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    answer({ banks: [bank({ state: 'syncing' })] });
    await jest.advanceTimersByTimeAsync(SYNC_POLL_MS);
    expect(fetchMock).toHaveBeenCalledTimes(2);

    answer({ banks: [bank({ state: 'connected' })] });
    await jest.advanceTimersByTimeAsync(SYNC_POLL_MS);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(getMyBanks().banks[0].state).toBe('connected');

    // Nothing syncs any more: the timer ticks, the network stays quiet.
    await jest.advanceTimersByTimeAsync(SYNC_POLL_MS * 3);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    stop();
    expect(jest.getTimerCount()).toBe(0);
  });

  it('keeps the timer until the last screen lets go', async () => {
    jest.useFakeTimers();
    answer({ banks: [] });
    const first = startMyBanksPolling();
    const second = startMyBanksPolling();
    await jest.advanceTimersByTimeAsync(0);
    first();
    first();
    expect(jest.getTimerCount()).toBe(1);
    second();
    expect(jest.getTimerCount()).toBe(0);
  });
});

describe('searchBanks', () => {
  it('asks with the encoded text and returns the hits', async () => {
    answer({ banks: [{ name: 'Example Bank', country: 'DE', connected: false }] });
    const result = await searchBanks('  exa & mple ');
    expect(result).toEqual({ ok: true, banks: [{ name: 'Example Bank', country: 'DE', connected: false }] });
    expect(fetchMock.mock.calls[0][0]).toBe('http://backend.test/banks?q=exa%20%26%20mple');
  });

  it('sends nothing for text that is too short', async () => {
    expect(await searchBanks(' e ')).toEqual({ ok: false });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('caps the text at 100 characters', async () => {
    answer({ banks: [] });
    await searchBanks('a'.repeat(150));
    expect(fetchMock.mock.calls[0][0]).toBe(`http://backend.test/banks?q=${'a'.repeat(100)}`);
  });

  it('says failed, not "no hit", when the answer is an error or unreadable', async () => {
    answer({}, false);
    expect(await searchBanks('example')).toEqual({ ok: false });
    answer({ nonsense: true });
    expect(await searchBanks('example')).toEqual({ ok: false });
  });

  it('never logs the typed text', async () => {
    fetchMock.mockRejectedValueOnce(new Error('http://backend.test/banks?q=secrettext'));
    await searchBanks('secrettext');
    answer({}, false);
    await searchBanks('secrettext');
    expect(JSON.stringify(warn.mock.calls)).not.toContain('secrettext');
  });
});

describe('Story 5.8: the first sync', () => {
  const failed = bank({ state: 'sync_failed', reason: 'bank-error' });
  const reply = (status: number, body: unknown = {}) =>
    fetchMock.mockResolvedValueOnce({ ok: status < 300, status, json: () => Promise.resolve(body) });
  const load = async (banks: unknown[]) => {
    answer({ banks });
    await refreshMyBanks();
    fetchMock.mockClear();
  };

  it('finds the saved bank by normalized name, and by country when two share a name', () => {
    const rows = parseMyBanksBody({ banks: [bank({ country: 'AT' }), bank({ country: 'DE' })] })!;
    expect(findMyBank(rows, 'example-bank', 'DE')?.country).toBe('DE');
    expect(findMyBank(rows, 'example-bank', 'AT')?.country).toBe('AT');
    expect(findMyBank(rows, 'EXAMPLE BANK', null)?.country).toBe('AT');
    expect(findMyBank(rows, 'Other', 'DE')).toBeUndefined();
  });

  it('with a country, never takes a row of another country; an old row without one still counts', () => {
    const other = parseMyBanksBody({ banks: [bank({ country: 'AT' })] })!;
    expect(findMyBank(other, 'Example Bank', 'DE')).toBeUndefined();
    const old = parseMyBanksBody({ banks: [bank({ country: null }), bank({ country: 'AT' })] })!;
    expect(findMyBank(old, 'Example Bank', 'DE')?.country).toBeNull();
    expect(findMyBank(old, 'Example Bank', 'AT')?.country).toBe('AT');
  });

  it('offers Try again for a failed sync, but not for the daily limit or other states', () => {
    const [plain, limit, ok] = parseMyBanksBody({
      banks: [failed, bank({ name: 'B', state: 'sync_failed', reason: 'daily-limit' }), bank({ name: 'C' })],
    })!;
    expect(canRetryFirstSync(plain)).toBe(true);
    expect(canRetryFirstSync(limit)).toBe(false);
    expect(canRetryFirstSync(ok)).toBe(false);
  });

  it('posts the bank name, shows it syncing at once and reads again', async () => {
    await load([failed]);
    reply(200);
    answer({ banks: [bank({ state: 'syncing' })] });
    expect(await retryFirstSync('Example Bank')).toBe('queued');
    expect(fetchMock.mock.calls[0][0]).toBe('http://backend.test/my-banks/first-sync');
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ aspsp: 'Example Bank' });
    expect(getMyBanks().banks[0].state).toBe('syncing');
    expect(fetchMock.mock.calls[1][0]).toBe('http://backend.test/my-banks');
  });

  it('on 409 reads the state again and shows what it says', async () => {
    await load([failed]);
    reply(409);
    answer({ banks: [bank({ state: 'sync_failed', reason: 'daily-limit' })] });
    expect(await retryFirstSync('Example Bank')).toBe('conflict');
    expect(getMyBanks().banks[0].reason).toBe('daily-limit');
  });

  it('on 503 or no network changes nothing', async () => {
    await load([failed]);
    reply(503);
    expect(await retryFirstSync('Example Bank')).toBe('unavailable');
    fetchMock.mockRejectedValueOnce(new Error('network'));
    expect(await retryFirstSync('Example Bank')).toBe('unavailable');
    expect(getMyBanks().banks[0].state).toBe('sync_failed');
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('ignores a second tap while the first is on its way', async () => {
    await load([failed]);
    reply(200);
    answer({ banks: [bank({ state: 'syncing' })] });
    const first = retryFirstSync('Example Bank');
    expect(await retryFirstSync('Example Bank')).toBe('busy');
    expect(await first).toBe('queued');
    expect(fetchMock.mock.calls.filter(([, init]) => init.method === 'POST')).toHaveLength(1);
  });

  it('a failed read already in flight does not bring back the old state after a queued retry', async () => {
    await load([failed]);
    let release!: () => void;
    fetchMock.mockImplementationOnce(
      () =>
        new Promise(resolve => {
          release = () => resolve({ ok: true, status: 200, json: () => Promise.resolve({ banks: [failed] }) });
        }),
    );
    const stale = refreshMyBanks();
    reply(200);
    answer({ banks: [bank({ state: 'syncing' })] });
    const retry = retryFirstSync('Example Bank');
    await Promise.resolve();
    release();
    await stale;
    expect(await retry).toBe('queued');
    expect(getMyBanks().banks[0].state).toBe('syncing');
  });

  it('logs no bank name', async () => {
    await load([failed]);
    reply(503);
    await retryFirstSync('Example Bank');
    expect(JSON.stringify(warn.mock.calls)).not.toContain('Example');
  });

  it('a watching screen keeps reading although no bank is syncing, and stops when it lets go', async () => {
    jest.useFakeTimers();
    answer({ banks: [] });
    const stopPolling = startMyBanksPolling();
    const stop = watchMyBanks();
    await jest.advanceTimersByTimeAsync(0);
    fetchMock.mockClear();
    answer({ banks: [] });
    await jest.advanceTimersByTimeAsync(SYNC_POLL_MS);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    stop();
    await jest.advanceTimersByTimeAsync(SYNC_POLL_MS * 3);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    stopPolling();
    expect(jest.getTimerCount()).toBe(0);
  });
});
