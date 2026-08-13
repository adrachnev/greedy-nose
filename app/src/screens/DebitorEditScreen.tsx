import { NavigationProp, ParamListBase, RouteProp } from '@react-navigation/native';
import React, { useEffect, useRef, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, TextInput, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import MockDataBadge from '../components/MockDataBadge';
import Toast from '../components/Toast';
import {
  useAcknowledgeAutoFlipNotice,
  useDebtor,
  useRuleForDebtor,
  useSetDebtorRule,
  useSetDebtorTrusted,
} from '../data/hooks';
import { dark, light } from '../theme/colors';
import { formatCurrencyEUR } from '../utils/format';

const TOAST_DURATION_MS = 2000;

// This screen is registered in both RulesStackParamList and
// TransactionsStackParamList (reachable from RulesListScreen and, as a
// read-only debtor's "Manage this debitor" link, from
// TransactionDetailScreen) — see CLAUDE.md/mocks/03-transaction-detail.html.
// A local, minimal param list keeps this screen's prop typing independent
// of either specific parent stack, avoiding a type mismatch (or `as any`)
// from dual registration. The screen only ever calls navigation.goBack(),
// which NavigationProp<ParamListBase> supports.
type DebitorEditParamList = { DebitorEdit: { debtorId: string } };
type Props = {
  route: RouteProp<DebitorEditParamList, 'DebitorEdit'>;
  navigation: NavigationProp<ParamListBase>;
};

export default function DebitorEditScreen({ route, navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const debtor = useDebtor(route.params.debtorId);
  const rule = useRuleForDebtor(route.params.debtorId);
  const setDebtorTrusted = useSetDebtorTrusted();
  const setDebtorRule = useSetDebtorRule();
  const acknowledgeAutoFlipNotice = useAcknowledgeAutoFlipNotice();

  const [amountText, setAmountText] = useState(rule?.amountThresholdEUR?.toString() ?? '');
  const [toastMessage, setToastMessage] = useState<string | null>(null);
  const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  // Reseed the field if the underlying rule changes out from under us
  // (e.g. navigating here for a different debtor while this screen is
  // still mounted, or the rule being cleared elsewhere).
  useEffect(() => {
    setAmountText(rule?.amountThresholdEUR?.toString() ?? '');
  }, [rule]);

  useEffect(() => {
    return () => clearTimeout(toastTimer.current);
  }, []);

  // The user opening this screen is the point a pending passive auto-flip
  // notice (see RulesListScreen's "Auto-marked Bad · Xm ago" marker) has
  // been seen — clear it.
  useEffect(() => {
    acknowledgeAutoFlipNotice(route.params.debtorId);
  }, [route.params.debtorId, acknowledgeAutoFlipNotice]);

  function showToast(message: string) {
    setToastMessage(message);
    clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToastMessage(null), TOAST_DURATION_MS);
  }

  function handleSetTrusted(trusted: boolean) {
    if (!debtor || debtor.trusted === trusted) {
      return;
    }
    const { clearedAmountThresholdEUR } = setDebtorTrusted(debtor.id, trusted);
    if (clearedAmountThresholdEUR != null) {
      showToast(
        `Saved — ${debtor.name} marked ${trusted ? 'Trusted' : 'Bad'}, ${formatCurrencyEUR(clearedAmountThresholdEUR)} alert amount cleared`,
      );
    } else {
      showToast(`Saved — ${debtor.name} marked ${trusted ? 'Trusted' : 'Bad'}`);
    }
  }

  function handleSave() {
    if (!debtor) {
      return;
    }
    const trimmed = amountText.trim();
    let amountThresholdEUR: number | undefined;
    if (trimmed !== '') {
      const parsedAmount = parseFloat(trimmed);
      if (!Number.isFinite(parsedAmount) || parsedAmount <= 0) {
        showToast('Enter a valid positive amount, or leave it blank');
        return;
      }
      amountThresholdEUR = parsedAmount;
    }
    const { autoFlippedTo } = setDebtorRule(debtor.id, { amountThresholdEUR });
    if (autoFlippedTo === 'Bad' && amountThresholdEUR != null) {
      showToast(
        `Saved — ${debtor.name}'s last charge exceeds ${formatCurrencyEUR(amountThresholdEUR)}, marked Bad`,
      );
    } else if (autoFlippedTo === 'Trusted' && amountThresholdEUR != null) {
      showToast(
        `Saved — ${debtor.name}'s last charge is under ${formatCurrencyEUR(amountThresholdEUR)}, marked Trusted`,
      );
    } else {
      showToast('Saved');
    }
  }

  function handleClear() {
    if (!debtor) {
      return;
    }
    setAmountText('');
    setDebtorRule(debtor.id, {});
    showToast('Saved');
  }

  if (!debtor) {
    return (
      <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
        <MockDataBadge />
        <Text style={[styles.hint, styles.notFoundText, { color: theme.textMuted }]}>
          Debitor not found.
        </Text>
      </View>
    );
  }

  const avatarColor = debtor.trusted ? theme.good : theme.bad;
  const avatarBg = debtor.trusted ? theme.goodBg : theme.badBg;

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <MockDataBadge />
      <View style={styles.navBar}>
        <Pressable onPress={() => navigation.goBack()} hitSlop={8}>
          <Text style={[styles.navLink, { color: theme.accent }]}>‹ Back</Text>
        </Pressable>
        <Text style={[styles.navTitle, { color: theme.text }]}>Edit Rule</Text>
        <View style={styles.navSpacer} />
      </View>

      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <View style={[styles.card, styles.summaryCard, { backgroundColor: theme.surface }]}>
          <View style={[styles.avatarLarge, { backgroundColor: avatarBg }]}>
            <Text style={[styles.avatarLargeText, { color: avatarColor }]}>{debtor.initials}</Text>
          </View>
          <Text style={[styles.debtorName, { color: theme.text }]}>{debtor.name}</Text>
        </View>

        <View style={styles.toggleRow}>
          <Pressable
            onPress={() => handleSetTrusted(true)}
            style={[
              styles.toggleBtn,
              { borderColor: theme.border, backgroundColor: theme.surface },
              debtor.trusted && { borderColor: theme.good, backgroundColor: theme.goodBg },
            ]}
          >
            <Text
              style={[
                styles.toggleBtnText,
                { color: theme.textMuted },
                debtor.trusted && { color: theme.good },
              ]}
            >
              Trusted
            </Text>
          </Pressable>
          <Pressable
            onPress={() => handleSetTrusted(false)}
            style={[
              styles.toggleBtn,
              { borderColor: theme.border, backgroundColor: theme.surface },
              !debtor.trusted && { borderColor: theme.bad, backgroundColor: theme.badBg },
            ]}
          >
            <Text
              style={[
                styles.toggleBtnText,
                { color: theme.textMuted },
                !debtor.trusted && { color: theme.bad },
              ]}
            >
              Bad
            </Text>
          </Pressable>
        </View>

        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
          Alert condition (optional)
        </Text>
        <Text style={[styles.hint, { color: theme.textMuted }]}>
          Leave blank to get notified on every charge from this debitor. Must be a positive
          amount.
        </Text>

        <View style={[styles.card, { backgroundColor: theme.surface }]}>
          <View style={styles.infoRow}>
            <Text style={[styles.infoLabel, { color: theme.textMuted }]}>
              Only alert if amount exceeds (EUR)
            </Text>
            <TextInput
              value={amountText}
              onChangeText={setAmountText}
              placeholder="Any amount"
              placeholderTextColor={theme.textMuted}
              keyboardType="decimal-pad"
              style={[styles.input, { color: theme.text, borderColor: theme.border }]}
            />
          </View>
        </View>

        <Pressable
          onPress={handleSave}
          style={[styles.btnPrimary, { backgroundColor: theme.accent }]}
        >
          <Text style={styles.btnPrimaryText}>Save</Text>
        </Pressable>

        <Pressable
          onPress={handleClear}
          style={[styles.btnOutline, { borderColor: theme.border }]}
        >
          <Text style={[styles.btnOutlineText, { color: theme.text }]}>
            Clear alert condition
          </Text>
        </Pressable>
        <Text style={[styles.hint, styles.centerText, { color: theme.textMuted }]}>
          Resets the amount above — you'll be alerted on every charge again. Status (
          {debtor.trusted ? 'Trusted' : 'Bad'}) is unchanged.
        </Text>
      </ScrollView>
      <Toast message={toastMessage} />
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
    position: 'relative',
  },
  navBar: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 12,
  },
  navLink: {
    fontSize: 15,
  },
  navTitle: {
    fontSize: 17,
    fontWeight: '600',
  },
  navSpacer: {
    width: 44,
  },
  content: {
    padding: 16,
    gap: 12,
  },
  card: {
    borderRadius: 14,
    padding: 16,
    gap: 8,
  },
  summaryCard: {
    alignItems: 'center',
  },
  avatarLarge: {
    width: 56,
    height: 56,
    borderRadius: 28,
    alignItems: 'center',
    justifyContent: 'center',
  },
  avatarLargeText: {
    fontWeight: '700',
    fontSize: 18,
  },
  debtorName: {
    fontSize: 18,
    fontWeight: '600',
  },
  hint: {
    fontSize: 12,
    lineHeight: 17,
  },
  notFoundText: {
    padding: 16,
  },
  centerText: {
    textAlign: 'center',
  },
  infoRow: {
    gap: 6,
  },
  infoLabel: {
    fontSize: 12,
  },
  input: {
    fontSize: 15,
    fontWeight: '600',
    borderWidth: 1,
    borderRadius: 8,
    paddingHorizontal: 10,
    paddingVertical: 8,
  },
  sectionLabel: {
    fontSize: 13,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.3,
    marginTop: 8,
    marginHorizontal: 4,
  },
  toggleRow: {
    flexDirection: 'row',
    gap: 8,
  },
  toggleBtn: {
    flex: 1,
    minHeight: 44,
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: 10,
    borderWidth: 1,
  },
  toggleBtnText: {
    fontSize: 14,
    fontWeight: '600',
  },
  btnPrimary: {
    minHeight: 44,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 14,
    marginTop: 8,
  },
  btnPrimaryText: {
    color: '#fff',
    fontSize: 16,
    fontWeight: '600',
  },
  btnOutline: {
    minHeight: 44,
    borderRadius: 12,
    borderWidth: 1,
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 14,
  },
  btnOutlineText: {
    fontSize: 16,
    fontWeight: '600',
  },
});
