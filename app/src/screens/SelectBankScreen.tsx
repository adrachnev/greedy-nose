import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import Button from '../components/Button';
import SearchField from '../components/SearchField';
import { useBankSearch, useConnectAttempt } from '../data/hooks';
import { BankHit, MAX_SEARCH_LENGTH } from '../data/myBanksStore';
import { toInitials } from '../domain/payees';
import { SettingsStackParamList } from '../navigation/types';
import { dark, light, Theme } from '../theme/colors';
import { formatCountry } from '../utils/format';

type Props = NativeStackScreenProps<SettingsStackParamList, 'SelectBank'>;

function BankRow({ theme, bank, onPress }: { theme: Theme; bank: BankHit; onPress: () => void }) {
  const content = (
    <>
      <View style={[styles.logo, { backgroundColor: theme.neutralBg }]}>
        <Text style={[styles.logoText, { color: theme.textMuted }]}>{toInitials(bank.name)}</Text>
      </View>
      <View style={styles.info}>
        <Text style={[styles.name, { color: theme.text }]} numberOfLines={1}>
          {bank.name}
        </Text>
        <Text style={[styles.meta, { color: theme.textMuted }]}>{formatCountry(bank.country)}</Text>
      </View>
      {bank.connected && (
        <View style={[styles.pill, { backgroundColor: theme.goodBg }]}>
          <Text style={[styles.pillText, { color: theme.good }]}>Connected</Text>
        </View>
      )}
    </>
  );

  // A connected bank cannot be tapped (one consent per bank, R22a).
  if (bank.connected) {
    return <View style={[styles.row, styles.rowConnected, { backgroundColor: theme.surface }]}>{content}</View>;
  }
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={[styles.row, { backgroundColor: theme.surface }]}
    >
      {content}
    </Pressable>
  );
}

export default function SelectBankScreen({ navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const [text, setText] = useState('');
  const { search, retry } = useBankSearch(text);
  const { start } = useConnectAttempt();

  // The attempt starts here, the screen after it shows every state (also the first, 'opening').
  const connect = (bank: BankHit) => {
    start(bank.name, bank.country);
    navigation.navigate('ConnectAttempt');
  };

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <View style={styles.navBar}>
        <Text style={[styles.navTitle, { color: theme.text }]}>Connect a bank</Text>
        <Pressable
          accessibilityRole="button"
          hitSlop={10}
          onPress={() => navigation.navigate('SettingsHome')}
        >
          <Text style={[styles.cancel, { color: theme.accent }]}>Cancel</Text>
        </Pressable>
      </View>

      <SearchField
        value={text}
        onChangeText={setText}
        placeholder="Search your bank…"
        maxLength={MAX_SEARCH_LENGTH}
      />

      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        {search.phase === 'ready' && search.banks.length > 0 && (
          <>
            <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
              {search.banks.length === 1 ? '1 bank' : `${search.banks.length} banks`}
            </Text>
            {search.banks.map((bank, index) => (
              <BankRow
                key={`${bank.name}:${bank.country}:${index}`}
                theme={theme}
                bank={bank}
                onPress={() => connect(bank)}
              />
            ))}
          </>
        )}

        {search.phase === 'ready' && search.banks.length === 0 && (
          <View style={styles.centered}>
            <Text style={[styles.emptyTitle, { color: theme.text }]}>No bank found</Text>
            <Text style={[styles.note, { color: theme.textMuted }]}>
              {`Nothing matches “${text.trim()}”. Check the spelling, or try the name without extra words.`}
            </Text>
          </View>
        )}

        {search.phase === 'failed' && (
          <View style={styles.centered}>
            <Text style={[styles.emptyTitle, { color: theme.text }]}>Couldn't load banks</Text>
            <Text style={[styles.note, { color: theme.textMuted }]}>
              Check your connection and try again.
            </Text>
            <Button label="Try again" onPress={retry} />
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
  sectionLabel: {
    fontSize: 13,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.3,
    marginHorizontal: 4,
  },
  row: { flexDirection: 'row', alignItems: 'center', gap: 12, borderRadius: 12, padding: 14 },
  rowConnected: { opacity: 0.55 },
  logo: { width: 36, height: 36, borderRadius: 8, alignItems: 'center', justifyContent: 'center' },
  logoText: { fontSize: 13, fontWeight: '800' },
  info: { flex: 1, gap: 2, minWidth: 0 },
  name: { fontSize: 15, fontWeight: '600' },
  meta: { fontSize: 12 },
  pill: { borderRadius: 999, paddingVertical: 3, paddingHorizontal: 8 },
  pillText: { fontSize: 11, fontWeight: '600' },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 8 },
  emptyTitle: { fontSize: 16, fontWeight: '600' },
  note: { fontSize: 13, textAlign: 'center', paddingBottom: 16 },
});
