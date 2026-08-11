import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React from 'react';
import { Pressable, SectionList, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import MockDataBadge from '../components/MockDataBadge';
import { useDebtor, useTransactions } from '../data/hooks';
import { TransactionsStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { Transaction } from '../mocks/data';
import { formatCurrencyEUR, formatTime, groupByDateSection } from '../utils/format';

type Props = NativeStackScreenProps<TransactionsStackParamList, 'TransactionList'>;

function TransactionRow({
  theme,
  transaction,
  onPress,
}: {
  theme: Theme;
  transaction: Transaction;
  onPress: () => void;
}) {
  const debtor = useDebtor(transaction.debtorId);
  if (!debtor) {
    return null;
  }
  const avatarColor = debtor.trusted ? theme.good : theme.bad;
  const avatarBg = debtor.trusted ? theme.goodBg : theme.badBg;

  return (
    <Pressable
      onPress={onPress}
      style={[styles.txRow, { backgroundColor: theme.surface }]}
    >
      <View style={[styles.avatar, { backgroundColor: avatarBg }]}>
        <Text style={[styles.avatarText, { color: avatarColor }]}>
          {debtor.initials}
        </Text>
      </View>
      <View style={styles.txInfo}>
        <Text style={[styles.txName, { color: theme.text }]} numberOfLines={1}>
          {debtor.name}
        </Text>
        <Text style={[styles.txDate, { color: theme.textMuted }]}>
          {transaction.paymentType} · {formatTime(transaction.timestamp)}
        </Text>
      </View>
      <Text style={[styles.txAmount, { color: theme.text }]}>
        {formatCurrencyEUR(transaction.amountEUR)}
      </Text>
    </Pressable>
  );
}

export default function TransactionListScreen({ navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const transactions = useTransactions();

  const sections = groupByDateSection(transactions, t => t.timestamp).map(
    section => ({ title: section.label, data: section.data }),
  );

  return (
    <View
      style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}
    >
      <MockDataBadge />
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>Transactions</Text>
      </View>

      <SectionList
        contentContainerStyle={styles.content}
        sections={sections}
        keyExtractor={item => item.id}
        stickySectionHeadersEnabled={false}
        renderSectionHeader={({ section }) => (
          <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
            {section.title}
          </Text>
        )}
        renderItem={({ item }) => (
          <TransactionRow
            theme={theme}
            transaction={item}
            onPress={() =>
              navigation.navigate('TransactionDetail', { transactionId: item.id })
            }
          />
        )}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
  },
  navBar: {
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 12,
  },
  navTitle: {
    fontSize: 20,
    fontWeight: '600',
  },
  content: {
    padding: 16,
    gap: 8,
  },
  sectionLabel: {
    fontSize: 13,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.3,
    marginTop: 8,
    marginBottom: 4,
    marginHorizontal: 4,
  },
  txRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    borderRadius: 12,
    padding: 14,
    marginBottom: 8,
  },
  avatar: {
    width: 40,
    height: 40,
    borderRadius: 20,
    alignItems: 'center',
    justifyContent: 'center',
  },
  avatarText: {
    fontWeight: '700',
    fontSize: 13,
  },
  txInfo: {
    flex: 1,
    flexDirection: 'column',
    gap: 2,
    minWidth: 0,
  },
  txName: {
    fontSize: 15,
    fontWeight: '600',
  },
  txDate: {
    fontSize: 12,
  },
  txAmount: {
    fontSize: 15,
    fontWeight: '600',
  },
});
