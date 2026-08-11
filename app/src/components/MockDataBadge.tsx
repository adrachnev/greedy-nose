import React from 'react';
import { StyleSheet, Text, View } from 'react-native';

/**
 * Slim "MOCK DATA" indicator, __DEV__-only, so a dev/fixture-backed build
 * can never be mistaken for one talking to the real backend. Rendered as
 * the first child inside each main-tab screen's already-safe-area-padded
 * container (see TransactionListScreen / StubScreen) — it does not manage
 * insets itself. Renders nothing in production builds.
 */
export default function MockDataBadge() {
  if (!__DEV__) {
    return null;
  }

  return (
    <View style={styles.bar}>
      <Text style={styles.text}>MOCK DATA — no backend connected</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    backgroundColor: '#8b5cf6',
  },
  text: {
    color: '#fff',
    fontSize: 11,
    fontWeight: '700',
    textAlign: 'center',
    paddingVertical: 3,
    letterSpacing: 0.3,
  },
});
