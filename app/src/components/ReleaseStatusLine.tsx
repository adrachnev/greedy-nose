import React from 'react';
import { Pressable, StyleSheet, Text } from 'react-native';
import { useDataSource, useRegistrationState } from '../data/hooks';
import { FeedProblem } from '../data/backendFeed';
import { Theme } from '../theme/colors';

const MESSAGES: Record<FeedProblem, string> = {
  unreachable: "Can't reach the server",
  unauthorized: 'Not authorized',
  empty: 'No debits yet',
  unreadable: "Can't read the server's answer",
  notConfigured: 'App not configured',
};

const ALERTS_NOT_SET_UP = 'Alerts are not set up on this phone';

/**
 * The release build's counterpart to DataSourceBadge (UX-DR2): one plain line
 * above the Debits list saying which of three different things is going on —
 * server unreachable, key refused or missing, or a healthy account with no
 * debits. Renders nothing in __DEV__ (the badge owns that), in fixture mode,
 * while loading, and when the feed is healthy with rows. Tapping retries.
 * Text only: state is colour, not iconography (AGENTS.md).
 */
export default function ReleaseStatusLine({ theme }: { theme: Theme }) {
  const { live, feed, refresh } = useDataSource();
  const registration = useRegistrationState();

  if (__DEV__ || !live) {
    return null;
  }
  // The feed's problem wins; the alerts line shows only when the feed itself is fine.
  const text = feed.problem
    ? MESSAGES[feed.problem]
    : registration === 'failed'
      ? ALERTS_NOT_SET_UP
      : null;
  if (text === null) {
    return null;
  }
  const isError = feed.problem !== 'empty';
  return (
    <Pressable
      accessibilityRole="button"
      onPress={refresh}
      style={[styles.line, { backgroundColor: isError ? theme.badBg : theme.neutralBg }]}
    >
      <Text style={[styles.text, { color: isError ? theme.bad : theme.textMuted }]}>
        {text}
      </Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  line: {
    paddingHorizontal: 16,
    paddingVertical: 8,
  },
  text: {
    fontSize: 13,
    fontWeight: '600',
    textAlign: 'center',
  },
});
