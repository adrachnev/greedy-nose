import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useMemo } from 'react';
import { Pressable, SectionList, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import MockDataBadge from '../components/MockDataBadge';
import { useDebits, usePayee, useRuleByPayeeId } from '../data/hooks';
import { classifyDebit, classifyPayee } from '../domain/classification';
import { Debit, Rule } from '../domain/model';
import { DebitsStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { formatCurrencyEUR, formatTime, groupByDateSection } from '../utils/format';

type Props = NativeStackScreenProps<DebitsStackParamList, 'DebitList'>;

/**
 * Two verdicts share this row, and they are not the same thing (R5):
 *
 * - the **avatar** shows the *payee's* classification — is this party good?
 * - the **amount + pill** show the *debit's* — is this charge good?
 *
 * They differ exactly when a good payee exceeds their limit, which is the
 * subtle half of R5 and the reason the row is drawn this way. See
 * mocks/02-debit-list.html's REWE row.
 */
function DebitRow({
  theme,
  debit,
  rule,
  onPress,
}: {
  theme: Theme;
  debit: Debit;
  rule: Rule | undefined;
  onPress: () => void;
}) {
  const payee = usePayee(debit.payeeId);
  if (!payee) {
    return null;
  }
  const payeeIsGood = classifyPayee(rule) === 'good';
  const debitIsBad = classifyDebit(debit, rule).classification === 'bad';

  const avatarColor = payeeIsGood ? theme.good : theme.bad;
  const avatarBg = payeeIsGood ? theme.goodBg : theme.badBg;

  return (
    <Pressable onPress={onPress} style={[styles.debitRow, { backgroundColor: theme.surface }]}>
      <View style={[styles.avatar, { backgroundColor: avatarBg }]}>
        <Text style={[styles.avatarText, { color: avatarColor }]}>{payee.initials}</Text>
      </View>
      <View style={styles.debitInfo}>
        <Text style={[styles.debitName, { color: theme.text }]} numberOfLines={1}>
          {payee.name}
        </Text>
        <Text style={[styles.debitDate, { color: theme.textMuted }]}>
          {debit.paymentType} · {formatTime(debit.timestamp)}
        </Text>
      </View>
      <View style={styles.debitRight}>
        <Text style={[styles.debitAmount, { color: debitIsBad ? theme.bad : theme.text }]}>
          {formatCurrencyEUR(debit.amountEUR)}
        </Text>
        {debitIsBad && (
          <View style={[styles.pill, { backgroundColor: theme.badBg }]}>
            <Text style={[styles.pillText, { color: theme.bad }]}>Bad</Text>
          </View>
        )}
      </View>
    </Pressable>
  );
}

export default function DebitListScreen({ navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const debits = useDebits();
  const ruleByPayeeId = useRuleByPayeeId();

  // Rebuilt only when the debits themselves change: grouping sorts the whole
  // list and constructs a Date per item, and doing that inline handed
  // SectionList a brand-new `sections` array on every render (a rule edit, a
  // theme change), so it could never bail out of re-rendering rows.
  const sections = useMemo(
    () =>
      groupByDateSection(debits, d => d.timestamp).map(section => ({
        title: section.label,
        data: section.data,
      })),
    [debits],
  );

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <MockDataBadge />
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>Debits</Text>
      </View>

      <SectionList
        contentContainerStyle={styles.content}
        sections={sections}
        keyExtractor={item => item.id}
        stickySectionHeadersEnabled={false}
        renderSectionHeader={({ section }) => (
          <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>{section.title}</Text>
        )}
        renderItem={({ item }) => (
          <DebitRow
            theme={theme}
            debit={item}
            rule={ruleByPayeeId.get(item.payeeId)}
            onPress={() => navigation.navigate('DebitDetail', { debitId: item.id })}
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
  debitRow: {
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
  debitInfo: {
    flex: 1,
    flexDirection: 'column',
    gap: 2,
    minWidth: 0,
  },
  debitName: {
    fontSize: 15,
    fontWeight: '600',
  },
  debitDate: {
    fontSize: 12,
  },
  debitRight: {
    alignItems: 'flex-end',
    gap: 4,
  },
  debitAmount: {
    fontSize: 15,
    fontWeight: '600',
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
