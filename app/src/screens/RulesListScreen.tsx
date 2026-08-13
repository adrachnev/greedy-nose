import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useMemo, useState } from 'react';
import {
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  useColorScheme,
  View,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import MockDataBadge from '../components/MockDataBadge';
import { useAutoFlipNotices, useDebtors, useRules, useTransactions } from '../data/hooks';
import { AutoFlipNotice, Debtor, Rule } from '../mocks/data';
import { RulesStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { formatCurrencyEUR, formatRelativeTime } from '../utils/format';

type Props = NativeStackScreenProps<RulesStackParamList, 'RulesList'>;

/** Mirrors mocks/04-debitor-rules.html's "Alerts on every charge" subtitle,
 * extended to describe whatever thresholds are actually set. */
function describeAlertCondition(rule: Rule | undefined): string {
  const amount = rule?.amountThresholdEUR;
  if (amount == null) {
    return 'Alerts on every charge';
  }
  return `Alerts if over ${formatCurrencyEUR(amount)}`;
}

function DebtorRow({
  theme,
  debtor,
  subtitle,
  onPress,
}: {
  theme: Theme;
  debtor: Debtor;
  subtitle: string;
  onPress: () => void;
}) {
  const avatarColor = debtor.trusted ? theme.good : theme.bad;
  const avatarBg = debtor.trusted ? theme.goodBg : theme.badBg;
  const pillColor = debtor.trusted ? theme.good : theme.bad;
  const pillBg = debtor.trusted ? theme.goodBg : theme.badBg;

  const rowStyle = [styles.txRow, { backgroundColor: theme.surface }];
  const content = (
    <>
      <View style={[styles.avatar, { backgroundColor: avatarBg }]}>
        <Text style={[styles.avatarText, { color: avatarColor }]}>{debtor.initials}</Text>
      </View>
      <View style={styles.txInfo}>
        <Text style={[styles.txName, { color: theme.text }]} numberOfLines={1}>
          {debtor.name}
        </Text>
        <Text style={[styles.txDate, { color: theme.textMuted }]} numberOfLines={1}>
          {subtitle}
        </Text>
      </View>
      <View style={[styles.pill, { backgroundColor: pillBg }]}>
        <Text style={[styles.pillText, { color: pillColor }]}>
          {debtor.trusted ? 'Trusted' : 'Bad'}
        </Text>
      </View>
    </>
  );

  return (
    <Pressable onPress={onPress} style={rowStyle}>
      {content}
    </Pressable>
  );
}

export default function RulesListScreen({ navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const debtors = useDebtors();
  const transactions = useTransactions();
  const rules = useRules();
  const autoFlipNotices = useAutoFlipNotices();
  const [search, setSearch] = useState('');

  const ruleByDebtorId = useMemo(() => {
    const map = new Map<string, Rule>();
    for (const rule of rules) {
      map.set(rule.debtorId, rule);
    }
    return map;
  }, [rules]);

  const noticeByDebtorId = useMemo(() => {
    const map = new Map<string, AutoFlipNotice>();
    for (const notice of autoFlipNotices) {
      map.set(notice.debtorId, notice);
    }
    return map;
  }, [autoFlipNotices]);

  // PRAGMATIC: the mock's trusted-row subtitle also shows "marked <date>",
  // which isn't modeled anywhere in the fixture layer (no "trusted since"
  // timestamp exists on Debtor). Falling back to a transaction count only —
  // revisit once real debtor data carries a trust-change timestamp.
  const transactionCountByDebtor = useMemo(() => {
    const counts = new Map<string, number>();
    for (const t of transactions) {
      counts.set(t.debtorId, (counts.get(t.debtorId) ?? 0) + 1);
    }
    return counts;
  }, [transactions]);

  const isSearching = search.trim().length > 0;

  const filtered = useMemo(() => {
    const query = search.trim().toLowerCase();
    if (!query) {
      return debtors;
    }
    return debtors.filter(d => d.name.toLowerCase().includes(query));
  }, [debtors, search]);

  const badDebtors = filtered.filter(d => !d.trusted);
  const trustedDebtors = filtered.filter(d => d.trusted);

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <MockDataBadge />
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>Rules</Text>
      </View>

      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <Text style={[styles.hint, { color: theme.textMuted }]}>
          Every debitor is Bad by default and alerts you on any new charge, until you mark it
          Trusted.
        </Text>

        <View style={[styles.searchBar, { backgroundColor: theme.neutralBg }]}>
          <TextInput
            value={search}
            onChangeText={setSearch}
            placeholder="Search debitors…"
            placeholderTextColor={theme.textMuted}
            style={[styles.searchInput, { color: theme.text }]}
            autoCapitalize="none"
            autoCorrect={false}
          />
        </View>

        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
          Bad ({badDebtors.length})
        </Text>
        {badDebtors.length === 0 ? (
          <Text style={[styles.emptyNote, { color: theme.textMuted }]}>
            {isSearching
              ? 'No bad debitors match your search.'
              : "No bad debitors yet — they'll appear here as soon as a transaction comes in."}
          </Text>
        ) : (
          badDebtors.map(debtor => {
            const notice = noticeByDebtorId.get(debtor.id);
            const subtitle = notice
              ? `Auto-marked Bad · ${formatRelativeTime(notice.timestamp)}`
              : describeAlertCondition(ruleByDebtorId.get(debtor.id));
            return (
              <DebtorRow
                key={debtor.id}
                theme={theme}
                debtor={debtor}
                subtitle={subtitle}
                onPress={() => navigation.navigate('DebitorEdit', { debtorId: debtor.id })}
              />
            );
          })
        )}

        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
          Trusted ({trustedDebtors.length})
        </Text>
        {trustedDebtors.length === 0 ? (
          <Text style={[styles.emptyNote, { color: theme.textMuted }]}>
            {isSearching
              ? 'No trusted debitors match your search.'
              : 'Debitors you mark Trusted from a transaction show up here.'}
          </Text>
        ) : (
          trustedDebtors.map(debtor => {
            const count = transactionCountByDebtor.get(debtor.id) ?? 0;
            return (
              <DebtorRow
                key={debtor.id}
                theme={theme}
                debtor={debtor}
                subtitle={`${count} transaction${count === 1 ? '' : 's'}`}
                onPress={() => navigation.navigate('DebitorEdit', { debtorId: debtor.id })}
              />
            );
          })
        )}
      </ScrollView>
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
  hint: {
    fontSize: 12,
    lineHeight: 17,
  },
  searchBar: {
    flexDirection: 'row',
    alignItems: 'center',
    borderRadius: 14,
    paddingHorizontal: 14,
    paddingVertical: 4,
  },
  searchInput: {
    flex: 1,
    fontSize: 15,
    minHeight: 40,
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
  emptyNote: {
    fontSize: 13,
    textAlign: 'center',
    padding: 24,
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
