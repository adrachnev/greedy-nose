// The local rule store. Rules are the one piece of user-created state this
// app has (R4 — mark a payee good/bad, optionally set a €-limit), and until
// now they lived only in a module-level array in src/mocks/data.ts: reload the
// JS bundle or restart the app and every rule was gone. That was a real bug
// the moment the app started showing real bank data rather than fixtures.
//
// Shaped like src/data/backendFeed.ts on purpose, and for the same reasons:
// module-level state, a Set<Listener>, a getX() snapshot for
// useSyncExternalStore, and the same defensive-parsing posture
// toPayee/toDebit apply there — applied here to JSON read back off disk
// instead of off a socket, which deserves exactly as little trust.
//
// Unlike payees/debits, rules are NOT behind the USE_BACKEND flag: the
// backend deliberately sends no classification (R6 — the client derives it
// from the rule), so there is nothing on the wire to switch between, and this
// store is the only source of truth a rule ever has, under either flag.

import AsyncStorage from '@react-native-async-storage/async-storage';
import { Rule, RuleDraft } from '../domain/model';
import { FIXTURE_SEED_RULES } from '../mocks/data';
import { USE_BACKEND } from './config';

/**
 * Versioned in the key itself: a future breaking schema change moves to
 * `v2` and starts clean rather than needing migration code, since this is a
 * single-user local cache, not a database.
 */
const RULES_KEY = '@greedy-nose/rules/v1';

// Starts empty rather than undefined/null, and stays that way until hydration
// resolves. That is intentional and matches R4b: no rule = unreviewed = bad,
// which is already the correct answer for "haven't loaded yet" — it fails
// safe (a genuinely-bad payee is never shown good during the brief gap) and
// needs no loading UI, consistent with the rest of the app.
let rules: Rule[] = [];

type Listener = () => void;
const listeners = new Set<Listener>();

function notify(): void {
  listeners.forEach(listener => listener());
}

export function getRules(): Rule[] {
  return rules;
}

let hydrationStarted = false;

/**
 * Subscribing is also what starts hydration, mirroring backendFeed.ts's
 * subscribe(). Reading AsyncStorage at module-load time instead would fire in
 * every Jest test that merely imports hooks.ts, including ones that only
 * touch payees/debits and never render a rule.
 */
export function subscribe(listener: Listener): () => void {
  listeners.add(listener);
  if (!hydrationStarted) {
    hydrationStarted = true;
    // Not awaited: hydration reports through the store itself (setRules +
    // notify), same as backendFeed.subscribe() kicking off refresh().
    hydrate();
  }
  return () => {
    listeners.delete(listener);
  };
}

function setRules(next: Rule[]): void {
  rules = next;
  notify();
}

/** What a genuinely first launch (or a cleared/reinstalled app) seeds with. */
function seedRules(): Rule[] {
  // Fixture mode gets the demo mix (see FIXTURE_SEED_RULES's own comment for
  // why these five payees specifically). Backend mode gets nothing: fixture
  // payee ids (`payee-netflix`) and backend payee ids (`name:LIDL CONNECT`)
  // are different schemes, so the demo rules would not match anything real —
  // seeding them there would be inert at best and confusing demo data at
  // worst.
  return USE_BACKEND ? [] : FIXTURE_SEED_RULES;
}

/**
 * The fallback every hydrate() branch below reaches for once it has decided
 * the stored file is unusable (missing, unreadable, corrupted JSON, or not an
 * array): install the seed in memory and persist it, so "unusable" always
 * resolves to the same well-defined state through one path rather than three
 * copies of the same three lines that would drift apart from each other the
 * next time one of them needs to change.
 */
function reseed(): void {
  const seed = seedRules();
  setRules(seed);
  persist(seed);
}

async function hydrate(): Promise<void> {
  let raw: string | null = null;
  try {
    raw = await AsyncStorage.getItem(RULES_KEY);
  } catch (error) {
    console.warn('[rulesStore] failed to read rules from storage; starting from the seed', error);
  }

  if (raw == null) {
    reseed();
    return;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (error) {
    console.warn('[rulesStore] stored rules were not valid JSON; reseeding', error);
    reseed();
    return;
  }

  if (!Array.isArray(parsed)) {
    console.warn('[rulesStore] stored rules were not an array; reseeding', parsed);
    reseed();
    return;
  }

  const valid: Rule[] = [];
  for (const entry of parsed) {
    const rule = toRule(entry);
    if (rule) {
      valid.push(rule);
    } else {
      console.warn('[rulesStore] dropping malformed stored rule', entry);
    }
  }
  setRules(valid);
}

/**
 * Same distrust backendFeed.ts's toPayee/toDebit apply to JSON off a socket,
 * applied here to JSON off disk: a corrupted or hand-edited value drops the
 * one bad row rather than losing every rule to a single bad entry.
 */
function toRule(raw: unknown): Rule | null {
  const r = raw as Partial<Rule> | null;
  if (typeof r?.payeeId !== 'string' || r.payeeId.length === 0) {
    return null;
  }
  if (r.classification !== 'good' && r.classification !== 'bad') {
    return null;
  }
  if (
    r.amountEUR != null &&
    (typeof r.amountEUR !== 'number' || !Number.isFinite(r.amountEUR) || r.amountEUR <= 0)
  ) {
    return null;
  }
  const rule: Rule = { payeeId: r.payeeId, classification: r.classification };
  if (r.amountEUR != null) {
    rule.amountEUR = r.amountEUR;
  }
  return rule;
}

// --- Writes ------------------------------------------------------------

/**
 * A promise queue rather than a bare, unawaited `AsyncStorage.setItem(...)`
 * per save: PayeeEditScreen calls saveRule() synchronously immediately before
 * navigation.goBack(), so two saves in quick succession (e.g. Save then
 * Clear, or two edits before the first write lands) must not let their
 * underlying native writes resolve out of order and silently revert the
 * second one on disk. The in-memory `rules` array above is always correct
 * immediately via the synchronous reassignment in setRules(); this queue only
 * protects what ends up on disk.
 *
 * The leading `.catch(() => {})` matters as much as the write itself: without
 * it, one failed `setItem` leaves `writeQueue` permanently rejected, and every
 * `.then()` chained onto a rejected promise skips its callback rather than
 * running it — so a single transient failure would silently stop every save
 * after it from ever reaching disk for the rest of the session, with the
 * in-memory `rules` array still looking correct the whole time. The trailing
 * `.catch()` reports (rather than swallows) the specific write that failed,
 * without poisoning the queue for the next one.
 */
let writeQueue: Promise<unknown> = Promise.resolve();
function persist(next: Rule[]): void {
  writeQueue = writeQueue
    .catch(() => {})
    .then(() => AsyncStorage.setItem(RULES_KEY, JSON.stringify(next)))
    .catch(error => console.warn('[rulesStore] failed to persist rules', error));
}

/**
 * Upserts a payee's whole rule — classification and amount together, in one
 * reassignment and one notification. PayeeEditScreen is a form with a single
 * commit point (Save), so a per-field setter would only let a save land half
 * applied and make subscribers re-render twice for one user action.
 *
 * This is the *only* thing that moves a payee's classification: nothing the
 * app does on its own ever flips it (R8). Creating a rule where there was none
 * is what "reviewed" means (R4b), and rules are never deleted (R13) — marking
 * a payee bad already expresses everything a delete would.
 *
 * `amountEUR: undefined` means no limit — every charge from that payee is good
 * (R4a). The caller decides what to pass while a payee is bad; the amount is
 * kept rather than wiped there (R8a/R14), it simply has no effect (R5).
 *
 * Every write reassigns `rules` to a *new* array rather than mutating an
 * element in place, so useSyncExternalStore's getSnapshot() returns a changed
 * reference and subscribers actually re-render — this codebase shipped that
 * exact regression once already (see the array-reference discipline pinned in
 * this module's tests).
 */
export function saveRule(payeeId: string, draft: RuleDraft): void {
  const existing = rules.find(r => r.payeeId === payeeId);
  if (
    existing &&
    existing.classification === draft.classification &&
    existing.amountEUR === draft.amountEUR
  ) {
    return;
  }
  const next: Rule = { payeeId, classification: draft.classification };
  if (draft.amountEUR != null) {
    next.amountEUR = draft.amountEUR;
  }
  const nextRules = existing ? rules.map(r => (r.payeeId === payeeId ? next : r)) : [...rules, next];
  setRules(nextRules);
  persist(nextRules);
}
