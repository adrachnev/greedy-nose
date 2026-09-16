import React from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useDataSource, useDebits, usePayees } from '../data/hooks';

/**
 * Slim __DEV__-only bar saying where the rows above it came from, so a build
 * can never be mistaken for one talking to the real backend — nor the reverse.
 * Rendered as the first child inside each main-tab screen's already-safe-area-
 * padded container (see DebitListScreen / StubScreen); it does not manage
 * insets itself, and renders nothing in production builds.
 *
 * It grew from a fixed "MOCK DATA" label into a status line for one reason:
 * once the feed is real, an empty list is ambiguous. Backend not started,
 * `adb reverse` not set up, consent expired, or an account with genuinely no
 * debits all paint the same blank screen, and the device is the most expensive
 * place to guess between them. Tapping it retries.
 */
export default function DataSourceBadge() {
  const { live, feed, refresh } = useDataSource();
  const payees = usePayees();
  const debits = useDebits();

  if (!__DEV__) {
    return null;
  }

  if (!live) {
    return <Bar color={colors.mock} text="MOCK DATA — no backend connected" />;
  }

  switch (feed.status) {
    case 'idle':
    case 'loading':
      return <Bar color={colors.loading} text="LIVE — loading from backend…" />;
    case 'error':
      // Deliberately not "BACKEND UNREACHABLE" any more: a 200 whose body is
      // not the contract is now an error too, and the backend was very much
      // reachable in that case. The message carries the specific
      // failure — "No answer within 15s" (usually a missing `adb reverse`),
      // "HTTP 503", "Backend sent no payees/debits arrays" — so the prefix
      // does not need to guess at one.
      return (
        <Bar
          color={colors.error}
          text={`BACKEND ERROR — ${feed.error ?? 'unknown error'} · tap to retry`}
          onPress={refresh}
        />
      );
    case 'ready': {
      // Two counters, never one. `dropped` is a row the backend sent that this
      // app refused to render; `skipped` is a charge the backend's own mapper
      // refused to put on the wire (non-EUR, unreadable amount or date). Both
      // mean money the list is not showing, and adding them together would say
      // so without saying which side to go and look at. Zero stays silent so
      // the normal line is not noise.
      const dropped = feed.dropped > 0 ? ` · ${feed.dropped} dropped by app` : '';
      const skipped = feed.skipped > 0 ? ` · ${feed.skipped} skipped by backend` : '';
      return (
        <Bar
          color={colors.live}
          text={`LIVE — ${debits.length} debits · ${payees.length} payees${dropped}${skipped} · tap to refresh`}
          onPress={refresh}
        />
      );
    }
  }
}

function Bar({ color, text, onPress }: { color: string; text: string; onPress?: () => void }) {
  const body = (
    <View style={[styles.bar, { backgroundColor: color }]}>
      {/*
        Two lines, not one. The line grew a "dropped by app"/"skipped by
        backend" tail, and at 375pt one line clips exactly the part that only
        appears when something went wrong — the counters and the tap-to-refresh
        affordance, in that order. A dev bar that hides its own bad news is
        worse than a dev bar two rows tall.
      */}
      <Text style={styles.text} numberOfLines={2}>
        {text}
      </Text>
    </View>
  );
  return onPress ? <Pressable onPress={onPress}>{body}</Pressable> : body;
}

// Deliberately not from src/theme/colors.ts: this bar is scaffolding that comes
// out with __DEV__, and it must stay legible in both themes without taking part
// in either one's palette.
const colors = {
  mock: '#8b5cf6',
  loading: '#64748b',
  error: '#dc2626',
  live: '#15803d',
};

const styles = StyleSheet.create({
  bar: {
    paddingHorizontal: 8,
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
