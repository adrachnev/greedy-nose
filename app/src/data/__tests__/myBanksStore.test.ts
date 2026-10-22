/**
 * Story 5.6: the saved banks and the bank search. The network is a mocked
 * fetch; timers are fake so the polling interval is exact.
 */

import {
  __resetMyBanksStoreForTests,
  getMyBanks,
  parseBankHitsBody,
  parseMyBanksBody,
  refreshMyBanks,
  searchBanks,
  startMyBanksPolling,
  SYNC_POLL_MS,
  takeFirstStartNoBank,
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
