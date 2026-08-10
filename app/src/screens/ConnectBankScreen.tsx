import React from 'react';
import {
  Pressable,
  StyleSheet,
  Text,
  useColorScheme,
  View,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { dark, light, Theme } from '../theme/colors';

function BankRow({
  theme,
  logoText,
  logoBg,
  logoColor,
  name,
  subtitle,
  disabled,
  onPress,
}: {
  theme: Theme;
  logoText: string;
  logoBg: string;
  logoColor: string;
  name: string;
  subtitle: string;
  disabled?: boolean;
  onPress?: () => void;
}) {
  return (
    <Pressable
      disabled={disabled}
      onPress={onPress}
      style={[
        styles.bankRow,
        { backgroundColor: theme.surface, opacity: disabled ? 0.5 : 1 },
      ]}
    >
      <View style={[styles.bankLogo, { backgroundColor: logoBg }]}>
        <Text style={[styles.bankLogoText, { color: logoColor }]}>
          {logoText}
        </Text>
      </View>
      <View style={styles.bankInfo}>
        <Text style={[styles.bankName, { color: theme.text }]}>{name}</Text>
        <Text style={[styles.bankSubtitle, { color: theme.textMuted }]}>
          {subtitle}
        </Text>
      </View>
    </Pressable>
  );
}

export default function ConnectBankScreen({
  onContinue,
}: {
  onContinue?: () => void;
}) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <Text style={[styles.title, { color: theme.text }]}>Connect Bank</Text>

      <View style={styles.content}>
        <Text style={[styles.hint, { color: theme.textMuted }]}>
          Connect your bank account securely via Enable Banking (PSD2). We
          only ever receive read-only access to your transactions — never
          able to move money.
        </Text>

        <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
          Choose your bank
        </Text>

        <BankRow
          theme={theme}
          logoText="N26"
          logoBg="#00e5b0"
          logoColor="#06251f"
          name="N26"
          subtitle="Germany · Personal account"
          onPress={onContinue}
        />

        <BankRow
          theme={theme}
          logoText="···"
          logoBg={theme.neutralBg}
          logoColor={theme.textMuted}
          name="Other EU banks"
          subtitle="Coming later"
          disabled
        />

        <View style={{ flex: 1 }} />

        <Pressable
          style={[styles.btnPrimary, { backgroundColor: theme.accent }]}
          onPress={onContinue}
        >
          <Text style={styles.btnPrimaryText}>Continue with N26</Text>
        </Pressable>
        <Text
          style={[styles.hint, { color: theme.textMuted, textAlign: 'center' }]}
        >
          You'll be redirected to N26 to log in and grant consent. We never
          see your bank credentials.
        </Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
  },
  title: {
    fontSize: 20,
    fontWeight: '600',
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 12,
  },
  content: {
    flex: 1,
    padding: 16,
    gap: 12,
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
  bankRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    borderRadius: 12,
    padding: 14,
  },
  bankLogo: {
    width: 36,
    height: 36,
    borderRadius: 8,
    alignItems: 'center',
    justifyContent: 'center',
  },
  bankLogoText: {
    fontWeight: '800',
    fontSize: 12,
  },
  bankInfo: {
    flexDirection: 'column',
    gap: 2,
    minWidth: 0,
  },
  bankName: {
    fontSize: 15,
    fontWeight: '600',
  },
  bankSubtitle: {
    fontSize: 12,
  },
  btnPrimary: {
    minHeight: 44,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 14,
  },
  btnPrimaryText: {
    color: '#fff',
    fontSize: 16,
    fontWeight: '600',
  },
});
