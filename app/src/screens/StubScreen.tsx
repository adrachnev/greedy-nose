import React from 'react';
import { Pressable, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import DataSourceBadge from '../components/DataSourceBadge';
import { dark, light } from '../theme/colors';

/**
 * Placeholder for a screen that hasn't been ported from mocks/ yet.
 * Keeps navigation fully click-through-able without pretending the real
 * UI exists.
 */
export default function StubScreen({
  title,
  mockFile,
  onContinue,
  continueLabel = 'Continue',
  showDataSourceBadge = false,
}: {
  title: string;
  mockFile: string;
  onContinue?: () => void;
  continueLabel?: string;
  /** Set for stub screens reachable inside the main tabs (Rules/Settings). */
  showDataSourceBadge?: boolean;
}) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();

  return (
    <View
      style={[
        styles.screen,
        { backgroundColor: theme.bg, paddingTop: insets.top },
      ]}
    >
      {showDataSourceBadge && <DataSourceBadge />}
      <View style={styles.content}>
        <Text style={[styles.title, { color: theme.text }]}>{title}</Text>
        <Text style={[styles.hint, { color: theme.textMuted }]}>
          TODO: port from mocks/{mockFile}
        </Text>
      </View>

      {onContinue && (
        <Pressable
          style={[styles.btnPrimary, { backgroundColor: theme.accent }]}
          onPress={onContinue}
        >
          <Text style={styles.btnPrimaryText}>{continueLabel}</Text>
        </Pressable>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
  },
  content: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    padding: 24,
    gap: 8,
  },
  title: {
    fontSize: 18,
    fontWeight: '600',
    textAlign: 'center',
  },
  hint: {
    fontSize: 13,
    textAlign: 'center',
  },
  btnPrimary: {
    minHeight: 44,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 14,
    marginHorizontal: 16,
    marginBottom: 16,
  },
  btnPrimaryText: {
    color: '#fff',
    fontSize: 16,
    fontWeight: '600',
  },
});
