/**
 * Story 5.6: the bank search. Empty until two characters, one request after a
 * pause, newest answer wins, three different results, a connected row that
 * cannot be tapped.
 */

import React from 'react';
import { Text, TextInput } from 'react-native';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { __resetConnectStoreForTests, getConnectFlow } from '../../data/connectStore';
import { MAX_SEARCH_LENGTH, SEARCH_PAUSE_MS } from '../../data/myBanksStore';
import { formatCountry } from '../../utils/format';
import SelectBankScreen from '../SelectBankScreen';

jest.mock('react-native-safe-area-context', () => ({
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));
jest.mock('../../data/config', () => ({
  USE_BACKEND: true,
  BACKEND_BASE_URL: 'http://backend.test',
  BACKEND_TIMEOUT_MS: 1000,
}));
jest.mock('../../data/openConnectLink', () => ({
  openConnectLink: jest.fn(() => Promise.resolve(false)),
  dismissConnectLink: jest.fn(() => Promise.resolve()),
}));
jest.mock('../../data/backendAuth', () => ({
  hasApiKey: () => true,
  backendHeaders: () => ({ Authorization: 'Bearer test' }),
}));

const fetchMock = jest.fn();
const navigate = jest.fn();
let mounted: ReactTestRenderer.ReactTestRenderer | undefined;

const hit = (name: string, country: string, connected = false) => ({ name, country, connected });

function answer(banks: unknown[] | null) {
  fetchMock.mockResolvedValueOnce(
    banks === null
      ? { ok: false, status: 500, json: () => Promise.resolve({}) }
      : { ok: true, status: 200, json: () => Promise.resolve({ banks }) },
  );
}

beforeEach(() => {
  jest.useFakeTimers();
  navigate.mockReset();
  __resetConnectStoreForTests();
  fetchMock.mockReset();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
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
      <SelectBankScreen
        navigation={{ navigate } as never}
        route={{ key: 'k', name: 'SelectBank' } as never}
      />,
    );
  });
  mounted = tree;
  return tree;
}

async function type(tree: ReactTestRenderer.ReactTestRenderer, text: string) {
  await act(async () => {
    tree.root.findByType(TextInput).props.onChangeText(text);
  });
}

async function wait(ms: number) {
  await act(async () => {
    await jest.advanceTimersByTimeAsync(ms);
  });
}

it('opens with an empty field and no list, and Cancel returns to Settings', async () => {
  const tree = await render();
  expect(tree.root.findByType(TextInput).props.value).toBe('');
  expect(tree.root.findByType(TextInput).props.maxLength).toBe(MAX_SEARCH_LENGTH);
  expect(texts(tree)).toEqual(['Connect a bank', 'Cancel']);
  const cancel = pressables(tree)[0];
  act(() => cancel.props.onPress());
  expect(navigate).toHaveBeenCalledWith('SettingsHome');
});

it('asks nothing below two characters', async () => {
  const tree = await render();
  await type(tree, 'e');
  await wait(SEARCH_PAUSE_MS * 2);
  expect(fetchMock).not.toHaveBeenCalled();
});

it('asks once after the pause, not on every key', async () => {
  answer([hit('Example Bank', 'DE'), hit('Example Direct', 'AT')]);
  const tree = await render();
  await type(tree, 'ex');
  await wait(SEARCH_PAUSE_MS - 100);
  await type(tree, 'exa');
  await wait(SEARCH_PAUSE_MS - 100);
  expect(fetchMock).not.toHaveBeenCalled();
  await wait(100);
  expect(fetchMock).toHaveBeenCalledTimes(1);
  expect(fetchMock.mock.calls[0][0]).toBe('http://backend.test/banks?q=exa');
});

it('shows name and country of each hit, and a connected bank as "Connected" that cannot be tapped', async () => {
  answer([hit('Example Bank', 'DE'), hit('Example Bank', 'AT', true)]);
  const tree = await render();
  await type(tree, 'example');
  await wait(SEARCH_PAUSE_MS);
  const all = texts(tree);
  expect(all).toContain('2 banks');
  expect(all).toContain(formatCountry('DE'));
  expect(all).toContain(formatCountry('AT'));
  expect(all).toContain('Connected');
  // Only the unconnected row is a button; the connected one is a plain view.
  const rowTexts = pressables(tree).map(b => b.findAllByType(Text).map(joined).join('|'));
  expect(rowTexts.some(t => t.includes('Connected'))).toBe(false);
  const row = pressables(tree).find(b => b.findAllByType(Text).some(t => joined(t) === formatCountry('DE')))!;
  expect(row).toBeDefined();
});

it('starts the attempt with the name and country of the tapped bank and shows its screen', async () => {
  answer([hit('Example Bank', 'DE')]);
  const tree = await render();
  await type(tree, 'example');
  await wait(SEARCH_PAUSE_MS);
  fetchMock.mockResolvedValueOnce({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ url: 'https://example.test/connect?token=t', attemptId: 'a1' }),
  });
  const row = pressables(tree).find(b => b.findAllByType(Text).some(t => joined(t) === formatCountry('DE')))!;
  await act(async () => {
    row.props.onPress();
  });
  const post = fetchMock.mock.calls[1];
  expect(post[0]).toBe('http://backend.test/connect-links');
  expect(JSON.parse(post[1].body)).toEqual({ aspsp: 'Example Bank', country: 'DE' });
  expect(navigate).toHaveBeenCalledWith('ConnectAttempt');
  expect(getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Example Bank', country: 'DE' });
});

it('while an attempt is running a tap starts nothing and shows that attempt', async () => {
  answer([hit('Example Bank', 'DE'), hit('Other Bank', 'AT')]);
  const tree = await render();
  await type(tree, 'bank');
  await wait(SEARCH_PAUSE_MS);
  fetchMock.mockResolvedValueOnce({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ url: 'https://example.test/connect?token=t', attemptId: 'a1' }),
  });
  const rowFor = (country: string) =>
    pressables(tree).find(b => b.findAllByType(Text).some(t => joined(t) === formatCountry(country)))!;
  await act(async () => {
    rowFor('DE').props.onPress();
  });
  fetchMock.mockClear();
  navigate.mockClear();
  await act(async () => {
    rowFor('AT').props.onPress();
  });
  expect(fetchMock).not.toHaveBeenCalled();
  expect(navigate).toHaveBeenCalledWith('ConnectAttempt');
  expect(getConnectFlow()).toMatchObject({ step: 'waiting', bank: 'Example Bank' });
});

it('shows "No bank found" with the typed text when nothing matches', async () => {
  answer([]);
  const tree = await render();
  await type(tree, ' zzz ');
  await wait(SEARCH_PAUSE_MS);
  expect(texts(tree)).toContain('No bank found');
  expect(texts(tree).join('\n')).toContain('Nothing matches “zzz”.');
});

it('says "Couldn\'t load banks" with Try again, never "No bank found", when the read fails', async () => {
  answer(null);
  const tree = await render();
  await type(tree, 'example');
  await wait(SEARCH_PAUSE_MS);
  expect(texts(tree)).toContain("Couldn't load banks");
  expect(texts(tree)).not.toContain('No bank found');

  answer([hit('Example Bank', 'DE')]);
  const retry = pressables(tree).find(n => n.findAllByType(Text).some(t => joined(t) === 'Try again'))!;
  await act(async () => {
    retry.props.onPress();
  });
  await wait(1);
  expect(texts(tree)).toContain('1 bank');
});

it('never lets a slow old answer overwrite the newer one', async () => {
  let resolveOld!: (value: unknown) => void;
  fetchMock.mockReturnValueOnce(new Promise(resolve => (resolveOld = resolve)));
  answer([hit('Newer Bank', 'DE')]);
  const tree = await render();
  await type(tree, 'old');
  await wait(SEARCH_PAUSE_MS);
  await type(tree, 'newer');
  await wait(SEARCH_PAUSE_MS);
  expect(texts(tree)).toContain('Newer Bank');

  await act(async () => {
    resolveOld({ ok: true, status: 200, json: () => Promise.resolve({ banks: [hit('Old Bank', 'DE')] }) });
    await jest.advanceTimersByTimeAsync(0);
  });
  expect(texts(tree)).toContain('Newer Bank');
  expect(texts(tree)).not.toContain('Old Bank');
});

it('clears the list when the text gets short again', async () => {
  answer([hit('Example Bank', 'DE')]);
  const tree = await render();
  await type(tree, 'example');
  await wait(SEARCH_PAUSE_MS);
  await type(tree, 'e');
  expect(texts(tree)).not.toContain('Example Bank');
});

it('keeps the previous hits on screen while new ones load', async () => {
  answer([hit('Example Bank', 'DE')]);
  let resolveNew!: (value: unknown) => void;
  fetchMock.mockReturnValueOnce(new Promise(resolve => (resolveNew = resolve)));
  const tree = await render();
  await type(tree, 'exam');
  await wait(SEARCH_PAUSE_MS);
  await type(tree, 'examp');
  await wait(SEARCH_PAUSE_MS);
  expect(texts(tree)).toContain('Example Bank');
  await act(async () => {
    resolveNew({ ok: true, status: 200, json: () => Promise.resolve({ banks: [hit('Other Bank', 'AT')] }) });
    await jest.advanceTimersByTimeAsync(0);
  });
  expect(texts(tree)).toContain('Other Bank');
  expect(texts(tree)).not.toContain('Example Bank');
});

it('does not ask again for the same text with a trailing space', async () => {
  answer([hit('Example Bank', 'DE')]);
  const tree = await render();
  await type(tree, 'example');
  await wait(SEARCH_PAUSE_MS);
  await type(tree, 'example ');
  await wait(SEARCH_PAUSE_MS * 2);
  expect(fetchMock).toHaveBeenCalledTimes(1);
});
