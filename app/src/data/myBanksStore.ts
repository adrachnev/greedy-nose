// The banks the owner has saved, and the search for a new one (Story 5.6).
//
// Module-level like connectStore.ts and deviceStore.ts, same posture: nothing
// here throws or rejects unhandled; every failure ends as a state a screen can
// show. Nothing is logged except the path (without its query) and an HTTP
// status: never an account name, an IBAN, a bank name or the text the user
// typed into the search.
//
import { backendCall } from './backendCall';
import { isUsableBankName, normalizeBankName } from './connectStore';

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
export const SYNC_POLL_MS = 4000;

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

/**
 * The saved bank a name from the search stands for: the same normalized name. With a country, only a row of that
 * country (or one that has none, an old row) counts; the name alone decides only when no country is given.
 * Names are matched, not ids, because the search hit is all the app has when an attempt ends.
 */
export function findMyBank(banks: MyBank[], name: string, country: string | null = null): MyBank | undefined {
  const wanted = normalizeBankName(name);
  const same = banks.filter(bank => normalizeBankName(bank.name) === wanted);
  if (country === null) {
    return same[0];
  }
  return same.find(bank => bank.country === country) ?? same.find(bank => bank.country === null);
}

/** Whether "Try again" is offered: a failed first sync, except when today's fetches of the bank are used up. */
export function canRetryFirstSync(bank: MyBank): boolean {
  return bank.state === 'sync_failed' && bank.reason !== 'daily-limit';
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
  const reply = await backendCall('myBanksStore', path, logPath, { method: 'GET' });
  return reply.ok ? reply.body : null;
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

let inFlight: Promise<boolean> | null = null;

/** Reads `/my-banks`. Concurrent calls share one request. Never rejects; `true` when the answer was usable. */
export function refreshMyBanks(): Promise<boolean> {
  if (inFlight === null) {
    inFlight = (async () => {
      const banks = parseMyBanksBody(await get('/my-banks', '/my-banks'));
      if (banks === null) {
        if (state.status !== 'ready') {
          publish({ status: 'failed', banks: NO_BANKS });
        }
        return false;
      }
      if (!firstAnswerSeen) {
        firstAnswerSeen = true;
        firstStartHadNoBank = banks.length === 0;
      }
      // Same answer as before: no new object, so a poll every few seconds does not re-render.
      if (state.status === 'ready' && JSON.stringify(state.banks) === JSON.stringify(banks)) {
        return true;
      }
      publish({ status: 'ready', banks });
      return true;
    })().finally(() => {
      inFlight = null;
    });
  }
  return inFlight;
}

// --- Trying the first sync again -----------------------------------------------------

/**
 * - `queued`: the backend took it; the bank shows as syncing at once.
 * - `conflict`: the backend says it is not failed (any more) or "try again tomorrow"; the state was read again.
 * - `unavailable`: no answer or a refusal; nothing changed.
 * - `busy`: the same bank is already being sent; this call did nothing.
 */
export type RetryOutcome = 'queued' | 'conflict' | 'unavailable' | 'busy';

const retrying = new Set<string>();

/**
 * `POST /my-banks/first-sync`: repeats only the pull (R25), no new consent. Never rejects.
 * The contract takes the name only (`aspsp`), so two saved banks of one name cannot be told apart here.
 */
export async function retryFirstSync(name: string): Promise<RetryOutcome> {
  if (retrying.has(name)) {
    return 'busy';
  }
  retrying.add(name);
  try {
    const reply = await backendCall('myBanksStore', '/my-banks/first-sync', '/my-banks/first-sync', {
      method: 'POST',
      body: { aspsp: name },
    });
    if (reply.ok) {
      // A read begun before the POST would answer with the old state; let it land first, then show the new one.
      await inFlight;
      publish({
        status: 'ready',
        banks: state.banks.map(bank =>
          bank.name === name && bank.state === 'sync_failed' ? { ...bank, state: 'syncing', reason: null } : bank,
        ),
      });
      refreshMyBanks();
      return 'queued';
    }
    if (reply.status === 409) {
      await refreshMyBanks();
      return 'conflict';
    }
    return 'unavailable';
  } finally {
    retrying.delete(name);
  }
}

// --- Polling ----------------------------------------------------------------------

let pollUsers = 0;
let watchers = 0;
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
      if (watchers > 0 || state.banks.some(bank => bank.state === 'syncing')) {
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

/**
 * While held, the running poll reads also when no bank is known to be syncing: the screen that waits for a
 * just-saved bank (Story 5.8) may not have its row yet. Call the returned function once to let go.
 */
export function watchMyBanks(): () => void {
  watchers += 1;
  let released = false;
  return () => {
    if (!released) {
      released = true;
      watchers -= 1;
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
  watchers = 0;
  retrying.clear();
  if (pollTimer !== null) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
  listeners.clear();
}
