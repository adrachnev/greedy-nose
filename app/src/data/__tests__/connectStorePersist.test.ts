/**
 * Android may kill the app while the user is at the bank. The running attempt
 * is stored (bank name, baseline validUntil, start time — nothing else) and
 * resumed by the next process. Each case loads a fresh copy of the store under
 * jest.isolateModules, which is what a new process is; the fake storage is
 * shared across copies because it plays the disk.
 */

export {};

const SECRET_LINK = 'https://example.test/connect?token=one-time-secret';
const KEY = 'connect.attempt.v1';

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

const bank = (validUntil: string) => ({ aspsp: 'Example Bank', state: 'connected', validUntil });
const ok = (body: unknown) => ({ ok: true, status: 200, json: () => Promise.resolve(body) });

function backend(statuses: unknown[]) {
  let i = 0;
  fetchMock.mockImplementation((url: string) =>
    Promise.resolve(
      url.endsWith('/connect-links')
        ? ok({ url: SECRET_LINK })
        : ok(statuses[Math.min(i++, statuses.length - 1)]),
    ),
  );
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
  jest.useRealTimers();
});

async function startAndKill(statuses: unknown[] = [{ banks: [bank('2026-12-01T00:00:00Z')] }]) {
  backend(statuses);
  const first = newProcess();
  await first.startConnect('Example Bank');
  expect(first.getConnectFlow().step).toBe('waiting');
}

it('resumes after a restart and shows the bank as connected', async () => {
  await startAndKill();

  backend([{ banks: [bank('2027-01-19T00:00:00Z')] }]);
  const second = newProcess();
  expect(second.getConnectFlow().step).toBe('idle');
  await expect(second.returnToApp(false)).resolves.toBe(true);

  expect(second.getConnectFlow()).toEqual({
    step: 'connected',
    bank: 'Example Bank',
    validUntil: '2027-01-19T00:00:00Z',
  });
  await flush();
  expect(disk.has(KEY)).toBe(false);
});

it('reports false for a foreground event after the result, so the user is not pulled back again', async () => {
  await startAndKill();
  backend([{ banks: [bank('2027-01-19T00:00:00Z')] }]);
  const second = newProcess();
  await expect(second.returnToApp(false)).resolves.toBe(true);
  await expect(second.returnToApp(false)).resolves.toBe(false);
});

it('still uses the stored baseline: an unchanged consent is "not yet", not success', async () => {
  await startAndKill();
  backend([{ banks: [bank('2026-12-01T00:00:00Z')] }]);
  const second = newProcess();
  await second.returnToApp(true);
  expect(second.getConnectFlow()).toMatchObject({ step: 'waiting', notYet: true, tries: 1 });
  expect(disk.has(KEY)).toBe(true);
});

it('drops an attempt older than 30 minutes without a word', async () => {
  await startAndKill();
  const stored = JSON.parse(disk.get(KEY)!);
  stored.startedAt = Date.now() - 31 * 60 * 1000;
  disk.set(KEY, JSON.stringify(stored));

  const second = newProcess();
  fetchMock.mockClear();
  await second.returnToApp(false);
  expect(second.getConnectFlow().step).toBe('idle');
  expect(fetchMock).not.toHaveBeenCalled();
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

  disk.set(KEY, JSON.stringify({ bank: 5, startedAt: 'x' }));
  const wrongShape = newProcess();
  await wrongShape.returnToApp(false);
  expect(wrongShape.getConnectFlow().step).toBe('idle');
});

it('clears the stored attempt on Start over and on a definite failure', async () => {
  await startAndKill();
  expect(disk.has(KEY)).toBe(true);
  const second = newProcess();
  backend([{ banks: [bank('2026-12-01T00:00:00Z')] }]);
  await second.returnToApp(false);
  second.resetConnectFlow();
  await flush();
  expect(disk.has(KEY)).toBe(false);

  await startAndKill();
  backend([{ banks: [{ ...bank('2027-02-01T00:00:00Z'), state: 'dead' }] }]);
  const third = newProcess();
  await third.returnToApp(false);
  expect(third.getConnectFlow()).toMatchObject({ step: 'failed', reason: 'ended' });
  await flush();
  expect(disk.has(KEY)).toBe(false);
});

it('stores only the bank name, the baseline date and the start time', async () => {
  await startAndKill();
  const raw = disk.get(KEY)!;
  expect(Object.keys(JSON.parse(raw)).sort()).toEqual(['bank', 'baseline', 'startedAt']);
  expect(JSON.parse(raw).baseline).toEqual({ validUntil: '2026-12-01T00:00:00Z' });
  expect(raw).not.toContain('one-time-secret');
  expect(raw).not.toContain('http');
  expect(raw).not.toContain('test-api-key');
});

it('stores "not there" when the bank was not known before', async () => {
  await startAndKill([{ banks: [] }]);
  expect(JSON.parse(disk.get(KEY)!).baseline).toBeNull();
  backend([{ banks: [bank('2027-01-19T00:00:00Z')] }]);
  const second = newProcess();
  await second.returnToApp(false);
  expect(second.getConnectFlow().step).toBe('connected');
});

describe('storage failures behave as "no attempt"', () => {
  it('a failing write does not stop the attempt', async () => {
    storage.setItem.mockRejectedValueOnce(new Error('disk full'));
    backend([{ banks: [] }]);
    const store = newProcess();
    await store.startConnect('Example Bank');
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
});

it('shows Settings on the first resume after a restart even when nothing is new, but not on the next plain foreground event', async () => {
  await startAndKill();
  backend([{ banks: [bank('2026-12-01T00:00:00Z')] }]);
  const second = newProcess();
  await expect(second.returnToApp(false)).resolves.toBe(true);
  expect(second.getConnectFlow()).toMatchObject({ step: 'waiting', notYet: true });
  await expect(second.returnToApp(false)).resolves.toBe(false);
});
