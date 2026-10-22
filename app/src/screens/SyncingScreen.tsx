import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import Button from '../components/Button';
import { useDataSource, useFirstSyncRetry, useMyBanks, useMyBanksPolling } from '../data/hooks';
import { canRetryFirstSync, findMyBank, MyBank, refreshMyBanks, SYNC_POLL_MS } from '../data/myBanksStore';
import { prepareOffer } from '../data/onboardingOffer';
import { leaveSettingsForDebitList } from '../navigation/openDebitList';
import { SettingsStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';

type Props = NativeStackScreenProps<SettingsStackParamList, 'Syncing'>;

// The first sync of a just-saved bank (Story 5.8, mock 01bb and 01bb#failed). The
// sync runs in the backend; this screen only watches the bank's row and can ask
// for the pull again. Leaving it, by any road, changes nothing there. The words
// never name a bank.

/** After this long in "syncing" the screen stops waiting and points to the card in Settings. */
export const SYNC_WATCH_MS = 120_000;

type Phase = 'syncing' | 'timeout' | 'done' | 'failed' | 'limit' | 'expired';

/** What the saved bank's row says, in the screen's own terms. No row yet counts as syncing. */
function viewOf(bank: MyBank | undefined): Exclude<Phase, 'timeout'> {
  switch (bank?.state) {
    case 'connected':
      return 'done';
    case 'sync_failed':
      return canRetryFirstSync(bank) ? 'failed' : 'limit';
    case 'expired':
      return 'expired';
    default:
      return 'syncing';
  }
}

function Title({ theme, children, tone }: { theme: Theme; children: string; tone?: 'bad' }) {
  return <Text style={[styles.title, { color: tone === 'bad' ? theme.bad : theme.text }]}>{children}</Text>;
}

function Hint({ theme, children }: { theme: Theme; children: string }) {
  return <Text style={[styles.hint, { color: theme.textMuted }]}>{children}</Text>;
}

export default function SyncingScreen({ navigation, route }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const { bank: bankName, country } = route.params;
  const { banks } = useMyBanks();
  const bank = findMyBank(banks, bankName, country);
  const [timedOut, setTimedOut] = useState(false);
  // Whether one read has succeeded since this screen opened. Until then the store may hold an older row of
  // the same bank (expired, failed), which says nothing about this sync; the view is "syncing".
  const [fresh, setFresh] = useState(false);
  const rowView = fresh ? viewOf(bank) : 'syncing';
  const view: Phase = rowView === 'syncing' && timedOut ? 'timeout' : rowView;
  const { retry, couldNot } = useFirstSyncRetry();
  const { refreshFresh } = useDataSource();

  // Reads every few seconds while the row says syncing, or while no row has arrived yet; none after the stop.
  useMyBanksPolling({ enabled: view !== 'timeout', watch: fresh && rowView === 'syncing' });

  // Reads until the first good answer, also when the polling above would not (the stale row may not say syncing).
  const stopped = view === 'timeout';
  useEffect(() => {
    if (fresh || stopped) {
      return;
    }
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const read = () => {
      refreshMyBanks().then(ok => {
        if (cancelled) {
          return;
        }
        if (ok) {
          setFresh(true);
        } else {
          timer = setTimeout(read, SYNC_POLL_MS);
        }
      });
    };
    read();
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [fresh, stopped]);

  // The wait starts each time the view enters "syncing" (also after "Try again") and ends when it leaves.
  useEffect(() => {
    if (rowView !== 'syncing') {
      setTimedOut(false);
      return;
    }
    const timer = setTimeout(() => setTimedOut(true), SYNC_WATCH_MS);
    return () => clearTimeout(timer);
  }, [rowView]);

  // Done: the debits the sync stored are fetched once. With payees that have no rule (Story 5.9) the stack becomes
  // [SettingsHome, ClassifyPayees] and the offer is shown; otherwise the debit list opens. Either way this screen is
  // dropped from the Settings stack: it has nothing left to show, and coming back to the tab must not find it.
  // A user who left while the feed was loading chose nothing: no offer, no navigation (= Keep all Bad).
  const left = useRef(false);
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  useEffect(() => {
    if (view === 'done' && !left.current) {
      left.current = true;
      // A load already running may have begun before the sync finished; refreshFresh waits for it and loads again.
      // If that load fails the feed may be older than the sync: no offer, the debit list opens.
      refreshFresh().then(loaded => {
        if (!mounted.current) {
          return;
        }
        if (prepareOffer(loaded).length > 0) {
          navigation.reset({
            index: 1,
            routes: [{ name: 'SettingsHome' }, { name: 'ClassifyPayees', params: { bank: bank?.name ?? bankName } }],
          });
        } else {
          leaveSettingsForDebitList(navigation);
        }
      });
    }
  }, [view, refreshFresh, navigation, bank, bankName]);

  // Settings home is always under this screen (the hand-over resets the stack); a restart may have lost it.
  const toSettings = () => {
    if (navigation.getState().routes.some(route_ => route_.name === 'SettingsHome')) {
      navigation.popTo('SettingsHome');
    } else {
      navigation.replace('SettingsHome');
    }
  };
  const again = () => {
    if (bank) {
      retry(bank.name);
    }
  };

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <View style={styles.navBar}>
        <Pressable accessibilityRole="button" hitSlop={10} onPress={toSettings}>
          <Text style={[styles.link, { color: theme.accent }]}>Settings</Text>
        </Pressable>
      </View>

      <ScrollView contentContainerStyle={styles.content}>
        {view === 'syncing' && (
          <View style={styles.centered}>
            <ActivityIndicator color={theme.accent} size="large" />
            <Title theme={theme}>Fetching your debits</Title>
            <Hint theme={theme}>
              This usually takes a few seconds. We're pulling your recent history from your bank. You can leave this
              screen: it keeps running and Settings shows the state.
            </Hint>
            <Button variant="outline" label="Back to Settings" onPress={toSettings} />
          </View>
        )}

        {view === 'timeout' && (
          <View style={styles.centered}>
            <Title theme={theme}>Still fetching your debits</Title>
            <Hint theme={theme}>
              This takes longer than usual. It keeps running in the background; the card in Settings shows its state.
            </Hint>
            <Button label="Back to Settings" onPress={toSettings} />
          </View>
        )}

        {(view === 'failed' || view === 'limit' || view === 'expired') && (
          <View style={styles.centered}>
            <Title theme={theme} tone="bad">
              Couldn't fetch your debits
            </Title>
            <Hint theme={theme}>
              {view === 'failed'
                ? "Your bank didn't answer. Your bank stays connected."
                : view === 'limit'
                ? 'Your bank allows no more requests today. Your bank stays connected. Try again tomorrow.'
                : 'Access to your bank has ended. Settings shows the state.'}
            </Hint>
            {view === 'failed' && (
              <>
                {couldNot && <Text style={[styles.error, { color: theme.bad }]}>Couldn't try again just now.</Text>}
                <Button label="Try again" onPress={again} />
              </>
            )}
            <Pressable accessibilityRole="button" onPress={toSettings}>
              <Text style={[styles.link, { color: theme.accent }]}>Back to Settings</Text>
            </Pressable>
          </View>
        )}
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1 },
  navBar: {
    flexDirection: 'row',
    justifyContent: 'flex-end',
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 12,
  },
  content: { padding: 16, gap: 12, flexGrow: 1 },
  centered: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 16,
  },
  title: { fontSize: 16, fontWeight: '600', textAlign: 'center' },
  hint: { fontSize: 12, lineHeight: 17, textAlign: 'center' },
  error: { fontSize: 13, textAlign: 'center' },
  link: { fontSize: 15, paddingVertical: 8 },
});
