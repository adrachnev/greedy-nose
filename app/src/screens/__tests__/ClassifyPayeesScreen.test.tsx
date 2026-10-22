/**
 * Story 5.9: the one-time payee offer. The real stores run over a scripted
 * backend (fetch is a mock): both buttons, Skip and back, a failed save (nothing
 * written, the list stays, trying again works), a double tap, a payee with a rule
 * or one that was there before the attempt (not listed), and that nothing is
 * left behind when the screen goes.
 */

import React from 'react';
import { BackHandler, Text } from 'react-native';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { Debit, Payee } from '../../domain/model';
import { clearOffer, getOffer, prepareOffer, rememberPayeesBeforeAttempt } from '../../data/onboardingOffer';
import ClassifyPayeesScreen from '../ClassifyPayeesScreen';

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

let run = 0;
const mine = (x: string) => `${x}-${run}`;
let mockPayees: Payee[] = [];
let mockDebits: Debit[] = [];
jest.mock('../../data/backendFeed', () => ({
  ...jest.requireActual('../../data/backendFeed'),
  getPayees: () => mockPayees,
  getDebits: () => mockDebits,
}));

const payee = (id: string, name: string, initials: string): Payee => ({ id, name, initials, iban: '' });
const debit = (payeeId: string, amountEUR: number, day: number, n: number): Debit => ({
  id: `${payeeId}-${n}`,
  payeeId,
  amountEUR,
  timestamp: `2026-10-${String(day).padStart(2, '0')}T00:00:00Z`,
  hasTime: false,
  paymentType: 'Direct debit',
  reference: '',
});

const fetchMock = jest.fn();
const resetStack = jest.fn();
const tabNavigate = jest.fn();
let mounted: ReactTestRenderer.ReactTestRenderer | undefined;
let backHandler: (() => boolean) | undefined;
const removeBack = jest.fn();

type Reply = { status: number } | null;
const answers: Reply[] = [];
let held: Promise<void> | null = null;

beforeEach(() => {
  run += 1; // the rules store lives as long as the file: every test gets payees of its own
  fetchMock.mockReset();
  resetStack.mockReset();
  tabNavigate.mockReset();
  removeBack.mockReset();
  backHandler = undefined;
  answers.length = 0;
  held = null;
  mockPayees = [payee(mine('a'), 'Spotify AB', 'SA'), payee(mine('b'), 'Vodafone GmbH', 'VG'), payee(mine('old'), 'Old Shop', 'OS')];
  mockDebits = [
    debit(mine('a'), 9.99, 3, 1),
    debit(mine('a'), 9.99, 10, 2),
    debit(mine('b'), 24.99, 5, 1),
    debit(mine('old'), 3, 6, 1),
  ];
  fetchMock.mockImplementation(async () => {
    await held;
    const next = answers.length > 0 ? answers.shift()! : { status: 204 };
    if (next === null) {
      throw new Error('network');
    }
    return { ok: next.status < 300, status: next.status, json: () => Promise.reject(new Error('no body')) };
  });
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
  jest.spyOn(BackHandler, 'addEventListener').mockImplementation(((_: string, handler: () => boolean) => {
    backHandler = handler;
    return { remove: removeBack };
  }) as never);
});
afterEach(() => {
  act(() => mounted?.unmount());
  mounted = undefined;
  jest.restoreAllMocks();
});

const joined = (t: ReactTestRenderer.ReactTestInstance) => [t.props.children].flat().join('');
const texts = (tree: ReactTestRenderer.ReactTestRenderer) => tree.root.findAllByType(Text).map(joined);
const posts = () => fetchMock.mock.calls.filter(([, init]) => init.method === 'POST');
const rules = () => (require('../../data/rulesStore') as typeof import('../../data/rulesStore')).getRules();

function offerForNewPayees() {
  // The feed held "old" before the attempt started.
  rememberPayeesBeforeAttempt([mine('old')]);
  return prepareOffer();
}

async function render(isFocused = true, expected = 2) {
  expect(offerForNewPayees()).toHaveLength(expected);
  let tree!: ReactTestRenderer.ReactTestRenderer;
  await act(async () => {
    tree = ReactTestRenderer.create(
      <ClassifyPayeesScreen
        navigation={{ reset: resetStack, isFocused: () => isFocused, getParent: () => ({ navigate: tabNavigate }) } as never}
        route={{ key: 'k', name: 'ClassifyPayees', params: { bank: 'Example Bank' } } as never}
      />,
    );
  });
  mounted = tree;
  return tree;
}

async function press(tree: ReactTestRenderer.ReactTestRenderer, label: string, times = 1) {
  const found = tree.root.find(
    n =>
      n.props.accessibilityRole === 'button' &&
      typeof n.props.onPress === 'function' &&
      n.findAllByType(Text).some(t => joined(t) === label),
  );
  await act(async () => {
    for (let i = 0; i < times; i++) {
      found.props.onPress();
    }
  });
}

const leftForDebitList = () => {
  expect(resetStack).toHaveBeenCalledWith({ index: 0, routes: [{ name: 'SettingsHome' }] });
  expect(tabNavigate).toHaveBeenCalledWith('Debits');
};

describe('the list', () => {
  it('shows the payees without a rule that are new, newest first, with amount, type and charges', async () => {
    const tree = await render();
    const all = texts(tree);
    expect(all).toContain('Example Bank: we found 2 payees without a rule in your last 90 days. Until you choose, they stay Bad.');
    expect(all).toContain('Spotify AB');
    expect(all).toContain('Vodafone GmbH');
    expect(all).not.toContain('Old Shop');
    expect(all.indexOf('Spotify AB')).toBeLessThan(all.indexOf('Vodafone GmbH'));
    expect(all.some(t => /Direct debit · 2 charges$/.test(t))).toBe(true);
    expect(all.some(t => /Direct debit · 1 charge$/.test(t))).toBe(true);
  });

});

describe('Mark all Good and start', () => {
  it('sends ONE batch, writes a Good rule without a limit for each payee, and opens the debit list', async () => {
    const tree = await render();
    await press(tree, 'Mark all Good and start');
    expect(posts()).toHaveLength(1);
    expect(posts()[0][0]).toBe('http://backend.test/rules/batch');
    expect(JSON.parse(posts()[0][1].body).rules.map((r: { payeeId: string }) => r.payeeId).sort()).toEqual([mine('a'), mine('b')]);
    expect(rules()).toEqual(
      expect.arrayContaining([
        { payeeId: mine('a'), classification: 'good' },
        { payeeId: mine('b'), classification: 'good' },
      ]),
    );
    expect(rules().some(r => r.payeeId === mine('old'))).toBe(false);
    leftForDebitList();
    expect(getOffer()).toEqual([]);
  });

  it('ignores a second tap while the first is on its way', async () => {
    let release!: () => void;
    held = new Promise(resolve => {
      release = resolve;
    });
    const tree = await render();
    await press(tree, 'Mark all Good and start', 2);
    expect(posts()).toHaveLength(1);
    expect(texts(tree)).toContain('Saving…');
    await act(async () => {
      release();
    });
    expect(posts()).toHaveLength(1);
    leftForDebitList();
  });

  it.each([
    ['a 400', { status: 400 }],
    ['a 503', { status: 503 }],
    ['no network', null],
  ])('on %s writes no rule, stays on the list with "Couldn\'t save", and trying again works', async (_, answer) => {
    answers.push(answer);
    const tree = await render();
    const before = rules().length;
    await press(tree, 'Mark all Good and start');
    expect(rules()).toHaveLength(before);
    expect(resetStack).not.toHaveBeenCalled();
    expect(texts(tree)).toContain("Couldn't save");
    expect(texts(tree)).toContain('Spotify AB');
    expect(texts(tree)).toContain('Mark all Good and start');

    await press(tree, 'Mark all Good and start');
    expect(posts()).toHaveLength(2);
    expect(texts(tree)).not.toContain("Couldn't save");
    expect(rules()).toHaveLength(before + 2);
    leftForDebitList();
  });
});

describe('too many payees for one request', () => {
  it('says so without sending, and Keep all Bad still works', async () => {
    mockPayees = Array.from({ length: 2001 }, (_, i) => payee(`many-${run}-${i}`, `Shop ${i}`, 'SH'));
    mockDebits = mockPayees.map((p, i) => debit(p.id, 5, 1 + (i % 28), i));
    const tree = await render(true, 2001);
    await press(tree, 'Mark all Good and start');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(texts(tree).join(' ')).toMatch(/Too many payees to sort at once/);
    expect(texts(tree).join(' ')).not.toMatch(/Check your connection/);
    await press(tree, 'Keep all Bad and start');
    leftForDebitList();
  });
});

describe('Keep all Bad and start, Skip and back', () => {
  it.each(['Keep all Bad and start', 'Skip'])('%s writes nothing, sends nothing and opens the debit list', async label => {
    const tree = await render();
    const before = rules();
    await press(tree, label);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(rules()).toBe(before);
    leftForDebitList();
  });

  it('going back is the same, and ends in the debit list', async () => {
    await render();
    const before = rules();
    expect(backHandler?.()).toBe(true);
    expect(fetchMock).not.toHaveBeenCalled();
    expect(rules()).toBe(before);
    leftForDebitList();
  });

  it('going back while another screen is focused is left to the system', async () => {
    await render(false);
    expect(backHandler?.()).toBe(false);
    expect(resetStack).not.toHaveBeenCalled();
  });

  it('going back and Skip are ignored while the rules are on their way', async () => {
    let release!: () => void;
    held = new Promise(resolve => {
      release = resolve;
    });
    const tree = await render();
    await press(tree, 'Mark all Good and start');
    expect(backHandler?.()).toBe(true);
    await press(tree, 'Skip');
    expect(resetStack).not.toHaveBeenCalled();
    await act(async () => {
      release();
    });
    expect(resetStack).toHaveBeenCalledTimes(1);
  });
});

describe('leaving and the offer', () => {
  it('spends the offer when the screen goes away, so it cannot come back', async () => {
    await render();
    expect(getOffer()).toHaveLength(2);
    act(() => mounted?.unmount());
    mounted = undefined;
    expect(getOffer()).toEqual([]);
    expect(removeBack).toHaveBeenCalled();
  });

  it('without an offer (a restart lost it) the screen opens the debit list at once', async () => {
    clearOffer();
    await act(async () => {
      mounted = ReactTestRenderer.create(
        <ClassifyPayeesScreen
          navigation={{ reset: resetStack, isFocused: () => true, getParent: () => ({ navigate: tabNavigate }) } as never}
          route={{ key: 'k', name: 'ClassifyPayees', params: { bank: 'Example Bank' } } as never}
        />,
      );
    });
    leftForDebitList();
  });
});
