import React from 'react';
import { StyleSheet, Text, useColorScheme, View } from 'react-native';
import { dark, light } from '../theme/colors';
import Button from './Button';

/**
 * The "No bank connected" body of the empty debit list (02b#no-bank) and the
 * empty rules list (04c#no-bank): a title, one sentence, and the way to the
 * bank search.
 */
export default function NoBankNotice({
  title,
  note,
  onConnect,
}: {
  title: string;
  note: string;
  onConnect: () => void;
}) {
  const theme = useColorScheme() === 'dark' ? dark : light;

  return (
    <View style={styles.body}>
      <Text style={[styles.title, { color: theme.text }]}>{title}</Text>
      <Text style={[styles.note, { color: theme.textMuted }]}>{note}</Text>
      <Button label="Connect a bank" onPress={onConnect} />
    </View>
  );
}

const styles = StyleSheet.create({
  body: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 8, padding: 16 },
  title: { fontSize: 16, fontWeight: '600' },
  note: { fontSize: 13, textAlign: 'center', paddingBottom: 16 },
});
