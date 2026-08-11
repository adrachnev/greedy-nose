import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useEffect, useRef, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import Toast from '../components/Toast';
import { useDebtor, useRuleForDebtor, useSetDebtorTrusted, useTransaction } from '../data/hooks';
import { TransactionsStackParamList } from '../navigation/types';
import { dark, light } from '../theme/colors';
import { formatCurrencyEUR, formatLongDate, formatTime } from '../utils/format';

const TOAST_DURATION_MS = 2000;

type Props = NativeStackScreenProps<TransactionsStackParamList, 'TransactionDetail'>;

export default function TransactionDetailScreen({ route, navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const transaction = useTransaction(route.params.transactionId);
  const debtor = useDebtor(transaction?.debtorId ?? '');
  const rule = useRuleForDebtor(transaction?.debtorId ?? '');
  const setDebtorTrusted = useSetDebtorTrusted();
  const [toastMessage, setToastMessage] = useState<string | null>(null);
  const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(() => {
    return () => clearTimeout(toastTimer.current);
  }, []);

  function handleSetTrusted(trusted: boolean) {
    if (!debtor || debtor.trusted === trusted) {
      return;
    }
    setDebtorTrusted(debtor.id, trusted);
    setToastMessage(`Saved — ${debtor.name} marked ${trusted ? 'Trusted' : 'Bad'}`);
    clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToastMessage(null), TOAST_DURATION_MS);
  }

  if (!transaction || !debtor) {
    return (
      <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
        <Text style={[styles.hint, styles.notFoundText, { color: theme.textMuted }]}>
          Transaction not found.
        </Text>
      </View>
    );
  }

  const avatarColor = debtor.trusted ? theme.good : theme.bad;
  const avatarBg = debtor.trusted ? theme.goodBg : theme.badBg;

  return (
    <View
      style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}
    >
      <View style={styles.navBar}>
        <Pressable onPress={() => navigation.goBack()} hitSlop={8}>
          <Text style={[styles.navLink, { color: theme.accent }]}>‹ Back</Text>
        </Pressable>
        <Text style={[styles.navTitle, { color: theme.text }]}>Transaction</Text>
        <View style={styles.navSpacer} />
      </View>

      <ScrollView contentContainerStyle={styles.content}>
        <View style={[styles.card, styles.summaryCard, { backgroundColor: theme.surface }]}>
          <View style={[styles.avatarLarge, { backgroundColor: avatarBg }]}>
            <Text style={[styles.avatarLargeText, { color: avatarColor }]}>
              {debtor.initials}
            </Text>
          </View>
          <Text style={[styles.debtorName, { color: theme.text }]}>{debtor.name}</Text>
          <Text style={[styles.amount, { color: theme.text }]}>
            {formatCurrencyEUR(transaction.amountEUR)}
          </Text>
          <Text style={[styles.hint, { color: theme.textMuted }]}>
            {transaction.paymentType} · {formatLongDate(transaction.timestamp)} ·{' '}
            {formatTime(transaction.timestamp)}
          </Text>
        </View>

        <View style={[styles.card, { backgroundColor: theme.surface }]}>
          <View style={styles.infoRow}>
            <Text style={[styles.infoLabel, { color: theme.textMuted }]}>Debitor IBAN</Text>
            <Text style={[styles.infoValue, { color: theme.text }]}>{debtor.iban}</Text>
          </View>
          <View style={[styles.divider, { backgroundColor: theme.border }]} />
          <View style={styles.infoRow}>
            <Text style={[styles.infoLabel, { color: theme.textMuted }]}>Reference</Text>
            <Text style={[styles.infoValue, { color: theme.text }]}>{transaction.reference}</Text>
          </View>
        </View>

        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
          Mark this debitor
        </Text>
        <Text style={[styles.hint, { color: theme.textMuted }]}>
          This applies to <Text style={styles.bold}>all</Text> current and future transactions
          from {debtor.name} — not just this one.
        </Text>

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
          Alert conditions (optional)
        </Text>
        <Text style={[styles.hint, { color: theme.textMuted }]}>
          Leave blank to get notified on every charge from this debitor.
        </Text>

        <View style={[styles.card, { backgroundColor: theme.surface }]}>
          <View style={styles.infoRow}>
            <Text style={[styles.infoLabel, { color: theme.textMuted }]}>
              Only alert if amount exceeds
            </Text>
            <Text style={[styles.infoValue, { color: theme.text }]}>
              {rule?.amountThresholdEUR != null
                ? formatCurrencyEUR(rule.amountThresholdEUR)
                : '— (every charge)'}
            </Text>
          </View>
          <View style={[styles.divider, { backgroundColor: theme.border }]} />
          <View style={styles.infoRow}>
            <Text style={[styles.infoLabel, { color: theme.textMuted }]}>
              Only alert if charged more than
            </Text>
            <Text style={[styles.infoValue, { color: theme.text }]}>
              {rule?.frequencyThreshold
                ? `${rule.frequencyThreshold.count}x per ${rule.frequencyThreshold.period}`
                : '— (every charge)'}
            </Text>
          </View>
        </View>
        <Text style={[styles.hint, { color: theme.textMuted }]}>
          If you set both, <Text style={styles.bold}>both</Text> must be true to trigger an
          alert (AND).
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
  amount: {
    fontSize: 22,
    fontWeight: '600',
  },
  hint: {
    fontSize: 12,
    lineHeight: 17,
  },
  notFoundText: {
    padding: 16,
  },
  bold: {
    fontWeight: '700',
  },
  infoRow: {
    gap: 2,
  },
  infoLabel: {
    fontSize: 12,
  },
  infoValue: {
    fontSize: 14,
    fontWeight: '600',
  },
  divider: {
    height: 1,
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
});
