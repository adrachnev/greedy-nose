// The live feed: GET /debits from the tracer-bullet backend, held in a module
// store shaped exactly like the fixture store so `hooks.ts` can pick either one
// without any screen knowing which it got.
//
// Same array-reference discipline as src/mocks/data.ts, and for the same
// reason: every write installs a *new* array so useSyncExternalStore's
// getSnapshot returns a changed reference. Returning a fresh `[]` from
// getSnapshot instead would loop React forever, which is why the empty case is
// a shared constant rather than a literal.

import { Debit, Payee, PaymentType } from '../domain/model';
import { toInitials, unknownPayee } from '../domain/payees';
import { BACKEND_BASE_URL, BACKEND_TIMEOUT_MS } from './config';

export type FeedStatus = 'idle' | 'loading' | 'ready' | 'error';

export type FeedState = {
  status: FeedStatus;
  /** Human-readable and meant for the on-device badge, not for parsing. */
  error?: string;
  /** How many rows the backend sent that *we* could not render — see `toDebit`. */
  dropped: number;
  /**
   * How many charges the **backend's** mapper refused to represent at all
   * (non-EUR, unreadable amount or date) and therefore never put on the wire.
   *
   * Kept apart from `dropped` on purpose: they are two different failures, one
   * ours and one the bank's, and a single number would say a charge is missing
   * without saying which half to go and look at. Absent from an older
   * backend's payload ⇒ 0, since the field is additive.
   */
  skipped: number;
};

const NO_PAYEES: Payee[] = [];
const NO_DEBITS: Debit[] = [];

const PAYMENT_TYPES: readonly PaymentType[] = [
  'Direct debit',
  'Card payment',
  'Subscription',
  'Bank transfer',
];

let payees: Payee[] = NO_PAYEES;
let debits: Debit[] = NO_DEBITS;
let state: FeedState = { status: 'idle', dropped: 0, skipped: 0 };

type Listener = () => void;
const listeners = new Set<Listener>();

function notify(): void {
  listeners.forEach(listener => listener());
}

// --- Snapshots --------------------------------------------------------------

export function getPayees(): Payee[] {
  return payees;
}

export function getDebits(): Debit[] {
  return debits;
}

export function getState(): FeedState {
  return state;
}

/**
 * Subscribing is also what starts the first load. The alternative — fetching at
 * module load — fires in every Jest run that so much as imports `hooks.ts`,
 * including the ones that only touch rules, and in an environment with no
 * `fetch` at all. Loading when something actually wants the data keeps the
 * cost where the need is.
 */
export function subscribe(listener: Listener): () => void {
  listeners.add(listener);
  if (state.status === 'idle') {
    // Not awaited and not caught on purpose: refresh() reports failure through
    // the store rather than by rejecting, so there is nothing here to handle.
    refresh();
  }
  return () => {
    listeners.delete(listener);
  };
}

// --- Loading ----------------------------------------------------------------

/** The load currently running, so concurrent callers can await *it*. */
let inFlight: Promise<void> | null = null;

/**
 * Re-reads the whole feed. Idempotent while a load is in flight, so the several
 * screens that mount at once produce one request rather than three — and a
 * caller that arrives mid-load gets the promise of the load actually running,
 * so `await refresh()` means "the data has landed". Returning a fresh resolved
 * promise instead is what let the store's tests assert against a load that had
 * not finished.
 *
 * One caller is excluded from that promise, and only one: a listener that
 * calls refresh() from the 'loading' notification, which fires before
 * `inFlight` has been assigned. It gets an already-resolved promise back. That
 * branch exists to stop a second request, not to be awaited.
 *
 * Deliberately a full replace, not a merge: the backend holds no state we could
 * merge against yet (one consent, no database), and pretending otherwise would
 * invent the incremental-ingestion semantics R20 has to get right later.
 */
export function refresh(): Promise<void> {
  if (state.status === 'loading') {
    // `?? Promise.resolve()` covers one moment only: `load()` sets 'loading'
    // and notifies before this function has assigned `inFlight`, so a listener
    // that calls refresh() from that notification lands here with nothing to
    // join. It must still not start a second request, which is what the status
    // check — set synchronously — guarantees.
    return inFlight ?? Promise.resolve();
  }
  inFlight = load().finally(() => {
    inFlight = null;
  });
  return inFlight;
}

async function load(): Promise<void> {
  setState({ status: 'loading', dropped: state.dropped, skipped: state.skipped });

  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), BACKEND_TIMEOUT_MS);
  try {
    const response = await fetch(`${BACKEND_BASE_URL}/debits`, {
      signal: controller.signal,
      headers: { Accept: 'application/json' },
    });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status} ${response.statusText}`.trim());
    }
    const body: unknown = await response.json();
    const { nextPayees, nextDebits, dropped, skipped } = parseFeed(body);

    payees = nextPayees.length > 0 ? nextPayees : NO_PAYEES;
    debits = nextDebits.length > 0 ? nextDebits : NO_DEBITS;
    setState({ status: 'ready', dropped, skipped });
  } catch (error) {
    // The store keeps whatever it last held: a transient failure should not
    // blank a list the user is reading. The badge is what says it is stale.
    setState({
      status: 'error',
      error: describeError(error),
      dropped: state.dropped,
      skipped: state.skipped,
    });
  } finally {
    clearTimeout(timeout);
  }
}

function setState(next: FeedState): void {
  state = next;
  notify();
}

function describeError(error: unknown): string {
  if (error instanceof Error) {
    // AbortError is our own timeout, and its message ("Aborted") says nothing
    // about the thing the reader needs to check.
    return error.name === 'AbortError'
      ? `No answer within ${Math.round(BACKEND_TIMEOUT_MS / 1000)}s`
      : error.message;
  }
  return String(error);
}

// --- Parsing ----------------------------------------------------------------
//
// The backend and app/src/domain/model.ts agree by construction (the DTOs in
// EnableBanking/TransactionContracts.cs mirror this file's types), but JSON off
// a socket is `unknown` however carefully the other end was written. What is
// checked here is only what would break a screen; nothing is "cleaned up".

type ParsedFeed = {
  nextPayees: Payee[];
  nextDebits: Debit[];
  dropped: number;
  skipped: number;
};

/**
 * Throws when the body is not the shape the contract promises. A `ready`
 * status with no rows paints exactly the same screen as an account with
 * genuinely nothing on it, and R19 is the requirement that a dead connection
 * is never silent — so a payload we could not read has to reach the badge as
 * an error. An *empty* `debits` array is a different thing entirely and stays
 * `ready`: that is the account with no charges, and it is a real state.
 *
 * `payees` is validated the same way. It is not decoration: without it every
 * debit is an orphan, and the screen would fill with "Unknown payee" rows
 * rather than saying the payload was wrong.
 */
function parseFeed(body: unknown): ParsedFeed {
  const root = body as { payees?: unknown; debits?: unknown; skipped?: unknown } | null;
  if (!Array.isArray(root?.debits) || !Array.isArray(root?.payees)) {
    throw new Error('Backend sent no payees/debits arrays');
  }
  const rawPayees = root.payees;
  const rawDebits = root.debits;

  const nextPayees: Payee[] = [];
  for (const raw of rawPayees) {
    const payee = toPayee(raw);
    if (payee) {
      nextPayees.push(payee);
    }
  }

  const known = new Set(nextPayees.map(p => p.id));
  const seenIds = new Set<string>();
  const nextDebits: Debit[] = [];
  let dropped = 0;
  for (const raw of rawDebits) {
    const parsed = toDebit(raw);
    if (!parsed) {
      dropped += 1;
      continue;
    }
    // Two rows sharing an id are two React children sharing a key, and React
    // is explicitly allowed to render only one of them — a charge lost to a
    // rendering rule, which is the failure this whole file is arranged to
    // avoid. The duplicate is renamed rather than dropped, because both rows
    // are real money: the backend's own fallback id restarts its ordinal at
    // each page (see TransactionMapper.ResolveDebitId's note), so a same-day
    // group split across a page boundary is the way this arrives in practice.
    const debit = seenIds.has(parsed.id) ? { ...parsed, id: uniqueId(parsed.id, seenIds) } : parsed;
    seenIds.add(debit.id);
    if (!known.has(debit.payeeId)) {
      // The charge is kept and the payee invented, not the other way round.
      // An orphan used to be dropped here, and dropping it is the failure R1
      // exists to prevent: outside __DEV__ the console.warn goes nowhere, so a
      // real charge would leave no trace anywhere the user can see. A row
      // reading "Unknown payee" is worse-looking and strictly better — it is
      // visible, it classifies bad (no rule, R4b/R5), and the user can put a
      // rule on it like any other payee.
      console.warn(`[feed] debit ${debit.id} references unsent payee ${debit.payeeId}`);
      const synthesized = unknownPayee(debit.payeeId);
      known.add(synthesized.id);
      nextPayees.push(synthesized);
    }
    nextDebits.push(debit);
  }

  if (dropped > 0) {
    console.warn(`[feed] dropped ${dropped} of ${rawDebits.length} debits — see warnings above`);
  }
  return { nextPayees, nextDebits, dropped, skipped: toSkipped(root.skipped) };
}

/**
 * The first free `<id>#dup<n>`. Deterministic in feed order, so the same
 * collision produces the same id on every refresh rather than a fresh "new"
 * debit each time — which matters the moment R10b starts keying notifications
 * on it.
 */
function uniqueId(id: string, taken: Set<string>): string {
  let attempt = 2;
  while (taken.has(`${id}#dup${attempt}`)) {
    attempt += 1;
  }
  const unique = `${id}#dup${attempt}`;
  console.warn(`[feed] duplicate debit id ${id}; keeping the charge as ${unique}`);
  return unique;
}

function toSkipped(raw: unknown): number {
  if (typeof raw !== 'number' || !Number.isFinite(raw) || raw < 0) {
    return 0;
  }
  return Math.floor(raw);
}

function toPayee(raw: unknown): Payee | null {
  const p = raw as Partial<Payee> | null;
  if (!isNonEmptyString(p?.id) || !isNonEmptyString(p?.name)) {
    console.warn('[feed] skipping payee with no id or name', raw);
    return null;
  }
  // Initials and IBAN are presentational; a bank that sends neither should cost
  // the user a plainer row, not a missing payee. The fallback runs the *same*
  // rule the backend used to produce the field in the first place (toInitials,
  // mirroring its ToInitials) rather than taking the name's first character:
  // "4711 REWE" would otherwise be a 4 here and an R there, which is exactly
  // the two-rules-for-one-thing that src/domain/payees.ts exists to end.
  return {
    id: p.id,
    name: p.name,
    initials: isNonEmptyString(p.initials) ? p.initials : toInitials(p.name),
    iban: typeof p.iban === 'string' ? p.iban : '',
  };
}

/**
 * Returns null only when the row cannot be rendered at all. An unrecognised
 * `paymentType` is *not* one of those cases: dropping a charge because the bank
 * used a code we have no word for would hide real money, so it falls back to
 * the union's most common member and says so in the log. That is the same
 * "unknown code guessed as a card payment" gap the backend already carries, and
 * it stays visible on both sides rather than being papered over on one.
 */
function toDebit(raw: unknown): Debit | null {
  const d = raw as Partial<Debit> | null;
  if (!isNonEmptyString(d?.id) || !isNonEmptyString(d?.payeeId)) {
    console.warn('[feed] skipping debit with no id or payeeId', raw);
    return null;
  }
  if (typeof d.amountEUR !== 'number' || !Number.isFinite(d.amountEUR) || d.amountEUR <= 0) {
    // R2a: every debit is money going out and is stored positive, so a zero,
    // negative or unparseable amount means the mapper produced something it
    // should not have — not something to render as "€0.00".
    console.warn(`[feed] skipping debit ${d.id} with unusable amount`, d.amountEUR);
    return null;
  }
  if (!isNonEmptyString(d.timestamp) || Number.isNaN(Date.parse(d.timestamp))) {
    // The list is grouped by date; an unparseable one cannot be placed at all.
    console.warn(`[feed] skipping debit ${d.id} with unusable timestamp`, d.timestamp);
    return null;
  }

  let paymentType: PaymentType = 'Card payment';
  if (PAYMENT_TYPES.includes(d.paymentType as PaymentType)) {
    paymentType = d.paymentType as PaymentType;
  } else {
    console.warn(`[feed] debit ${d.id}: unknown paymentType`, d.paymentType);
  }

  return {
    id: d.id,
    payeeId: d.payeeId,
    amountEUR: d.amountEUR,
    timestamp: d.timestamp,
    // Absent ⇒ false, which hides the time rather than inventing one. See
    // Debit.hasTime: the sandbox's 100 rows were all date-only bookings, so
    // this is the common case and not the edge.
    hasTime: d.hasTime === true,
    paymentType,
    reference: typeof d.reference === 'string' ? d.reference : '',
  };
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0;
}

/** Test-only: drops everything the module holds so each case starts clean. */
export function __resetFeedForTests(): void {
  payees = NO_PAYEES;
  debits = NO_DEBITS;
  state = { status: 'idle', dropped: 0, skipped: 0 };
  inFlight = null;
  listeners.clear();
}
