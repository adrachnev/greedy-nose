import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React from 'react';
import { Pressable, ScrollView, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import Button from '../components/Button';
import DataSourceBadge from '../components/DataSourceBadge';
import { useFirstSyncRetry, useMyBanks, useMyBanksPolling } from '../data/hooks';
import { canRetryFirstSync, MyBank, MyBankState, refreshMyBanks } from '../data/myBanksStore';
import { toInitials } from '../domain/payees';
import { SettingsStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { formatDeviceDate, joinMeta } from '../utils/format';

type Props = NativeStackScreenProps<SettingsStackParamList, 'SettingsHome'>;

// The cards of mocks/06-settings.html. Delete (Epic 6) and Reconnect (Epic 7) are
// not here yet; a card reports its state and, after a failed first sync, offers
// "Try again" (Story 5.8).

const PILL: Record<MyBankState, { label: string; tone: 'good' | 'bad' | 'neutral' }> = {
  connected: { label: 'Active', tone: 'good' },
  syncing: { label: 'Syncing', tone: 'neutral' },
  sync_failed: { label: 'Sync failed', tone: 'bad' },
  expired: { label: 'Expired', tone: 'bad' },
};

/** The second line of a card: the account, then what the state means for the user. */
export function describeBank(bank: MyBank): string {
  const until = formatDeviceDate(bank.validUntil);
  const status = {
    connected: until ? `access valid until ${until}` : 'access valid',
    syncing: 'fetching your debits',
    sync_failed: "couldn't fetch your debits",
    expired: 'access expired',
  }[bank.state];
  // Without an account name the status opens the line, so it takes a capital.
  return joinMeta(bank.account, bank.account ? status : status.charAt(0).toUpperCase() + status.slice(1));
}

function BankCard({ theme, bank }: { theme: Theme; bank: MyBank }) {
  const pill = PILL[bank.state];
  const color = { good: theme.good, bad: theme.bad, neutral: theme.textMuted }[pill.tone];
  const background = { good: theme.goodBg, bad: theme.badBg, neutral: theme.neutralBg }[pill.tone];
  const { retry, couldNot } = useFirstSyncRetry();

  return (
    <View style={[styles.card, { backgroundColor: theme.surface }]}>
      <View style={styles.cardRow}>
        <View style={[styles.logo, { backgroundColor: theme.neutralBg }]}>
          <Text style={[styles.logoText, { color: theme.textMuted }]}>{toInitials(bank.name)}</Text>
        </View>
        <View style={styles.info}>
          <Text style={[styles.name, { color: theme.text }]} numberOfLines={1}>
            {bank.name}
          </Text>
          <Text style={[styles.meta, { color: theme.textMuted }]}>{describeBank(bank)}</Text>
        </View>
        <View style={[styles.pill, { backgroundColor: background }]}>
          <Text style={[styles.pillText, { color }]}>{pill.label}</Text>
        </View>
      </View>
      {canRetryFirstSync(bank) && (
        <>
          <View style={[styles.divider, { backgroundColor: theme.border }]} />
          <View style={styles.actions}>
            <Pressable accessibilityRole="button" onPress={() => retry(bank.name)}>
              <Text style={[styles.action, { color: theme.accent }]}>Try again</Text>
            </Pressable>
            {couldNot && <Text style={[styles.meta, { color: theme.bad }]}>Couldn't try again just now</Text>}
          </View>
        </>
      )}
    </View>
  );
}

export default function SettingsScreen({ navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const { status, banks } = useMyBanks();
  useMyBanksPolling();

  const connect = () => navigation.navigate('SelectBank');

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <DataSourceBadge />
      <Text style={[styles.title, { color: theme.text }]}>Settings</Text>
      <ScrollView contentContainerStyle={styles.content}>
        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>Banks</Text>

        {status === 'loading' && (
          <Text style={[styles.note, { color: theme.textMuted }]}>Loading your banks...</Text>
        )}

        {status === 'failed' && (
          <View style={[styles.emptyCard, { backgroundColor: theme.surface }]}>
            <Text style={[styles.emptyTitle, { color: theme.text }]}>Couldn't load your banks</Text>
            <Text style={[styles.note, { color: theme.textMuted }]}>
              Check your connection and try again.
            </Text>
            <Button label="Try again" onPress={() => refreshMyBanks()} />
          </View>
        )}

        {status === 'ready' && banks.length === 0 && (
          <View style={[styles.emptyCard, { backgroundColor: theme.surface }]}>
            <Text style={[styles.emptyTitle, { color: theme.text }]}>No bank connected</Text>
            <Text style={[styles.note, { color: theme.textMuted }]}>
              Greedy Nose alerts you the moment a payee you marked Bad charges you. Connect your
              bank to start.
            </Text>
            <Button label="Connect a bank" onPress={connect} />
          </View>
        )}

        {status === 'ready' && banks.length > 0 && (
          <>
            {banks.map((bank, index) => (
              // Names are not unique (one bank in two countries); the list is short and has no reordering.
              <BankCard key={`${bank.name}:${bank.country ?? ''}:${index}`} theme={theme} bank={bank} />
            ))}
            <Button variant="outline" label="Connect a bank" onPress={connect} />
          </>
        )}
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1 },
  title: { fontSize: 22, fontWeight: '700', paddingHorizontal: 16, paddingVertical: 12 },
  content: { paddingHorizontal: 16, paddingBottom: 16, gap: 12 },
  sectionLabel: {
    fontSize: 13,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.3,
    marginTop: 8,
    marginHorizontal: 4,
  },
  card: { gap: 12, borderRadius: 14, padding: 16 },
  cardRow: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  divider: { height: StyleSheet.hairlineWidth },
  actions: { flexDirection: 'row', alignItems: 'center', gap: 16 },
  action: { fontSize: 14, fontWeight: '600' },
  logo: { width: 36, height: 36, borderRadius: 8, alignItems: 'center', justifyContent: 'center' },
  logoText: { fontSize: 13, fontWeight: '800' },
  info: { flex: 1, gap: 2, minWidth: 0 },
  name: { fontSize: 15, fontWeight: '600' },
  meta: { fontSize: 12 },
  pill: { borderRadius: 999, paddingVertical: 3, paddingHorizontal: 8 },
  pillText: { fontSize: 11, fontWeight: '600' },
  emptyCard: {
    alignItems: 'center',
    gap: 12,
    borderRadius: 14,
    paddingVertical: 24,
    paddingHorizontal: 16,
  },
  emptyTitle: { fontSize: 17, fontWeight: '600' },
  note: { fontSize: 13, lineHeight: 18, textAlign: 'center' },
});
