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
 */

import React from 'react';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { Text } from 'react-native';
import { useRuleForPayee, useSavePayeeRule } from '../hooks';
import { classifyPayee } from '../../domain/classification';

const PAYEE_ID = 'payee-fitline';

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
  it('updates a sibling consumer (the "list") after savePayeeRule() is called elsewhere (the edit screen)', () => {
    let renderer!: ReactTestRenderer.ReactTestRenderer;
    act(() => {
      renderer = ReactTestRenderer.create(<Screens />);
    });

    expect(textAt(renderer.root, 'list-status')).toBe('bad');
    expect(textAt(renderer.root, 'detail-status')).toBe('bad');

    const saveRule = captureSaveRule();
    act(() => {
      saveRule(PAYEE_ID, { classification: 'good' });
    });

    // The regression: this must reflect the change, not just the detail
    // probe. Under the old in-place-mutation bug it stayed 'bad'.
    expect(textAt(renderer.root, 'list-status')).toBe('good');
    expect(textAt(renderer.root, 'detail-status')).toBe('good');
  });
});

/** Both fields at once, since one Save now commits both. */
const LIMIT_PAYEE_ID = 'payee-baeckerei';

function RuleProbe() {
  const rule = useRuleForPayee(LIMIT_PAYEE_ID);
  const limit = rule?.amountEUR != null ? `over-${rule.amountEUR}` : 'no-limit';
  // One interpolated child, so `textAt` reads a string rather than an array.
  return <Text testID="rule-status">{`${classifyPayee(rule)}/${limit}`}</Text>;
}

describe('useSavePayeeRule re-render on mutation', () => {
  it('updates a sibling consumer for both fields of a single save', () => {
    let renderer!: ReactTestRenderer.ReactTestRenderer;
    act(() => {
      renderer = ReactTestRenderer.create(<RuleProbe />);
    });

    // payee-baeckerei ships good with a €30 limit in the fixtures.
    expect(textAt(renderer.root, 'rule-status')).toBe('good/over-30');

    const saveRule = captureSaveRule();

    act(() => {
      saveRule(LIMIT_PAYEE_ID, { classification: 'good', amountEUR: 15 });
    });
    expect(textAt(renderer.root, 'rule-status')).toBe('good/over-15');

    // Clearing the limit is the other half of the same write path (R4a).
    act(() => {
      saveRule(LIMIT_PAYEE_ID, { classification: 'good', amountEUR: undefined });
    });
    expect(textAt(renderer.root, 'rule-status')).toBe('good/no-limit');

    // And both fields moving in one save must land in one render pass.
    act(() => {
      saveRule(LIMIT_PAYEE_ID, { classification: 'bad', amountEUR: 40 });
    });
    expect(textAt(renderer.root, 'rule-status')).toBe('bad/over-40');
  });
});
