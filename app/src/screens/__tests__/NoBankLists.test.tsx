/**
 * Story 5.6: the empty debit list (02b#no-bank) and the empty rules list
 * (04c#no-bank) point to the bank search, but only when the backend said "no
 * bank". Loading, failed, or a bank present keep today's lists.
 */

import React from 'react';
import { Text } from 'react-native';
import ReactTestRenderer, { act } from 'react-test-renderer';
import type { MyBanksState } from '../../data/myBanksStore';
import DebitListScreen from '../DebitListScreen';
import RulesListScreen from '../RulesListScreen';

jest.mock('react-native-safe-area-context', () => ({
  useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }),
}));
jest.mock('../../components/DataSourceBadge', () => () => null);
jest.mock('../../components/ReleaseStatusLine', () => () => null);
jest.mock('../../hooks/useTabScopedSearch', () => ({
  useTabScopedSearch: () => ({ search: '', setSearch: () => {} }),
}));
jest.mock('../../hooks/useCurrentDateKey', () => ({
  ...jest.requireActual('../../hooks/useCurrentDateKey'),
  useCurrentDateKey: () => '2026-10-22',
}));

let mockMyBanks: MyBanksState;
let mockPayees: unknown[];
let mockRules: unknown[];
jest.mock('../../data/hooks', () => ({
  useMyBanks: () => mockMyBanks,
  useDebits: () => [],
  usePayees: () => mockPayees,
  useRules: () => mockRules,
  useRuleByPayeeId: () => new Map(),
}));

const mockOpenTabs = jest.fn();
const navigation = {
  navigate: jest.fn(),
  getParent: (id: string) => {
    mockOpenTabs(id);
    return { navigate: mockTabNavigate };
  },
};
const mockTabNavigate = jest.fn();

beforeEach(() => {
  mockPayees = [];
  mockRules = [];
  mockTabNavigate.mockReset();
  mockOpenTabs.mockReset();
});

const pressables = (tree: ReactTestRenderer.ReactTestRenderer) =>
  tree.root.findAll(
    n =>
      n.props.accessibilityRole === 'button' &&
      typeof n.props.onPress === 'function' &&
      n.parent?.props.accessibilityRole !== 'button',
  );

const joined = (t: ReactTestRenderer.ReactTestInstance) => [t.props.children].flat().join('');

function render(element: React.ReactElement) {
  let tree!: ReactTestRenderer.ReactTestRenderer;
  act(() => {
    tree = ReactTestRenderer.create(element);
  });
  return tree;
}
const texts = (tree: ReactTestRenderer.ReactTestRenderer) => tree.root.findAllByType(Text).map(joined);

const debits = () =>
  render(<DebitListScreen navigation={navigation as never} route={{} as never} />);
const rulesList = () =>
  render(<RulesListScreen navigation={navigation as never} route={{} as never} />);

it('the debit list says "No bank connected" and opens the search below Settings', () => {
  mockMyBanks = { status: 'ready', banks: [] };
  const tree = debits();
  expect(texts(tree)).toContain('No bank connected');
  expect(texts(tree)).toContain('Debits show up here once a bank is connected.');
  const button = pressables(tree)[0];
  act(() => button.props.onPress());
  expect(mockOpenTabs).toHaveBeenCalledWith('MainTabs');
  expect(mockTabNavigate).toHaveBeenCalledWith('Settings', { screen: 'SelectBank', initial: false });
});

it('the rules list says "No payees yet" with the same way on', () => {
  mockMyBanks = { status: 'ready', banks: [] };
  const tree = rulesList();
  expect(texts(tree)).toContain('No payees yet');
  const button = pressables(tree)[0];
  act(() => button.props.onPress());
  expect(mockTabNavigate).toHaveBeenCalledWith('Settings', { screen: 'SelectBank', initial: false });
});

it('keeps the plain lists while loading, after a failed read, and with a bank present', () => {
  const bank = { name: 'Example Bank', country: 'DE', account: null, validUntil: null, state: 'connected', reason: null };
  const states: MyBanksState[] = [
    { status: 'loading', banks: [] },
    { status: 'failed', banks: [] },
    { status: 'ready', banks: [bank] as never },
  ];
  for (const state of states) {
    mockMyBanks = state;
    expect(texts(debits())).not.toContain('No bank connected');
    expect(texts(rulesList())).not.toContain('No payees yet');
  }
});

it('keeps the rules list when rules exist, bank or no bank (rules outlive their bank)', () => {
  mockMyBanks = { status: 'ready', banks: [] };
  mockRules = [{ payeeId: 'p1' }];
  expect(texts(rulesList())).not.toContain('No payees yet');
});
