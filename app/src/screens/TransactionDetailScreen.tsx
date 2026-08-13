import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React from 'react';
import { Pressable, ScrollView, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import MockDataBadge from '../components/MockDataBadge';
import { useDebtor, useTransaction } from '../data/hooks';
import { TransactionsStackParamList } from '../navigation/types';
import { dark, light } from '../theme/colors';
import { formatCurrencyEUR, formatLongDate, formatTime } from '../utils/format';

type Props = NativeStackScreenProps<TransactionsStackParamList, 'TransactionDetail'>;

export default function TransactionDetailScreen({ route, navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const transaction = useTransaction(route.params.transactionId);
  const debtor = useDebtor(transaction?.debtorId ?? '');

  if (!transaction || !debtor) {
    return (
      <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
        <MockDataBadge />
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
      <MockDataBadge />
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

        {/*
          Display-only: this screen shows the transaction and the debitor's
          current status, but does not edit either. Classification
          (Trusted/Bad) and alert-threshold editing both live on the Edit
          Rule screen now — reached via "Manage this debitor" below — so
          there is exactly one place that owns that state. See
          mocks/03-transaction-detail.html.
        */}
        <Pressable
          onPress={() => navigation.navigate('DebitorEdit', { debtorId: debtor.id })}
          style={[styles.card, styles.manageRow, { backgroundColor: theme.surface }]}
        >
          <View>
            <Text style={[styles.infoLabel, { color: theme.textMuted }]}>Debitor status</Text>
            <Text style={[styles.debtorNameSmall, { color: theme.text }]}>{debtor.name}</Text>
          </View>
          <View style={styles.manageRowRight}>
            <View style={[styles.pill, { backgroundColor: avatarBg }]}>
              <Text style={[styles.pillText, { color: avatarColor }]}>
                {debtor.trusted ? 'Trusted' : 'Bad'}
              </Text>
            </View>
            <Text style={[styles.manageLink, { color: theme.textMuted }]}>Manage ›</Text>
          </View>
        </Pressable>
      </ScrollView>
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
  debtorNameSmall: {
    fontSize: 15,
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
  manageRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  manageRowRight: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
  },
  manageLink: {
    fontSize: 14,
  },
  pill: {
    borderRadius: 999,
    paddingVertical: 3,
    paddingHorizontal: 8,
  },
  pillText: {
    fontSize: 11,
    fontWeight: '600',
  },
});
