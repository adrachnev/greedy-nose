/**
 * Story 5.6: the Settings cards for each state, the no-bank variant, the
 * polling that follows a syncing bank, and the first-start jump.
 */

import React from 'react';
import { NavigationContext } from '@react-navigation/native';
import { AppState, Text } from 'react-native';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { useNoBankStartup } from '../../data/hooks';
import { __resetMyBanksStoreForTests, refreshMyBanks, SYNC_POLL_MS } from '../../data/myBanksStore';
import { formatDeviceDate } from '../../utils/format';
import SettingsScreen, { describeBank } from '../SettingsScreen';

jest.mock('react-native-safe-area-context', () => ({
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));
jest.mock('../../components/DataSourceBadge', () => () => null);
jest.mock('../../data/config', () => ({
  USE_BACKEND: true,
  BACKEND_BASE_URL: 'http://backend.test',
  BACKEND_TIMEOUT_MS: 1000,
}));
jest.mock('../../data/backendAuth', () => ({
  hasApiKey: () => true,
  backendHeaders: () => ({ Authorization: 'Bearer test' }),
}));

const fetchMock = jest.fn();
const navigate = jest.fn();
const UNTIL = '2027-01-19T12:00:00';

const bank = (over: Record<string, unknown> = {}) => ({
  name: 'Example Bank',
  country: 'DE',
  account: 'Main account',
  validUntil: UNTIL,
  state: 'connected',
  ...over,
});

function answer(banks: unknown[] | null) {
  fetchMock.mockResolvedValueOnce(
    banks === null
      ? { ok: false, status: 500, json: () => Promise.resolve({}) }
      : { ok: true, status: 200, json: () => Promise.resolve({ banks }) },
  );
}

let mounted: ReactTestRenderer.ReactTestRenderer | undefined;
let removeAppState: jest.Mock;
let appStateListener: ((state: string) => void) | undefined;

beforeEach(() => {
  __resetMyBanksStoreForTests();
  navigate.mockReset();
  fetchMock.mockReset();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
  removeAppState = jest.fn();
  jest.spyOn(AppState, 'addEventListener').mockImplementation(((_: string, cb: (state: string) => void) => {
    appStateListener = cb;
    return { remove: removeAppState };
  }) as never);
});
afterEach(() => {
  act(() => mounted?.unmount());
  mounted = undefined;
  jest.useRealTimers();
  jest.restoreAllMocks();
});

const pressables = (tree: ReactTestRenderer.ReactTestRenderer) =>
  tree.root.findAll(
    n =>
      n.props.accessibilityRole === 'button' &&
      typeof n.props.onPress === 'function' &&
      n.parent?.props.accessibilityRole !== 'button',
  );

const joined = (t: ReactTestRenderer.ReactTestInstance) => [t.props.children].flat().join('');
const texts = (tree: ReactTestRenderer.ReactTestRenderer) => tree.root.findAllByType(Text).map(joined);

async function render() {
  let tree!: ReactTestRenderer.ReactTestRenderer;
  await act(async () => {
    tree = ReactTestRenderer.create(
      <SettingsScreen
        navigation={{ navigate } as never}
        route={{ key: 'k', name: 'SettingsHome' } as never}
      />,
    );
  });
  mounted = tree;
  return tree;
}

async function press(tree: ReactTestRenderer.ReactTestRenderer, label: string) {
  const button = pressables(tree).find(n => n.findAllByType(Text).some(t => joined(t) === label));
  if (!button) {
    throw new Error(`no button "${label}"`);
  }
  await act(async () => {
    button.props.onPress();
  });
}

describe('Try again on a failed first sync (Story 5.8)', () => {
  const failed = (over: Record<string, unknown> = {}) => bank({ state: 'sync_failed', reason: 'bank-error', ...over });
  const post = () => fetchMock.mock.calls.filter(([, init]) => init.method === 'POST');
  const reply = (status: number) =>
    fetchMock.mockResolvedValueOnce({ ok: status < 300, status, json: () => Promise.resolve({}) });

  it('is offered on a failed card only, not for the daily limit', async () => {
    answer([failed(), failed({ name: 'Limit Bank', reason: 'daily-limit' }), bank({ name: 'Fine Bank' })]);
    const tree = await render();
    const labels = pressables(tree).flatMap(n => n.findAllByType(Text).map(joined));
    expect(labels.filter(label => label === 'Try again')).toHaveLength(1);
  });

  it('posts the bank and shows the card as syncing at once', async () => {
    answer([failed()]);
    const tree = await render();
    reply(200);
    answer([bank({ state: 'syncing' })]);
    await press(tree, 'Try again');
    expect(post()).toHaveLength(1);
    expect(JSON.parse(post()[0][1].body)).toEqual({ aspsp: 'Example Bank' });
    expect(texts(tree)).toContain('Syncing');
    expect(texts(tree)).not.toContain('Try again');
  });

  it('on 409 shows what the state says, without an error text', async () => {
    answer([failed()]);
    const tree = await render();
    reply(409);
    answer([failed({ reason: 'daily-limit' })]);
    await press(tree, 'Try again');
    expect(texts(tree).join(' ')).not.toMatch(/just now/);
    expect(texts(tree)).not.toContain('Try again');
  });

  it('on 503 says so and keeps the button', async () => {
    answer([failed()]);
    const tree = await render();
    reply(503);
    await press(tree, 'Try again');
    expect(texts(tree)).toContain("Couldn't try again just now");
    expect(texts(tree)).toContain('Try again');
  });

  it('on no network says so and keeps the button', async () => {
    answer([failed()]);
    const tree = await render();
    fetchMock.mockRejectedValueOnce(new Error('network'));
    await press(tree, 'Try again');
    expect(texts(tree)).toContain("Couldn't try again just now");
  });

  it('a second tap that is ignored does not wipe the message that is showing', async () => {
    answer([failed()]);
    const tree = await render();
    reply(503);
    await press(tree, 'Try again');
    expect(texts(tree)).toContain("Couldn't try again just now");
    let release!: () => void;
    fetchMock.mockImplementationOnce(
      () =>
        new Promise(resolve => {
          release = () => resolve({ ok: false, status: 503, json: () => Promise.resolve({}) });
        }),
    );
    await act(async () => {
      const again = pressables(tree).find(n => n.findAllByType(Text).some(t => joined(t) === 'Try again'))!;
      again.props.onPress();
      again.props.onPress();
    });
    expect(texts(tree)).toContain("Couldn't try again just now");
    await act(async () => {
      release();
    });
  });

  it('leaving the screen while the try is on its way ends without an error', async () => {
    answer([failed()]);
    const tree = await render();
    let release!: () => void;
    fetchMock.mockImplementationOnce(
      () =>
        new Promise(resolve => {
          release = () => resolve({ ok: false, status: 503, json: () => Promise.resolve({}) });
        }),
    );
    await press(tree, 'Try again');
    act(() => tree.unmount());
    mounted = undefined;
    const errors = jest.spyOn(console, 'error').mockImplementation(() => {});
    await act(async () => {
      release();
    });
    expect(errors).not.toHaveBeenCalled();
  });

  it('ignores a second tap while the first is on its way', async () => {
    answer([failed()]);
    const tree = await render();
    reply(200);
    answer([bank({ state: 'syncing' })]);
    await act(async () => {
      const again = pressables(tree).find(n => n.findAllByType(Text).some(t => joined(t) === 'Try again'))!;
      again.props.onPress();
      again.props.onPress();
    });
    expect(post()).toHaveLength(1);
  });
});

describe('the cards', () => {
  it('shows one card per bank with name, account, date and one state label each', async () => {
    answer([
      bank(),
      bank({ name: 'Example Direct', state: 'syncing' }),
      bank({ name: 'Example Bank Austria', state: 'sync_failed' }),
      bank({ name: 'Example Savings', account: 'Current account', state: 'expired' }),
    ]);
    const all = texts(await render());
    const date = formatDeviceDate(UNTIL);
    expect(all).toContain('Example Bank');
    expect(all).toContain(`Main account · access valid until ${date}`);
    expect(all).toContain('Active');
    expect(all).toContain('Main account · fetching your debits');
    expect(all).toContain('Syncing');
    expect(all).toContain("Main account · couldn't fetch your debits");
    expect(all).toContain('Sync failed');
    expect(all).toContain('Current account · access expired');
    expect(all).toContain('Expired');
  });

  it('starts the line with a capital when the bank gave no account name', () => {
    const syncing = bank({ account: null, state: 'syncing' });
    expect(describeBank(syncing as never)).toBe('Fetching your debits');
  });

  it('has no Delete, Reconnect or Try again on a card that does not need one, only "Connect a bank"', async () => {
    answer([bank({ state: 'expired' }), bank({ name: 'Other', state: 'syncing' })]);
    const tree = await render();
    const labels = pressables(tree).flatMap(n => n.findAllByType(Text).map(joined));
    expect(labels).toEqual(['Connect a bank']);
  });

  it('opens the bank search from "Connect a bank"', async () => {
    answer([bank()]);
    const tree = await render();
    await press(tree, 'Connect a bank');
    expect(navigate).toHaveBeenCalledWith('SelectBank');
  });

  it('names no bank in its own words', async () => {
    answer([]);
    expect(texts(await render()).join('\n')).not.toMatch(/\b(N26|DKB|ING)\b/);
  });
});

describe('without a bank, and when the read fails', () => {
  it('shows the no-bank variant with one sentence and the main action', async () => {
    answer([]);
    const tree = await render();
    expect(texts(tree)).toContain('No bank connected');
    expect(texts(tree).join('\n')).toContain('Greedy Nose alerts you the moment a payee you marked Bad charges you.');
    await press(tree, 'Connect a bank');
    expect(navigate).toHaveBeenCalledWith('SelectBank');
  });

  it('never claims "no bank" while loading or after a failed read, and retries', async () => {
    answer(null);
    const tree = await render();
    expect(texts(tree)).not.toContain('No bank connected');
    expect(texts(tree)).toContain("Couldn't load your banks");
    answer([bank()]);
    await press(tree, 'Try again');
    expect(texts(tree)).toContain('Example Bank');
  });

  it('shows a waiting text, not "no bank", before the first answer', async () => {
    fetchMock.mockReturnValueOnce(new Promise(() => {}));
    const tree = await render();
    expect(texts(tree)).toContain('Loading your banks...');
    expect(texts(tree)).not.toContain('No bank connected');
  });
});

describe('a syncing bank', () => {
  it('asks again every few seconds, changes by itself, and stops asking when the screen is left', async () => {
    jest.useFakeTimers();
    answer([bank({ state: 'syncing' })]);
    const tree = await render();
    expect(texts(tree)).toContain('Syncing');

    answer([bank({ state: 'connected' })]);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(SYNC_POLL_MS);
    });
    expect(texts(tree)).toContain('Active');
    expect(fetchMock).toHaveBeenCalledTimes(2);

    await act(async () => {
      await jest.advanceTimersByTimeAsync(SYNC_POLL_MS * 3);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);

    act(() => tree.unmount());
    mounted = undefined;
    expect(jest.getTimerCount()).toBe(0);
    expect(removeAppState).toHaveBeenCalled();
  });
});

describe('focus and foreground', () => {
  function fakeNavigation(focused: boolean) {
    const listeners: Record<string, () => void> = {};
    return {
      listeners,
      navigation: {
        isFocused: () => focused,
        addListener: (event: string, cb: () => void) => {
          listeners[event] = cb;
          return jest.fn();
        },
        navigate,
      },
    };
  }

  it('stops asking on blur while the screen stays mounted, and resumes on focus', async () => {
    jest.useFakeTimers();
    const fake = fakeNavigation(true);
    answer([bank({ state: 'syncing' })]);
    await act(async () => {
      mounted = ReactTestRenderer.create(
        <NavigationContext.Provider value={fake.navigation as never}>
          <SettingsScreen navigation={{ navigate } as never} route={{} as never} />
        </NavigationContext.Provider>,
      );
    });
    await act(async () => {
      await jest.advanceTimersByTimeAsync(0);
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    // The timer count is global (the reads below are the real check); with one screen it is exactly the interval.
    expect(jest.getTimerCount()).toBe(1);

    act(() => fake.listeners.blur());
    expect(jest.getTimerCount()).toBe(0);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(SYNC_POLL_MS * 3);
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    answer([bank({ state: 'syncing' })]);
    await act(async () => {
      fake.listeners.focus();
      await jest.advanceTimersByTimeAsync(0);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    // Polling really resumed: the interval reads again (checked by reads, the timer count is only a hint).
    answer([bank({ state: 'syncing' })]);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(SYNC_POLL_MS);
    });
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('does not poll at all while the screen is not focused', async () => {
    jest.useFakeTimers();
    const fake = fakeNavigation(false);
    await act(async () => {
      mounted = ReactTestRenderer.create(
        <NavigationContext.Provider value={fake.navigation as never}>
          <SettingsScreen navigation={{ navigate } as never} route={{} as never} />
        </NavigationContext.Provider>,
      );
    });
    await act(async () => {
      await jest.advanceTimersByTimeAsync(SYNC_POLL_MS * 3);
    });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('reads again when the app comes to the foreground', async () => {
    answer([bank()]);
    await render();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    answer([bank({ state: 'expired' })]);
    await act(async () => {
      appStateListener?.('background');
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => {
      appStateListener?.('active');
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

describe('first start', () => {
  function Root({ onNoBank }: { onNoBank: () => boolean }) {
    useNoBankStartup(onNoBank);
    return null;
  }
  async function renderRoot(onNoBank: () => boolean) {
    await act(async () => {
      mounted = ReactTestRenderer.create(<Root onNoBank={onNoBank} />);
    });
  }

  it('goes to Settings once when the first good answer has no bank', async () => {
    answer([]);
    const onNoBank = jest.fn(() => true);
    await renderRoot(onNoBank);
    expect(onNoBank).toHaveBeenCalledTimes(1);
  });

  it('keeps trying until the navigator can take the jump, then jumps once', async () => {
    jest.useFakeTimers();
    answer([]);
    let ready = false;
    const onNoBank = jest.fn(() => ready);
    await renderRoot(onNoBank);
    expect(onNoBank).toHaveBeenCalledTimes(1);

    ready = true;
    await act(async () => {
      await jest.advanceTimersByTimeAsync(600);
    });
    expect(onNoBank).toHaveBeenCalledTimes(2);

    await act(async () => {
      await jest.advanceTimersByTimeAsync(5000);
    });
    expect(onNoBank).toHaveBeenCalledTimes(2);
    expect(jest.getTimerCount()).toBe(0);
  });

  it('gives up after 20 tries', async () => {
    jest.useFakeTimers();
    answer([]);
    const onNoBank = jest.fn(() => false);
    await renderRoot(onNoBank);
    await act(async () => {
      await jest.advanceTimersByTimeAsync(500 * 40);
    });
    expect(onNoBank).toHaveBeenCalledTimes(21);
    expect(jest.getTimerCount()).toBe(0);
  });

  it('stops retrying when the root unmounts', async () => {
    jest.useFakeTimers();
    answer([]);
    const onNoBank = jest.fn(() => false);
    await renderRoot(onNoBank);
    expect(onNoBank).toHaveBeenCalledTimes(1);
    act(() => mounted?.unmount());
    mounted = undefined;
    await act(async () => {
      await jest.advanceTimersByTimeAsync(5000);
    });
    expect(onNoBank).toHaveBeenCalledTimes(1);
  });

  it('keeps one retry chain when the store publishes while a retry is pending', async () => {
    jest.useFakeTimers();
    answer([]);
    const onNoBank = jest.fn(() => false);
    await renderRoot(onNoBank);
    expect(onNoBank).toHaveBeenCalledTimes(1);

    answer([bank()]);
    await act(async () => {
      await refreshMyBanks();
    });
    expect(onNoBank).toHaveBeenCalledTimes(2); // the publish itself checked once

    await act(async () => {
      await jest.advanceTimersByTimeAsync(500);
    });
    expect(onNoBank).toHaveBeenCalledTimes(3); // two chains would have made it 4
  });

  it('does not move the user when a bank exists, or when the read failed', async () => {
    answer([bank()]);
    const withBank = jest.fn(() => true);
    await renderRoot(withBank);
    expect(withBank).not.toHaveBeenCalled();
    act(() => mounted?.unmount());

    __resetMyBanksStoreForTests();
    answer(null);
    const failed = jest.fn(() => true);
    await renderRoot(failed);
    expect(failed).not.toHaveBeenCalled();
  });
});

it('formats the date by the device locale and refuses non-dates', () => {
  expect(formatDeviceDate('2027-01-19T12:00:00', 'en-GB')).toBe('19 Jan 2027');
  expect(formatDeviceDate('nonsense')).toBeNull();
  expect(formatDeviceDate(null)).toBeNull();
});
