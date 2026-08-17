import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useCallback, useMemo } from 'react';
import { Pressable, SectionList, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import MockDataBadge from '../components/MockDataBadge';
import SearchField from '../components/SearchField';
import { useDebits, usePayees, useRuleByPayeeId } from '../data/hooks';
import { classifyDebit, classifyPayee } from '../domain/classification';
import { Debit, Payee, Rule } from '../domain/model';
import { dateFromDateKey, useCurrentDateKey } from '../hooks/useCurrentDateKey';
import { useTabScopedSearch } from '../hooks/useTabScopedSearch';
import { DebitsStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import {
  DateSection,
  formatCurrencyEUR,
  formatDayShort,
  formatTime,
  groupByDateSection,
} from '../utils/format';
import { matchesDebitSearch } from '../utils/search';

type Props = NativeStackScreenProps<DebitsStackParamList, 'DebitList'>;

/** Module-level, so the list never sees a new function for it. */
const keyExtractor = (debit: Debit) => debit.id;

/**
 * Two verdicts share this row, and they are not the same thing (R5):
 *
 * - the **avatar** shows the *payee's* classification — is this party good?
 * - the **amount + pill** show the *debit's* — is this charge good?
 *
 * They differ exactly when a good payee exceeds their limit, which is the
 * subtle half of R5 and the reason the row is drawn this way. See
 * mocks/02-debit-list.html's Bäckerei Müller rows.
 *
 * The payee is passed in rather than looked up here: the list needs every
 * payee's name anyway to filter on it, so a per-row store subscription would
 * be a second source for something the screen already has.
 *
 * Memoized, and that only works because every prop is referentially stable:
 * the theme is a module constant, the payee and rule come out of memoized
 * maps, and `onPress` takes the debit id rather than closing over it — an
 * inline `() => navigate(id)` would be a new function per render and would
 * defeat the memo on every keystroke, which is exactly the cost removing the
 * per-row subscription was meant to avoid.
 */
function DebitRowView({
  theme,
  debit,
  payee,
  rule,
  showDay,
  onPress,
}: {
  theme: Theme;
  debit: Debit;
  payee: Payee;
  rule: Rule | undefined;
  /** Rows under a month header carry the day; Today/Yesterday rows do not. */
  showDay: boolean;
  onPress: (debitId: string) => void;
}) {
  const payeeIsGood = classifyPayee(rule) === 'good';
  const debitIsBad = classifyDebit(debit, rule).classification === 'bad';

  const avatarColor = payeeIsGood ? theme.good : theme.bad;
  const avatarBg = payeeIsGood ? theme.goodBg : theme.badBg;

  const when = showDay
    ? `${formatDayShort(debit.timestamp)}, ${formatTime(debit.timestamp)}`
    : formatTime(debit.timestamp);

  return (
    <Pressable
      onPress={() => onPress(debit.id)}
      style={[styles.debitRow, { backgroundColor: theme.surface }]}
    >
      <View style={[styles.avatar, { backgroundColor: avatarBg }]}>
        <Text style={[styles.avatarText, { color: avatarColor }]}>{payee.initials}</Text>
      </View>
      <View style={styles.debitInfo}>
        <Text style={[styles.debitName, { color: theme.text }]} numberOfLines={1}>
          {payee.name}
        </Text>
        <Text style={[styles.debitDate, { color: theme.textMuted }]}>
          {debit.paymentType} · {when}
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

/**
 * The name the screen uses. Split from the function above only so the two do
 * not shadow each other — `React.memo(function DebitRow…)` reads better but
 * declares the name twice.
 */
const DebitRow = React.memo(DebitRowView);

export default function DebitListScreen({ navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const debits = useDebits();
  const payees = usePayees();
  const ruleByPayeeId = useRuleByPayeeId();
  const { search, setSearch } = useTabScopedSearch();
  const dateKey = useCurrentDateKey();

  const payeeById = useMemo(() => new Map(payees.map(p => [p.id, p])), [payees]);

  // Filter, then group — never the other way round, because R23 requires the
  // date grouping to survive filtering: searching one subscription must still
  // show one row per month under its month header, since the rhythm of the
  // charge is the thing worth seeing.
  //
  // Memoized because grouping sorts the whole list and builds a Date per item.
  // `dateKey` is a real dependency, not a tripwire: grouping is relative to the
  // current *day*, so this recomputes at midnight and the Today/Yesterday
  // labels cannot go stale in a long-lived session.
  const sections = useMemo(() => {
    const matching = debits.filter(debit => {
      const payee = payeeById.get(debit.payeeId);
      return payee != null && matchesDebitSearch(search, payee.name, debit.amountEUR);
    });
    return groupByDateSection(matching, d => d.timestamp, dateFromDateKey(dateKey));
  }, [debits, payeeById, search, dateKey]);

  const query = search.trim();
  // R23: a search that matches nothing has to say so, and name the query. An
  // empty body on its own reads as "no debits", which is the picture a dead
  // bank connection paints (R19) — and here it would be a lie, since the list
  // is full, just filtered. Built as a string rather than inline JSX so the
  // typographic quotes stay out of the markup.
  const noMatchText = `No debits match “${query}”.`;

  const openDebit = useCallback(
    (debitId: string) => navigation.navigate('DebitDetail', { debitId }),
    [navigation],
  );

  // Hoisted out of the JSX, and deliberately not dependent on `search`: a
  // fresh renderItem on every keystroke re-renders every visible row, which
  // would give back most of what dropping the per-row subscription bought.
  const renderItem = useCallback<
    NonNullable<React.ComponentProps<typeof SectionList<Debit, DateSection<Debit>>>['renderItem']>
  >(
    ({ item, section }) => {
      const payee = payeeById.get(item.payeeId);
      if (!payee) {
        return null;
      }
      return (
        <DebitRow
          theme={theme}
          debit={item}
          payee={payee}
          rule={ruleByPayeeId.get(item.payeeId)}
          showDay={section.kind === 'month'}
          onPress={openDebit}
        />
      );
    },
    [theme, payeeById, ruleByPayeeId, openDebit],
  );

  const renderSectionHeader = useCallback(
    ({ section }: { section: DateSection<Debit> }) => (
      <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>{section.label}</Text>
    ),
    [theme],
  );

  /**
   * The no-match message rides on the list rather than replacing it, which is
   * what keeps the scroll offset: swapping the SectionList for a plain View
   * unmounts it, so deleting one character out of a transiently-empty query
   * threw the reader back to the top of a year of history.
   *
   * Renders nothing when there is no query — an account with genuinely no
   * debits is a different state (mocks/02b), and it is not ported yet.
   */
  const renderEmpty = useCallback(() => {
    if (query === '') {
      return null;
    }
    return (
      <View style={styles.emptyBody}>
        <Text style={[styles.emptyNote, { color: theme.textMuted }]}>{noMatchText}</Text>
      </View>
    );
  }, [query, noMatchText, theme]);

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <MockDataBadge />
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>Debits</Text>
      </View>

      <SearchField value={search} onChangeText={setSearch} placeholder="Search debits…" />

      <SectionList
        contentContainerStyle={styles.content}
        sections={sections}
        keyExtractor={keyExtractor}
        stickySectionHeadersEnabled={false}
        keyboardShouldPersistTaps="handled"
        renderSectionHeader={renderSectionHeader}
        renderItem={renderItem}
        ListEmptyComponent={renderEmpty}
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
    // 12px between rows, as in mocks/style.css's `.content` — the row itself
    // carries no bottom margin, or the two would add up.
    gap: 12,
    // Lets ListEmptyComponent centre itself in the viewport; no effect once
    // the list has rows.
    flexGrow: 1,
  },
  emptyBody: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },
  emptyNote: {
    fontSize: 13,
    textAlign: 'center',
  },
  sectionLabel: {
    fontSize: 13,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.3,
    marginTop: 8,
    marginHorizontal: 4,
  },
  debitRow: {
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
