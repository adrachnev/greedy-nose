import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useEffect, useRef } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import Button from '../components/Button';
import { ChoiceAccount, ConnectFlow, FailureReason } from '../data/connectStore';
import { useConnectAttempt, useConnectPolling } from '../data/hooks';
import { SettingsStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { joinMeta } from '../utils/format';

type Props = NativeStackScreenProps<SettingsStackParamList, 'ConnectAttempt'>;

// One screen for the whole attempt of Story 5.7, by the state of the flow:
// opening, waiting (the bank's own pages are in the browser; mock 01b is theirs
// and is not built), the account choice (mock 01ba), and the end states (mock
// 01e, 01ba#failed). The words never name a bank; the bank's name is data from
// the search and is shown as such.

const KIND_LABEL: Record<string, string> = {
  current: 'Current account',
  savings: 'Savings account',
  card: 'Card account',
  loan: 'Loan account',
};

/** "Current account · …4521"; the end of the IBAN is all there is of it. */
export function describeAccount(account: ChoiceAccount): string {
  return joinMeta(KIND_LABEL[account.kind] ?? 'Account', account.last4 && `…${account.last4}`);
}

/** The states in which leaving the screen must also end the attempt at the backend. */
const isActive = (flow: ConnectFlow) =>
  flow.step === 'opening' || flow.step === 'waiting' || flow.step === 'choose_account';

function Title({ theme, children, tone }: { theme: Theme; children: string; tone?: 'bad' }) {
  return (
    <Text style={[styles.title, { color: tone === 'bad' ? theme.bad : theme.text }]}>{children}</Text>
  );
}

function Hint({ theme, children }: { theme: Theme; children: string }) {
  return <Text style={[styles.hint, { color: theme.textMuted }]}>{children}</Text>;
}

function AccountRow({
  theme,
  account,
  disabled,
  onPress,
}: {
  theme: Theme;
  account: ChoiceAccount;
  disabled: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      disabled={disabled}
      onPress={onPress}
      style={[styles.row, { backgroundColor: theme.surface }, disabled && styles.rowDisabled]}
    >
      <View style={styles.info}>
        <Text style={[styles.name, { color: theme.text }]} numberOfLines={1}>
          {account.name}
        </Text>
        <Text style={[styles.meta, { color: theme.textMuted }]}>{describeAccount(account)}</Text>
      </View>
      {account.suggested && (
        <View style={[styles.pill, { backgroundColor: theme.goodBg }]}>
          <Text style={[styles.pillText, { color: theme.good }]}>Suggested</Text>
        </View>
      )}
    </Pressable>
  );
}

function failureTitle(reason: FailureReason): string {
  switch (reason) {
    case 'no-supported-account':
      return 'No supported account';
    case 'already-connected':
      return 'Already connected';
    default:
      return "Couldn't connect to your bank";
  }
}

export default function ConnectAttemptScreen({ navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const { flow, start, checkNow, choose, cancel, reset } = useConnectAttempt();
  useConnectPolling();

  // The listeners below must see the newest flow without being re-attached on every change.
  const flowRef = useRef(flow);
  flowRef.current = flow;

  // Back to a screen of this stack. If it is not in the stack (the app restarted with only Settings home and
  // this screen) it replaces this one: popTo would push it on top and leave this screen mounted under it.
  const goTo = (name: 'SettingsHome' | 'SelectBank') => {
    if (navigation.getState().routes.some(route => route.name === name)) {
      navigation.popTo(name);
    } else {
      navigation.replace(name);
    }
  };
  const toSettings = () => goTo('SettingsHome');

  // Cancel, or going back, ends a running attempt: the backend closes the consent and nothing is saved.
  // The flow is then idle, which is what sends the user to Settings (effect below).
  const leave = () => {
    if (isActive(flowRef.current)) {
      cancel();
    } else {
      toSettings();
    }
  };
  const leaveRef = useRef(leave);
  leaveRef.current = leave;

  useEffect(() => {
    return navigation.addListener('beforeRemove', event => {
      const type = event.data.action.type;
      // Only the user's back (hardware or gesture): a tab switch pops this screen too and must not cancel anything.
      if ((type === 'GO_BACK' || type === 'POP') && isActive(flowRef.current)) {
        event.preventDefault();
        leaveRef.current();
      }
    });
  }, [navigation]);

  // An ended attempt is forgotten when its screen goes away, however it was left (the store ignores a running one).
  useEffect(() => reset, [reset]);

  const toSettingsRef = useRef(toSettings);
  toSettingsRef.current = toSettings;
  useEffect(() => {
    if (flow.step === 'saved') {
      // The bank is saved: the first sync (Story 5.8) takes this screen's place. The stack is rebuilt rather than
      // replaced, so back from the sync screen lands on Settings and not on the search that led here.
      navigation.reset({
        index: 1,
        routes: [
          { name: 'SettingsHome' },
          { name: 'Syncing', params: { bank: flow.bank, country: flow.country } },
        ],
      });
    } else if (flow.step === 'idle') {
      toSettingsRef.current();
    }
  }, [flow, navigation]);

  const again = () => {
    if (flow.step === 'failed' || flow.step === 'expired') {
      start(flow.bank, flow.country);
    }
  };
  // Not reset here: the idle flow would send this screen to Settings (effect above) while it is popping to the
  // search. The ended attempt is forgotten when the screen unmounts (cleanup above).
  const differentBank = () => goTo('SelectBank');

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>
          {flow.step === 'choose_account' ? 'Choose account' : 'Connect a bank'}
        </Text>
        <Pressable accessibilityRole="button" hitSlop={10} onPress={leave}>
          <Text style={[styles.cancel, { color: theme.accent }]}>Cancel</Text>
        </Pressable>
      </View>

      <ScrollView contentContainerStyle={styles.content}>
        {flow.step === 'opening' && (
          <View style={styles.centered}>
            <Title theme={theme}>Opening your bank</Title>
            <Hint theme={theme}>Your bank's own page opens in the browser.</Hint>
          </View>
        )}

        {flow.step === 'waiting' && (
          <View style={styles.centered}>
            <Title theme={theme}>Waiting for your bank</Title>
            {flow.noAnswer ? (
              <Hint theme={theme}>
                No answer from the backend. Check your connection, then check again. Nothing is saved until you
                approve.
              </Hint>
            ) : (
              <Hint theme={theme}>
                Approve the connection at your bank, then come back here. After you approved in your bank's app,
                switch back to your browser tab. It brings you back.
              </Hint>
            )}
            <Button label="Check again" onPress={checkNow} />
            <Button variant="outline" label="Cancel" onPress={leave} />
          </View>
        )}

        {flow.step === 'choose_account' && (
          <>
            <Hint theme={theme}>
              {`${flow.bank} has more than one account. Greedy Nose watches one account per bank — pick the one you pay with.`}
            </Hint>
            <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>{flow.bank}</Text>
            {flow.accounts.map(account => (
              <AccountRow
                key={account.id}
                theme={theme}
                account={account}
                disabled={flow.posting}
                onPress={() => choose(account.id)}
              />
            ))}
            {flow.choiceFailed && (
              <Text style={[styles.error, { color: theme.bad }]}>
                Couldn't save your choice. Check your connection and tap the account again.
              </Text>
            )}
            <Hint theme={theme}>You can add or remove a bank later in Settings.</Hint>
          </>
        )}

        {flow.step === 'failed' && (
          <View style={styles.centered}>
            <Title theme={theme} tone={flow.reason === 'no-supported-account' ? undefined : 'bad'}>
              {failureTitle(flow.reason)}
            </Title>
            {flow.reason === 'no-supported-account' && (
              <>
                <Hint theme={theme}>{`${flow.bank} returned no account in euro that we can watch.`}</Hint>
                <Button label="Back to Settings" onPress={toSettings} />
              </>
            )}
            {flow.reason === 'already-connected' && (
              <>
                <Hint theme={theme}>
                  This bank is already connected and its access has not expired. Settings shows it.
                </Hint>
                <Button label="Back to Settings" onPress={toSettings} />
              </>
            )}
            {flow.reason !== 'no-supported-account' && flow.reason !== 'already-connected' && (
              <>
                <Hint theme={theme}>
                  The connection couldn't be completed. This can happen if the login was cancelled, the session
                  timed out, or your bank is temporarily unavailable.
                </Hint>
                <Button label="Try Again" onPress={again} />
                <Pressable accessibilityRole="button" onPress={differentBank}>
                  <Text style={[styles.link, { color: theme.accent }]}>Choose a different bank</Text>
                </Pressable>
              </>
            )}
          </View>
        )}

        {flow.step === 'expired' && (
          <View style={styles.centered}>
            <Title theme={theme} tone="bad">
              This sign-in has ended
            </Title>
            <Hint theme={theme}>
              The time for it ran out, or the backend no longer knows it. Nothing was saved. Start again to get a
              new sign-in.
            </Hint>
            <Button label="Start again" onPress={again} />
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
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 12,
  },
  navTitle: { fontSize: 20, fontWeight: '600' },
  cancel: { fontSize: 15 },
  content: { padding: 16, gap: 12, flexGrow: 1 },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 16 },
  title: { fontSize: 18, fontWeight: '600', textAlign: 'center' },
  hint: { fontSize: 12, lineHeight: 17, textAlign: 'center' },
  error: { fontSize: 13, textAlign: 'center' },
  link: { fontSize: 15, paddingVertical: 8 },
  sectionLabel: {
    fontSize: 13,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.3,
    marginHorizontal: 4,
  },
  row: { flexDirection: 'row', alignItems: 'center', gap: 12, borderRadius: 12, padding: 14 },
  rowDisabled: { opacity: 0.5 },
  info: { flex: 1, gap: 2, minWidth: 0 },
  name: { fontSize: 15, fontWeight: '600' },
  meta: { fontSize: 12 },
  pill: { borderRadius: 999, paddingVertical: 3, paddingHorizontal: 8 },
  pillText: { fontSize: 11, fontWeight: '600' },
});
