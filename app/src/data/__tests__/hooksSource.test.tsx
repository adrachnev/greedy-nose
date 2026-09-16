/**
 * Which store the hooks read from is decided once, at module load, from
 * USE_BACKEND in ./config — and until now nothing tested either branch. That
 * left the fixture path completely unexercised the moment the flag was flipped
 * to the backend, which is backwards: "flip one line back to fixtures" is this
 * project's safety net for when the live feed misbehaves, and a safety net
 * nobody tests is a rope of unknown length.
 *
 * The binding is module-level, so the only way to see both halves is to load
 * the module twice against two different configs — hence jest.isolateModules
 * plus a mocked ./config rather than anything cleverer.
 */

import React from 'react';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { Text } from 'react-native';

type Modules = {
  hooks: typeof import('../hooks');
  feed: typeof import('../backendFeed');
  fixtures: typeof import('../../mocks/data');
};

function loadWith(useBackend: boolean): Modules {
  let modules!: Modules;
  jest.isolateModules(() => {
    jest.doMock('../config', () => ({
      USE_BACKEND: useBackend,
      BACKEND_BASE_URL: 'http://backend.test',
      BACKEND_TIMEOUT_MS: 1000,
    }));
    // React is pinned to the copy this file (and the renderer) already hold.
    // Without it the isolated registry hands hooks.ts a *second* React whose
    // hook dispatcher is never installed, and every render dies on
    // "Cannot read properties of null (reading 'useSyncExternalStore')" —
    // which looks like a bug in the code under test and is not.
    jest.doMock('react', () => React);
    modules = {
      hooks: require('../hooks'),
      feed: require('../backendFeed'),
      fixtures: require('../../mocks/data'),
    };
  });
  return modules;
}

const BACKEND_PAYEE = {
  id: 'name:LIDL CONNECT',
  name: 'LIDL CONNECT',
  initials: 'LC',
  iban: '',
};
const BACKEND_DEBIT = {
  id: 'DE26:2026-08-18:20.00:name:LIDL CONNECT#0',
  payeeId: 'name:LIDL CONNECT',
  amountEUR: 20,
  timestamp: '2026-08-18T00:00:00Z',
  hasTime: false,
  paymentType: 'Card payment',
  reference: '',
};

const fetchMock = jest.fn();

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({
    ok: true,
    status: 200,
    statusText: 'OK',
    json: async () => ({ payees: [BACKEND_PAYEE], debits: [BACKEND_DEBIT], skipped: 4 }),
  });
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
});

afterEach(() => {
  jest.dontMock('../config');
  jest.dontMock('react');
  jest.restoreAllMocks();
});

/**
 * Renders the two list hooks a screen would use and prints what they returned,
 * so the assertion is on data that actually made it through
 * useSyncExternalStore rather than on a store read directly.
 */
function renderProbe(hooks: Modules['hooks']) {
  function Probe() {
    const payees = hooks.usePayees();
    const debits = hooks.useDebits();
    const source = hooks.useDataSource();
    return (
      <>
        <Text testID="payees">{payees.map(p => p.name).join(',')}</Text>
        <Text testID="debits">{String(debits.length)}</Text>
        <Text testID="live">{String(source.live)}</Text>
        <Text testID="feed">{`${source.feed.status}/${source.feed.dropped}/${source.feed.skipped}`}</Text>
      </>
    );
  }

  let renderer!: ReactTestRenderer.ReactTestRenderer;
  act(() => {
    renderer = ReactTestRenderer.create(<Probe />);
  });
  return (testID: string): string => renderer.root.findByProps({ testID }).props.children;
}

describe('USE_BACKEND = true', () => {
  it('reads payees and debits from the feed, and reports the feed’s counters', async () => {
    const { hooks, feed } = loadWith(true);
    const textAt = renderProbe(hooks);

    // Mounting subscribes, which is what starts the first load; awaiting
    // refresh() joins that same load rather than starting another.
    await act(async () => {
      await feed.refresh();
    });

    expect(textAt('payees')).toBe('LIDL CONNECT');
    expect(textAt('debits')).toBe('1');
    expect(textAt('live')).toBe('true');
    expect(textAt('feed')).toBe('ready/0/4');
    expect(fetchMock).toHaveBeenCalledWith('http://backend.test/debits', expect.anything());
  });

  it('serves an empty local rule store on first launch — no demo data pretending to be real (R6)', async () => {
    const { hooks } = loadWith(true);
    let rules!: ReturnType<typeof hooks.useRules>;
    function Capture() {
      rules = hooks.useRules();
      return null;
    }
    // rulesStore.ts seeds [] rather than the fixture mix in backend mode: the
    // fixture payee ids (`payee-netflix`) don't exist under a real account
    // (`name:LIDL CONNECT`), so seeding them would be inert demo data at best.
    await act(async () => {
      ReactTestRenderer.create(<Capture />);
    });

    expect(rules).toEqual([]);
  });
});

describe('USE_BACKEND = false', () => {
  it('reads the fixtures and never touches the network', () => {
    const { hooks, fixtures } = loadWith(false);
    const textAt = renderProbe(hooks);

    expect(textAt('payees')).toBe(fixtures.payees.map(p => p.name).join(','));
    expect(textAt('debits')).toBe(String(fixtures.debits.length));
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('reports a source that never loads and never fails, so the badge stays quiet', () => {
    const { hooks } = loadWith(false);
    const textAt = renderProbe(hooks);

    expect(textAt('live')).toBe('false');
    // Both counters are 0 by construction: nothing is fetched, so nothing can
    // be refused on the way in by either side.
    expect(textAt('feed')).toBe('ready/0/0');
  });

  it('leaves refresh() a no-op rather than making callers branch on the flag', () => {
    const { hooks } = loadWith(false);
    let refresh!: () => void;
    function Capture() {
      refresh = hooks.useDataSource().refresh;
      return null;
    }
    act(() => {
      ReactTestRenderer.create(<Capture />);
    });

    act(() => {
      refresh();
    });

    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('still serves rules from the local store, seeded with the fixture mix — the backend sends no classification (R6)', async () => {
    const { hooks, fixtures } = loadWith(false);
    let rules!: ReturnType<typeof hooks.useRules>;
    function Capture() {
      rules = hooks.useRules();
      return null;
    }
    // Hydration off the (mocked) AsyncStorage is async — see rulesStore.ts —
    // so the render has to be awaited before rules reflects the seed.
    await act(async () => {
      ReactTestRenderer.create(<Capture />);
    });

    expect(rules).toEqual(fixtures.FIXTURE_SEED_RULES);
  });
});
