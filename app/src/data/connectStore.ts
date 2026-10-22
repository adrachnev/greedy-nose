// Connecting a bank from the app (Story 5.7), built on the attempt of Story 5.3:
// ask the backend for a one-time link and an attempt id, open the link, then
// read the state of the attempt (waiting, choose an account, saved, failed,
// expired) until it ends. The way to the bank and back is the one decided in
// Story 5.1a (ARCHITECTURE.md, "Consent lifecycle"): openConnectLink.ts opens
// the Custom Tab, the callback page returns with `greedynose://connected`.
//
// Module-level like deviceStore.ts and backendFeed.ts, same posture: nothing
// here throws or rejects unhandled; every failure ends as a flow state the
// screen can show. Logs carry the path without the attempt id and the HTTP
// status (backendCall.ts), never a link, an id, a bank or an account name. The
// one-time link is a credential until it is used, so it is never kept in state
// or storage.

import AsyncStorage from '@react-native-async-storage/async-storage';
import { getPayees, getState as getFeedState } from './backendFeed';
import { backendCall } from './backendCall';
import { rememberPayeesBeforeAttempt } from './onboardingOffer';
import { dismissConnectLink, openConnectLink } from './openConnectLink';

const TAG = 'connectStore';

// --- Wire shapes --------------------------------------------------------------

export type AttemptState = 'waiting' | 'choose_account' | 'saved' | 'failed' | 'expired';

const ATTEMPT_STATES: readonly AttemptState[] = ['waiting', 'choose_account', 'saved', 'failed', 'expired'];

/** What the backend names, plus the three the app itself can end an attempt with. */
export type FailureReason =
  | 'cancelled'
  | 'bank-error'
  | 'code-not-traded'
  | 'bank-answer-unusable'
  | 'store-failed'
  | 'no-supported-account'
  | 'unknown' // the backend gave a reason we do not know, or none
  | 'link' // the app could not get a link (no network, backend error)
  | 'browser' // no browser opened the link
  | 'already-connected'; // the backend refused: this bank is connected and not expired

const BACKEND_REASONS: readonly FailureReason[] = [
  'cancelled',
  'bank-error',
  'code-not-traded',
  'bank-answer-unusable',
  'store-failed',
  'no-supported-account',
];

/** One row of the account choice. `last4` is the end of the IBAN, never more. */
export type ChoiceAccount = {
  id: string;
  name: string;
  /** `current`, `savings`, `card`, `loan` or `account`; the screen words it. */
  kind: string;
  last4: string | null;
  suggested: boolean;
};

function textOrNull(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

/** The state of `GET /connect-attempts/{id}`; `null` when the body is not that shape (never guessed). */
export function parseAttemptBody(body: unknown): { state: AttemptState; reason: FailureReason } | null {
  if (typeof body !== 'object' || body === null) {
    return null;
  }
  const { state, reason } = body as Record<string, unknown>;
  if (!ATTEMPT_STATES.includes(state as AttemptState)) {
    return null;
  }
  const known = BACKEND_REASONS.includes(reason as FailureReason);
  return { state: state as AttemptState, reason: known ? (reason as FailureReason) : 'unknown' };
}

function parseAccount(raw: unknown): ChoiceAccount | null {
  if (typeof raw !== 'object' || raw === null) {
    return null;
  }
  const row = raw as Record<string, unknown>;
  const id = textOrNull(row.id);
  const name = textOrNull(row.name);
  if (id === null || name === null) {
    return null;
  }
  // The backend sends four characters; cut anyway so a full IBAN could never reach a screen.
  const last4 = textOrNull(row.last4)?.slice(-4) ?? null;
  return { id, name, kind: textOrNull(row.kind) ?? 'account', last4, suggested: row.suggested === true };
}

/** The rows of `GET .../accounts`; `null` when the body is not that shape or lists nothing to choose. */
export function parseAccountsBody(body: unknown): ChoiceAccount[] | null {
  const list =
    typeof body === 'object' && body !== null ? (body as Record<string, unknown>).accounts : undefined;
  if (!Array.isArray(list)) {
    return null;
  }
  const accounts = list.map(parseAccount).filter((account): account is ChoiceAccount => account !== null);
  return accounts.length > 0 ? accounts : null;
}

// --- Flow state -----------------------------------------------------------------

/**
 * - `idle`: nothing running (or reset).
 * - `opening`: asking for the link / opening it.
 * - `waiting`: the user is at the bank; `noAnswer` after repeated reads that got no answer.
 * - `choose_account`: the bank has several accounts; `posting` while the choice is sent,
 *   `choiceFailed` when it could not be saved (the list stays).
 * - `saved`, `failed`, `expired`: the attempt is over; the screen says so and leaves.
 */
type Target = { bank: string; country: string };
export type ConnectFlow =
  | { step: 'idle' }
  | ({ step: 'opening' } & Target)
  | ({ step: 'waiting'; noAnswer: boolean } & Target)
  | ({ step: 'choose_account'; accounts: ChoiceAccount[]; posting: boolean; choiceFailed: boolean } & Target)
  | ({ step: 'saved' } & Target)
  | ({ step: 'failed'; reason: FailureReason } & Target)
  | ({ step: 'expired' } & Target);

/** How often a waiting attempt is read while its screen is visible. */
export const CONNECT_POLL_MS = 4000;

/** Reads in a row without an answer before the screen says "no answer from the backend". */
export const NO_ANSWER_AFTER_READS = 3;

/** The one-time link lives 30 minutes; an older attempt can no longer succeed. */
export const ATTEMPT_MAX_AGE_MS = 30 * 60 * 1000;

/** A tab-dismiss this soon after a check ended is the echo of our own close, not a new return. */
const RECENT_CHECK_MS = 2000;

/** Fewer normalized characters than this match too much ("n", "bank"). */
export const MIN_NAME_LENGTH = 2;

export const normalizeBankName = (name: string) =>
  name.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');

/** Whether a typed name is long enough to look anything up. */
export function isUsableBankName(typed: string): boolean {
  return normalizeBankName(typed).length >= MIN_NAME_LENGTH;
}

type Attempt = { id: string; bank: string; country: string; startedAt: number };

const IDLE: ConnectFlow = { step: 'idle' };
let flow = IDLE as ConnectFlow; // widened: async code below sees every step, not just "idle"
/** The running attempt; `null` while idle or once it ended. The id is no secret, the API key protects the reads. */
let attempt: Attempt | null = null;
let failedReads = 0;
let lastCheckEndedAt = 0;

const listeners = new Set<() => void>();

const isEnded = (step: ConnectFlow['step']) => step === 'saved' || step === 'failed' || step === 'expired';

function setFlow(next: ConnectFlow): void {
  flow = next;
  if (next.step === 'idle' || isEnded(next.step)) {
    attempt = null;
    failedReads = 0;
    clearAttempt();
  }
  listeners.forEach(listener => listener());
}

export function getConnectFlow(): ConnectFlow {
  return flow;
}

export function subscribeToConnectFlow(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Back to nothing running. Does not tell the backend (see cancelAttempt). */
export function resetConnectFlow(): void {
  startSeq += 1;
  setFlow(IDLE);
}

/**
 * The screen of an ended attempt is gone: forget the attempt. Does nothing when the flow is no longer ended, so a
 * bank picked while the old screen was leaving keeps its start (and its link reply).
 */
export function forgetEndedAttempt(): void {
  if (isEnded(flow.step)) {
    setFlow(IDLE);
  }
}

/** Test-only. */
export function __resetConnectStoreForTests(): void {
  resetConnectFlow();
  inFlightCheck = null;
  restoring = null;
  returning = null;
  lastCheckEndedAt = 0;
  listeners.clear();
}

const attemptPath = (id: string, suffix = '') => `/connect-attempts/${encodeURIComponent(id)}${suffix}`;
const ATTEMPT_LOG_PATH = '/connect-attempts/{id}';

// --- Starting -------------------------------------------------------------------

/** Counts the starts; a reply that comes back for an older start than the newest (or after a cancel) is stale. */
let startSeq = 0;

const isActive = (step: ConnectFlow['step']) =>
  step === 'opening' || step === 'waiting' || step === 'choose_account';

function idOf(body: unknown): string | null {
  return typeof body === 'object' && body !== null ? textOrNull((body as Record<string, unknown>).attemptId) : null;
}

/** Best effort: closes an attempt now. The answer does not matter; it ends at its deadline anyway. */
function cancelAtBackend(id: string | null): void {
  if (id !== null) {
    backendCall(TAG, attemptPath(id, '/cancel'), `${ATTEMPT_LOG_PATH}/cancel`, { method: 'POST' });
  }
}

/**
 * Starts an attempt for a bank of the search: asks for the link and the attempt
 * id, keeps the attempt (see below), opens the link. When the user dismisses
 * the Custom Tab the opener resolves and the state is read at once; with the
 * plain browser the way back is the link or the app coming to the foreground.
 */
export async function startConnect(bankName: string, country: string): Promise<void> {
  const bank = bankName.trim();
  // One attempt at a time: a running one is never replaced (it would be orphaned at the backend).
  // The caller shows the running attempt's screen instead.
  if (!isUsableBankName(bank) || isActive(flow.step)) {
    return;
  }
  const run = ++startSeq;
  // The payees known now are not new when the first sync is done (Story 5.9, onboardingOffer.ts).
  // A feed that is not loaded cannot say what was known: then nothing is offered for this attempt.
  rememberPayeesBeforeAttempt(getFeedState().status === 'ready' ? getPayees().map(payee => payee.id) : null);
  const target: Target = { bank, country };
  lastCheckEndedAt = 0;
  failedReads = 0;
  setFlow({ step: 'opening', ...target });

  const reply = await backendCall(TAG, '/connect-links', '/connect-links', {
    method: 'POST',
    body: country ? { aspsp: bank, country } : { aspsp: bank },
  });
  if (run !== startSeq) {
    // The user left (and maybe started another bank) while the link was being asked for: this reply is
    // nobody's. Its attempt, if it came to exist, is closed at once instead of at its deadline.
    cancelAtBackend(reply.ok ? idOf(reply.body) : null);
    return;
  }
  if (!reply.ok) {
    setFlow({ step: 'failed', ...target, reason: reply.status === 409 ? 'already-connected' : 'link' });
    return;
  }
  const body = (typeof reply.body === 'object' && reply.body !== null ? reply.body : {}) as Record<string, unknown>;
  const url = textOrNull(body.url);
  const id = idOf(body);
  if (url === null || !/^https?:\/\//i.test(url) || id === null) {
    cancelAtBackend(id);
    setFlow({ step: 'failed', ...target, reason: 'link' });
    return;
  }

  const started: Attempt = { id, ...target, startedAt: Date.now() };
  try {
    // Saved while still 'opening': a foreground event in between cannot start an early check.
    attempt = started;
    await saveAttempt(started);
    if (attempt !== started) {
      return; // cancelled or replaced while the attempt was being stored
    }
    setFlow({ step: 'waiting', noAnswer: false, ...target });
    const dismissed = await openConnectLink(url);
    if (dismissed && getConnectFlow().step === 'waiting' && Date.now() - lastCheckEndedAt > RECENT_CHECK_MS) {
      await checkAttempt();
    }
  } catch {
    if (getConnectFlow().step === 'waiting' && attempt === started) {
      cancelAtBackend(started.id);
      setFlow({ step: 'failed', ...target, reason: 'browser' });
    }
  }
}

// --- Reading the state ----------------------------------------------------------

let inFlightCheck: Promise<void> | null = null;

/**
 * Reads the state of the running attempt. Does nothing unless one is waiting or
 * showing the account choice, so a stray foreground event costs nothing.
 * Concurrent calls share one request: the link event and the foreground event
 * usually arrive together.
 *
 * The Custom Tab is closed when the attempt leaves "waiting" (the user is done
 * at the bank) or when `viaLink` (the callback page itself sent the user back,
 * so the page is finished). A foreground event alone must not close it: the
 * user may be in a password manager or the bank's app, and the page would be
 * gone on return.
 */
export function checkAttempt(viaLink = false): Promise<void> {
  if (inFlightCheck !== null) {
    if (viaLink) {
      dismissConnectLink();
    }
    return inFlightCheck;
  }
  if (attempt === null || (flow.step !== 'waiting' && flow.step !== 'choose_account')) {
    return Promise.resolve();
  }
  inFlightCheck = runCheck(attempt, viaLink).finally(() => {
    inFlightCheck = null;
    lastCheckEndedAt = Date.now();
  });
  return inFlightCheck;
}

/** A read that got no usable answer; a few in a row turn the waiting screen into "no answer". */
function readFailed(): void {
  failedReads += 1;
  if (flow.step === 'waiting' && failedReads >= NO_ANSWER_AFTER_READS && !flow.noAnswer) {
    setFlow({ ...flow, noAnswer: true });
  }
}

async function endWith(current: Attempt, next: ConnectFlow): Promise<void> {
  await dismissConnectLink();
  if (attempt === current) {
    setFlow(next); // not if the user cancelled while the tab was closing
  }
}

async function runCheck(current: Attempt, viaLink: boolean): Promise<void> {
  const target: Target = { bank: current.bank, country: current.country };
  if (Date.now() - current.startedAt >= ATTEMPT_MAX_AGE_MS) {
    await endWith(current, { step: 'expired', ...target });
    return;
  }
  const reply = await backendCall(TAG, attemptPath(current.id), ATTEMPT_LOG_PATH, { method: 'GET' });
  if (attempt !== current) {
    return; // cancelled or replaced while we were reading
  }
  if (!reply.ok && reply.status === 404) {
    await endWith(current, { step: 'expired', ...target });
    return;
  }
  const read = reply.ok ? parseAttemptBody(reply.body) : null;
  if (read === null) {
    readFailed();
    return;
  }
  failedReads = 0;

  switch (read.state) {
    case 'waiting':
      if (viaLink) {
        await dismissConnectLink();
      }
      if (flow.step === 'waiting' && flow.noAnswer) {
        setFlow({ ...flow, noAnswer: false });
      }
      return;
    case 'choose_account': {
      if (flow.step === 'choose_account') {
        return; // already showing it
      }
      const rows = await backendCall(TAG, attemptPath(current.id, '/accounts'), `${ATTEMPT_LOG_PATH}/accounts`, {
        method: 'GET',
      });
      const accounts = rows.ok ? parseAccountsBody(rows.body) : null;
      if (attempt !== current) {
        return;
      }
      if (accounts === null) {
        readFailed();
        return;
      }
      await dismissConnectLink();
      if (attempt === current) {
        setFlow({ step: 'choose_account', accounts, posting: false, choiceFailed: false, ...target });
      }
      return;
    }
    case 'saved':
      await endWith(current, { step: 'saved', ...target });
      return;
    case 'failed':
      await endWith(current, { step: 'failed', ...target, reason: read.reason });
      return;
    case 'expired':
      await endWith(current, { step: 'expired', ...target });
      return;
  }
}

// --- The account choice and leaving ---------------------------------------------

/** Sends the chosen row. A second tap while the first is on its way is ignored. */
export async function chooseAccount(accountId: string): Promise<void> {
  const current = attempt;
  const shown = flow;
  if (current === null || shown.step !== 'choose_account' || shown.posting) {
    return;
  }
  setFlow({ ...shown, posting: true, choiceFailed: false });
  const reply = await backendCall(TAG, attemptPath(current.id, '/choice'), `${ATTEMPT_LOG_PATH}/choice`, {
    method: 'POST',
    body: { accountId },
  });
  if (attempt !== current) {
    return;
  }
  if (reply.ok) {
    setFlow({ step: 'saved', bank: shown.bank, country: shown.country });
    return;
  }
  setFlow({ ...shown, posting: false, choiceFailed: reply.status !== 409 });
  if (reply.status === 409) {
    // The attempt is no longer where we thought (decided elsewhere, or past its deadline): show what the backend says.
    await checkAttempt();
    if (attempt === current && flow.step === 'choose_account') {
      setFlow({ ...flow, choiceFailed: true });
    }
  }
}

/**
 * Cancel, or going back, on an attempt that is waiting or showing the account
 * choice. The flow is left at once; the backend is told afterwards and a failed
 * call changes nothing: the attempt then ends by itself at its deadline and
 * nothing was saved.
 */
export async function cancelAttempt(): Promise<void> {
  const current = attempt;
  resetConnectFlow();
  if (current === null) {
    return;
  }
  dismissConnectLink();
  await backendCall(TAG, attemptPath(current.id, '/cancel'), `${ATTEMPT_LOG_PATH}/cancel`, { method: 'POST' });
}

// --- A running attempt survives the app process ---------------------------------
//
// Android may kill the app while the user is in the bank's browser, app or a
// password manager. The attempt is therefore written to AsyncStorage the moment
// the link is about to be opened, and picked up again on the next start or
// return. What is stored is only: the attempt id, the bank name and country of
// the search hit, and a start time. Never the link, a token, a session id, an
// IBAN, or any account. Every storage error behaves as "no attempt": the app
// works, it just cannot resume.

const ATTEMPT_KEY = 'connect.attempt.v2';

type StoredAttempt = Attempt;

/** Counts the writes of a new attempt, so a late clean-up of an old one cannot overwrite it. */
let storeGeneration = 0;

async function saveAttempt(value: Attempt): Promise<void> {
  storeGeneration += 1;
  try {
    await AsyncStorage.setItem(ATTEMPT_KEY, JSON.stringify(value satisfies StoredAttempt));
  } catch {
    console.warn(`[${TAG}] could not remember the running attempt`);
  }
}

/** Forgets the stored attempt. If it cannot be removed it is overwritten, so a finished attempt is never resumed. */
function clearAttempt(): void {
  const generation = storeGeneration;
  try {
    Promise.resolve(AsyncStorage.removeItem(ATTEMPT_KEY)).catch(() => {
      if (generation === storeGeneration) {
        Promise.resolve(AsyncStorage.setItem(ATTEMPT_KEY, 'ended')).catch(() => {});
      }
    });
  } catch {
    // Nothing stored, or no storage: nothing to forget.
  }
}

function parseStoredAttempt(raw: string | null): StoredAttempt | null {
  if (raw === null) {
    return null;
  }
  try {
    const value = JSON.parse(raw) as Record<string, unknown>;
    const { id, bank, country, startedAt } = value;
    if (
      typeof id !== 'string' ||
      id === '' ||
      typeof bank !== 'string' ||
      typeof country !== 'string' ||
      typeof startedAt !== 'number'
    ) {
      return null;
    }
    return { id, bank, country, startedAt };
  } catch {
    return null;
  }
}

let restoring: Promise<boolean> | null = null;

/** Puts a stored, still-fresh attempt back into the waiting state. `true` if it did. */
function restoreAttempt(): Promise<boolean> {
  if (flow.step !== 'idle') {
    return Promise.resolve(false);
  }
  if (restoring === null) {
    restoring = (async () => {
      let raw: string | null = null;
      try {
        raw = await AsyncStorage.getItem(ATTEMPT_KEY);
      } catch {
        return false;
      }
      const stored = parseStoredAttempt(raw);
      if (stored === null) {
        if (raw !== null) {
          clearAttempt();
        }
        return false;
      }
      const age = Date.now() - stored.startedAt;
      if (age < 0 || age >= ATTEMPT_MAX_AGE_MS || !isUsableBankName(stored.bank)) {
        clearAttempt();
        return false;
      }
      if (flow.step !== 'idle') {
        return false; // the user started something while we were reading
      }
      attempt = stored;
      failedReads = 0;
      lastCheckEndedAt = 0;
      flow = { step: 'waiting', noAnswer: false, bank: stored.bank, country: stored.country };
      listeners.forEach(listener => listener());
      return true;
    })().finally(() => {
      restoring = null;
    });
  }
  return restoring;
}

let returning: Promise<boolean> | null = null;

/**
 * The user is back in the app (start, foreground, or the app link). With an
 * attempt in memory this reads its state; after the process was killed it first
 * restores the stored attempt. Without any attempt: nothing.
 *
 * Resolves `true` when the user should be shown the attempt screen: an attempt
 * is running, or this call is what ended it. It resolves `false` when nothing
 * runs, when the attempt had ended before (its result was already shown), and
 * for a call that joins one already running (the app start and the app link
 * arrive together after a cold start; the first call reports for both).
 */
export function returnToApp(viaLink = false): Promise<boolean> {
  if (returning !== null) {
    if (viaLink) {
      dismissConnectLink();
    }
    return Promise.resolve(false);
  }
  returning = (async () => {
    if (flow.step === 'idle') {
      if (!(await restoreAttempt())) {
        return false;
      }
    } else if (flow.step === 'opening' || isEnded(flow.step)) {
      return false;
    }
    await checkAttempt(viaLink);
    return getConnectFlow().step !== 'idle';
  })().finally(() => {
    returning = null;
  });
  return returning;
}
