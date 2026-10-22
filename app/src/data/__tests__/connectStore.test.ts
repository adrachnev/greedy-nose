/**
 * Connecting a bank: the store parses what the backend sends defensively, turns
 * every failure into a flow state (never a throw), and decides "connected" only
 * from a status that is newer than the one before the attempt — otherwise an
 * old consent of the same bank would read as success.
 */

import {
  __resetConnectStoreForTests,
  checkAfterReturn,
  findBank,
  isUsableBankName,
  returnToApp,
  fetchBankStatuses,
  getConnectFlow,
  parseBankStatus,
  parseStatusBody,
  requestConnectLink,
  startConnect,
} from '../connectStore';
import { dismissConnectLink, openConnectLink } from '../openConnectLink';

jest.mock('../config', () => ({
  USE_BACKEND: true,
  BACKEND_BASE_URL: 'http://backend.test',
  BACKEND_TIMEOUT_MS: 1000,
}));
jest.mock('../openConnectLink', () => ({
  openConnectLink: jest.fn(),
  dismissConnectLink: jest.fn(() => Promise.resolve()),
}));

const fetchMock = jest.fn();
const openMock = openConnectLink as jest.Mock;

const SECRET_LINK = 'https://example.test/connect?token=one-time-secret';

function respond(body: unknown, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) };
}

const bank = (over: Record<string, unknown> = {}) => ({
  aspsp: 'Example Bank',
  state: 'connected',
  validUntil: '2027-01-19T00:00:00Z',
  lastSuccessAt: null,
  lastAttemptAt: null,
  lastError: null,
  ...over,
});

/** Routes the two endpoints; `statuses` are consumed one per /status call, the last repeats. */
function backend(statuses: unknown[], link: unknown = { url: SECRET_LINK }) {
  let i = 0;
  fetchMock.mockImplementation((url: string) => {
    if (url.endsWith('/connect-links')) {
      return Promise.resolve(link === null ? respond({}, false, 500) : respond(link));
    }
    const body = statuses[Math.min(i++, statuses.length - 1)];
    return Promise.resolve(body === null ? respond({}, false, 503) : respond(body));
  });
}

let logged: jest.SpyInstance[];

beforeEach(() => {
  fetchMock.mockReset();
  openMock.mockReset();
  openMock.mockResolvedValue(false);
  (dismissConnectLink as jest.Mock).mockClear();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  logged = (['warn', 'log', 'info', 'debug', 'error'] as const).map(m =>
    jest.spyOn(console, m).mockImplementation(() => {}),
  );
  __resetConnectStoreForTests();
});

afterEach(() => jest.restoreAllMocks());

describe('parsing', () => {
  it('reads camelCase and PascalCase rows and drops rows it cannot trust', () => {
    expect(parseBankStatus(bank())).toMatchObject({ aspsp: 'Example Bank', state: 'connected' });
    expect(parseBankStatus({ Aspsp: 'X', State: 'expired', ValidUntil: '2027-01-01' })).toMatchObject({
      aspsp: 'X',
      state: 'expired',
      validUntil: '2027-01-01',
    });
    expect(parseBankStatus(bank({ state: 'mystery' }))).toBeNull();
    expect(parseBankStatus(bank({ aspsp: '' }))).toBeNull();
    expect(parseBankStatus(null)).toBeNull();
    expect(parseBankStatus('x')).toBeNull();
  });

  it('accepts {banks: []}, {Banks: []} and a bare array, and refuses anything else', () => {
    expect(parseStatusBody({ banks: [bank(), { junk: 1 }] })).toHaveLength(1);
    expect(parseStatusBody({ Banks: [bank()] })).toHaveLength(1);
    expect(parseStatusBody([bank()])).toHaveLength(1);
    expect(parseStatusBody({ banks: 'no' })).toBeNull();
    expect(parseStatusBody(null)).toBeNull();
  });

  it('knows all four states', () => {
    for (const state of ['connected', 'expired', 'dead', 'first sync pending']) {
      expect(parseBankStatus(bank({ state }))?.state).toBe(state);
    }
  });
});

describe('requests', () => {
  it('posts the bank name and returns the https link; a link of another shape is refused', async () => {
    backend([{ banks: [] }]);
    await expect(requestConnectLink('Example Bank')).resolves.toBe(SECRET_LINK);
    const [, init] = fetchMock.mock.calls[0];
    expect(JSON.parse(init.body)).toEqual({ aspsp: 'Example Bank' });
    expect(init.headers.Authorization).toBe('Bearer test-api-key');

    backend([{ banks: [] }], { Url: 'ftp://x.test/y' });
    await expect(requestConnectLink('Example Bank')).resolves.toBeNull();
    backend([{ banks: [] }], { Url: 'https://x.test/y' });
    await expect(requestConnectLink('Example Bank')).resolves.toBe('https://x.test/y');
  });

  it('turns a network error and an HTTP error into null, never a throw', async () => {
    fetchMock.mockRejectedValue(new Error(`boom ${SECRET_LINK}`));
    await expect(fetchBankStatuses()).resolves.toBeNull();
    fetchMock.mockResolvedValue(respond({}, false, 500));
    await expect(requestConnectLink('X')).resolves.toBeNull();
  });
});

describe('the flow', () => {
  it('opens the link, waits, and shows the bank as connected once the status is new', async () => {
    backend([{ banks: [bank({ validUntil: '2026-12-01T00:00:00Z' })] }, { banks: [bank()] }]);

    await startConnect('example bank');
    expect(openMock).toHaveBeenCalledWith(SECRET_LINK);
    expect(getConnectFlow()).toEqual({ step: 'waiting', bank: 'example bank', tries: 0 });

    await checkAfterReturn();
    expect(getConnectFlow()).toEqual({
      step: 'connected',
      bank: 'Example Bank',
      validUntil: '2027-01-19T00:00:00Z',
    });
    expect(dismissConnectLink).toHaveBeenCalled();
  });

  it('a first-sync-pending bank counts as connected (sign-in and first sync are two steps)', async () => {
    backend([{ banks: [] }, { banks: [bank({ state: 'first sync pending' })] }]);
    await startConnect('Example Bank');
    await checkAfterReturn();
    expect(getConnectFlow().step).toBe('connected');
  });

  it('keeps waiting, without a dead end, while the status is still the old one or the bank is missing', async () => {
    backend([{ banks: [bank()] }, { banks: [bank()] }]);
    await startConnect('Example Bank');
    await checkAfterReturn();
    expect(getConnectFlow()).toEqual({ step: 'waiting', bank: 'Example Bank', notYet: true, tries: 1 });

    backend([{ banks: [] }, { banks: [] }]);
    __resetConnectStoreForTests();
    await startConnect('Example Bank');
    await checkAfterReturn();
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', notYet: true });
  });

  it('fails with "ended" when the bank is expired or dead', async () => {
    backend([{ banks: [] }, { banks: [bank({ state: 'dead' })] }]);
    await startConnect('Example Bank');
    await checkAfterReturn();
    expect(getConnectFlow()).toEqual({ step: 'failed', bank: 'Example Bank', reason: 'ended' });
  });

  it('fails with "link" when no link comes, and opens nothing', async () => {
    backend([{ banks: [] }], null);
    await startConnect('Example Bank');
    expect(getConnectFlow()).toEqual({ step: 'failed', bank: 'Example Bank', reason: 'link' });
    expect(openMock).not.toHaveBeenCalled();
  });

  it('fails with "browser" when the page cannot be opened', async () => {
    backend([{ banks: [] }]);
    openMock.mockRejectedValue(new Error('no browser'));
    await startConnect('Example Bank');
    expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'browser' });
  });

  it('fails with "status" when /status cannot be read after the return', async () => {
    backend([{ banks: [] }, null]);
    await startConnect('Example Bank');
    await checkAfterReturn();
    expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'status' });
  });

  it('checks at once when the user dismisses the in-app tab', async () => {
    backend([{ banks: [] }, { banks: [bank()] }]);
    openMock.mockResolvedValue(true);
    await startConnect('Example Bank');
    expect(getConnectFlow().step).toBe('connected');
  });

  it('ignores a return when no attempt is waiting, and shares one request between two returns', async () => {
    backend([{ banks: [bank()] }]);
    await checkAfterReturn();
    expect(fetchMock).not.toHaveBeenCalled();

    backend([{ banks: [] }, { banks: [bank()] }]);
    await startConnect('Example Bank');
    fetchMock.mockClear();
    await Promise.all([checkAfterReturn(), checkAfterReturn()]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('never logs the one-time link', async () => {
    backend([{ banks: [] }, null]);
    fetchMock.mockImplementationOnce(() => Promise.reject(new Error(SECRET_LINK)));
    await startConnect('Example Bank');
    await checkAfterReturn();
    const text = logged.flatMap(s => s.mock.calls.flat()).map(String).join('\n');
    expect(text).not.toContain('one-time-secret');
    expect(text).not.toContain('test-api-key');
  });
});

describe('review fixes', () => {
  it('does not open anything when the status before the attempt cannot be read', async () => {
    backend([null]);
    await startConnect('Example Bank');
    expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'status' });
    expect(openMock).not.toHaveBeenCalled();
  });

  it('matches exact first, then a single partial match; ambiguous or too short is no match', () => {
    const banks = [
      parseBankStatus(bank({ aspsp: 'Example Bank' }))!,
      parseBankStatus(bank({ aspsp: 'Example Bank Austria' }))!,
      parseBankStatus(bank({ aspsp: 'Other Savings' }))!,
    ];
    expect(findBank(banks, 'example bank')?.aspsp).toBe('Example Bank');
    expect(findBank(banks, 'savings')?.aspsp).toBe('Other Savings');
    expect(findBank(banks, 'austria')?.aspsp).toBe('Example Bank Austria');
    expect(findBank(banks, 'bank')).toBeUndefined(); // two candidates
    expect(findBank(banks, 'n')).toBeUndefined(); // too short
    expect(findBank(banks, '  ')).toBeUndefined();
    expect(isUsableBankName('n')).toBe(false);
    expect(isUsableBankName('dkb')).toBe(true);
  });

  it('refuses to start with a too short name', async () => {
    backend([{ banks: [] }]);
    await startConnect('n');
    expect(getConnectFlow().step).toBe('idle');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('counts checks without a match', async () => {
    backend([{ banks: [] }]);
    await startConnect('Example Bank');
    await checkAfterReturn();
    await checkAfterReturn();
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', notYet: true, tries: 2 });
  });

  it('keeps the tab open on a "not yet" foreground check, closes it by link, and when done', async () => {
    backend([{ banks: [] }, { banks: [] }, { banks: [] }, { banks: [bank()] }]);
    await startConnect('Example Bank');
    await checkAfterReturn(false);
    expect(dismissConnectLink).not.toHaveBeenCalled();
    await checkAfterReturn(true);
    expect(dismissConnectLink).toHaveBeenCalledTimes(1);
    await checkAfterReturn(false);
    expect(getConnectFlow().step).toBe('connected');
    expect(dismissConnectLink).toHaveBeenCalledTimes(2);
  });

  it('a tab dismissal right after a "not yet" check does not start another check', async () => {
    backend([{ banks: [] }]);
    let dismissTab!: (dismissed: boolean) => void;
    openMock.mockReturnValue(new Promise<boolean>(resolve => {
      dismissTab = resolve;
    }));
    const started = startConnect('Example Bank');
    await Promise.resolve();
    await new Promise<void>(r => setTimeout(r, 0));
    await checkAfterReturn(true);
    fetchMock.mockClear();
    dismissTab(true);
    await started;
    expect(fetchMock).not.toHaveBeenCalled();
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', notYet: true });
  });
});

describe('second review fixes', () => {
  afterEach(() => jest.useRealTimers());

  it('drops an in-memory attempt older than 30 minutes without a request', async () => {
    jest.useFakeTimers({ now: new Date('2026-10-22T10:00:00Z') });
    backend([{ banks: [] }, { banks: [bank()] }]);
    await startConnect('Example Bank');
    fetchMock.mockClear();

    jest.setSystemTime(new Date('2026-10-22T10:31:00Z'));
    await expect(returnToApp(false)).resolves.toBe(false);
    expect(getConnectFlow().step).toBe('idle');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('keeps a younger attempt and checks it', async () => {
    jest.useFakeTimers({ now: new Date('2026-10-22T10:00:00Z') });
    backend([{ banks: [] }, { banks: [bank()] }]);
    await startConnect('Example Bank');
    jest.setSystemTime(new Date('2026-10-22T10:29:00Z'));
    await expect(returnToApp(false)).resolves.toBe(true);
    expect(getConnectFlow().step).toBe('connected');
  });

  it('shows Settings on a "not yet" only for a link return, not for a plain foreground event', async () => {
    backend([{ banks: [] }]);
    await startConnect('Example Bank');
    await expect(returnToApp(false)).resolves.toBe(false);
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', notYet: true });
    await expect(returnToApp(true)).resolves.toBe(true);
  });

  it('always shows Settings when the check ends connected or failed', async () => {
    backend([{ banks: [] }, { banks: [bank()] }]);
    await startConnect('Example Bank');
    await expect(returnToApp(false)).resolves.toBe(true);

    __resetConnectStoreForTests();
    backend([{ banks: [] }, { banks: [bank({ state: 'dead' })] }]);
    await startConnect('Example Bank');
    await expect(returnToApp(false)).resolves.toBe(true);
    expect(getConnectFlow().step).toBe('failed');
  });

  it('saves the attempt before the flow turns to waiting', async () => {
    const seen: string[] = [];
    const AsyncStorage = require('@react-native-async-storage/async-storage').default;
    jest.spyOn(AsyncStorage, 'setItem').mockImplementation(async () => {
      seen.push(getConnectFlow().step);
    });
    backend([{ banks: [] }]);
    await startConnect('Example Bank');
    expect(seen).toEqual(['opening']);
    expect(getConnectFlow().step).toBe('waiting');
  });
});
