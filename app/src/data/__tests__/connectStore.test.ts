/**
 * Connecting a bank (Story 5.7): the store parses what the backend sends
 * defensively, turns every failure into a flow state (never a throw), follows
 * the attempt through its states, and logs nothing but paths and statuses.
 */

import AsyncStorage from '@react-native-async-storage/async-storage';
import {
  __resetConnectStoreForTests,
  ATTEMPT_MAX_AGE_MS,
  cancelAttempt,
  checkAttempt,
  chooseAccount,
  forgetEndedAttempt,
  getConnectFlow,
  isUsableBankName,
  NO_ANSWER_AFTER_READS,
  parseAccountsBody,
  parseAttemptBody,
  returnToApp,
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
const dismissMock = dismissConnectLink as jest.Mock;

const SECRET_LINK = 'https://example.test/connect?token=one-time-secret';
const ATTEMPT_ID = 'attempt-7f3a91';
const BASE = 'http://backend.test';

type Reply = { status: number; body?: unknown };
const ok = (body: unknown): Reply => ({ status: 200, body });
const refused = (status: number): Reply => ({ status });

function toResponse(reply: Reply) {
  return {
    ok: reply.status >= 200 && reply.status < 300,
    status: reply.status,
    json: () => (reply.body === undefined ? Promise.reject(new Error('no body')) : Promise.resolve(reply.body)),
  };
}

const account = (over: Record<string, unknown> = {}) => ({
  id: '1',
  name: 'Main account',
  kind: 'current',
  last4: '4521',
  suggested: true,
  ...over,
});

type Script = {
  link?: Reply | null;
  /** One per GET of the attempt; the last repeats. A `null` entry is a network failure. */
  states?: (Reply | null)[];
  accounts?: Reply | null;
  choice?: Reply | null;
  cancel?: Reply | null;
};

/** Routes the five endpoints of the attempt. */
function backend(script: Script = {}) {
  const states = script.states ?? [ok({ state: 'waiting' })];
  let read = 0;
  fetchMock.mockImplementation((url: string, init: { method: string }) => {
    const path = url.replace(BASE, '');
    const pick = (reply: Reply | null | undefined, fallback: Reply) => {
      const chosen = reply === undefined ? fallback : reply;
      return chosen === null ? Promise.reject(new Error(`network ${url}`)) : Promise.resolve(toResponse(chosen));
    };
    if (path === '/connect-links') {
      return pick(script.link, ok({ url: SECRET_LINK, attemptId: ATTEMPT_ID }));
    }
    if (path.endsWith('/accounts')) {
      return pick(script.accounts, ok({ accounts: [account(), account({ id: '2', name: 'Savings', kind: 'savings', last4: '0937', suggested: false })] }));
    }
    if (path.endsWith('/choice')) {
      return pick(script.choice, ok({ state: 'saved' }));
    }
    if (path.endsWith('/cancel')) {
      return pick(script.cancel, ok({ state: 'failed', reason: 'cancelled' }));
    }
    if (init.method === 'GET' && path.startsWith('/connect-attempts/')) {
      return pick(states[Math.min(read++, states.length - 1)], ok({ state: 'waiting' }));
    }
    return Promise.reject(new Error(`unexpected ${path}`));
  });
}

/** `METHOD /path` of every request so far, the attempt id written as {id}. */
const requests = () =>
  fetchMock.mock.calls.map(([url, init]) => `${init.method} ${url.replace(BASE, '').replace(ATTEMPT_ID, '{id}')}`);

let logged: jest.SpyInstance[];

beforeEach(async () => {
  fetchMock.mockReset();
  openMock.mockReset();
  openMock.mockResolvedValue(false);
  dismissMock.mockClear();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  logged = (['warn', 'log', 'info', 'debug', 'error'] as const).map(m =>
    jest.spyOn(console, m).mockImplementation(() => {}),
  );
  await AsyncStorage.clear();
  __resetConnectStoreForTests();
});

afterEach(() => {
  jest.restoreAllMocks();
});

const flush = () => new Promise<void>(resolve => setTimeout(resolve, 0));

async function startWaiting(script: Script = {}) {
  backend(script);
  await startConnect('Example Bank', 'DE');
  expect(getConnectFlow().step).toBe('waiting');
}

describe('parsing', () => {
  it('reads the five states and maps a reason it does not know to "unknown"', () => {
    for (const state of ['waiting', 'choose_account', 'saved', 'failed', 'expired']) {
      expect(parseAttemptBody({ state })?.state).toBe(state);
    }
    expect(parseAttemptBody({ state: 'failed', reason: 'no-supported-account' })?.reason).toBe(
      'no-supported-account',
    );
    expect(parseAttemptBody({ state: 'failed', reason: 'something new' })?.reason).toBe('unknown');
    expect(parseAttemptBody({ state: 'mystery' })).toBeNull();
    expect(parseAttemptBody(null)).toBeNull();
    expect(parseAttemptBody('x')).toBeNull();
  });

  it('reads account rows, cuts last4 to four characters, drops rows it cannot trust, and refuses an empty list', () => {
    const rows = parseAccountsBody({
      accounts: [account({ last4: 'DE0000004521' }), { name: 'no id' }, account({ id: '3', last4: null, kind: undefined })],
    });
    expect(rows).toEqual([
      { id: '1', name: 'Main account', kind: 'current', last4: '4521', suggested: true },
      { id: '3', name: 'Main account', kind: 'account', last4: null, suggested: true },
    ]);
    expect(parseAccountsBody({ accounts: [] })).toBeNull();
    expect(parseAccountsBody({ accounts: 'no' })).toBeNull();
    expect(parseAccountsBody([account()])).toBeNull();
    expect(parseAccountsBody(null)).toBeNull();
  });

  it('needs two usable characters for a bank name', () => {
    expect(isUsableBankName('n')).toBe(false);
    expect(isUsableBankName(' - ')).toBe(false);
    expect(isUsableBankName('N26')).toBe(true);
  });
});

describe('starting', () => {
  it('posts bank and country with the key, opens the link, and waits', async () => {
    await startWaiting();
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(`${BASE}/connect-links`);
    expect(JSON.parse(init.body)).toEqual({ aspsp: 'Example Bank', country: 'DE' });
    expect(init.headers.Authorization).toBe('Bearer test-api-key');
    expect(openMock).toHaveBeenCalledWith(SECRET_LINK);
    expect(getConnectFlow()).toEqual({ step: 'waiting', noAnswer: false, bank: 'Example Bank', country: 'DE' });
  });

  it('shows "opening" while the link is asked for', async () => {
    let release!: (value: unknown) => void;
    fetchMock.mockReturnValue(new Promise(resolve => (release = resolve)));
    const started = startConnect('Example Bank', 'DE');
    expect(getConnectFlow()).toEqual({ step: 'opening', bank: 'Example Bank', country: 'DE' });
    release(toResponse(refused(500)));
    await started;
    expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'link' });
  });

  it('ignores a second start while one is opening, and a name that is too short', async () => {
    fetchMock.mockReturnValue(new Promise(() => {}));
    startConnect('Example Bank', 'DE');
    startConnect('Other Bank', 'AT');
    startConnect('x', 'DE');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('says "already connected" for a 409, and "link" for any other refusal or a link of the wrong shape', async () => {
    backend({ link: refused(409) });
    await startConnect('Example Bank', 'DE');
    expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'already-connected' });

    for (const link of [refused(500), null, ok({ url: SECRET_LINK }), ok({ attemptId: ATTEMPT_ID }), ok({ url: 'ftp://x.test', attemptId: ATTEMPT_ID }), ok(null)]) {
      backend({ link });
      await startConnect('Example Bank', 'DE');
      expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'link' });
    }
    expect(openMock).not.toHaveBeenCalled();
  });

  it('fails with "browser" when no browser opens the link', async () => {
    openMock.mockRejectedValue(new Error('nothing opened'));
    backend();
    await startConnect('Example Bank', 'DE');
    expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'browser' });
  });

  it('reads the state at once when the user dismissed the Custom Tab', async () => {
    openMock.mockResolvedValue(true);
    backend({ states: [ok({ state: 'saved' })] });
    await startConnect('Example Bank', 'DE');
    expect(getConnectFlow().step).toBe('saved');
  });

  it('does not read the state when the page went to the plain browser', async () => {
    await startWaiting();
    expect(requests()).toEqual(['POST /connect-links']);
  });

  it('does not come back to life when the user cancelled while the link was being asked for', async () => {
    let release!: (value: unknown) => void;
    fetchMock.mockReturnValue(new Promise(resolve => (release = resolve)));
    const started = startConnect('Example Bank', 'DE');
    await cancelAttempt();
    release(toResponse(ok({ url: SECRET_LINK, attemptId: ATTEMPT_ID })));
    await started;
    expect(getConnectFlow().step).toBe('idle');
    expect(openMock).not.toHaveBeenCalled();
  });
});

describe('one attempt at a time', () => {
  it('refuses a second start while an attempt waits or shows the account choice', async () => {
    await startWaiting({ states: [ok({ state: 'choose_account' })] });
    fetchMock.mockClear();
    await startConnect('Other Bank', 'AT');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Example Bank' });

    await checkAttempt();
    fetchMock.mockClear();
    await startConnect('Other Bank', 'AT');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(getConnectFlow()).toMatchObject({ step: 'choose_account', bank: 'Example Bank' });
  });

  it('allows a new start once the attempt has ended', async () => {
    await startWaiting({ states: [ok({ state: 'failed', reason: 'cancelled' })] });
    await checkAttempt();
    backend();
    await startConnect('Other Bank', 'AT');
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Other Bank' });
  });

  it('a late link reply of a cancelled start starts nothing, opens no tab, and closes its attempt', async () => {
    let releaseFirst!: (value: unknown) => void;
    fetchMock.mockReturnValueOnce(new Promise(resolve => (releaseFirst = resolve)));
    const first = startConnect('Example Bank', 'DE');
    await cancelAttempt();
    backend({ link: ok({ url: 'https://example.test/second', attemptId: 'attempt-second' }) });
    await startConnect('Other Bank', 'AT');
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Other Bank' });
    openMock.mockClear();
    fetchMock.mockClear();

    releaseFirst(toResponse(ok({ url: SECRET_LINK, attemptId: ATTEMPT_ID })));
    await first;
    expect(openMock).not.toHaveBeenCalled();
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Other Bank' });
    expect(requests()).toEqual(['POST /connect-attempts/{id}/cancel']);
    expect(fetchMock.mock.calls[0][0]).toBe(`${BASE}/connect-attempts/${ATTEMPT_ID}/cancel`);
  });

  it('closes the attempt at the backend when no browser opened the link', async () => {
    openMock.mockRejectedValue(new Error('nothing opened'));
    backend();
    await startConnect('Example Bank', 'DE');
    expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'browser' });
    expect(requests()).toEqual(['POST /connect-links', 'POST /connect-attempts/{id}/cancel']);
  });
});

describe('reading the state', () => {
  it('stays waiting while the bank has not answered, and does not close the tab for a plain foreground', async () => {
    await startWaiting({ states: [ok({ state: 'waiting' })] });
    await checkAttempt();
    expect(getConnectFlow().step).toBe('waiting');
    expect(requests()).toEqual(['POST /connect-links', 'GET /connect-attempts/{id}']);
    expect(dismissMock).not.toHaveBeenCalled();
  });

  it('closes the tab when the callback page sent the user back, even if the bank has not answered', async () => {
    await startWaiting();
    await checkAttempt(true);
    expect(dismissMock).toHaveBeenCalled();
    expect(getConnectFlow().step).toBe('waiting');
  });

  it('ends saved: tab closed, the stored attempt gone', async () => {
    await startWaiting({ states: [ok({ state: 'saved' })] });
    expect(await AsyncStorage.getItem('connect.attempt.v2')).not.toBeNull();
    await checkAttempt();
    expect(getConnectFlow()).toEqual({ step: 'saved', bank: 'Example Bank', country: 'DE' });
    expect(dismissMock).toHaveBeenCalled();
    await flush();
    expect(await AsyncStorage.getItem('connect.attempt.v2')).toBeNull();
  });

  it('ends failed with the backend reason', async () => {
    await startWaiting({ states: [ok({ state: 'failed', reason: 'no-supported-account' })] });
    await checkAttempt();
    expect(getConnectFlow()).toMatchObject({ step: 'failed', reason: 'no-supported-account' });
  });

  it('ends expired for the state "expired" and for a 404', async () => {
    await startWaiting({ states: [ok({ state: 'expired' })] });
    await checkAttempt();
    expect(getConnectFlow().step).toBe('expired');

    await startWaiting({ states: [refused(404)] });
    await checkAttempt();
    expect(getConnectFlow().step).toBe('expired');
  });

  it('ends expired locally after 30 minutes, without asking', async () => {
    await startWaiting();
    fetchMock.mockClear();
    const real = Date.now();
    jest.spyOn(Date, 'now').mockReturnValue(real + ATTEMPT_MAX_AGE_MS + 1000);
    await checkAttempt();
    expect(getConnectFlow().step).toBe('expired');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('shows the account choice with the rows, and keeps it on later reads without asking for the rows again', async () => {
    await startWaiting({ states: [ok({ state: 'choose_account' })] });
    await checkAttempt();
    const flow = getConnectFlow();
    expect(flow).toMatchObject({ step: 'choose_account', posting: false, choiceFailed: false });
    expect(flow.step === 'choose_account' && flow.accounts.map(a => a.name)).toEqual(['Main account', 'Savings']);
    expect(dismissMock).toHaveBeenCalled();
    fetchMock.mockClear();
    await checkAttempt();
    expect(requests()).toEqual(['GET /connect-attempts/{id}']);
  });

  it('counts a failed account list as a read without an answer', async () => {
    await startWaiting({ states: [ok({ state: 'choose_account' })], accounts: refused(500) });
    await checkAttempt();
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', noAnswer: false });
  });

  it('says "no answer" after repeated reads that failed, and recovers with the next good one', async () => {
    await startWaiting({ states: [null, refused(500), ok('junk'), ok({ state: 'waiting' })] });
    for (let i = 1; i < NO_ANSWER_AFTER_READS; i += 1) {
      await checkAttempt();
      expect(getConnectFlow()).toMatchObject({ step: 'waiting', noAnswer: false });
    }
    await checkAttempt();
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', noAnswer: true });
    await checkAttempt();
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', noAnswer: false });
  });

  it('does not end an attempt the user cancelled while the tab was closing', async () => {
    await startWaiting({ states: [ok({ state: 'saved' })] });
    let release!: () => void;
    dismissMock.mockReturnValueOnce(new Promise<void>(resolve => (release = resolve)));
    const reading = checkAttempt();
    await flush();
    await cancelAttempt();
    release();
    await reading;
    expect(getConnectFlow().step).toBe('idle');
  });

  it('does not show the account choice for an attempt the user cancelled while the tab was closing', async () => {
    await startWaiting({ states: [ok({ state: 'choose_account' })] });
    let release!: () => void;
    dismissMock.mockReturnValueOnce(new Promise<void>(resolve => (release = resolve)));
    const reading = checkAttempt();
    await flush();
    await flush();
    await cancelAttempt();
    release();
    await reading;
    expect(getConnectFlow().step).toBe('idle');
    backend();
    await startConnect('Other Bank', 'AT');
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Other Bank' });
  });

  it('forgetting an ended attempt leaves a start that is already on its way alone', async () => {
    await startWaiting({ states: [ok({ state: 'failed', reason: 'cancelled' })] });
    await checkAttempt();
    backend();
    const starting = startConnect('Other Bank', 'AT'); // picked while the old screen is still leaving
    forgetEndedAttempt(); // the old screen's cleanup, late
    await starting;
    expect(getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Other Bank' });
    forgetEndedAttempt(); // a running attempt is not forgotten
    expect(getConnectFlow().step).toBe('waiting');
  });

  it('joins concurrent reads into one request', async () => {
    await startWaiting();
    fetchMock.mockClear();
    await Promise.all([checkAttempt(), checkAttempt(true), checkAttempt()]);
    expect(requests()).toEqual(['GET /connect-attempts/{id}']);
  });

  it('does nothing without a running attempt', async () => {
    await checkAttempt();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('drops the answer of a read that finished after the user cancelled', async () => {
    await startWaiting();
    let release!: (value: unknown) => void;
    fetchMock.mockReturnValueOnce(new Promise(resolve => (release = resolve)));
    const reading = checkAttempt();
    await cancelAttempt();
    release(toResponse(ok({ state: 'saved' })));
    await reading;
    expect(getConnectFlow().step).toBe('idle');
  });
});

describe('choosing an account', () => {
  async function startChoosing(script: Script = {}) {
    await startWaiting({ states: [ok({ state: 'choose_account' })], ...script });
    await checkAttempt();
    expect(getConnectFlow().step).toBe('choose_account');
    fetchMock.mockClear();
  }

  it('posts the row id and ends saved', async () => {
    await startChoosing();
    await chooseAccount('2');
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ accountId: '2' });
    expect(requests()).toEqual(['POST /connect-attempts/{id}/choice']);
    expect(getConnectFlow()).toEqual({ step: 'saved', bank: 'Example Bank', country: 'DE' });
  });

  it('ignores a second tap while the first is on its way', async () => {
    await startChoosing();
    let release!: (value: unknown) => void;
    fetchMock.mockReturnValueOnce(new Promise(resolve => (release = resolve)));
    const first = chooseAccount('1');
    expect(getConnectFlow()).toMatchObject({ step: 'choose_account', posting: true });
    await chooseAccount('2');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    release(toResponse(ok({ state: 'saved' })));
    await first;
    expect(getConnectFlow().step).toBe('saved');
  });

  it('keeps the list and says so after a 400 or no network', async () => {
    for (const choice of [refused(400), null]) {
      await startChoosing({ choice });
      await chooseAccount('1');
      expect(getConnectFlow()).toMatchObject({ step: 'choose_account', posting: false, choiceFailed: true });
      resetBetween();
    }
  });

  it('after a 409 reads the state again and shows what it says', async () => {
    await startChoosing({ choice: refused(409), states: [ok({ state: 'choose_account' }), ok({ state: 'expired' })] });
    await chooseAccount('1');
    expect(requests()).toEqual(['POST /connect-attempts/{id}/choice', 'GET /connect-attempts/{id}']);
    expect(getConnectFlow().step).toBe('expired');
  });

  it('after a 409 that changes nothing shows the failed choice', async () => {
    await startChoosing({ choice: refused(409) });
    await chooseAccount('1');
    expect(getConnectFlow()).toMatchObject({ step: 'choose_account', posting: false, choiceFailed: true });
  });

  it('does nothing outside the account choice', async () => {
    await startWaiting();
    fetchMock.mockClear();
    await chooseAccount('1');
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

function resetBetween() {
  __resetConnectStoreForTests();
}

describe('cancelling', () => {
  it('leaves at once, tells the backend and forgets the attempt', async () => {
    await startWaiting();
    fetchMock.mockClear();
    const cancelling = cancelAttempt();
    expect(getConnectFlow().step).toBe('idle');
    await cancelling;
    expect(requests()).toEqual(['POST /connect-attempts/{id}/cancel']);
    expect(dismissMock).toHaveBeenCalled();
    await flush();
    expect(await AsyncStorage.getItem('connect.attempt.v2')).toBeNull();
  });

  it('leaves even when the backend cannot be reached, and never claims saved', async () => {
    await startWaiting({ cancel: null });
    await cancelAttempt();
    expect(getConnectFlow().step).toBe('idle');
  });

  it('is harmless with nothing running', async () => {
    await cancelAttempt();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe('coming back', () => {
  it('does nothing and says false without an attempt', async () => {
    await expect(returnToApp(false)).resolves.toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('reads the state and says true while the attempt runs', async () => {
    await startWaiting();
    await expect(returnToApp(false)).resolves.toBe(true);
    expect(requests()).toEqual(['POST /connect-links', 'GET /connect-attempts/{id}']);
  });

  it('says true for the call that ends the attempt, and false for the next foreground event', async () => {
    await startWaiting({ states: [ok({ state: 'saved' })] });
    await expect(returnToApp(false)).resolves.toBe(true);
    expect(getConnectFlow().step).toBe('saved');
    await expect(returnToApp(false)).resolves.toBe(false);
  });

  it('lets a call that joins a running one report nothing, but still closes the tab for the link', async () => {
    await startWaiting();
    fetchMock.mockClear();
    const first = returnToApp(false);
    await expect(returnToApp(true)).resolves.toBe(false);
    await expect(first).resolves.toBe(true);
    expect(requests()).toEqual(['GET /connect-attempts/{id}']);
    expect(dismissMock).toHaveBeenCalled();
  });

  it('does not touch an attempt that is only opening', async () => {
    fetchMock.mockReturnValue(new Promise(() => {}));
    startConnect('Example Bank', 'DE');
    fetchMock.mockClear();
    await expect(returnToApp(false)).resolves.toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

describe('what is logged', () => {
  it('carries the path without the id and the status, never a link, an id, a bank or an account', async () => {
    backend({
      link: ok({ url: SECRET_LINK, attemptId: ATTEMPT_ID }),
      states: [refused(500), null, ok({ state: 'choose_account' })],
      choice: refused(400),
      cancel: refused(500),
    });
    await startConnect('Example Bank', 'DE');
    await checkAttempt();
    await checkAttempt();
    await checkAttempt();
    await chooseAccount('1');
    await cancelAttempt();

    const output = JSON.stringify(logged.flatMap(spy => spy.mock.calls));
    expect(output).toContain('/connect-attempts/{id} answered HTTP 500');
    expect(output).toContain('/connect-attempts/{id} failed');
    expect(output).toContain('/connect-attempts/{id}/choice answered HTTP 400');
    for (const secret of [SECRET_LINK, 'one-time-secret', ATTEMPT_ID, 'Example Bank', 'Main account', 'Savings', '4521', 'test-api-key']) {
      expect(output).not.toContain(secret);
    }
  });
});
