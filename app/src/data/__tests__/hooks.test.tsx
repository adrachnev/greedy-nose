/**
 * Regression test for the exact on-device failure mode: changing a payee's
 * classification on one screen updated that screen but left the debit list
 * stale, because the mutation changed an object in place instead of producing
 * a new array reference for useSyncExternalStore to detect.
 *
 * These render two independent consumers of the same hook (one standing in
 * for the list, one for the detail screen), mutate via the hook screens
 * actually use, and assert the "list" consumer's rendered output updates.
 * Against the old in-place-mutation implementation they fail; against the
 * current code they pass.
 *
 * They also cover R7 by construction: the list re-labels immediately when a
 * rule changes, because classification is derived at read time (R6) rather
 * than stored on the debit.
 *
 * Rules now hydrate from AsyncStorage (src/data/rulesStore.ts) asynchronously
 * on first subscribe, so the initial render has to be wrapped in
 * `await act(async () => {...})` rather than the synchronous `act` this file
 * used before persistence existed — otherwise the first assertion below would
 * see the pre-hydration empty state rather than the seeded fixture rules.
 * Forced to fixture mode here (rather than whatever ./config's checked-in
 * USE_BACKEND happens to be) because rulesStore.ts seeds the fixture mix only
 * in fixture mode — see its own seedRules() — and that mix is what this file's
 * assertions are pinned against.
 */

import React from 'react';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { Text } from 'react-native';
import { useRuleForPayee, useSavePayeeRule } from '../hooks';
import { classifyPayee } from '../../domain/classification';
import { Payee } from '../../domain/model';

jest.mock('../config', () => ({
  USE_BACKEND: false,
  BACKEND_BASE_URL: 'http://backend.invalid',
  BACKEND_TIMEOUT_MS: 1000,
}));

const PAYEE_ID = 'payee-fitline';
// Fixture mode never syncs (USE_BACKEND is mocked false above), so these
// stand-ins only need to satisfy saveRule()'s Payee parameter — their
// name/initials/iban are not asserted on anywhere in this file.
const PAYEE: Payee = { id: PAYEE_ID, name: 'FitLine Gym', initials: 'FG', iban: '' };

// Stands in for the debit list: derives the payee's status from the rule.
function ListProbe() {
  const rule = useRuleForPayee(PAYEE_ID);
  return <Text testID="list-status">{classifyPayee(rule)}</Text>;
}

// Stands in for the detail screen: same source, separate consumer.
function DetailProbe() {
  const rule = useRuleForPayee(PAYEE_ID);
  return <Text testID="detail-status">{classifyPayee(rule)}</Text>;
}

function Screens() {
  return (
    <>
      <ListProbe />
      <DetailProbe />
    </>
  );
}

function textAt(root: ReactTestRenderer.ReactTestInstance, testID: string): string {
  return root.findByProps({ testID }).props.children;
}

/**
 * Grabs the write hook outside any probe's own render, so the mutation below
 * really goes through useSyncExternalStore rather than a local state update.
 */
function captureSaveRule(): ReturnType<typeof useSavePayeeRule> {
  let saveRule!: ReturnType<typeof useSavePayeeRule>;
  function Capture() {
    saveRule = useSavePayeeRule();
    return null;
  }
  act(() => {
    ReactTestRenderer.create(<Capture />);
  });
  return saveRule;
}

describe('useRuleForPayee re-render on mutation', () => {
  it('updates a sibling consumer (the "list") after savePayeeRule() is called elsewhere (the edit screen)', async () => {
    let renderer!: ReactTestRenderer.ReactTestRenderer;
    await act(async () => {
      renderer = ReactTestRenderer.create(<Screens />);
    });

    expect(textAt(renderer.root, 'list-status')).toBe('bad');
    expect(textAt(renderer.root, 'detail-status')).toBe('bad');

    const saveRule = captureSaveRule();
    act(() => {
      saveRule(PAYEE, { classification: 'good' });
    });

    // The regression: this must reflect the change, not just the detail
    // probe. Under the old in-place-mutation bug it stayed 'bad'.
    expect(textAt(renderer.root, 'list-status')).toBe('good');
    expect(textAt(renderer.root, 'detail-status')).toBe('good');
  });
});

/** Both fields at once, since one Save now commits both. */
const LIMIT_PAYEE_ID = 'payee-baeckerei';
const LIMIT_PAYEE: Payee = { id: LIMIT_PAYEE_ID, name: 'Bäckerei Müller', initials: 'BM', iban: '' };

function RuleProbe() {
  const rule = useRuleForPayee(LIMIT_PAYEE_ID);
  const limit = rule?.amountEUR != null ? `over-${rule.amountEUR}` : 'no-limit';
  // One interpolated child, so `textAt` reads a string rather than an array.
  return <Text testID="rule-status">{`${classifyPayee(rule)}/${limit}`}</Text>;
}

describe('useSavePayeeRule re-render on mutation', () => {
  it('updates a sibling consumer for both fields of a single save', async () => {
    let renderer!: ReactTestRenderer.ReactTestRenderer;
    await act(async () => {
      renderer = ReactTestRenderer.create(<RuleProbe />);
    });

    // payee-baeckerei ships good with a €30 limit in the fixtures.
    expect(textAt(renderer.root, 'rule-status')).toBe('good/over-30');

    const saveRule = captureSaveRule();

    act(() => {
      saveRule(LIMIT_PAYEE, { classification: 'good', amountEUR: 15 });
    });
    expect(textAt(renderer.root, 'rule-status')).toBe('good/over-15');

    // Clearing the limit is the other half of the same write path (R4a).
    act(() => {
      saveRule(LIMIT_PAYEE, { classification: 'good', amountEUR: undefined });
    });
    expect(textAt(renderer.root, 'rule-status')).toBe('good/no-limit');

    // And both fields moving in one save must land in one render pass.
    act(() => {
      saveRule(LIMIT_PAYEE, { classification: 'bad', amountEUR: 40 });
    });
    expect(textAt(renderer.root, 'rule-status')).toBe('bad/over-40');
  });
});
