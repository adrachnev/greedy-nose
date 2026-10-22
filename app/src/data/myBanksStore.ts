// The banks the owner has saved, and the search for a new one (Story 5.6).
//
// Module-level like connectStore.ts and deviceStore.ts, same posture: nothing
// here throws or rejects unhandled; every failure ends as a state a screen can
// show. Nothing is logged except the path (without its query) and an HTTP
// status: never an account name, an IBAN, a bank name or the text the user
// typed into the search.
//
// PRAGMATIC: `get()` below repeats the fetch/timeout/header pattern of
// connectStore.ts instead of sharing it. The 5.1a flow store is deliberately
// left untouched in this story and Story 5.7 rebuilds it on the attempt
// endpoints; the two copies merge then — see bead gn-1u1.7.

import { backendHeaders, hasApiKey } from './backendAuth';
import { BACKEND_BASE_URL, BACKEND_TIMEOUT_MS, USE_BACKEND } from './config';
import { isUsableBankName } from './connectStore';

// --- Wire shapes --------------------------------------------------------------

export type MyBankState = 'connected' | 'syncing' | 'sync_failed' | 'expired';

export type MyBank = {
  name: string;
  /** ISO country code, `null` when the bank did not give one. */
  country: string | null;
  /** The account's display name; never an IBAN. */
  account: string | null;
  validUntil: string | null;
  state: MyBankState;
  /** A short backend reason for a failed state; not shown today. */
  reason: string | null;
};

export type BankHit = { name: string; country: string; connected: boolean };

const MY_BANK_STATES: readonly MyBankState[] = ['connected', 'syncing', 'sync_failed', 'expired'];

/** The longest search text sent; the field enforces the same bound. */
export const MAX_SEARCH_LENGTH = 100;

/** Pause after the last key before the backend is asked. */
export const SEARCH_PAUSE_MS = 400;

/** How often a list with a syncing bank is read again. */
export const SYNC_POLL_MS = 5000;

function textOrNull(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

/** One `/my-banks` row, or `null` when it lacks a name or a known state (dropped, not guessed). */
export function parseMyBank(raw: unknown): MyBank | null {
  if (typeof raw !== 'object' || raw === null) {
    return null;
  }
  const row = raw as Record<string, unknown>;
  const name = textOrNull(row.name);
  if (name === null || !MY_BANK_STATES.includes(row.state as MyBankState)) {
    return null;
  }
  return {
    name,
    country: textOrNull(row.country),
    account: textOrNull(row.account),
    validUntil: textOrNull(row.validUntil),
    state: row.state as MyBankState,
    reason: textOrNull(row.reason),
  };
}

function bankList(body: unknown): unknown[] | null {
  const list =
    typeof body === 'object' && body !== null ? (body as Record<string, unknown>).banks : undefined;
  return Array.isArray(list) ? list : null;
}

/** The `banks` array of a `/my-banks` body; `null` when the body is not that shape. */
export function parseMyBanksBody(body: unknown): MyBank[] | null {
  const list = bankList(body);
  return list === null ? null : list.map(parseMyBank).filter((bank): bank is MyBank => bank !== null);
}

/** One `/banks?q=` hit, or `null` when it lacks a name or a country. */
export function parseBankHit(raw: unknown): BankHit | null {
  if (typeof raw !== 'object' || raw === null) {
    return null;
  }
  const row = raw as Record<string, unknown>;
  const name = textOrNull(row.name);
  const country = textOrNull(row.country);
  if (name === null || country === null) {
    return null;
  }
  return { name, country, connected: row.connected === true };
}

export function parseBankHitsBody(body: unknown): BankHit[] | null {
  const list = bankList(body);
  return list === null ? null : list.map(parseBankHit).filter((hit): hit is BankHit => hit !== null);
}

// --- Request --------------------------------------------------------------------

/** `GET`; `null` means "no usable answer". `logPath` is the path without its query. */
async function get(path: string, logPath: string): Promise<unknown | null> {
  if (!USE_BACKEND || !hasApiKey() || BACKEND_BASE_URL === '') {
    return null;
  }
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), BACKEND_TIMEOUT_MS);
  try {
    const response = await fetch(`${BACKEND_BASE_URL}${path}`, {
      method: 'GET',
      signal: controller.signal,
      headers: backendHeaders(),
    });
    if (!response.ok) {
      console.warn(`[myBanksStore] ${logPath} answered HTTP ${response.status}`);
      return null;
    }
    return await response.json();
  } catch {
    // The error object can echo the URL, which carries the search text.
    console.warn(`[myBanksStore] ${logPath} failed`);
    return null;
  } finally {
    clearTimeout(timeout);
  }
}

export type BankSearchResult = { ok: true; banks: BankHit[] } | { ok: false };

/** `GET /banks?q=`. A text too short to look up is not sent (the caller shows no list for it). */
export async function searchBanks(text: string): Promise<BankSearchResult> {
  const query = text.trim().slice(0, MAX_SEARCH_LENGTH);
  if (!isUsableBankName(query)) {
    return { ok: false };
  }
  const banks = parseBankHitsBody(await get(`/banks?q=${encodeURIComponent(query)}`, '/banks'));
  return banks === null ? { ok: false } : { ok: true, banks };
}

// --- My banks -------------------------------------------------------------------

/**
 * - `loading`: no answer yet.
 * - `ready`: `banks` is what the backend said last (possibly empty).
 * - `failed`: no good answer has ever arrived; `banks` is empty and means nothing.
 * A later failed read keeps `ready` and the last good list.
 */
export type MyBanksState =
  | { status: 'loading'; banks: MyBank[] }
  | { status: 'ready'; banks: MyBank[] }
  | { status: 'failed'; banks: MyBank[] };

const NO_BANKS: MyBank[] = [];
const LOADING: MyBanksState = { status: 'loading', banks: NO_BANKS };
let state: MyBanksState = LOADING;
const listeners = new Set<() => void>();

function publish(next: MyBanksState): void {
  state = next;
  listeners.forEach(listener => listener());
}

export function getMyBanks(): MyBanksState {
  return state;
}

export function subscribeToMyBanks(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

// What the first good answer of this process said, for the first-start jump to
// Settings. A failed or unfinished read leaves it undecided: "no bank" is never
// claimed from a read that did not happen.
let firstAnswerSeen = false;
let firstStartHadNoBank = false;

// peek and take are split on purpose: the flag is spent (take) only after the jump to Settings
// really happened, so a navigator that was not ready yet does not lose it.
/** Whether the first good answer was empty and the jump has not been made yet. */
export function peekFirstStartNoBank(): boolean {
  return firstStartHadNoBank;
}

/** `true` once, when the first good answer of this process was an empty list. */
export function takeFirstStartNoBank(): boolean {
  const value = firstStartHadNoBank;
  firstStartHadNoBank = false;
  return value;
}

let inFlight: Promise<void> | null = null;

/** Reads `/my-banks`. Concurrent calls share one request. Never rejects. */
export function refreshMyBanks(): Promise<void> {
  if (inFlight === null) {
    inFlight = (async () => {
      const banks = parseMyBanksBody(await get('/my-banks', '/my-banks'));
      if (banks === null) {
        if (state.status !== 'ready') {
          publish({ status: 'failed', banks: NO_BANKS });
        }
        return;
      }
      if (!firstAnswerSeen) {
        firstAnswerSeen = true;
        firstStartHadNoBank = banks.length === 0;
      }
      // Same answer as before: no new object, so a poll every few seconds does not re-render.
      if (state.status === 'ready' && JSON.stringify(state.banks) === JSON.stringify(banks)) {
        return;
      }
      publish({ status: 'ready', banks });
    })().finally(() => {
      inFlight = null;
    });
  }
  return inFlight;
}

// --- Polling ----------------------------------------------------------------------

let pollUsers = 0;
let pollTimer: ReturnType<typeof setInterval> | null = null;

/**
 * Reads now, and then again every few seconds while a bank is syncing. Counted:
 * the timer runs while at least one screen asked for it, and the returned
 * function (call it once, on blur or unmount) lets go of that screen's share.
 */
export function startMyBanksPolling(): () => void {
  pollUsers += 1;
  refreshMyBanks();
  if (pollTimer === null) {
    pollTimer = setInterval(() => {
      if (state.banks.some(bank => bank.state === 'syncing')) {
        refreshMyBanks();
      }
    }, SYNC_POLL_MS);
  }
  let released = false;
  return () => {
    if (released) {
      return;
    }
    released = true;
    pollUsers -= 1;
    if (pollUsers === 0 && pollTimer !== null) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  };
}

/** Test-only. */
export function __resetMyBanksStoreForTests(): void {
  state = LOADING;
  inFlight = null;
  firstAnswerSeen = false;
  firstStartHadNoBank = false;
  pollUsers = 0;
  if (pollTimer !== null) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
  listeners.clear();
}
