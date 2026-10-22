/**
 * Android may kill the app while the user is at the bank. The running attempt
 * is stored (attempt id, bank, country, start time: nothing else) and resumed
 * by the next process. Each case loads a fresh copy of the store under
 * jest.isolateModules, which is what a new process is; the fake storage is
 * shared across copies because it plays the disk.
 */

export {};

const SECRET_LINK = 'https://example.test/connect?token=one-time-secret';
const ATTEMPT_ID = 'attempt-7f3a91';
const KEY = 'connect.attempt.v2';

const disk = new Map<string, string>();
const storage = {
  getItem: jest.fn((key: string) => Promise.resolve(disk.get(key) ?? null)),
  setItem: jest.fn((key: string, value: string) => {
    disk.set(key, value);
    return Promise.resolve();
  }),
  removeItem: jest.fn((key: string) => {
    disk.delete(key);
    return Promise.resolve();
  }),
};

const fetchMock = jest.fn();
const openMock = jest.fn();

const reply = (body: unknown) => ({ ok: true, status: 200, json: () => Promise.resolve(body) });

/** The state the backend reports for the attempt; `/connect-links` answers with the link and the id. */
function backend(state: string) {
  fetchMock.mockImplementation((url: string) => {
    if (url.endsWith('/connect-links')) {
      return Promise.resolve(reply({ url: SECRET_LINK, attemptId: ATTEMPT_ID }));
    }
    if (url.endsWith('/accounts')) {
      return Promise.resolve(reply({ accounts: [{ id: '1', name: 'Main account', kind: 'current', last4: '4521', suggested: true }] }));
    }
    return Promise.resolve(reply({ state }));
  });
}

/** A new "process": a fresh store module over the same disk. */
function newProcess() {
  let store!: typeof import('../connectStore');
  jest.isolateModules(() => {
    jest.doMock('../config', () => ({
      USE_BACKEND: true,
      BACKEND_BASE_URL: 'http://backend.test',
      BACKEND_TIMEOUT_MS: 1000,
    }));
    jest.doMock('../openConnectLink', () => ({
      openConnectLink: openMock,
      dismissConnectLink: jest.fn(() => Promise.resolve()),
    }));
    jest.doMock('@react-native-async-storage/async-storage', () => ({
      __esModule: true,
      default: storage,
    }));
    store = require('../connectStore');
  });
  return store;
}

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));

beforeEach(() => {
  disk.clear();
  jest.clearAllMocks();
  openMock.mockResolvedValue(false);
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
});
afterEach(() => {
  jest.restoreAllMocks();
});

async function startAndKill() {
  backend('waiting');
  const first = newProcess();
  await first.startConnect('Example Bank', 'DE');
  expect(first.getConnectFlow().step).toBe('waiting');
}

it('resumes after a restart and shows the result', async () => {
  await startAndKill();

  backend('saved');
  const second = newProcess();
  expect(second.getConnectFlow().step).toBe('idle');
  await expect(second.returnToApp(false)).resolves.toBe(true);

  expect(second.getConnectFlow()).toEqual({ step: 'saved', bank: 'Example Bank', country: 'DE' });
  await flush();
  expect(disk.has(KEY)).toBe(false);
});

it('resumes into the account choice', async () => {
  await startAndKill();
  backend('choose_account');
  const second = newProcess();
  await expect(second.returnToApp(false)).resolves.toBe(true);
  expect(second.getConnectFlow()).toMatchObject({ step: 'choose_account', bank: 'Example Bank', country: 'DE' });
  expect(disk.has(KEY)).toBe(true);
});

it('resumes a bank that is still waiting, and keeps the stored attempt', async () => {
  await startAndKill();
  backend('waiting');
  const second = newProcess();
  await expect(second.returnToApp(false)).resolves.toBe(true);
  expect(second.getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Example Bank' });
  expect(disk.has(KEY)).toBe(true);
});

it('drops an attempt older than 30 minutes without a word and without a request', async () => {
  await startAndKill();
  const stored = JSON.parse(disk.get(KEY)!);
  stored.startedAt = Date.now() - 31 * 60 * 1000;
  disk.set(KEY, JSON.stringify(stored));

  const second = newProcess();
  fetchMock.mockClear();
  await expect(second.returnToApp(false)).resolves.toBe(false);
  expect(second.getConnectFlow().step).toBe('idle');
  expect(fetchMock).not.toHaveBeenCalled();
  await flush();
  expect(disk.has(KEY)).toBe(false);
});

it('shows an unknown attempt (404) as expired', async () => {
  await startAndKill();
  fetchMock.mockResolvedValue({ ok: false, status: 404, json: () => Promise.resolve({}) });
  const second = newProcess();
  await expect(second.returnToApp(false)).resolves.toBe(true);
  expect(second.getConnectFlow()).toMatchObject({ step: 'expired', bank: 'Example Bank', country: 'DE' });
  await flush();
  expect(disk.has(KEY)).toBe(false);
});

it('does nothing when no attempt is stored, and ignores unreadable stored data', async () => {
  const none = newProcess();
  await expect(none.returnToApp(true)).resolves.toBe(false);
  expect(none.getConnectFlow().step).toBe('idle');

  disk.set(KEY, '{not json');
  const garbage = newProcess();
  await garbage.returnToApp(false);
  expect(garbage.getConnectFlow().step).toBe('idle');

  disk.set(KEY, JSON.stringify({ id: 5, bank: 5, startedAt: 'x' }));
  const wrongShape = newProcess();
  await wrongShape.returnToApp(false);
  expect(wrongShape.getConnectFlow().step).toBe('idle');
  expect(fetchMock).not.toHaveBeenCalledWith(expect.stringContaining('/connect-attempts'), expect.anything());
});

it('clears the stored attempt when the user cancels', async () => {
  await startAndKill();
  const second = newProcess();
  backend('waiting');
  await second.returnToApp(false);
  await second.cancelAttempt();
  await flush();
  expect(disk.has(KEY)).toBe(false);
});

it('stores only the attempt id, the bank, the country and the start time', async () => {
  await startAndKill();
  const raw = disk.get(KEY)!;
  expect(Object.keys(JSON.parse(raw)).sort()).toEqual(['bank', 'country', 'id', 'startedAt']);
  expect(raw).not.toContain('one-time-secret');
  expect(raw).not.toContain('http');
  expect(raw).not.toContain('test-api-key');
});

describe('storage failures behave as "no attempt"', () => {
  it('a failing write does not stop the attempt', async () => {
    storage.setItem.mockRejectedValueOnce(new Error('disk full'));
    backend('waiting');
    const store = newProcess();
    await store.startConnect('Example Bank', 'DE');
    expect(store.getConnectFlow().step).toBe('waiting');
    expect(openMock).toHaveBeenCalledWith(SECRET_LINK);
  });

  it('a failing read and a failing delete never throw', async () => {
    storage.getItem.mockRejectedValueOnce(new Error('unreadable'));
    const store = newProcess();
    await expect(store.returnToApp(false)).resolves.toBe(false);
    expect(store.getConnectFlow().step).toBe('idle');

    storage.removeItem.mockRejectedValueOnce(new Error('nope'));
    expect(() => store.resetConnectFlow()).not.toThrow();
    await flush();
  });

  it('a delete that fails overwrites the attempt, so a finished attempt is not resumed', async () => {
    await startAndKill();
    const second = newProcess();
    backend('saved');
    storage.removeItem.mockRejectedValueOnce(new Error('nope'));
    await second.returnToApp(false);
    expect(second.getConnectFlow().step).toBe('saved');
    await flush();
    expect(disk.get(KEY)).toBe('ended');

    fetchMock.mockClear();
    const third = newProcess();
    await expect(third.returnToApp(false)).resolves.toBe(false);
    expect(third.getConnectFlow().step).toBe('idle');
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

it('after a cold start by the link, the start and the link share one read and one navigation', async () => {
  await startAndKill();
  backend('waiting');
  const second = newProcess();
  fetchMock.mockClear();
  // useConnectReturn calls once at the app start and once for the link that started the app.
  const [atStart, byLink] = await Promise.all([second.returnToApp(false), second.returnToApp(true)]);
  expect([atStart, byLink]).toEqual([true, false]);
  expect(fetchMock.mock.calls.filter(([url]) => String(url).includes('/connect-attempts/'))).toHaveLength(1);
});

it('a late fallback after a failed delete does not overwrite a newer attempt', async () => {
  await startAndKill();
  const store = newProcess();
  backend('saved');
  let failDelete!: (error: Error) => void;
  storage.removeItem.mockReturnValueOnce(new Promise<void>((_, reject) => (failDelete = reject)));
  await store.returnToApp(false);
  expect(store.getConnectFlow().step).toBe('saved');

  backend('waiting');
  await store.startConnect('Other Bank', 'AT');
  const newer = disk.get(KEY);
  expect(JSON.parse(newer!).bank).toBe('Other Bank');

  failDelete(new Error('nope'));
  await flush();
  expect(disk.get(KEY)).toBe(newer);
});
