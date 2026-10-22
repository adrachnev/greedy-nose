import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { BackHandler, FlatList, Pressable, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import Button from '../components/Button';
import { useSaveRulesBatch } from '../data/hooks';
import { clearOffer, getOffer } from '../data/onboardingOffer';
import { MAX_BATCH_RULES } from '../data/rulesStore';
import { OfferRow } from '../domain/payeeOffer';
import { leaveSettingsForDebitList } from '../navigation/openDebitList';
import { SettingsStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { formatCurrencyEUR, joinMeta } from '../utils/format';

type Props = NativeStackScreenProps<SettingsStackParamList, 'ClassifyPayees'>;

// The one-time offer after a bank's first sync (Story 5.9, mock 01c and 01c#failed).
// It lives only inside the connect flow: the list is kept in memory (onboardingOffer.ts)
// and spent when this screen goes away, by any road. Skip, going back and leaving
// the tab write nothing, the same as "Keep all Bad and start" (R26a). The words
// never name a bank; the bank shown is data.

const rowKey = (row: OfferRow) => row.payee.id;

/** "up to €34.21 · Card payment · 6 charges" (the amounts of one payee may differ). */
function describe(row: OfferRow): string {
  const amount = formatCurrencyEUR(row.amountEUR);
  return joinMeta(
    row.varies ? `up to ${amount}` : amount,
    row.paymentType,
    `${row.charges} ${row.charges === 1 ? 'charge' : 'charges'}`,
  );
}

function Row({ theme, row }: { theme: Theme; row: OfferRow }) {
  return (
    <View style={[styles.row, { backgroundColor: theme.surface }]}>
      <View style={[styles.avatar, { backgroundColor: theme.neutralBg }]}>
        <Text style={[styles.avatarText, { color: theme.textMuted }]}>{row.payee.initials}</Text>
      </View>
      <View style={styles.info}>
        <Text style={[styles.name, { color: theme.text }]} numberOfLines={1}>
          {row.payee.name}
        </Text>
        <Text style={[styles.meta, { color: theme.textMuted }]} numberOfLines={1}>
          {describe(row)}
        </Text>
      </View>
    </View>
  );
}

export default function ClassifyPayeesScreen({ navigation, route }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const saveBatch = useSaveRulesBatch();
  // Read once: the offer is spent with this screen, and a later change of the feed must not reshuffle the list.
  const [rows] = useState(getOffer);
  const [saving, setSaving] = useState(false);
  const [problem, setProblem] = useState<'save' | 'many' | null>(null);
  const busy = useRef(false);
  const mounted = useRef(true);

  // The navigation prop may change identity mid-save; everything below reads the latest through this ref, so
  // no effect restarts (and none skips the leave after a save).
  const navigationRef = useRef(navigation);
  navigationRef.current = navigation;
  const leave = useCallback(() => {
    clearOffer();
    leaveSettingsForDebitList(navigationRef.current);
  }, []);

  // Only on mount and unmount.
  useEffect(() => {
    mounted.current = true;
    // Nothing to offer (a restart lost it): the connect flow is over, the debit list opens.
    if (rows.length === 0) {
      leave();
    }
    return () => {
      mounted.current = false;
      clearOffer();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Going back is "Keep all Bad": it ends in the debit list, not in Settings. Not while the rules are on their way.
  useEffect(() => {
    const subscription = BackHandler.addEventListener('hardwareBackPress', () => {
      if (!navigationRef.current.isFocused()) {
        return false;
      }
      if (!busy.current) {
        leave();
      }
      return true;
    });
    return () => subscription.remove();
  }, [leave]);

  const entries = useMemo(
    () => rows.map(row => ({ payee: row.payee, draft: { classification: 'good' as const } })),
    [rows],
  );

  const markAllGood = async () => {
    if (busy.current) {
      return;
    }
    if (rows.length > MAX_BATCH_RULES) {
      setProblem('many'); // the backend would refuse the whole set; nothing is sent
      return;
    }
    busy.current = true;
    setSaving(true);
    setProblem(null);
    const saved = await saveBatch(entries);
    busy.current = false;
    if (!mounted.current) {
      return;
    }
    setSaving(false);
    if (saved) {
      leave();
    } else {
      setProblem('save');
    }
  };
  const keepAllBad = () => {
    if (!busy.current) {
      leave();
    }
  };

  const hint = `${route.params.bank}: we found ${rows.length} ${
    rows.length === 1 ? 'payee' : 'payees'
  } without a rule in your last 90 days. Until you choose, they stay Bad.`;

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>Your payees</Text>
        <Pressable accessibilityRole="button" hitSlop={10} onPress={keepAllBad}>
          <Text style={[styles.link, { color: theme.accent }]}>Skip</Text>
        </Pressable>
      </View>

      <FlatList
        data={rows}
        keyExtractor={rowKey}
        renderItem={({ item }) => <Row theme={theme} row={item} />}
        ListHeaderComponent={<Text style={[styles.hint, { color: theme.textMuted }]}>{hint}</Text>}
        contentContainerStyle={styles.content}
        initialNumToRender={20}
      />

      <View style={[styles.footer, { paddingBottom: insets.bottom + 16, borderTopColor: theme.border }]}>
        {problem !== null && (
          <View style={[styles.notice, { backgroundColor: theme.badBg, borderLeftColor: theme.bad }]}>
            <Text style={[styles.noticeTitle, { color: theme.bad }]}>Couldn't save</Text>
            <Text style={[styles.noticeText, { color: theme.bad }]}>
              {problem === 'many'
                ? 'Too many payees to sort at once. Keep them all Bad and classify them on the Rules tab.'
                : 'No rule was saved. Check your connection and try again.'}
            </Text>
          </View>
        )}
        <Button label={saving ? 'Saving…' : 'Mark all Good and start'} onPress={markAllGood} />
        <Button variant="outline" label="Keep all Bad and start" onPress={keepAllBad} />
        <Text style={[styles.hint, styles.legend, { color: theme.textMuted }]}>
          <Text style={styles.strong}>Good</Text>: these payees never alert you. <Text style={styles.strong}>Bad</Text>: each of
          them alerts you on its next charge until you mark it Good on the Rules tab.
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1 },
  navBar: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 12,
  },
  navTitle: { fontSize: 20, fontWeight: '600' },
  link: { fontSize: 15, paddingVertical: 8 },
  content: { padding: 16, gap: 12 },
  hint: { fontSize: 12, lineHeight: 17 },
  legend: { textAlign: 'center' },
  strong: { fontWeight: '700' },
  row: { flexDirection: 'row', alignItems: 'center', gap: 12, borderRadius: 12, padding: 14 },
  avatar: { width: 40, height: 40, borderRadius: 20, alignItems: 'center', justifyContent: 'center' },
  avatarText: { fontWeight: '700', fontSize: 13 },
  info: { flex: 1, gap: 2, minWidth: 0 },
  name: { fontSize: 15, fontWeight: '600' },
  meta: { fontSize: 12 },
  footer: { paddingHorizontal: 16, paddingTop: 12, gap: 12, borderTopWidth: StyleSheet.hairlineWidth },
  notice: { borderRadius: 12, borderLeftWidth: 3, padding: 14, gap: 4 },
  noticeTitle: { fontSize: 15, fontWeight: '600' },
  noticeText: { fontSize: 12, lineHeight: 17, opacity: 0.85 },
});
