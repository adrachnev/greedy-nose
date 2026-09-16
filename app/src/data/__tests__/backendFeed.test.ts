/**
 * The feed is the first code in the app that reads something it did not write.
 * These pin the things that cost the most when they go wrong: a charge the
 * user never sees (R1 — so a row must never disappear quietly), an empty list
 * that is really a broken payload (R19 — silence is the one failure this
 * product cannot have), and a getSnapshot that returns a new array every call,
 * which is how useSyncExternalStore renders forever.
 */

import {
  __resetFeedForTests,
  getDebits,
  getPayees,
  getState,
  refresh,
  subscribe,
} from '../backendFeed';

const PAYEE = { id: 'name:LIDL CONNECT', name: 'LIDL CONNECT', initials: 'LC', iban: '' };

// Shaped like the sandbox's rows rather than like a tidy example: a booking
// date with no time (hasTime false on all 100 of them) and an empty reference
// (empty on 91 of 92).
const DEBIT = {
  id: 'DE26:2026-08-18:20.00:name:LIDL CONNECT#0',
  payeeId: 'name:LIDL CONNECT',
  amountEUR: 20,
  timestamp: '2026-08-18T00:00:00Z',
  hasTime: false,
  paymentType: 'Card payment',
  reference: '',
};

// `fetch` is a global the React Native runtime provides and Jest's environment
// does not, so it is installed rather than spied on.
const fetchMock = jest.fn();

function respondWith(body: unknown, ok = true, status = 200) {
  fetchMock.mockResolvedValueOnce({
    ok,
    status,
    statusText: ok ? 'OK' : 'Internal Server Error',
    json: async () => body,
  });
}

beforeEach(() => {
  __resetFeedForTests();
  fetchMock.mockReset();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
});

afterEach(() => {
  jest.restoreAllMocks();
});

describe('refresh', () => {
  it('loads payees and debits and reports ready', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT], skipped: 0 });
    await refresh();

    expect(getState()).toEqual({ status: 'ready', dropped: 0, skipped: 0 });
    expect(getPayees()).toEqual([PAYEE]);
    expect(getDebits()).toEqual([DEBIT]);
  });

  it('falls back to the same initials rule the backend uses, not to a first character', async () => {
    respondWith({ payees: [{ ...PAYEE, initials: '' }], debits: [] });
    await refresh();

    // "LC", the two-word rule — a plain slice(0, 1) would give "L" here and
    // "4" for a name like "4711 REWE", where the backend gives "R".
    expect(getPayees()[0].initials).toBe('LC');
  });

  it('agrees with the backend on a name that starts with a branch number', async () => {
    respondWith({ payees: [{ ...PAYEE, name: '4711 REWE', initials: '' }], debits: [] });
    await refresh();

    expect(getPayees()[0].initials).toBe('R');
  });

  it('keeps a debit whose paymentType is unknown rather than hiding the charge', async () => {
    respondWith({ payees: [PAYEE], debits: [{ ...DEBIT, paymentType: 'Standing order' }] });
    await refresh();

    expect(getDebits()).toHaveLength(1);
    expect(getDebits()[0].paymentType).toBe('Card payment');
    expect(getState().dropped).toBe(0);
  });

  it.each([
    ['a zero amount', { amountEUR: 0 }],
    ['a negative amount', { amountEUR: -5 }],
    ['a non-numeric amount', { amountEUR: '20.00' }],
    ['an unparseable timestamp', { timestamp: 'not a date' }],
    ['no id', { id: '' }],
  ])('drops a debit with %s, and counts it', async (_label, override) => {
    respondWith({ payees: [PAYEE], debits: [{ ...DEBIT, ...override }] });
    await refresh();

    expect(getDebits()).toEqual([]);
    expect(getState().dropped).toBe(1);
  });
});

/**
 * Two debits with one id are two React children with one key, and React may
 * render just one of them. Unreachable while `/debits` reads a single page,
 * and cheap to close now: the backend's fallback id restarts its ordinal per
 * page, so the collision arrives with pagination rather than as a freak event.
 */
describe('duplicate debit ids', () => {
  it('keeps both charges by renaming the second, rather than losing one to a key clash', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT, { ...DEBIT, amountEUR: 31 }] });
    await refresh();

    const ids = getDebits().map(d => d.id);
    expect(getDebits()).toHaveLength(2);
    expect(new Set(ids).size).toBe(2);
    expect(ids[0]).toBe(DEBIT.id);
    expect(ids[1]).toBe(`${DEBIT.id}#dup2`);
    // Nothing was refused, so neither counter moves.
    expect(getState()).toMatchObject({ dropped: 0, skipped: 0 });
  });

  it('gives the same collision the same id on every refresh', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT, DEBIT] });
    await refresh();
    const first = getDebits().map(d => d.id);

    respondWith({ payees: [PAYEE], debits: [DEBIT, DEBIT] });
    await refresh();

    // An id that churned per fetch would read as a brand-new charge every
    // time, which is what R10b keys "have I alerted on this?" on.
    expect(getDebits().map(d => d.id)).toEqual(first);
  });

  it('keeps going past a third collision', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT, DEBIT, DEBIT] });
    await refresh();

    expect(getDebits().map(d => d.id)).toEqual([
      DEBIT.id,
      `${DEBIT.id}#dup2`,
      `${DEBIT.id}#dup3`,
    ]);
  });
});

/**
 * The orphan case: a debit naming a payee the same payload did not send. It
 * used to be dropped, which is the exact failure R1 exists to prevent — the
 * console.warn goes nowhere outside __DEV__, so the charge left no trace
 * anywhere the user could see it.
 */
describe('a debit whose payee was not sent', () => {
  it('keeps the charge and invents the payee, rather than the other way round', async () => {
    respondWith({ payees: [PAYEE], debits: [{ ...DEBIT, payeeId: 'name:GONE' }] });
    await refresh();

    expect(getDebits()).toHaveLength(1);
    expect(getState().dropped).toBe(0);

    // Worded exactly as the backend words its own unnameable payee
    // (TransactionMapper.UnknownPayeeKey), because the user meets both in one
    // list.
    const synthesized = getPayees().find(p => p.id === 'name:GONE');
    expect(synthesized).toEqual({ id: 'name:GONE', name: 'Unknown payee', initials: 'UP', iban: '' });
  });

  it('still says so in the log, since a synthesized payee is not a normal payload', async () => {
    respondWith({ payees: [PAYEE], debits: [{ ...DEBIT, payeeId: 'name:GONE' }] });
    await refresh();

    expect(console.warn).toHaveBeenCalledWith(expect.stringContaining('name:GONE'));
  });

  it('survives a payee row the parser had to reject, without losing that payee’s charges', async () => {
    // toPayee refuses a row with no id or name, and its debits then reference a
    // payee that never made it into the list — the orphan case arriving from
    // inside a well-formed payload rather than from a mismatched one. The
    // charge is still money that left the account.
    respondWith({
      payees: [{ ...PAYEE, name: '' }],
      debits: [DEBIT],
    });
    await refresh();

    expect(getDebits()).toHaveLength(1);
    expect(getPayees()).toEqual([
      { id: PAYEE.id, name: 'Unknown payee', initials: 'UP', iban: '' },
    ]);
    expect(getState().dropped).toBe(0);
  });

  it('keeps two unsent payees apart — merging them could hand one the other’s rule (R3b)', async () => {
    respondWith({
      payees: [],
      debits: [
        { ...DEBIT, id: 'a', payeeId: 'name:GONE ONE' },
        { ...DEBIT, id: 'b', payeeId: 'name:GONE TWO' },
      ],
    });
    await refresh();

    expect(getDebits()).toHaveLength(2);
    expect(getPayees().map(p => p.id).sort()).toEqual(['name:GONE ONE', 'name:GONE TWO']);
  });

  it('reuses one payee for several debits that name it', async () => {
    respondWith({
      payees: [],
      debits: [
        { ...DEBIT, id: 'a', payeeId: 'name:GONE' },
        { ...DEBIT, id: 'b', payeeId: 'name:GONE' },
      ],
    });
    await refresh();

    expect(getDebits()).toHaveLength(2);
    expect(getPayees()).toHaveLength(1);
  });
});

/**
 * A 200 with the wrong shape is not "no debits". On screen the two are the
 * same blank list, which is the silence R19 forbids — so the malformed one has
 * to reach the badge as an error.
 */
describe('a body that is not the contract', () => {
  it.each([
    ['no debits key at all', { payees: [] }],
    ['a null debits key', { payees: [], debits: null }],
    ['a debits key that is not an array', { payees: [], debits: { '0': DEBIT } }],
    ['no payees key, which would orphan every debit', { debits: [] }],
    ['not an object at all', 'unexpected'],
    ['null', null],
  ])('reports an error for %s', async (_label, body) => {
    respondWith(body);
    await refresh();

    expect(getState().status).toBe('error');
  });

  it('reports ready for a genuinely empty account, which is a real state', async () => {
    respondWith({ payees: [], debits: [], skipped: 0 });
    await refresh();

    expect(getState()).toEqual({ status: 'ready', dropped: 0, skipped: 0 });
  });
});

/**
 * `skipped` is the backend's own count of charges its mapper refused to
 * represent (non-EUR above all). It is deliberately not folded into `dropped`:
 * one failure is ours and one is upstream, and the badge has to be able to say
 * which.
 */
describe('the backend’s skipped count', () => {
  it('carries it through untouched', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT], skipped: 3 });
    await refresh();

    expect(getState()).toEqual({ status: 'ready', dropped: 0, skipped: 3 });
  });

  it('counts our dropped rows separately from the backend’s skipped ones', async () => {
    respondWith({ payees: [PAYEE], debits: [{ ...DEBIT, amountEUR: 0 }], skipped: 2 });
    await refresh();

    expect(getState()).toMatchObject({ dropped: 1, skipped: 2 });
  });

  it.each([
    ['absent — the field is additive, so an older backend means 0', undefined],
    ['not a number', 'three'],
    ['negative', -1],
  ])('reads 0 when it is %s', async (_label, skipped) => {
    respondWith({ payees: [PAYEE], debits: [DEBIT], skipped });
    await refresh();

    expect(getState().skipped).toBe(0);
  });
});

/**
 * R2's date, and whether the bank gave a moment or only a day. Getting this
 * wrong is not cosmetic: it invents a time on every charge of an account whose
 * bank books to the date, which is every account seen so far.
 */
describe('hasTime', () => {
  it('is false when the field is absent, rather than assuming a time exists', async () => {
    // What an older backend — or any bank the field was never added for —
    // actually puts on the wire.
    const withoutFlag: Record<string, unknown> = { ...DEBIT };
    delete withoutFlag.hasTime;

    respondWith({ payees: [PAYEE], debits: [withoutFlag] });
    await refresh();

    expect(getDebits()[0].hasTime).toBe(false);
  });

  it('is true only when the backend says true', async () => {
    respondWith({ payees: [PAYEE], debits: [{ ...DEBIT, hasTime: true }] });
    await refresh();

    expect(getDebits()[0].hasTime).toBe(true);
  });

  it('ignores a value that is not a boolean', async () => {
    respondWith({ payees: [PAYEE], debits: [{ ...DEBIT, hasTime: 'yes' }] });
    await refresh();

    expect(getDebits()[0].hasTime).toBe(false);
  });
});

describe('failures', () => {
  it('reports an error and keeps the rows already on screen', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT] });
    await refresh();
    respondWith(null, false, 503);
    await refresh();

    expect(getState().status).toBe('error');
    expect(getState().error).toContain('503');
    expect(getDebits()).toEqual([DEBIT]);
  });

  it('reports an error when the request itself fails', async () => {
    fetchMock.mockRejectedValueOnce(new Error('Network request failed'));
    await refresh();

    expect(getState()).toMatchObject({ status: 'error', error: 'Network request failed' });
  });

  /**
   * The branch a device hits most: without `adb reverse tcp:5199 tcp:5199` the
   * request has nowhere to go and simply hangs, so the timeout is what the
   * developer actually sees. AbortError's own message is "Aborted", which says
   * nothing about the thing to go and check — the store replaces it, and this
   * is the only place that swap is pinned. The 15s comes from
   * BACKEND_TIMEOUT_MS in ./config.
   */
  it('says a timeout was a timeout, not "Aborted"', async () => {
    const aborted = new Error('Aborted');
    aborted.name = 'AbortError';
    fetchMock.mockRejectedValueOnce(aborted);
    await refresh();

    expect(getState()).toMatchObject({ status: 'error', error: 'No answer within 15s' });
  });

  it('describes a rejection that is not an Error at all', async () => {
    fetchMock.mockRejectedValueOnce('kaboom');
    await refresh();

    expect(getState()).toMatchObject({ status: 'error', error: 'kaboom' });
  });
});

describe('snapshots', () => {
  // useSyncExternalStore compares by reference: a fresh [] each call is an
  // infinite render loop, not a cosmetic issue.
  it('returns a stable reference while nothing has loaded', () => {
    expect(getDebits()).toBe(getDebits());
    expect(getPayees()).toBe(getPayees());
  });

  it('returns a stable reference for an empty result', async () => {
    respondWith({ payees: [], debits: [] });
    await refresh();

    expect(getDebits()).toBe(getDebits());
    expect(getPayees()).toBe(getPayees());
  });

  it('installs a new array when the data changes', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT] });
    await refresh();
    const first = getDebits();

    respondWith({ payees: [PAYEE], debits: [DEBIT] });
    await refresh();

    expect(getDebits()).not.toBe(first);
  });
});

describe('subscribe', () => {
  it('starts the first load and notifies when it lands', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT] });
    const listener = jest.fn();

    subscribe(listener);
    // Once, synchronously, for 'loading'.
    expect(listener).toHaveBeenCalledTimes(1);

    // Awaits the load subscribe() started — refresh() hands back the one in
    // flight rather than a fresh resolved promise. Counting microtask ticks
    // here instead would assert against whatever state an extra `await` inside
    // refresh() happened to leave behind.
    await refresh();

    expect(listener).toHaveBeenCalledTimes(2);
    expect(getState().status).toBe('ready');
    expect(getDebits()).toEqual([DEBIT]);
  });

  it('does not fetch again for every extra subscriber', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT] });
    subscribe(jest.fn());
    subscribe(jest.fn());
    subscribe(jest.fn());
    await refresh();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('does not start a second request when a listener refreshes on the ‘loading’ notification', async () => {
    // The one branch refresh() cannot hand a real promise to: 'loading' is set
    // and broadcast before `inFlight` exists, so a listener calling refresh()
    // from that notification gets an already-resolved promise. What it must
    // *not* get is a second request.
    respondWith({ payees: [PAYEE], debits: [DEBIT] });
    const reentrant = jest.fn(() => {
      if (getState().status === 'loading') {
        refresh();
      }
    });

    subscribe(reentrant);
    await refresh();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(getDebits()).toEqual([DEBIT]);
  });

  it('joins a load already in flight instead of starting a second one', async () => {
    respondWith({ payees: [PAYEE], debits: [DEBIT] });

    await Promise.all([refresh(), refresh()]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    // Both callers awaited the *running* load, so the data is there for both.
    expect(getDebits()).toEqual([DEBIT]);
  });

  it('stops notifying after unsubscribe', async () => {
    respondWith({ payees: [], debits: [] });
    const listener = jest.fn();
    const unsubscribe = subscribe(listener);
    // The load subscribe() started has to finish first: unsubscribing while it
    // is still in flight would leave the second refresh() below short-circuited
    // by the 'loading' guard, and the test would pass with an unsubscribe that
    // did nothing at all.
    await refresh();
    unsubscribe();
    listener.mockClear();

    respondWith({ payees: [PAYEE], debits: [DEBIT] });
    await refresh();

    expect(getDebits()).toEqual([DEBIT]);
    expect(listener).not.toHaveBeenCalled();
  });
});
