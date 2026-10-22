// Connecting a bank from the app (Story 5.1a, temporary entry in Settings until
// Stories 5.6/5.7): ask the backend for a one-time link, open it, and read
// /status when the user comes back.
//
// Module-level like deviceStore.ts and backendFeed.ts, same posture: nothing
// here throws or rejects unhandled; every failure ends as a flow state the
// screen can show with "Try again". Nothing is logged except HTTP status codes
// and the fact that something failed. The one-time link is a credential until
// it is used, so it is never logged or kept in state. /status carries no
// session id or IBAN, and this file does not log what it reads.

import AsyncStorage from '@react-native-async-storage/async-storage';
import { backendHeaders, hasApiKey } from './backendAuth';
import { BACKEND_BASE_URL, BACKEND_TIMEOUT_MS } from './config';
import { dismissConnectLink, openConnectLink } from './openConnectLink';

// --- Wire shapes --------------------------------------------------------------

export type BankState = 'connected' | 'expired' | 'dead' | 'first sync pending';

export type BankStatus = {
  aspsp: string;
  state: BankState;
  validUntil: string | null;
  lastSuccessAt: string | null;
  lastAttemptAt: string | null;
  lastError: string | null;
};

const BANK_STATES: readonly BankState[] = ['connected', 'expired', 'dead', 'first sync pending'];

/** Reads `camelCase` first, then `PascalCase`: the contract was written both ways. */
function field(source: Record<string, unknown>, name: string): unknown {
  return source[name] ?? source[name.charAt(0).toUpperCase() + name.slice(1)];
}

function textOrNull(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

/** One bank row, or `null` when it lacks a name or a known state (dropped, not guessed). */
export function parseBankStatus(raw: unknown): BankStatus | null {
  if (typeof raw !== 'object' || raw === null) {
    return null;
  }
  const row = raw as Record<string, unknown>;
  const aspsp = textOrNull(field(row, 'aspsp'));
  const state = field(row, 'state');
  if (aspsp === null || !BANK_STATES.includes(state as BankState)) {
    return null;
  }
  return {
    aspsp,
    state: state as BankState,
    validUntil: textOrNull(field(row, 'validUntil')),
    lastSuccessAt: textOrNull(field(row, 'lastSuccessAt')),
    lastAttemptAt: textOrNull(field(row, 'lastAttemptAt')),
    lastError: textOrNull(field(row, 'lastError')),
  };
}

/** The `banks` array of a /status body (or a bare array); `null` when the body is not that shape. */
export function parseStatusBody(body: unknown): BankStatus[] | null {
  const list =
    Array.isArray(body) || typeof body !== 'object' || body === null
      ? body
      : field(body as Record<string, unknown>, 'banks');
  if (!Array.isArray(list)) {
    return null;
  }
  return list.map(parseBankStatus).filter((bank): bank is BankStatus => bank !== null);
}

// --- Requests -------------------------------------------------------------------

/** Same AbortController + timeout as deviceStore.ts. `null` means "no usable answer". */
async function call(path: string, init: RequestInit): Promise<unknown | null> {
  if (!hasApiKey() || BACKEND_BASE_URL === '') {
    console.warn('[connectStore] API key or backend address not configured');
    return null;
  }
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), BACKEND_TIMEOUT_MS);
  try {
    const response = await fetch(`${BACKEND_BASE_URL}${path}`, {
      ...init,
      signal: controller.signal,
      headers: backendHeaders({ 'Content-Type': 'application/json' }),
    });
    if (!response.ok) {
      console.warn(`[connectStore] ${path} answered HTTP ${response.status}`);
      return null;
    }
    return await response.json();
  } catch {
    // The error object can echo the URL; the one-time link is never in a request URL, but keep it generic.
    console.warn(`[connectStore] ${path} failed`);
    return null;
  } finally {
    clearTimeout(timeout);
  }
}

/** `POST /connect-links`. The returned URL works once and expires after 30 minutes. */
export async function requestConnectLink(aspsp: string, country?: string): Promise<string | null> {
  const body = await call('/connect-links', {
    method: 'POST',
    body: JSON.stringify(country ? { aspsp, country } : { aspsp }),
  });
  if (typeof body !== 'object' || body === null) {
    return null;
  }
  const url = textOrNull(field(body as Record<string, unknown>, 'url'));
  return url !== null && /^https?:\/\//i.test(url) ? url : null;
}

/** `GET /status`; `null` when it could not be read. */
export async function fetchBankStatuses(): Promise<BankStatus[] | null> {
  const body = await call('/status', { method: 'GET' });
  return body === null ? null : parseStatusBody(body);
}

// --- Flow state -----------------------------------------------------------------

/**
 * - `idle`: nothing started (or reset).
 * - `opening`: asking for the link / opening it.
 * - `waiting`: the user is at the bank; we have not seen the result yet.
 *   `notYet` is set after a check that found nothing new; `tries` counts those.
 * - `checking`: reading /status after a return.
 * - `connected`: the new consent is in place.
 * - `failed`: something ended the attempt; `reason` says what to tell the user.
 */
export type ConnectFlow =
  | { step: 'idle' }
  | { step: 'opening'; bank: string }
  | { step: 'waiting'; bank: string; notYet?: boolean; tries: number }
  | { step: 'checking'; bank: string; tries: number }
  | { step: 'connected'; bank: string; validUntil: string | null }
  | { step: 'failed'; bank: string; reason: FailureReason };

export type FailureReason = 'link' | 'browser' | 'status' | 'ended';

/** After this many checks without a match the screen adds a hint about the bank name. */
export const HINT_AFTER_TRIES = 3;

/** Fewer normalized characters than this match too much ("n", "bank"). */
export const MIN_NAME_LENGTH = 2;

/** A tab-dismiss this soon after a check ended is the echo of our own close, not a new return. */
const RECENT_CHECK_MS = 2000;

const IDLE: ConnectFlow = { step: 'idle' };
let flow: ConnectFlow = IDLE;
/**
 * What /status said about this bank before the attempt (`undefined`: the bank
 * was not there), to tell a new consent from an old one. If that read fails the
 * attempt does not start at all, so "unknown" never has to be guessed here.
 */
let baseline: Baseline | undefined;
let requestedBank = '';
let lastCheckEndedAt = 0;
/** When the running attempt began (ms); an attempt older than ATTEMPT_MAX_AGE_MS can no longer succeed. */
let attemptStartedAt = 0;

const listeners = new Set<() => void>();

function setFlow(next: ConnectFlow): void {
  flow = next;
  if (next.step === 'idle' || next.step === 'connected' || next.step === 'failed') {
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

/** Back to the form (also "Try again"). Keeps nothing of the old attempt. */
export function resetConnectFlow(): void {
  baseline = undefined;
  requestedBank = '';
  setFlow(IDLE);
}

/** Test-only. */
export function __resetConnectStoreForTests(): void {
  resetConnectFlow();
  inFlightCheck = null;
  restoring = null;
  lastCheckEndedAt = 0;
  attemptStartedAt = 0;
  listeners.clear();
}

export const normalizeBankName = (name: string) =>
  name.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, '');

/** Whether a typed name is long enough to look anything up. */
export function isUsableBankName(typed: string): boolean {
  return normalizeBankName(typed).length >= MIN_NAME_LENGTH;
}

/**
 * The bank the user typed, or `undefined`. An exact normalized match wins; else
 * exactly one bank whose name contains the typed text. Several candidates
 * ("bank" matching two banks) are no match: guessing would show the wrong
 * bank as connected.
 */
export function findBank(banks: BankStatus[], typed: string): BankStatus | undefined {
  const wanted = normalizeBankName(typed);
  if (wanted.length < MIN_NAME_LENGTH) {
    return undefined;
  }
  const exact = banks.filter(bank => normalizeBankName(bank.aspsp) === wanted);
  if (exact.length > 0) {
    return exact.length === 1 ? exact[0] : undefined;
  }
  const partial = banks.filter(bank => normalizeBankName(bank.aspsp).includes(wanted));
  return partial.length === 1 ? partial[0] : undefined;
}

/**
 * Starts an attempt: reads the bank's current status (the baseline; if that
 * fails, nothing is opened and the user is told to try again), asks for the
 * link, opens it. When the user dismisses the Custom Tab the opener resolves
 * and we check at once; with the plain browser the way back is the link or the
 * app coming to the foreground.
 */
export async function startConnect(bankName: string): Promise<void> {
  const bank = bankName.trim();
  if (!isUsableBankName(bank) || flow.step === 'opening') {
    return;
  }
  requestedBank = bank;
  lastCheckEndedAt = 0;
  attemptStartedAt = Date.now();
  setFlow({ step: 'opening', bank });

  const before = await fetchBankStatuses();
  if (before === null) {
    setFlow({ step: 'failed', bank, reason: 'status' });
    return;
  }
  const row = findBank(before, bank);
  baseline = row ? { validUntil: row.validUntil } : undefined;

  const url = await requestConnectLink(bank);
  if (url === null) {
    setFlow({ step: 'failed', bank, reason: 'link' });
    return;
  }
  try {
    // Saved while still 'opening': a foreground event in between cannot start an early check.
    await saveAttempt(bank);
    setFlow({ step: 'waiting', bank, tries: 0 });
    const dismissed = await openConnectLink(url);
    if (dismissed && flow.step === 'waiting' && Date.now() - lastCheckEndedAt > RECENT_CHECK_MS) {
      await checkAfterReturn();
    }
  } catch {
    if (flow.step === 'waiting') {
      setFlow({ step: 'failed', bank, reason: 'browser' });
    }
  }
}

let inFlightCheck: Promise<void> | null = null;

/**
 * Called when the user is back (link, foreground, tab dismissed). Does nothing
 * unless an attempt is waiting, so a stray foreground event costs nothing.
 * Concurrent calls share one request: the link event and the foreground event
 * usually arrive together.
 *
 * The Custom Tab is closed only when the check ends connected or failed, or
 * when `viaLink` (the callback page itself sent the user back, so the page is
 * finished). A foreground event alone must not close it: the user may be in a
 * password manager or the bank's app, and the page would be gone on return.
 */
export function checkAfterReturn(viaLink = false): Promise<void> {
  if (flow.step !== 'waiting') {
    return inFlightCheck ?? Promise.resolve();
  }
  if (inFlightCheck === null) {
    inFlightCheck = runCheck(viaLink).finally(() => {
      inFlightCheck = null;
      lastCheckEndedAt = Date.now();
    });
  }
  return inFlightCheck;
}

async function runCheck(viaLink: boolean): Promise<void> {
  const bank = requestedBank;
  const tries = flow.step === 'waiting' ? flow.tries : 0;
  setFlow({ step: 'checking', bank, tries });

  const banks = await fetchBankStatuses();
  if (banks === null) {
    await dismissConnectLink();
    setFlow({ step: 'failed', bank, reason: 'status' });
    return;
  }
  const found = findBank(banks, bank);
  const isNew = found !== undefined && (baseline === undefined || found.validUntil !== baseline.validUntil);
  if (found === undefined || !isNew) {
    // Not confirmed (yet): the user may have come back early. Stay waiting, no dead end.
    if (viaLink) {
      await dismissConnectLink();
    }
    setFlow({ step: 'waiting', bank, notYet: true, tries: tries + 1 });
    return;
  }
  await dismissConnectLink();
  if (found.state === 'connected' || found.state === 'first sync pending') {
    setFlow({ step: 'connected', bank: found.aspsp, validUntil: found.validUntil });
  } else {
    setFlow({ step: 'failed', bank, reason: 'ended' });
  }
}

// --- A running attempt survives the app process ---------------------------------
//
// Android may kill the app while the user is in the bank's browser, app or a
// password manager. The attempt is therefore written to AsyncStorage the moment
// the link is about to be opened, and picked up again on the next start or
// return. What is stored is only: the bank name the user typed, what the bank's
// status looked like before (its validUntil, or "not there"), and a start time.
// Never the link, a token, a session id, an IBAN, or any other /status content.
// Every storage error behaves as "no attempt": the app works, it just cannot
// resume.

const ATTEMPT_KEY = 'connect.attempt.v1';
/** The one-time link lives 30 minutes; an older attempt can no longer succeed. */
export const ATTEMPT_MAX_AGE_MS = 30 * 60 * 1000;

type Baseline = { validUntil: string | null };
type StoredAttempt = { bank: string; baseline: Baseline | null; startedAt: number };

async function saveAttempt(bank: string): Promise<void> {
  const attempt: StoredAttempt = { bank, baseline: baseline ?? null, startedAt: attemptStartedAt };
  try {
    await AsyncStorage.setItem(ATTEMPT_KEY, JSON.stringify(attempt));
  } catch {
    console.warn('[connectStore] could not remember the running attempt');
  }
}

function clearAttempt(): void {
  try {
    Promise.resolve(AsyncStorage.removeItem(ATTEMPT_KEY)).catch(() => {});
  } catch {
    // Nothing stored, or no storage: nothing to forget.
  }
}

function parseAttempt(raw: string | null): StoredAttempt | null {
  if (raw === null) {
    return null;
  }
  try {
    const value = JSON.parse(raw) as Record<string, unknown>;
    const stored = value.baseline;
    const bank = value.bank;
    const startedAt = value.startedAt;
    if (typeof bank !== 'string' || typeof startedAt !== 'number') {
      return null;
    }
    if (stored !== null && (typeof stored !== 'object' || stored === undefined)) {
      return null;
    }
    const until = stored === null ? null : (stored as Record<string, unknown>).validUntil;
    if (stored !== null && until !== null && typeof until !== 'string') {
      return null;
    }
    return {
      bank,
      startedAt,
      baseline: stored === null ? null : { validUntil: (until as string | null) ?? null },
    };
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
      const attempt = parseAttempt(raw);
      if (attempt === null) {
        if (raw !== null) {
          clearAttempt();
        }
        return false;
      }
      const age = Date.now() - attempt.startedAt;
      if (age < 0 || age >= ATTEMPT_MAX_AGE_MS || !isUsableBankName(attempt.bank)) {
        clearAttempt();
        return false;
      }
      if (flow.step !== 'idle') {
        return false; // the user started something while we were reading
      }
      requestedBank = attempt.bank;
      baseline = attempt.baseline ?? undefined;
      lastCheckEndedAt = 0;
      attemptStartedAt = attempt.startedAt;
      setFlow({ step: 'waiting', bank: attempt.bank, tries: 0 });
      return true;
    })().finally(() => {
      restoring = null;
    });
  }
  return restoring;
}

/**
 * The user is back in the app (start, foreground, or the app link). With an
 * attempt in memory this is checkAfterReturn; after the process was killed it
 * first restores the stored attempt, then checks. Without any attempt: nothing.
 * Resolves `true` when the user should be shown the Settings screen: a check ran
 * and ended connected or failed, or ended "not yet" but the return came by the
 * app link or is the first resume after a restart. A plain foreground event
 * that finds nothing new (a password manager round trip) resolves `false`, as
 * does a later event after the result. An attempt older than the link's
 * lifetime is dropped quietly, with no request.
 */
export async function returnToApp(viaLink = false): Promise<boolean> {
  let restored = false;
  if (flow.step === 'idle') {
    restored = await restoreAttempt();
    if (!restored) {
      return false;
    }
  }
  if (flow.step !== 'waiting') {
    // Nothing to look at, or a check is already running (its starter reports it).
    return false;
  }
  if (Date.now() - attemptStartedAt >= ATTEMPT_MAX_AGE_MS) {
    resetConnectFlow();
    return false;
  }
  await checkAfterReturn(viaLink);
  return getConnectFlow().step !== 'waiting' || viaLink || restored;
}
