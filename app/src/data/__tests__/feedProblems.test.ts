/**
 * UX-DR2: the release status line needs three different answers, so the feed
 * classifies instead of passing free text along.
 */

import { __setApiKeyForTests } from '../backendAuth';
import { __resetFeedForTests, getState, refresh } from '../backendFeed';

const PAYEE = { id: 'name:LIDL CONNECT', name: 'LIDL CONNECT', initials: 'LC', iban: '' };
const DEBIT = {
  id: 'DE26:2026-08-18:20.00:name:LIDL CONNECT#0',
  payeeId: 'name:LIDL CONNECT',
  amountEUR: 20,
  timestamp: '2026-08-18T00:00:00Z',
  hasTime: false,
  paymentType: 'Card payment',
  reference: '',
};

const fetchMock = jest.fn();

function respond(status: number, body: unknown = {}) {
  fetchMock.mockResolvedValueOnce({
    ok: status >= 200 && status < 300,
    status,
    statusText: '',
    json: async () => body,
  });
}

beforeEach(() => {
  __resetFeedForTests();
  __setApiKeyForTests('test-api-key');
  fetchMock.mockReset();
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  jest.spyOn(console, 'warn').mockImplementation(() => {});
});

afterEach(() => {
  jest.restoreAllMocks();
});

describe('feed problem classes', () => {
  it('network error is unreachable', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Network request failed'));
    await refresh();
    expect(getState()).toMatchObject({ status: 'error', problem: 'unreachable' });
  });

  it('timeout is unreachable', async () => {
    const abort = new Error('Aborted');
    abort.name = 'AbortError';
    fetchMock.mockRejectedValueOnce(abort);
    await refresh();
    expect(getState()).toMatchObject({ status: 'error', problem: 'unreachable' });
  });

  it('other HTTP errors are unreachable and keep the status in the message', async () => {
    respond(503);
    await refresh();
    expect(getState()).toMatchObject({ status: 'error', problem: 'unreachable' });
    expect(getState().error).toContain('503');
  });

  it('an unreadable 200 body is unreachable', async () => {
    respond(200, { nope: true });
    await refresh();
    expect(getState()).toMatchObject({ status: 'error', problem: 'unreachable' });
  });

  it.each([401, 403])('HTTP %i is unauthorized', async status => {
    respond(status);
    await refresh();
    expect(getState()).toMatchObject({ status: 'error', problem: 'unauthorized' });
  });

  it('no API key is unauthorized and sends no request', async () => {
    __setApiKeyForTests('');
    await refresh();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(getState()).toMatchObject({ status: 'error', problem: 'unauthorized' });
  });

  it('a 200 with zero debits is empty, not an error', async () => {
    respond(200, { payees: [], debits: [] });
    await refresh();
    expect(getState()).toMatchObject({ status: 'ready', problem: 'empty' });
  });

  it('rows that all fail parsing are unreadable, not empty', async () => {
    respond(200, { payees: [PAYEE], debits: [{ ...DEBIT, amountEUR: -1 }] });
    await refresh();
    expect(getState()).toMatchObject({ status: 'ready', problem: 'unreadable', dropped: 1 });
  });

  it('a 200 with debits has no problem, and a later failure then a recovery clears it', async () => {
    respond(200, { payees: [PAYEE], debits: [DEBIT] });
    await refresh();
    expect(getState().problem).toBeUndefined();

    respond(401);
    await refresh();
    expect(getState().problem).toBe('unauthorized');

    respond(200, { payees: [PAYEE], debits: [DEBIT] });
    await refresh();
    expect(getState().problem).toBeUndefined();
  });
});
