/**
 * Story 5.8: the first-sync screen. It follows the real store over a scripted
 * backend (fetch is a mock, timers are fake): running, done, failed, the daily
 * limit, expired, the two-minute stop, leaving, and "Try again".
 */

import React from 'react';
import { ActivityIndicator, Text } from 'react-native';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { __resetMyBanksStoreForTests, SYNC_POLL_MS } from '../../data/myBanksStore';
import { __resetOnboardingOfferForTests, getOffer, rememberPayeesBeforeAttempt } from '../../data/onboardingOffer';
import SyncingScreen, { SYNC_WATCH_MS } from '../SyncingScreen';

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
  backendHeaders: (extra: Record<string, string> = {}) => ({ Authorization: 'Bearer test', ...extra }),
}));
const mockRefreshFeed = jest.fn();
let mockFeedState = { status: 'ready', dropped: 0, skipped: 0 };
let mockPayees: unknown[] = [];
let mockDebits: unknown[] = [];
jest.mock('../../data/backendFeed', () => ({
  ...jest.requireActual('../../data/backendFeed'),
  refresh: () => mockRefreshFeed(),
  refreshFresh: () => mockRefreshFeed(),
  getState: () => mockFeedState,
  getPayees: () => mockPayees,
  getDebits: () => mockDebits,
}));

const BASE = 'http://backend.test';
const fetchMock = jest.fn();
const popTo = jest.fn();
const replace = jest.fn();
const resetStack = jest.fn();
const tabNavigate = jest.fn();
let mounted: ReactTestRenderer.ReactTestRenderer | undefined;

type Reply = { status: number; body?: unknown } | null;
const row = (state: string, reason?: string) => ({
  status: 200,
  body: {
    banks: [
      {
        name: 'Example Bank',
        country: 'DE',
        account: 'Main account',
        validUntil: '2027-01-19T12:00:00',
        state,
        reason: reason ?? null,
      },
    ],
  },
});

/** `reads` are the answers of GET /my-banks in order (the last one repeats); `post` answers the retry. */
function backend(reads: Reply[], post: Reply = { status: 200 }) {
  let read = 0;
  fetchMock.mockImplementation((url: string) => {
    const path = url.replace(BASE, '');
    const reply = path === '/my-banks/first-sync' ? post : reads[Math.min(read++, reads.length - 1)];
    return reply === null
      ? Promise.reject(new Error('network'))
      : Promise.resolve({
          ok: reply.status < 300,
          status: reply.status,
          json: () => Promise.resolve(reply.body ?? {}),
        });
  });
}

const gets = () => fetchMock.mock.calls.filter(([, init]) => init.method === 'GET').length;
const posts = () => fetchMock.mock.calls.filter(([, init]) => init.method === 'POST').length;

beforeEach(() => {
  jest.useFakeTimers();
  __resetMyBanksStoreForTests();
  fetchMock.mockReset();
  popTo.mockReset();
  replace.mockReset();
  resetStack.mockReset();
  tabNavigate.mockReset();
  mockRefreshFeed.mockReset();
  mockPayees = [];
  mockDebits = [];
  mockFeedState = { status: 'ready', dropped: 0, skipped: 0 };
  __resetOnboardingOfferForTests();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
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
const labels = (tree: ReactTestRenderer.ReactTestRenderer) =>
  buttons(tree).flatMap(b => b.findAllByType(Text).map(joined));

async function press(tree: ReactTestRenderer.ReactTestRenderer, label: string) {
  const found = buttons(tree).find(b => b.findAllByType(Text).some(t => joined(t) === label));
  if (!found) {
    throw new Error(`no button "${label}" among ${labels(tree).join(' | ')}`);
  }
  await act(async () => {
    found.props.onPress();
  });
}

async function tick(ms: number) {
  await act(async () => {
    await jest.advanceTimersByTimeAsync(ms);
  });
}

async function render() {
  let tree!: ReactTestRenderer.ReactTestRenderer;
  await act(async () => {
    tree = ReactTestRenderer.create(
      <SyncingScreen
        navigation={
          {
            popTo,
            replace,
            reset: resetStack,
            getState: () => ({ routes: [{ name: 'SettingsHome' }, { name: 'Syncing' }] }),
            getParent: () => ({ navigate: tabNavigate }),
          } as never
        }
        route={{ key: 'k', name: 'Syncing', params: { bank: 'example bank', country: 'DE' } } as never}
      />,
    );
  });
  mounted = tree;
  return tree;
}

describe('running', () => {
  it('shows "Fetching your debits" and reads the bank again every few seconds', async () => {
    backend([row('syncing')]);
    const tree = await render();
    expect(texts(tree)).toContain('Fetching your debits');
    expect(labels(tree)).toEqual(['Settings', 'Back to Settings']);
    expect(gets()).toBe(1);
    await tick(SYNC_POLL_MS);
    await tick(SYNC_POLL_MS);
    expect(gets()).toBe(3);
  });

  it('keeps reading while the bank has no row yet, and keeps the last answer when a read fails', async () => {
    backend([{ status: 200, body: { banks: [] } }, null, row('syncing')]);
    const tree = await render();
    await tick(SYNC_POLL_MS);
    await tick(SYNC_POLL_MS);
    expect(gets()).toBe(3);
    expect(texts(tree)).toContain('Fetching your debits');
  });

  it('Back to Settings and the Settings link go to Settings and ask the backend for nothing', async () => {
    backend([row('syncing')]);
    const tree = await render();
    fetchMock.mockClear();
    await press(tree, 'Back to Settings');
    await press(tree, 'Settings');
    expect(popTo).toHaveBeenCalledTimes(2);
    expect(popTo).toHaveBeenCalledWith('SettingsHome');
    expect(posts()).toBe(0);
  });
});

describe('done', () => {
  it('refreshes the feed once and leaves for the debit list', async () => {
    backend([row('syncing'), row('connected')]);
    await render();
    expect(tabNavigate).not.toHaveBeenCalled();
    await tick(SYNC_POLL_MS);
    expect(mockRefreshFeed).toHaveBeenCalledTimes(1);
    expect(tabNavigate).toHaveBeenCalledWith('Debits');
    // The screen is dropped from the Settings stack, so the tab never shows it again.
    expect(resetStack).toHaveBeenCalledWith({ index: 0, routes: [{ name: 'SettingsHome' }] });
    await tick(SYNC_POLL_MS * 3);
    expect(tabNavigate).toHaveBeenCalledTimes(1);
  });
});

describe('done with payees to review (Story 5.9)', () => {
  const found = (id: string) => ({ id, name: `Shop ${id}`, initials: 'SH', iban: '' });
  const charge = (payeeId: string) => ({
    id: `d-${payeeId}`,
    payeeId,
    amountEUR: 5,
    timestamp: '2026-10-01T00:00:00Z',
    hasTime: false,
    paymentType: 'Card payment',
    reference: '',
  });

  it('shows the offer, after the feed has loaded, instead of the debit list; payees from before are not in it', async () => {
    mockPayees = [found('new'), found('old')];
    mockDebits = [charge('new'), charge('old')];
    rememberPayeesBeforeAttempt(['old']);
    backend([row('connected')]);
    await render();
    expect(mockRefreshFeed).toHaveBeenCalledTimes(1);
    expect(resetStack).toHaveBeenCalledWith({
      index: 1,
      routes: [{ name: 'SettingsHome' }, { name: 'ClassifyPayees', params: { bank: 'Example Bank' } }],
    });
    expect(tabNavigate).not.toHaveBeenCalled();
    expect(getOffer().map(r => r.payee.id)).toEqual(['new']);
  });

  it('goes straight to the debit list when every payee is known or the sync found no debits', async () => {
    mockPayees = [found('old')];
    mockDebits = [charge('old')];
    rememberPayeesBeforeAttempt(['old']);
    backend([row('connected')]);
    await render();
    expect(resetStack).toHaveBeenCalledWith({ index: 0, routes: [{ name: 'SettingsHome' }] });
    expect(tabNavigate).toHaveBeenCalledWith('Debits');
    expect(getOffer()).toEqual([]);
  });

  it('offers nothing when the app was restarted since the attempt began (no snapshot)', async () => {
    mockPayees = [found('new')];
    mockDebits = [charge('new')];
    backend([row('connected')]);
    await render();
    expect(tabNavigate).toHaveBeenCalledWith('Debits');
  });

  it('offers nothing when the load after the sync failed (the feed may be older than the sync)', async () => {
    mockPayees = [found('new')];
    mockDebits = [charge('new')];
    rememberPayeesBeforeAttempt([]);
    mockRefreshFeed.mockImplementation(() => {
      mockFeedState = { status: 'error', dropped: 0, skipped: 0 };
    });
    backend([row('connected')]);
    await render();
    expect(tabNavigate).toHaveBeenCalledWith('Debits');
    expect(resetStack).toHaveBeenCalledWith({ index: 0, routes: [{ name: 'SettingsHome' }] });
    expect(getOffer()).toEqual([]);
  });

  it('does nothing when the screen is left while the feed is loading', async () => {
    mockPayees = [found('new')];
    mockDebits = [charge('new')];
    rememberPayeesBeforeAttempt([]);
    let loaded!: () => void;
    mockRefreshFeed.mockImplementation(
      () =>
        new Promise<void>(resolve => {
          loaded = resolve;
        }),
    );
    backend([row('connected')]);
    await render();
    act(() => mounted?.unmount());
    mounted = undefined;
    await act(async () => {
      loaded();
    });
    expect(resetStack).not.toHaveBeenCalled();
    expect(tabNavigate).not.toHaveBeenCalled();
  });
});

describe('done view', () => {
  it('shows no spinner and no waiting text', async () => {
    backend([row('connected')]);
    const tree = await render();
    expect(tree.root.findAllByType(ActivityIndicator)).toHaveLength(0);
    expect(texts(tree)).not.toContain('Fetching your debits');
  });
});

describe('an older row in the store', () => {
  it('is not believed before one read has succeeded: expired row, failed first read, then a good read', async () => {
    backend([{ status: 200, body: row('expired').body }]);
    const first = await render();
    act(() => first.unmount());
    mounted = undefined;
    // The store now holds the old "expired" row. Open the screen again with a first read that fails.
    backend([null, row('syncing'), row('connected')]);
    const tree = await render();
    expect(texts(tree)).toContain('Fetching your debits');
    expect(texts(tree)).not.toContain("Couldn't fetch your debits");
    await tick(SYNC_POLL_MS);
    expect(gets()).toBeGreaterThanOrEqual(2);
    expect(texts(tree)).toContain('Fetching your debits');
    await tick(SYNC_POLL_MS);
    expect(tabNavigate).toHaveBeenCalledWith('Debits');
  });

  it('a stale failed row does not show a failed view while the first read is out', async () => {
    backend([row('sync_failed', 'bank-error')]);
    const first = await render();
    act(() => first.unmount());
    mounted = undefined;
    let release!: () => void;
    fetchMock.mockImplementationOnce(
      () =>
        new Promise(resolve => {
          release = () => resolve({ ok: true, status: 200, json: () => Promise.resolve(row('syncing').body) });
        }),
    );
    const tree = await render();
    expect(texts(tree)).toContain('Fetching your debits');
    await act(async () => {
      release();
    });
    expect(texts(tree)).toContain('Fetching your debits');
  });
});

describe('failed', () => {
  it('says so, keeps the bank connected, and offers Try again and Back to Settings', async () => {
    backend([row('sync_failed', 'bank-error')]);
    const tree = await render();
    expect(texts(tree)).toContain("Couldn't fetch your debits");
    expect(texts(tree).join(' ')).toMatch(/stays connected/);
    expect(labels(tree)).toEqual(['Settings', 'Try again', 'Back to Settings']);
  });

  it('Try again posts once, goes back to the syncing view and ignores a second tap', async () => {
    backend([row('sync_failed', 'bank-error'), row('syncing')]);
    const tree = await render();
    await act(async () => {
      const again = buttons(tree).find(b => b.findAllByType(Text).some(t => joined(t) === 'Try again'))!;
      again.props.onPress();
      again.props.onPress();
    });
    expect(posts()).toBe(1);
    expect(JSON.parse(fetchMock.mock.calls.find(([, init]) => init.method === 'POST')![1].body)).toEqual({
      aspsp: 'Example Bank',
    });
    expect(texts(tree)).toContain('Fetching your debits');
    expect(labels(tree)).not.toContain('Try again');
  });

  it('on 409 shows what the state says, without an error text', async () => {
    backend([row('sync_failed', 'bank-error'), row('sync_failed', 'daily-limit')], { status: 409 });
    const tree = await render();
    await press(tree, 'Try again');
    expect(texts(tree).join(' ')).toMatch(/Try again tomorrow/);
    expect(texts(tree).join(' ')).not.toMatch(/just now/);
    expect(labels(tree)).not.toContain('Try again');
  });

  it.each([
    ['503', { status: 503 }],
    ['no network', null],
  ])('on %s says it could not try again and stays on the failed view', async (_, post) => {
    backend([row('sync_failed', 'bank-error')], post);
    const tree = await render();
    await press(tree, 'Try again');
    expect(texts(tree)).toContain("Couldn't try again just now.");
    expect(labels(tree)).toContain('Try again');
    await press(tree, 'Try again');
    expect(posts()).toBe(2);
  });
});

describe('the other end states', () => {
  it('the daily limit says to try again tomorrow and has no Try again', async () => {
    backend([row('sync_failed', 'daily-limit')]);
    const tree = await render();
    expect(texts(tree).join(' ')).toMatch(/Try again tomorrow/);
    expect(labels(tree)).toEqual(['Settings', 'Back to Settings']);
  });

  it('expired has no Try again either', async () => {
    backend([row('expired')]);
    const tree = await render();
    expect(texts(tree)).toContain("Couldn't fetch your debits");
    expect(labels(tree)).toEqual(['Settings', 'Back to Settings']);
  });
});

describe('the two-minute stop', () => {
  it('stops waiting, stops reading, points to Settings and does not stop the sync', async () => {
    backend([row('syncing')]);
    const tree = await render();
    await tick(SYNC_WATCH_MS);
    expect(texts(tree)).toContain('Still fetching your debits');
    expect(texts(tree).join(' ')).toMatch(/card in Settings/);
    expect(labels(tree)).toEqual(['Settings', 'Back to Settings']);
    const reads = gets();
    await tick(SYNC_POLL_MS * 5);
    expect(gets()).toBe(reads);
    expect(posts()).toBe(0);
    expect(jest.getTimerCount()).toBe(0);
  });

  it('does not fire when the sync ended before', async () => {
    backend([row('syncing'), row('sync_failed', 'bank-error')]);
    const tree = await render();
    await tick(SYNC_POLL_MS);
    await tick(SYNC_WATCH_MS);
    expect(texts(tree)).toContain("Couldn't fetch your debits");
  });
});

describe('leaving', () => {
  it('stops all reading and timers when the screen goes away, and posts nothing', async () => {
    backend([row('syncing')]);
    await render();
    act(() => mounted?.unmount());
    mounted = undefined;
    fetchMock.mockClear();
    await tick(SYNC_WATCH_MS);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(jest.getTimerCount()).toBe(0);
  });
});
