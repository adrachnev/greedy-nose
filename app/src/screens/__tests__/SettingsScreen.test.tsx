/**
 * The temporary Settings entry for connecting a bank (Story 5.1a): what the
 * user reads at each step, and that the two ways back from the bank (the app
 * link, the app coming to the foreground) reach the store.
 */

import React from 'react';
import { AppState, Linking, Text, TextInput } from 'react-native';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { __resetConnectStoreForTests } from '../../data/connectStore';
import { useConnectReturn } from '../../data/hooks';
import SettingsScreen, { FAILURE_TEXT, formatValidUntil } from '../SettingsScreen';

jest.mock('react-native-safe-area-context', () => ({
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));
jest.mock('../../components/DataSourceBadge', () => () => null);
jest.mock('../../data/config', () => ({
  USE_BACKEND: true,
  BACKEND_BASE_URL: 'http://backend.test',
  BACKEND_TIMEOUT_MS: 1000,
}));
jest.mock('../../data/openConnectLink', () => ({
  openConnectLink: jest.fn(() => Promise.resolve(false)),
  dismissConnectLink: jest.fn(() => Promise.resolve()),
}));

const fetchMock = jest.fn();
let statusBodies: unknown[];
let linkOk: boolean;
let urlListener: ((event: { url: string }) => void) | undefined;
let stateListener: ((state: string) => void) | undefined;

const bank = (validUntil: string) => ({ aspsp: 'Example Bank', state: 'connected', validUntil });

beforeEach(() => {
  onResumed.mockClear();
  __resetConnectStoreForTests();
  linkOk = true;
  statusBodies = [{ banks: [] }, { banks: [bank('2027-01-19T12:00:00')] }];
  let i = 0;
  fetchMock.mockReset();
  fetchMock.mockImplementation((url: string) => {
    if (url.endsWith('/connect-links')) {
      return Promise.resolve({
        ok: linkOk,
        status: linkOk ? 200 : 500,
        json: () => Promise.resolve({ url: 'https://bank.test/x' }),
      });
    }
    const body = statusBodies[Math.min(i++, statusBodies.length - 1)];
    return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve(body) });
  });
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
  jest.spyOn(Linking, 'addEventListener').mockImplementation(((_: string, cb: never) => {
    urlListener = cb;
    return { remove: jest.fn() };
  }) as never);
  jest.spyOn(Linking, 'getInitialURL').mockImplementation(() => Promise.resolve(null));
  jest.spyOn(AppState, 'addEventListener').mockImplementation(((_: string, cb: never) => {
    stateListener = cb;
    return { remove: jest.fn() };
  }) as never);
});
let mounted: ReactTestRenderer.ReactTestRenderer | undefined;
afterEach(() => {
  act(() => mounted?.unmount());
  mounted = undefined;
  jest.restoreAllMocks();
});

// The listeners live at the app root (App.tsx), the screen only shows the state.
const onResumed = jest.fn();
function Harness() {
  useConnectReturn(onResumed);
  return <SettingsScreen />;
}

async function render() {
  let tree!: ReactTestRenderer.ReactTestRenderer;
  await act(async () => {
    tree = ReactTestRenderer.create(<Harness />);
  });
  mounted = tree;
  return tree;
}

const joined = (t: ReactTestRenderer.ReactTestInstance) => [t.props.children].flat().join('');

const texts = (tree: ReactTestRenderer.ReactTestRenderer) =>
  tree.root.findAllByType(Text).map(joined);

async function press(tree: ReactTestRenderer.ReactTestRenderer, label: string) {
  const button = tree.root
    .findAll(n => n.props.accessibilityRole === 'button' && typeof n.props.onPress === 'function')
    .find(n => n.findAllByType(Text).some(t => joined(t) === label));
  if (!button) {
    throw new Error(`no button "${label}"`);
  }
  await act(async () => {
    button.props.onPress();
  });
}

async function connectAs(tree: ReactTestRenderer.ReactTestRenderer, name = 'Example Bank') {
  await act(async () => {
    tree.root.findByType(TextInput).props.onChangeText(name);
  });
  await press(tree, 'Connect a bank');
}

it('explains the step before the bank, without naming any bank', async () => {
  const tree = await render();
  const all = texts(tree).join('\n');
  expect(all).toContain('You sign in at your bank and approve in its app. Then switch back to your browser tab, which brings you back here.');
  expect(all).not.toMatch(/\b(N26|DKB|ING)\b/);
});

it('does not enable the button before a name is typed', async () => {
  const tree = await render();
  const button = tree.root.findAll(n => n.props.accessibilityRole === 'button')[0];
  expect(button.props.disabled).toBe(true);
});

it('shows a waiting text while the bank asks for approval, including how to get back', async () => {
  const tree = await render();
  await connectAs(tree);
  expect(texts(tree)).toContain('Waiting for your bank');
  expect(texts(tree).join(' ')).not.toContain('come back here');
  expect(texts(tree)).toContain(
    "After you approved in your bank's app, switch back to your browser tab. It brings you back here.",
  );
});

it('shows "connected, valid until" after the app link arrives', async () => {
  const tree = await render();
  await connectAs(tree);
  await act(async () => {
    urlListener?.({ url: 'greedynose://connected' });
  });
  expect(texts(tree)).toContain('Example Bank is connected, valid until 19 Jan 2027');
});

it('reads the result when the app comes to the foreground by hand', async () => {
  const tree = await render();
  await connectAs(tree);
  await act(async () => {
    stateListener?.('active');
  });
  expect(texts(tree).join('\n')).toContain('is connected, valid until');
});

it('tells the root to show Settings after a resumed check, but not at app start without an attempt', async () => {
  const tree = await render();
  expect(onResumed).not.toHaveBeenCalled();
  await connectAs(tree);
  await act(async () => {
    urlListener?.({ url: 'greedynose://connected' });
  });
  expect(onResumed).toHaveBeenCalledTimes(1);
  await act(async () => {
    stateListener?.('active');
  });
  expect(onResumed).toHaveBeenCalledTimes(1);
});

it('ignores other links', async () => {
  const tree = await render();
  await connectAs(tree);
  await act(async () => {
    urlListener?.({ url: 'greedynose://something-else' });
  });
  expect(texts(tree)).toContain('Waiting for your bank');
});

it('offers "Check again" when the bank has not confirmed, so there is no dead end', async () => {
  statusBodies = [{ banks: [] }, { banks: [] }, { banks: [bank('2027-01-19T12:00:00')] }];
  const tree = await render();
  await connectAs(tree);
  await act(async () => {
    stateListener?.('active');
  });
  expect(texts(tree).join('\n')).toContain('has not confirmed yet');
  await press(tree, 'Check again');
  expect(texts(tree).join('\n')).toContain('is connected');

  await press(tree, 'Done');
  expect(texts(tree)).toContain('Connect a bank');
});

it('shows a failure with "Try again" that leads back to the form', async () => {
  linkOk = false;
  const tree = await render();
  await connectAs(tree);
  expect(texts(tree)).toContain(FAILURE_TEXT.link);
  await press(tree, 'Try again');
  expect(texts(tree)).toContain('Connect a bank');
});

it('formats the date as a plain day and refuses non-dates', () => {
  expect(formatValidUntil('2027-01-19T12:00:00')).toBe('19 Jan 2027');
  expect(formatValidUntil('nonsense')).toBeNull();
  expect(formatValidUntil(null)).toBeNull();
});
