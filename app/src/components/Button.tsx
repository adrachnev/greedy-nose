import React from 'react';
import { Pressable, StyleSheet, Text, useColorScheme } from 'react-native';
import { dark, light } from '../theme/colors';

/**
 * The full-width buttons of mocks/style.css: `.btn-primary` (filled) and
 * `.btn-outline` (accent text on a border).
 */
export default function Button({
  label,
  onPress,
  variant = 'primary',
}: {
  label: string;
  onPress: () => void;
  variant?: 'primary' | 'outline';
}) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const filled = variant === 'primary';

  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={[styles.btn, filled ? { backgroundColor: theme.accent } : [styles.outline, { borderColor: theme.border }]]}
    >
      <Text style={[filled ? styles.textFilled : { color: theme.accent }, styles.text]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  btn: {
    minHeight: 44,
    paddingHorizontal: 14,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
    alignSelf: 'stretch',
  },
  outline: { borderWidth: 1 },
  text: { fontSize: 16, fontWeight: '600' },
  textFilled: { color: '#fff' },
});
