import React, { useState } from 'react';
import {
  Pressable,
  StyleSheet,
  Text,
  TextInput,
  useColorScheme,
  View,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import DataSourceBadge from '../components/DataSourceBadge';
import { ConnectFlow, FailureReason, HINT_AFTER_TRIES, isUsableBankName } from '../data/connectStore';
import { useConnectBank } from '../data/hooks';
import { dark, light, Theme } from '../theme/colors';

// PRAGMATIC: temporary Settings entry for Story 5.1a, only to connect a bank and
// judge the way there and back on the phone. Stories 5.6/5.7 replace it with the
// bank cards of mocks/06-settings.html and the bank search of 01-select-bank.html
// — see bead gn-1u1.10.

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "19 Jan 2027" in the phone's local day; the raw text if it is not a date. */
export function formatValidUntil(iso: string | null): string | null {
  if (iso === null) {
    return null;
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return null;
  }
  return `${date.getDate()} ${MONTHS[date.getMonth()]} ${date.getFullYear()}`;
}

export const FAILURE_TEXT: Record<FailureReason, string> = {
  link: 'We could not start the sign-in. Check your connection and the bank name, then try again.',
  browser: 'The sign-in page could not be opened. Try again.',
  status: 'We could not check with your bank just now. Try again.',
  ended: 'Your bank did not give access this time. Try again.',
};

function connectedText(flow: Extract<ConnectFlow, { step: 'connected' }>): string {
  const until = formatValidUntil(flow.validUntil);
  return until ? `${flow.bank} is connected, valid until ${until}` : `${flow.bank} is connected`;
}

export default function SettingsScreen() {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const { flow, start, reset, checkNow } = useConnectBank();
  const [bankName, setBankName] = useState('');

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <DataSourceBadge />
      <Text style={[styles.title, { color: theme.text }]}>Settings</Text>
      <View style={styles.content}>
        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>Banks</Text>
        <View style={[styles.card, { backgroundColor: theme.surface }]}>
          {flow.step === 'idle' && (
            <>
              <Text style={[styles.hint, { color: theme.textMuted }]}>
                You sign in at your bank and approve in its app. Then switch back to your browser tab, which brings you back here.
              </Text>
              <TextInput
                accessibilityLabel="Bank name"
                placeholder="Name of your bank"
                placeholderTextColor={theme.textMuted}
                value={bankName}
                onChangeText={setBankName}
                autoCapitalize="words"
                autoCorrect={false}
                style={[styles.input, { color: theme.text, borderColor: theme.border }]}
              />
              <PrimaryButton
                theme={theme}
                label="Connect a bank"
                disabled={!isUsableBankName(bankName)}
                onPress={() => start(bankName)}
              />
            </>
          )}

          {(flow.step === 'opening' || flow.step === 'checking') && (
            <Text style={[styles.status, { color: theme.text }]}>
              {flow.step === 'opening' ? 'Preparing the sign-in...' : 'Checking with your bank...'}
            </Text>
          )}

          {flow.step === 'waiting' && (
            <>
              <Text style={[styles.status, { color: theme.text }]}>Waiting for your bank</Text>
              {flow.notYet && (
                <Text style={[styles.hint, { color: theme.textMuted }]}>
                  Your bank has not confirmed yet. If you have approved, check again in a moment.
                </Text>
              )}
              <Text style={[styles.hint, { color: theme.textMuted }]}>
                After you approved in your bank's app, switch back to your browser tab. It brings you back here.
              </Text>
              {flow.tries >= HINT_AFTER_TRIES && (
                <Text style={[styles.hint, { color: theme.textMuted }]}>
                  Still nothing? Check that the name matches your bank's own name, or start over with the full name.
                </Text>
              )}
              <PrimaryButton theme={theme} label="Check again" onPress={checkNow} />
              <TextButton theme={theme} label="Start over" onPress={reset} />
            </>
          )}

          {flow.step === 'connected' && (
            <>
              <Text style={[styles.status, { color: theme.good }]}>{connectedText(flow)}</Text>
              <PrimaryButton theme={theme} label="Done" onPress={reset} />
            </>
          )}

          {flow.step === 'failed' && (
            <>
              <Text style={[styles.status, { color: theme.bad }]}>{FAILURE_TEXT[flow.reason]}</Text>
              <PrimaryButton theme={theme} label="Try again" onPress={reset} />
            </>
          )}
        </View>
      </View>
    </View>
  );
}

function PrimaryButton({
  theme,
  label,
  onPress,
  disabled,
}: {
  theme: Theme;
  label: string;
  onPress: () => void;
  disabled?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      disabled={disabled}
      onPress={onPress}
      style={[styles.btn, { backgroundColor: theme.accent, opacity: disabled ? 0.5 : 1 }]}
    >
      <Text style={styles.btnText}>{label}</Text>
    </Pressable>
  );
}

function TextButton({ theme, label, onPress }: { theme: Theme; label: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.textBtn}>
      <Text style={[styles.textBtnText, { color: theme.accent }]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1 },
  title: { fontSize: 22, fontWeight: '700', paddingHorizontal: 16, paddingVertical: 12 },
  content: { paddingHorizontal: 16, gap: 8 },
  sectionLabel: { fontSize: 13, fontWeight: '600', textTransform: 'uppercase', marginTop: 8 },
  card: { borderRadius: 12, padding: 16, gap: 12 },
  hint: { fontSize: 14, lineHeight: 20 },
  status: { fontSize: 16, fontWeight: '600' },
  input: {
    minHeight: 44,
    borderWidth: 1,
    borderRadius: 10,
    paddingHorizontal: 12,
    fontSize: 16,
  },
  btn: { minHeight: 44, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  btnText: { color: '#fff', fontSize: 16, fontWeight: '600' },
  textBtn: { minHeight: 44, alignItems: 'center', justifyContent: 'center' },
  textBtnText: { fontSize: 15, fontWeight: '600' },
});
