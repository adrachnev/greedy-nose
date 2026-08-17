import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useMemo } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import MockDataBadge from '../components/MockDataBadge';
import SearchField from '../components/SearchField';
import { usePayees, useRuleByPayeeId } from '../data/hooks';
import { classifyPayee } from '../domain/classification';
import { Payee, Rule } from '../domain/model';
import { useTabScopedSearch } from '../hooks/useTabScopedSearch';
import { RulesStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { formatCurrencyEUR } from '../utils/format';
import { matchesNameSearch } from '../utils/search';

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
  const { search, setSearch } = useTabScopedSearch();

  const isSearching = search.trim().length > 0;

  // Name only (R23) — a limit is not something anyone searches for, and
  // matching amounts here would silently make "30" mean two different things
  // across the two lists. The matcher is shared with the debit list, so both
  // are equally tolerant of umlauts and case (R23a).
  const filtered = useMemo(
    () => (isSearching ? payees.filter(p => matchesNameSearch(search, p.name)) : payees),
    [payees, search, isSearching],
  );

  const badPayees = filtered.filter(p => classifyPayee(ruleByPayeeId.get(p.id)) === 'bad');
  const goodPayees = filtered.filter(p => classifyPayee(ruleByPayeeId.get(p.id)) === 'good');

  // R23: a search that matches nothing says so, and names the query — one
  // message for the screen, not one per section. Two notes reading "no bad
  // payees match" and "no good payees match" describe the sections rather than
  // the search, and between them they never mention what was typed.
  //
  // Only while searching: an account with genuinely no payees is a different
  // state, and its per-section notes below say something more useful.
  const query = search.trim();
  const noMatches = isSearching && badPayees.length === 0 && goodPayees.length === 0;
  const noMatchText = `No payees match “${query}”.`;

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <MockDataBadge />
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>Rules</Text>
      </View>

      {/* Pinned outside the ScrollView, like the debit list's — which pushes
          the hint below it, into the scroll area where it belongs: the box is
          chrome, the hint is content. */}
      <SearchField value={search} onChangeText={setSearch} placeholder="Search payees…" />

      <ScrollView
        contentContainerStyle={[styles.content, noMatches && styles.contentCentered]}
        keyboardShouldPersistTaps="handled"
      >
        {noMatches ? (
          <Text style={[styles.emptyNote, { color: theme.textMuted }]}>{noMatchText}</Text>
        ) : (
          <>
            <Text style={[styles.hint, { color: theme.textMuted }]}>
              Every payee is bad by default and alerts you on any new charge, until you mark them
              good.
            </Text>

            <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
              Bad ({badPayees.length})
            </Text>
            {/* Reachable while searching only when the *other* section has
                hits — "Bad (0), no bad payees match" next to a good payee the
                search did find is informative; on its own it is not, which is
                what the whole-screen message above handles. */}
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
          </>
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
    // Same 12px rhythm as the debit list and mocks/style.css's `.content`;
    // the row carries no bottom margin, or the two would add up.
    gap: 12,
  },
  // Only applied while the no-match message is the whole body: flexGrow is
  // what gives a ScrollView's content something to centre inside.
  contentCentered: {
    flexGrow: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },
  hint: {
    fontSize: 12,
    lineHeight: 17,
  },
  sectionLabel: {
    fontSize: 13,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.3,
    marginTop: 8,
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
