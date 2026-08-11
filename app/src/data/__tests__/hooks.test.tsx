/**
 * Regression test for the exact on-device failure mode: toggling
 * Trusted/Bad on TransactionDetailScreen updated that screen but
 * TransactionListScreen didn't re-render, because setDebtorTrusted()
 * originally mutated a debtor object in place instead of producing a new
 * `debtors` array reference for useSyncExternalStore to detect.
 *
 * This renders two independent consumers of useDebtors()/useDebtor() (one
 * standing in for the list, one for the detail screen), mutates via the
 * hook exposed to screens, and asserts the "list" consumer's rendered
 * output actually updates. Against the old in-place-mutation
 * implementation this test fails (list text stays stale); against the
 * current fix it passes.
 */

import React from 'react';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { Text } from 'react-native';
import { useDebtor, useDebtors, useSetDebtorTrusted } from '../hooks';

const DEBTOR_ID = 'debtor-scamyloans';

// Stands in for TransactionListScreen: renders every debtor's trust state
// derived from useDebtors().
function DebtorListProbe() {
  const debtors = useDebtors();
  const target = debtors.find(d => d.id === DEBTOR_ID)!;
  return <Text testID="list-status">{target.trusted ? 'Trusted' : 'Bad'}</Text>;
}

// Stands in for TransactionDetailScreen: reads a single debtor via
// useDebtor() and exposes the mutation used to toggle it.
function DebtorDetailProbe() {
  const debtor = useDebtor(DEBTOR_ID);
  return <Text testID="detail-status">{debtor?.trusted ? 'Trusted' : 'Bad'}</Text>;
}

function Screens() {
  return (
    <>
      <DebtorListProbe />
      <DebtorDetailProbe />
    </>
  );
}

function textAt(root: ReactTestRenderer.ReactTestInstance, testID: string): string {
  return root.findByProps({ testID }).props.children;
}

describe('useDebtors / useDebtor re-render on mutation', () => {
  it('updates a sibling consumer (the "list") after setDebtorTrusted() is called from elsewhere (the "detail" screen)', () => {
    let renderer!: ReactTestRenderer.ReactTestRenderer;
    act(() => {
      renderer = ReactTestRenderer.create(<Screens />);
    });

    expect(textAt(renderer.root, 'list-status')).toBe('Bad');
    expect(textAt(renderer.root, 'detail-status')).toBe('Bad');

    // Mutate via the same hook screens actually use, outside of either
    // probe's own render — this is what exercises useSyncExternalStore.
    let setTrusted!: ReturnType<typeof useSetDebtorTrusted>;
    function Capture() {
      setTrusted = useSetDebtorTrusted();
      return null;
    }
    act(() => {
      ReactTestRenderer.create(<Capture />);
    });

    act(() => {
      setTrusted(DEBTOR_ID, true);
    });

    // The regression: this must reflect the change, not just the detail
    // probe. Under the old in-place-mutation bug, `debtors` (the array
    // useDebtors() snapshots) never changed reference, so this stayed 'Bad'.
    expect(textAt(renderer.root, 'list-status')).toBe('Trusted');
    expect(textAt(renderer.root, 'detail-status')).toBe('Trusted');
  });
});
