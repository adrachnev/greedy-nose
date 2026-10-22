/**
 * Story 5.7: the screen of the attempt to connect a bank. It follows the real
 * store over a scripted backend: waiting, the polling and where it stops, the
 * account choice, Cancel and going back, and the end states.
 */

import React from 'react';
import { AppState, Text } from 'react-native';
import ReactTestRenderer, { act } from 'react-test-renderer';
import {
  __resetConnectStoreForTests,
  CONNECT_POLL_MS,
  getConnectFlow,
  NO_ANSWER_AFTER_READS,
  startConnect,
} from '../../data/connectStore';
import ConnectAttemptScreen from '../ConnectAttemptScreen';

jest.mock('react-native-safe-area-context', () => ({
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));
jest.mock('../../data/config', () => ({
  USE_BACKEND: true,
  BACKEND_BASE_URL: 'http://backend.test',
  BACKEND_TIMEOUT_MS: 1000,
}));
jest.mock('../../data/backendAuth', () => ({
  hasApiKey: () => true,
  backendHeaders: () => ({ Authorization: 'Bearer test' }),
}));
jest.mock('../../data/openConnectLink', () => ({
  openConnectLink: jest.fn(() => Promise.resolve(false)),
  dismissConnectLink: jest.fn(() => Promise.resolve()),
}));

const BASE = 'http://backend.test';
const fetchMock = jest.fn();
const popTo = jest.fn();
const resetStack = jest.fn();
const preventDefault = jest.fn();
let removeListener: ((event: unknown) => void) | undefined;
let mounted: ReactTestRenderer.ReactTestRenderer | undefined;

type Reply = { status: number; body?: unknown };
const ok = (body: unknown): Reply => ({ status: 200, body });

const accounts = [
  { id: '1', name: 'Main account', kind: 'current', last4: '4521', suggested: true },
  { id: '2', name: 'Savings', kind: 'savings', last4: 'DE89370400440532010937', suggested: false },
  { id: '3', name: 'Joint account', kind: 'current', last4: '7714', suggested: false },
];

type Script = { states?: (Reply | null)[]; choice?: Reply | null; link?: Reply };

function backend(script: Script = {}) {
  const states = script.states ?? [ok({ state: 'waiting' })];
  let read = 0;
  fetchMock.mockImplementation((url: string, init: { method: string }) => {
    const path = url.replace(BASE, '');
    const respond = (reply: Reply | null) =>
      reply === null
        ? Promise.reject(new Error('network'))
        : Promise.resolve({
            ok: reply.status < 300,
            status: reply.status,
            json: () => Promise.resolve(reply.body ?? {}),
          });
    if (path === '/connect-links') {
      return respond(script.link ?? ok({ url: 'https://example.test/connect?token=t', attemptId: 'a1' }));
    }
    if (path.endsWith('/accounts')) {
      return respond(ok({ accounts }));
    }
    if (path.endsWith('/choice')) {
      return respond(script.choice === undefined ? ok({ state: 'saved' }) : script.choice);
    }
    if (path.endsWith('/cancel')) {
      return respond(ok({ state: 'failed', reason: 'cancelled' }));
    }
    if (init.method === 'GET') {
      return respond(states[Math.min(read++, states.length - 1)]);
    }
    return Promise.reject(new Error(`unexpected ${path}`));
  });
}

const requests = () =>
  fetchMock.mock.calls.map(([url, init]) => `${init.method} ${url.replace(BASE, '').replace('/a1', '/{id}')}`);

beforeEach(() => {
  jest.useFakeTimers();
  fetchMock.mockReset();
  popTo.mockReset();
  resetStack.mockReset();
  preventDefault.mockReset();
  removeListener = undefined;
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
  __resetConnectStoreForTests();
});
afterEach(() => {
  act(() => mounted?.unmount());
  mounted = undefined;
  jest.useRealTimers();
  jest.restoreAllMocks();
});

const joined = (t: ReactTestRenderer.ReactTestInstance) => [t.props.children].flat().join('');
const texts = (tree: ReactTestRenderer.ReactTestRenderer) => tree.root.findAllByType(Text).map(joined);

const buttons = (tree: ReactTestRenderer.ReactTestRenderer) =>
  tree.root.findAll(
    n =>
      n.props.accessibilityRole === 'button' &&
      typeof n.props.onPress === 'function' &&
      n.parent?.props.accessibilityRole !== 'button',
  );
const button = (tree: ReactTestRenderer.ReactTestRenderer, label: string) => {
  const found = buttons(tree).find(b => b.findAllByType(Text).some(t => joined(t) === label));
  if (!found) {
    throw new Error(`no button "${label}" among ${texts(tree).join(' | ')}`);
  }
  return found;
};

async function press(tree: ReactTestRenderer.ReactTestRenderer, label: string) {
  await act(async () => {
    button(tree, label).props.onPress();
  });
}

async function render() {
  let tree!: ReactTestRenderer.ReactTestRenderer;
  await act(async () => {
    tree = ReactTestRenderer.create(
      <ConnectAttemptScreen
        navigation={
          {
            popTo,
            reset: resetStack,
            replace: jest.fn(),
            getState: () => ({
              routes: [{ name: 'SettingsHome' }, { name: 'SelectBank' }, { name: 'ConnectAttempt' }],
            }),
            addListener: (_name: string, listener: (event: unknown) => void) => {
              removeListener = listener;
              return () => {};
            },
          } as never
        }
        route={{ key: 'k', name: 'ConnectAttempt' } as never}
      />,
    );
  });
  mounted = tree;
  return tree;
}

/** Starts an attempt in the store (as the bank list does) and shows the screen. */
async function open(script: Script = {}) {
  backend(script);
  await act(async () => {
    await startConnect('Example Bank', 'DE');
  });
  return render();
}

async function tick(ms: number) {
  await act(async () => {
    await jest.advanceTimersByTimeAsync(ms);
  });
}

const backPress = async (type = 'GO_BACK') => {
  await act(async () => {
    removeListener?.({ preventDefault, data: { action: { type } } });
  });
};

describe('waiting', () => {
  it('tells the user to approve at the bank and come back, with Check again and Cancel', async () => {
    const tree = await open();
    const all = texts(tree);
    expect(all).toContain('Waiting for your bank');
    expect(all.join(' ')).toMatch(/Approve the connection at your bank/);
    expect(buttons(tree).length).toBeGreaterThanOrEqual(3); // nav Cancel, Check again, Cancel
  });

  it('Check again reads the state once', async () => {
    const tree = await open();
    fetchMock.mockClear();
    await press(tree, 'Check again');
    expect(requests()).toEqual(['GET /connect-attempts/{id}']);
  });

  it('reads the state every few seconds while visible, and stops at the end state', async () => {
    const tree = await open({ states: [ok({ state: 'waiting' }), ok({ state: 'waiting' }), ok({ state: 'saved' })] });
    fetchMock.mockClear();
    await tick(CONNECT_POLL_MS);
    await tick(CONNECT_POLL_MS);
    expect(requests()).toEqual(['GET /connect-attempts/{id}', 'GET /connect-attempts/{id}']);
    await tick(CONNECT_POLL_MS * 5);
    // The third read said "saved": the screen hands over to the first sync and nothing is read any more.
    expect(requests()).toHaveLength(3);
    expect(resetStack).toHaveBeenCalledTimes(1);
    expect(tree).toBeDefined();
  });

  it('reads only while the app is in the foreground', async () => {
    let onChange!: (state: string) => void;
    const remove = jest.fn();
    jest.spyOn(AppState, 'addEventListener').mockImplementation(((_: string, listener: (state: string) => void) => {
      onChange = listener;
      return { remove };
    }) as never);
    await open();
    fetchMock.mockClear();
    act(() => onChange('background'));
    await tick(CONNECT_POLL_MS * 3);
    expect(fetchMock).not.toHaveBeenCalled();
    act(() => onChange('active'));
    await tick(CONNECT_POLL_MS);
    expect(requests()).toEqual(['GET /connect-attempts/{id}']);
    act(() => mounted!.unmount());
    mounted = undefined;
    expect(remove).toHaveBeenCalled();
  });

  it('stops reading when the screen is left', async () => {
    await open();
    fetchMock.mockClear();
    act(() => mounted!.unmount());
    mounted = undefined;
    await tick(CONNECT_POLL_MS * 3);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('after repeated reads without an answer says so, stops reading, and offers Check again and Cancel', async () => {
    const tree = await open({ states: [null] });
    for (let i = 0; i < NO_ANSWER_AFTER_READS; i += 1) {
      await tick(CONNECT_POLL_MS);
    }
    expect(texts(tree).join(' ')).toMatch(/No answer from the backend/);
    fetchMock.mockClear();
    await tick(CONNECT_POLL_MS * 3);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(() => button(tree, 'Check again')).not.toThrow();
    expect(() => button(tree, 'Cancel')).not.toThrow();
  });

  it('Cancel tells the backend, nothing is saved, and the user is back on Settings', async () => {
    const tree = await open();
    fetchMock.mockClear();
    await act(async () => {
      buttons(tree)
        .filter(b => b.findAllByType(Text).some(t => joined(t) === 'Cancel'))
        .at(-1)!
        .props.onPress();
    });
    expect(requests()).toEqual(['POST /connect-attempts/{id}/cancel']);
    expect(getConnectFlow().step).toBe('idle');
    expect(popTo).toHaveBeenCalledWith('SettingsHome');
  });

  it('going back cancels the same way and holds the back action', async () => {
    await open();
    fetchMock.mockClear();
    await backPress('GO_BACK');
    expect(preventDefault).toHaveBeenCalled();
    expect(requests()).toEqual(['POST /connect-attempts/{id}/cancel']);
    expect(popTo).toHaveBeenCalledWith('SettingsHome');
  });

  it('a tab switch pops the screen without cancelling anything', async () => {
    await open();
    fetchMock.mockClear();
    await backPress('POP_TO_TOP');
    expect(preventDefault).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(getConnectFlow().step).toBe('waiting');
  });
});

describe('the account choice', () => {
  async function choosing(script: Script = {}) {
    const tree = await open({ states: [ok({ state: 'choose_account' })], ...script });
    await press(tree, 'Check again');
    expect(getConnectFlow().step).toBe('choose_account');
    return tree;
  }

  it('lists name, kind, last four digits and "Suggested", and never more of the IBAN', async () => {
    const tree = await choosing();
    const all = texts(tree);
    expect(all).toContain('Choose account');
    expect(all).toContain('Main account');
    expect(all).toContain('Current account · …4521');
    expect(all).toContain('Savings account · …0937');
    expect(all.filter(t => t === 'Suggested')).toHaveLength(1);
    expect(all.join(' ')).not.toMatch(/DE89|370400/);
  });

  it('a tap saves the choice and hands over to the first sync, with Settings under it', async () => {
    const tree = await choosing();
    fetchMock.mockClear();
    await press(tree, 'Joint account');
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ accountId: '3' });
    expect(resetStack).toHaveBeenCalledWith({
      index: 1,
      routes: [{ name: 'SettingsHome' }, { name: 'Syncing', params: { bank: 'Example Bank', country: 'DE' } }],
    });
    expect(popTo).not.toHaveBeenCalled();
  });

  it('keeps the list and says so when the choice could not be saved', async () => {
    const tree = await choosing({ choice: { status: 400 } });
    await press(tree, 'Savings');
    expect(texts(tree).join(' ')).toMatch(/Couldn't save your choice/);
    expect(texts(tree)).toContain('Savings');
    expect(getConnectFlow().step).toBe('choose_account');
    expect(popTo).not.toHaveBeenCalled();
  });

  it('Cancel on the choice cancels at the backend and returns to Settings', async () => {
    const tree = await choosing();
    fetchMock.mockClear();
    await press(tree, 'Cancel');
    expect(requests()).toEqual(['POST /connect-attempts/{id}/cancel']);
    expect(popTo).toHaveBeenCalledWith('SettingsHome');
  });

  it('going back on the choice does the same', async () => {
    await choosing();
    fetchMock.mockClear();
    await backPress('POP');
    expect(preventDefault).toHaveBeenCalled();
    expect(requests()).toEqual(['POST /connect-attempts/{id}/cancel']);
  });
});

describe('the end states', () => {
  it('"failed" shows the connection error with Try Again, which starts a new attempt for the same bank', async () => {
    const tree = await open({ states: [ok({ state: 'failed', reason: 'cancelled' })] });
    await press(tree, 'Check again');
    expect(texts(tree)).toContain("Couldn't connect to your bank");
    fetchMock.mockClear();
    await press(tree, 'Try Again');
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({ aspsp: 'Example Bank', country: 'DE' });
    expect(getConnectFlow().step).toBe('waiting');
  });

  it('"Choose a different bank" goes to the bank search', async () => {
    const tree = await open({ states: [ok({ state: 'failed', reason: 'bank-error' })] });
    await press(tree, 'Check again');
    await press(tree, 'Choose a different bank');
    expect(popTo).toHaveBeenCalledWith('SelectBank');
  });

  it('"no supported account" shows its own text and only a way back', async () => {
    const tree = await open({ states: [ok({ state: 'failed', reason: 'no-supported-account' })] });
    await press(tree, 'Check again');
    expect(texts(tree)).toContain('No supported account');
    expect(texts(tree)).toContain('Example Bank returned no account in euro that we can watch.');
    expect(() => button(tree, 'Try Again')).toThrow();
    await press(tree, 'Back to Settings');
    expect(popTo).toHaveBeenCalledWith('SettingsHome');
  });

  it('a bank that is already connected says so and offers no retry', async () => {
    backend({ link: { status: 409 } });
    await act(async () => {
      await startConnect('Example Bank', 'DE');
    });
    const tree = await render();
    expect(texts(tree)).toContain('Already connected');
    expect(() => button(tree, 'Try Again')).toThrow();
    expect(() => button(tree, 'Back to Settings')).not.toThrow();
  });

  it('"expired" says so plainly and starts again on request', async () => {
    const tree = await open({ states: [{ status: 404 }] });
    await press(tree, 'Check again');
    expect(texts(tree)).toContain('This sign-in has ended');
    fetchMock.mockClear();
    await press(tree, 'Start again');
    expect(requests()[0]).toBe('POST /connect-links');
  });

  it('forgets an ended attempt when the screen goes away', async () => {
    const tree = await open({ states: [ok({ state: 'expired' })] });
    await press(tree, 'Check again');
    expect(getConnectFlow().step).toBe('expired');
    act(() => tree.unmount());
    mounted = undefined;
    expect(getConnectFlow().step).toBe('idle');
  });
});

it('shows "Opening your bank" without a spinner while the link is asked for', async () => {
  fetchMock.mockReturnValue(new Promise(() => {}));
  act(() => {
    startConnect('Example Bank', 'DE');
  });
  const tree = await render();
  expect(texts(tree)).toContain('Opening your bank');
  expect(tree.root.findAll(n => n.type === ('ActivityIndicator' as never))).toHaveLength(0);
});

it('never names a bank in its own words', async () => {
  const tree = await open();
  expect(texts(tree).join(' ')).not.toMatch(/(?<![A-Za-z])(N26|DKB|ING|Enable Banking)(?![A-Za-z])/);
});
