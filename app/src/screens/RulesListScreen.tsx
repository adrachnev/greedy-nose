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
import { usePayees, useRuleByPayeeId } from '../data/hooks';
import { classifyPayee } from '../domain/classification';
import { Payee, Rule } from '../domain/model';
import { RulesStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { formatCurrencyEUR } from '../utils/format';

type Props = NativeStackScreenProps<RulesStackParamList, 'RulesList'>;

/**
 * The row subtitle from mocks/04-payee-rules.html — what the rule actually
 * does, worded as R5's three outcomes:
 *
 * - bad (or unreviewed, which is the same verdict — R4b): alerts on everything
 * - good with no limit: alerts on nothing at all (R4a)
 * - good with a limit: alerts only above it (R5a — the limit itself is good)
 *
 * The middle case is the one worth stating plainly. "Good, no limit" is the
 * *quietest* setting in the app, and describing it as "alerts on every charge"
 * — as this did — tells the user the exact opposite of what it does.
 */
function describeAlertCondition(rule: Rule | undefined): string {
  if (classifyPayee(rule) === 'bad') {
    return 'Alerts on every charge';
  }
  if (rule?.amountEUR == null) {
    return 'Never alerts';
  }
  return `Alerts above ${formatCurrencyEUR(rule.amountEUR)}`;
}

function PayeeRow({
  theme,
  payee,
  isGood,
  subtitle,
  onPress,
}: {
  theme: Theme;
  payee: Payee;
  isGood: boolean;
  subtitle: string;
  onPress: () => void;
}) {
  const color = isGood ? theme.good : theme.bad;
  const bg = isGood ? theme.goodBg : theme.badBg;

  return (
    <Pressable onPress={onPress} style={[styles.payeeRow, { backgroundColor: theme.surface }]}>
      <View style={[styles.avatar, { backgroundColor: bg }]}>
        <Text style={[styles.avatarText, { color }]}>{payee.initials}</Text>
      </View>
      <View style={styles.payeeInfo}>
        <Text style={[styles.payeeName, { color: theme.text }]} numberOfLines={1}>
          {payee.name}
        </Text>
        <Text style={[styles.payeeSubtitle, { color: theme.textMuted }]} numberOfLines={1}>
          {subtitle}
        </Text>
      </View>
      <View style={[styles.pill, { backgroundColor: bg }]}>
        <Text style={[styles.pillText, { color }]}>{isGood ? 'Good' : 'Bad'}</Text>
      </View>
    </Pressable>
  );
}

export default function RulesListScreen({ navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const payees = usePayees();
  const ruleByPayeeId = useRuleByPayeeId();
  const [search, setSearch] = useState('');

  const isSearching = search.trim().length > 0;

  const filtered = useMemo(() => {
    const query = search.trim().toLowerCase();
    if (!query) {
      return payees;
    }
    return payees.filter(p => p.name.toLowerCase().includes(query));
  }, [payees, search]);

  const badPayees = filtered.filter(p => classifyPayee(ruleByPayeeId.get(p.id)) === 'bad');
  const goodPayees = filtered.filter(p => classifyPayee(ruleByPayeeId.get(p.id)) === 'good');

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <MockDataBadge />
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>Rules</Text>
      </View>

      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <Text style={[styles.hint, { color: theme.textMuted }]}>
          Every payee is bad by default and alerts you on any new charge, until you mark them
          good.
        </Text>

        <View style={[styles.searchBar, { backgroundColor: theme.neutralBg }]}>
          <TextInput
            value={search}
            onChangeText={setSearch}
            placeholder="Search payees…"
            placeholderTextColor={theme.textMuted}
            style={[styles.searchInput, { color: theme.text }]}
            autoCapitalize="none"
            autoCorrect={false}
          />
        </View>

        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
          Bad ({badPayees.length})
        </Text>
        {badPayees.length === 0 ? (
          <Text style={[styles.emptyNote, { color: theme.textMuted }]}>
            {isSearching
              ? 'No bad payees match your search.'
              : "No bad payees yet — they'll appear here as soon as a charge comes in."}
          </Text>
        ) : (
          badPayees.map(payee => (
            <PayeeRow
              key={payee.id}
              theme={theme}
              payee={payee}
              isGood={false}
              subtitle={describeAlertCondition(ruleByPayeeId.get(payee.id))}
              onPress={() => navigation.navigate('PayeeEdit', { payeeId: payee.id })}
            />
          ))
        )}

        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
          Good ({goodPayees.length})
        </Text>
        {goodPayees.length === 0 ? (
          <Text style={[styles.emptyNote, { color: theme.textMuted }]}>
            {isSearching
              ? 'No good payees match your search.'
              : 'Payees you mark good show up here.'}
          </Text>
        ) : (
          goodPayees.map(payee => (
            <PayeeRow
              key={payee.id}
              theme={theme}
              payee={payee}
              isGood
              subtitle={describeAlertCondition(ruleByPayeeId.get(payee.id))}
              onPress={() => navigation.navigate('PayeeEdit', { payeeId: payee.id })}
            />
          ))
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
  payeeRow: {
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
  payeeInfo: {
    flex: 1,
    flexDirection: 'column',
    gap: 2,
    minWidth: 0,
  },
  payeeName: {
    fontSize: 15,
    fontWeight: '600',
  },
  payeeSubtitle: {
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
