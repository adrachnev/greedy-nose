import { NativeStackScreenProps } from '@react-navigation/native-stack';
import React, { useMemo } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, useColorScheme, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import DataSourceBadge from '../components/DataSourceBadge';
import { useDebit, usePayee, useRuleForPayee } from '../data/hooks';
import { classifyDebit, classifyPayee, describeBadReason } from '../domain/classification';
import { unknownPayee } from '../domain/payees';
import { DebitsStackParamList } from '../navigation/types';
import { dark, light } from '../theme/colors';
import { formatCurrencyEUR, formatLongDate, formatTime, joinMeta } from '../utils/format';

type Props = NativeStackScreenProps<DebitsStackParamList, 'DebitDetail'>;

export default function DebitDetailScreen({ route, navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const debit = useDebit(route.params.debitId);
  const knownPayee = usePayee(debit?.payeeId ?? '');
  const rule = useRuleForPayee(debit?.payeeId ?? '');

  // Memoized rather than rebuilt each render, so this screen keeps the same
  // "same object every time" guarantee createPayeeLookup gives the list. It
  // has to sit above the early return, because hooks do.
  const payee = useMemo(
    () => knownPayee ?? unknownPayee(debit?.payeeId ?? ''),
    [knownPayee, debit?.payeeId],
  );

  // Only a *missing debit* is 'not found'. A debit whose payee the app has no
  // record of used to land here too, so tapping such a row said the charge did
  // not exist — the same silent drop the list screen and the feed both used to
  // do, and the one thing R1 forbids. The placeholder keeps the money on
  // screen; see src/domain/payees.ts.
  if (!debit) {
    return (
      <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
        <DataSourceBadge />
        <Text style={[styles.hint, styles.notFoundText, { color: theme.textMuted }]}>
          Debit not found.
        </Text>
      </View>
    );
  }

  // The payee's verdict and this debit's verdict are separate questions (R5):
  // a good payee's charge can still be bad for exceeding their limit.
  const payeeIsGood = classifyPayee(rule) === 'good';
  const result = classifyDebit(debit, rule);
  const debitIsBad = result.classification === 'bad';
  const reason = describeBadReason(result);

  const payeeColor = payeeIsGood ? theme.good : theme.bad;
  const payeeBg = payeeIsGood ? theme.goodBg : theme.badBg;
  const debitColor = debitIsBad ? theme.bad : theme.good;
  const debitBg = debitIsBad ? theme.badBg : theme.goodBg;

  const hasIban = payee.iban.trim().length > 0;
  const hasReference = debit.reference.trim().length > 0;

  const payeeSubtitle =
    payeeIsGood && rule?.amountEUR != null
      ? `${payee.name} · limit ${formatCurrencyEUR(rule.amountEUR)}`
      : payee.name;

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <DataSourceBadge />
      <View style={styles.navBar}>
        <Pressable onPress={() => navigation.goBack()} hitSlop={8}>
          <Text style={[styles.navLink, { color: theme.accent }]}>‹ Back</Text>
        </Pressable>
        <Text style={[styles.navTitle, { color: theme.text }]}>Debit</Text>
        <View style={styles.navSpacer} />
      </View>

      <ScrollView contentContainerStyle={styles.content}>
        <View style={[styles.card, styles.summaryCard, { backgroundColor: theme.surface }]}>
          <View style={[styles.avatarLarge, { backgroundColor: payeeBg }]}>
            <Text style={[styles.avatarLargeText, { color: payeeColor }]}>{payee.initials}</Text>
          </View>
          <Text style={[styles.payeeName, { color: theme.text }]}>{payee.name}</Text>
          <Text style={[styles.amount, { color: theme.text }]}>
            {formatCurrencyEUR(debit.amountEUR)}
          </Text>
          {/*
            The time is shown only when the bank actually gave one
            (Debit.hasTime). It gave none on all 100 rows of the first real
            dump, and the backend's midnight-UTC placeholder rendered as a
            confident "02:00" on every charge — precision nobody had. joinMeta
            drops the separator with the part, so the line reads
            "Card payment · Aug 18, 2026" rather than trailing a middot.
          */}
          <Text style={[styles.hint, { color: theme.textMuted }]}>
            {joinMeta(
              debit.paymentType,
              formatLongDate(debit.timestamp),
              debit.hasTime && formatTime(debit.timestamp),
            )}
          </Text>
        </View>

        {/* Why this particular charge is good or bad, worded per R12a — the
            same three reasons the notification body uses. */}
        <View style={[styles.card, { backgroundColor: theme.surface }]}>
          <View style={styles.verdictRow}>
            <Text style={[styles.infoLabel, { color: theme.textMuted }]}>This debit</Text>
            <View style={[styles.pill, { backgroundColor: debitBg }]}>
              <Text style={[styles.pillText, { color: debitColor }]}>
                {debitIsBad ? 'Bad' : 'Good'}
              </Text>
            </View>
          </View>
          {reason != null && (
            <Text style={[styles.infoValue, { color: theme.text }]}>{reason}</Text>
          )}
        </View>

        {/*
          Both of these are usually absent, which the fixtures hid: in the first
          real dump the creditor IBAN was present on 1 of 92 debits and a
          remittance text on 4 of 100. A labelled row with nothing after it
          reads as data the app lost, so each row appears only when the bank
          actually sent the field — and when neither did, the card goes too
          rather than sitting empty.
        */}
        {(hasIban || hasReference) && (
          <View style={[styles.card, { backgroundColor: theme.surface }]}>
            {hasIban && (
              <View style={styles.infoRow}>
                <Text style={[styles.infoLabel, { color: theme.textMuted }]}>Payee IBAN</Text>
                <Text style={[styles.infoValue, { color: theme.text }]}>{payee.iban}</Text>
              </View>
            )}
            {hasIban && hasReference && (
              <View style={[styles.divider, { backgroundColor: theme.border }]} />
            )}
            {hasReference && (
              <View style={styles.infoRow}>
                <Text style={[styles.infoLabel, { color: theme.textMuted }]}>Reference</Text>
                <Text style={[styles.infoValue, { color: theme.text }]}>{debit.reference}</Text>
              </View>
            )}
          </View>
        )}

        {/*
          Display-only: this screen shows the debit and the payee's current
          status, but edits neither. Good/bad and the alert limit both live on
          the Edit Rule screen — reached via "Manage" below — so exactly one
          place owns that state. See mocks/03-debit-detail.html.
        */}
        <Pressable
          onPress={() => navigation.navigate('PayeeEdit', { payeeId: payee.id })}
          style={[styles.card, styles.manageRow, { backgroundColor: theme.surface }]}
        >
          <View style={styles.manageRowLeft}>
            <Text style={[styles.infoLabel, { color: theme.textMuted }]}>Payee status</Text>
            <Text style={[styles.payeeNameSmall, { color: theme.text }]} numberOfLines={1}>
              {payeeSubtitle}
            </Text>
          </View>
          <View style={styles.manageRowRight}>
            <View style={[styles.pill, { backgroundColor: payeeBg }]}>
              <Text style={[styles.pillText, { color: payeeColor }]}>
                {payeeIsGood ? 'Good' : 'Bad'}
              </Text>
            </View>
            <Text style={[styles.manageLink, { color: theme.textMuted }]}>Manage ›</Text>
          </View>
        </Pressable>
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    flex: 1,
    position: 'relative',
  },
  navBar: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 16,
    paddingTop: 8,
    paddingBottom: 12,
  },
  navLink: {
    fontSize: 15,
  },
  navTitle: {
    fontSize: 17,
    fontWeight: '600',
  },
  navSpacer: {
    width: 44,
  },
  content: {
    padding: 16,
    gap: 12,
  },
  card: {
    borderRadius: 14,
    padding: 16,
    gap: 8,
  },
  summaryCard: {
    alignItems: 'center',
  },
  avatarLarge: {
    width: 56,
    height: 56,
    borderRadius: 28,
    alignItems: 'center',
    justifyContent: 'center',
  },
  avatarLargeText: {
    fontWeight: '700',
    fontSize: 18,
  },
  payeeName: {
    fontSize: 18,
    fontWeight: '600',
  },
  payeeNameSmall: {
    fontSize: 15,
    fontWeight: '600',
  },
  amount: {
    fontSize: 22,
    fontWeight: '600',
  },
  hint: {
    fontSize: 12,
    lineHeight: 17,
  },
  notFoundText: {
    padding: 16,
  },
  verdictRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  infoRow: {
    gap: 2,
  },
  infoLabel: {
    fontSize: 12,
  },
  infoValue: {
    fontSize: 14,
    fontWeight: '600',
  },
  divider: {
    height: 1,
  },
  manageRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  manageRowLeft: {
    flex: 1,
    minWidth: 0,
    gap: 2,
  },
  manageRowRight: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
  },
  manageLink: {
    fontSize: 14,
  },
  pill: {
    borderRadius: 999,
    paddingVertical: 3,
    paddingHorizontal: 8,
  },
  pillText: {
    fontSize: 11,
    fontWeight: '600',
  },
});
