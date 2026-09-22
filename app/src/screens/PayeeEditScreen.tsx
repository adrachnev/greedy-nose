import { NavigationProp, ParamListBase, RouteProp } from '@react-navigation/native';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  useColorScheme,
  View,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import DataSourceBadge from '../components/DataSourceBadge';
import Toast from '../components/Toast';
import { usePayee, useRuleForPayee, useSavePayeeRule } from '../data/hooks';
import { classifyPayee } from '../domain/classification';
import { Classification } from '../domain/model';
import { unknownPayee } from '../domain/payees';
import { dark, light } from '../theme/colors';
import { parseAmountEUR } from '../utils/parseAmount';

const TOAST_DURATION_MS = 2000;
const INVALID_AMOUNT_MESSAGE = 'Enter a valid positive amount, or leave it blank';

// This screen is registered in both RulesStackParamList and
// DebitsStackParamList (reachable from RulesListScreen and, via the
// "Manage" link, from DebitDetailScreen) — see mocks/03-debit-detail.html.
// A local, minimal param list keeps this screen's prop typing independent of
// either specific parent stack, avoiding a type mismatch (or `as any`) from
// dual registration. The screen only ever calls navigation.goBack(), which
// NavigationProp<ParamListBase> supports.
type PayeeEditParamList = { PayeeEdit: { payeeId: string } };
type Props = {
  route: RouteProp<PayeeEditParamList, 'PayeeEdit'>;
  navigation: NavigationProp<ParamListBase>;
};

/**
 * A form with a single commit point, matching mocks/04b-payee-edit.html and
 * mocks/04d-payee-edit-bad.html: the toggle and the amount field are *draft*
 * state, and nothing is written until Save (or Clear, which is a save with the
 * amount removed). Both then navigate back, as the mock's buttons do.
 *
 * That is what makes the screen honest — the previous version wrote the
 * classification on tap while the amount waited for Save, so a half-edited
 * screen was already half-committed and leaving via Back kept part of it. Now
 * Back discards the draft, whole.
 */
export default function PayeeEditScreen({ route, navigation }: Props) {
  const theme = useColorScheme() === 'dark' ? dark : light;
  const insets = useSafeAreaInsets();
  const knownPayee = usePayee(route.params.payeeId);
  const rule = useRuleForPayee(route.params.payeeId);
  const savePayeeRule = useSavePayeeRule();

  // A payee the app has no record of is still rule-able, and that is not a
  // concession: a rule is keyed on the payee *id* (R4), the store of payees is
  // a separate thing, and classification is derived from the rule at read time
  // (R6). So saving here classifies that id's debits exactly as it would for
  // any other payee. This screen used to answer "Payee not found." instead,
  // which turned the debit detail's Manage link into a dead end for precisely
  // the charges that most need reviewing — the ones nobody can name.
  const payee = useMemo(
    () => knownPayee ?? unknownPayee(route.params.payeeId),
    [knownPayee, route.params.payeeId],
  );

  const [draftClassification, setDraftClassification] = useState<Classification>(
    classifyPayee(rule),
  );
  const [amountText, setAmountText] = useState(rule?.amountEUR?.toString() ?? '');
  const [toastMessage, setToastMessage] = useState<string | null>(null);
  const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  // Reseed the draft if the underlying rule changes out from under us (e.g.
  // navigating here for a different payee while this screen is still mounted).
  useEffect(() => {
    setDraftClassification(classifyPayee(rule));
    setAmountText(rule?.amountEUR?.toString() ?? '');
  }, [rule]);

  useEffect(() => {
    return () => clearTimeout(toastTimer.current);
  }, []);

  function showToast(message: string) {
    setToastMessage(message);
    clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToastMessage(null), TOAST_DURATION_MS);
  }

  const isGood = draftClassification === 'good';

  /**
   * Save is the only thing that ever moves a payee's classification (R8), and
   * it commits both fields at once.
   *
   * While the draft is bad the amount field is not on screen (R14), so there
   * is nothing to validate and nothing the user could fix if validation
   * failed: the payee's *saved* amount is carried through untouched, kept
   * rather than wiped (R8a), and comes back the next time they are good.
   */
  function handleSave() {
    if (!isGood) {
      savePayeeRule(payee, { classification: 'bad', amountEUR: rule?.amountEUR });
      navigation.goBack();
      return;
    }
    const parsed = parseAmountEUR(amountText);
    if (!parsed.ok) {
      // Stay put: navigating away would hide the field the user has to fix.
      showToast(INVALID_AMOUNT_MESSAGE);
      return;
    }
    savePayeeRule(payee, { classification: 'good', amountEUR: parsed.amountEUR });
    navigation.goBack();
  }

  /** Drops the limit and keeps the payee good — "no limit", not "alert on
   * everything" (R4a). Only reachable while the draft is good. */
  function handleClear() {
    savePayeeRule(payee, { classification: 'good', amountEUR: undefined });
    navigation.goBack();
  }

  const avatarColor = isGood ? theme.good : theme.bad;
  const avatarBg = isGood ? theme.goodBg : theme.badBg;

  return (
    <View style={[styles.screen, { backgroundColor: theme.bg, paddingTop: insets.top }]}>
      <DataSourceBadge />
      <View style={styles.navBar}>
        <Pressable onPress={() => navigation.goBack()} hitSlop={8}>
          <Text style={[styles.navLink, { color: theme.accent }]}>‹ Back</Text>
        </Pressable>
        <Text style={[styles.navTitle, { color: theme.text }]}>Edit Rule</Text>
        <View style={styles.navSpacer} />
      </View>

      <ScrollView
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode="on-drag"
      >
        <View style={[styles.card, styles.summaryCard, { backgroundColor: theme.surface }]}>
          <View style={[styles.avatarLarge, { backgroundColor: avatarBg }]}>
            <Text style={[styles.avatarLargeText, { color: avatarColor }]}>{payee.initials}</Text>
          </View>
          <Text style={[styles.payeeName, { color: theme.text }]}>{payee.name}</Text>
        </View>

        {/*
          Draft only: tapping either half changes what this screen shows, and
          nothing else. The write happens on Save (R8 — the user moves this
          flag, and only when they say so).
        */}
        <View style={styles.toggleRow}>
          <Pressable
            onPress={() => setDraftClassification('good')}
            style={[
              styles.toggleBtn,
              { borderColor: theme.border, backgroundColor: theme.surface },
              isGood && { borderColor: theme.good, backgroundColor: theme.goodBg },
            ]}
          >
            <Text
              style={[styles.toggleBtnText, { color: theme.textMuted }, isGood && { color: theme.good }]}
            >
              Good
            </Text>
          </Pressable>
          <Pressable
            onPress={() => setDraftClassification('bad')}
            style={[
              styles.toggleBtn,
              { borderColor: theme.border, backgroundColor: theme.surface },
              !isGood && { borderColor: theme.bad, backgroundColor: theme.badBg },
            ]}
          >
            <Text
              style={[styles.toggleBtnText, { color: theme.textMuted }, !isGood && { color: theme.bad }]}
            >
              Bad
            </Text>
          </Pressable>
        </View>

        {/*
          R14: the limit is hidden — not disabled — while the draft is bad,
          because it has no effect there: a bad payee alerts on every charge,
          whatever its size (R5), so a greyed-out field would only invite the
          user to fight a control that does nothing. It is hidden, not wiped
          (R8a): `amountText` survives the round trip, so Bad → Good brings the
          draft amount straight back, and saving while bad keeps the payee's
          stored amount. Save stays in both states (mocks/04d draws it) — it is
          what commits the classification.
        */}
        {isGood ? (
          <>
            <Text style={[styles.sectionLabel, { color: theme.textMuted }]}>
              Alert limit (optional)
            </Text>
            <Text style={[styles.hint, { color: theme.textMuted }]}>
              Charges up to and including this amount are good — you're only alerted above it.
              Leave blank to accept every charge from this payee. Must be a positive amount.
            </Text>

            <View style={[styles.card, { backgroundColor: theme.surface }]}>
              <View style={styles.infoRow}>
                <Text style={[styles.infoLabel, { color: theme.textMuted }]}>
                  Only alert if amount exceeds
                </Text>
                <View style={[styles.inputRow, { borderColor: theme.border }]}>
                  <Text style={[styles.inputPrefix, { color: theme.textMuted }]}>€</Text>
                  <TextInput
                    value={amountText}
                    onChangeText={setAmountText}
                    placeholder="No limit"
                    placeholderTextColor={theme.textMuted}
                    keyboardType="decimal-pad"
                    style={[styles.input, { color: theme.text }]}
                  />
                </View>
              </View>
            </View>
          </>
        ) : (
          <Text style={[styles.hint, { color: theme.textMuted }]}>
            Every charge from this payee alerts you, whatever the amount. Alert limits only apply
            to good payees — mark this one Good to set one.
          </Text>
        )}

        {/* The mock's `<div style="flex:1">`: buttons sit at the bottom of the
            screen, and scroll only once the content outgrows it. */}
        <View style={styles.spacer} />

        <Pressable
          onPress={handleSave}
          style={[styles.btnPrimary, { backgroundColor: theme.accent }]}
        >
          <Text style={styles.btnPrimaryText}>Save</Text>
        </Pressable>

        {isGood && (
          <>
            <Pressable
              onPress={handleClear}
              style={[styles.btnOutline, { borderColor: theme.border }]}
            >
              <Text style={[styles.btnOutlineText, { color: theme.text }]}>Clear alert limit</Text>
            </Pressable>
            <Text style={[styles.hint, styles.centerText, { color: theme.textMuted }]}>
              Clears the amount above — every charge from this payee counts as good again. Status
              (Good) is unchanged.
            </Text>
          </>
        )}
      </ScrollView>
      <Toast message={toastMessage} />
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
    // With the spacer below, this is the mock's full-height column: the
    // buttons rest at the bottom until the content is tall enough to scroll.
    flexGrow: 1,
  },
  spacer: {
    flex: 1,
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
  hint: {
    fontSize: 12,
    lineHeight: 17,
  },
  centerText: {
    textAlign: 'center',
  },
  infoRow: {
    gap: 6,
  },
  infoLabel: {
    fontSize: 12,
  },
  inputRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    borderWidth: 1,
    borderRadius: 8,
    paddingHorizontal: 10,
  },
  inputPrefix: {
    fontSize: 15,
    fontWeight: '600',
  },
  input: {
    flex: 1,
    fontSize: 15,
    fontWeight: '600',
    paddingVertical: 8,
  },
  sectionLabel: {
    fontSize: 13,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.3,
    marginTop: 8,
    marginHorizontal: 4,
  },
  toggleRow: {
    flexDirection: 'row',
    gap: 8,
  },
  toggleBtn: {
    flex: 1,
    minHeight: 44,
    alignItems: 'center',
    justifyContent: 'center',
    borderRadius: 10,
    borderWidth: 1,
  },
  toggleBtnText: {
    fontSize: 14,
    fontWeight: '600',
  },
  btnPrimary: {
    minHeight: 44,
    borderRadius: 12,
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 14,
    marginTop: 8,
  },
  btnPrimaryText: {
    color: '#fff',
    fontSize: 16,
    fontWeight: '600',
  },
  btnOutline: {
    minHeight: 44,
    borderRadius: 12,
    borderWidth: 1,
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: 14,
  },
  btnOutlineText: {
    fontSize: 16,
    fontWeight: '600',
  },
});
