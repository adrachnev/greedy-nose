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
import {
  useAcknowledgeAutoFlipNotice,
  useAutoFlipNotices,
  useDebtor,
  useDebtors,
  useRuleForDebtor,
  useSetDebtorRule,
  useSetDebtorTrusted,
} from '../hooks';
import { addTransaction } from '../../mocks/data';

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

    let result: ReturnType<ReturnType<typeof useSetDebtorTrusted>> | undefined;
    act(() => {
      result = setTrusted(DEBTOR_ID, true);
    });

    // The regression: this must reflect the change, not just the detail
    // probe. Under the old in-place-mutation bug, `debtors` (the array
    // useDebtors() snapshots) never changed reference, so this stayed 'Bad'.
    expect(textAt(renderer.root, 'list-status')).toBe('Trusted');
    expect(textAt(renderer.root, 'detail-status')).toBe('Trusted');
    // debtor-scamyloans had no rule to begin with, so nothing to clear.
    expect(result).toEqual({ clearedAmountThresholdEUR: undefined });
  });
});

/**
 * Same regression shape as above, applied to useSetDebtorRule()/
 * useRuleForDebtor(): a sibling consumer of the rule for the same debtor
 * must see the update once useSetDebtorRule() is called elsewhere, since
 * useRuleForDebtor() is now useSyncExternalStore-backed (via useRules()).
 */
const RULE_DEBTOR_ID = 'debtor-spotify';

function RuleProbe() {
  const rule = useRuleForDebtor(RULE_DEBTOR_ID);
  return (
    <Text testID="rule-status">
      {rule?.amountThresholdEUR != null ? `over-${rule.amountThresholdEUR}` : 'none'}
    </Text>
  );
}

describe('useRuleForDebtor re-render on mutation', () => {
  it('updates a sibling consumer after setDebtorRule() is called from elsewhere', () => {
    let renderer!: ReactTestRenderer.ReactTestRenderer;
    act(() => {
      renderer = ReactTestRenderer.create(<RuleProbe />);
    });

    expect(textAt(renderer.root, 'rule-status')).toBe('none');

    let setRule!: ReturnType<typeof useSetDebtorRule>;
    function Capture() {
      setRule = useSetDebtorRule();
      return null;
    }
    act(() => {
      ReactTestRenderer.create(<Capture />);
    });

    let result: ReturnType<ReturnType<typeof useSetDebtorRule>> | undefined;
    act(() => {
      result = setRule(RULE_DEBTOR_ID, { amountThresholdEUR: 15 });
    });

    expect(textAt(renderer.root, 'rule-status')).toBe('over-15');
    // debtor-spotify's most recent transaction (-9.99) does not exceed 15,
    // and debtor-spotify defaults to Bad — so this flips them to Trusted.
    expect(result).toEqual({ autoFlippedTo: 'Trusted' });
  });
});

/**
 * useAutoFlipNotices()/useAcknowledgeAutoFlipNotice(): same sibling-consumer
 * re-render shape as the hooks above, since autoFlipNotices is also
 * useSyncExternalStore-backed.
 */
const NOTICE_DEBTOR_ID = 'debtor-landlord';

function NoticeProbe() {
  const notices = useAutoFlipNotices();
  const notice = notices.find(n => n.debtorId === NOTICE_DEBTOR_ID);
  return <Text testID="notice-status">{notice ? 'pending' : 'none'}</Text>;
}

describe('useAutoFlipNotices / useAcknowledgeAutoFlipNotice', () => {
  it('reflects a passive auto-flip notice and clears it on acknowledge', () => {
    let renderer!: ReactTestRenderer.ReactTestRenderer;
    act(() => {
      renderer = ReactTestRenderer.create(<NoticeProbe />);
    });
    expect(textAt(renderer.root, 'notice-status')).toBe('none');

    let setRule!: ReturnType<typeof useSetDebtorRule>;
    let acknowledge!: ReturnType<typeof useAcknowledgeAutoFlipNotice>;
    function Capture() {
      setRule = useSetDebtorRule();
      acknowledge = useAcknowledgeAutoFlipNotice();
      return null;
    }
    act(() => {
      ReactTestRenderer.create(<Capture />);
    });

    // Setting a threshold via Save (setRule) their last charge (-850) does
    // NOT exceed keeps debtor-landlord Trusted (no flip, no notice) —
    // avoids the Save-time flip path entirely, so the notice below can only
    // come from the passive path.
    act(() => {
      setRule(NOTICE_DEBTOR_ID, { amountThresholdEUR: 900 });
    });
    expect(textAt(renderer.root, 'notice-status')).toBe('none');

    // A passively-arriving transaction exceeding the threshold flips
    // Trusted -> Bad and records a notice — no toast/screen involved.
    act(() => {
      addTransaction({
        id: 'tx-notice-test',
        debtorId: NOTICE_DEBTOR_ID,
        amountEUR: -950,
        timestamp: new Date().toISOString(),
        paymentType: 'Bank transfer',
        reference: 'Large unexpected charge',
      });
    });
    expect(textAt(renderer.root, 'notice-status')).toBe('pending');

    act(() => {
      acknowledge(NOTICE_DEBTOR_ID);
    });
    expect(textAt(renderer.root, 'notice-status')).toBe('none');
  });
});
