import React from 'react';
import { StyleSheet, Text, View } from 'react-native';

/**
 * Lightweight confirmation toast, matching mocks/style.css's `.toast`
 * (dark pill, bottom-anchored, auto-dismissing). Purely presentational —
 * callers own the show/hide timing (see PayeeEditScreen for the
 * "show for ~2s after a mutation" pattern). Renders nothing when `message`
 * is falsy so it can be mounted unconditionally at the bottom of a screen.
 */
export default function Toast({ message }: { message: string | null }) {
  if (!message) {
    return null;
  }

  return (
    <View style={styles.toast} pointerEvents="none">
      <Text style={styles.text}>{message}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  toast: {
    position: 'absolute',
    left: 16,
    right: 16,
    bottom: 24,
    backgroundColor: 'rgba(28,28,30,0.92)',
    paddingVertical: 12,
    paddingHorizontal: 16,
    borderRadius: 12,
    shadowColor: '#000',
    shadowOpacity: 0.25,
    shadowRadius: 20,
    shadowOffset: { width: 0, height: 8 },
    elevation: 6,
  },
  text: {
    color: '#fff',
    fontSize: 13,
    textAlign: 'center',
  },
});
