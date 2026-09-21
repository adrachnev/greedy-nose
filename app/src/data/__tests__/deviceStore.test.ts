/**
 * Device registration is the one place the app hands the backend something it
 * cannot recreate: the FCM token a push has to be addressed to. These tests pin
 * what a silent failure would hide — that the token really is POSTed under the
 * right conditions, that a denied permission or a dead backend never stops the
 * app, that a second call (Fast Refresh, or any remount of the root effect)
 * does not prompt or POST twice, and that the token — a delivery credential —
 * never lands in a log.
 *
 * The store is module-level state guarded by USE_BACKEND, `Platform` and a
 * "started" flag, so — same as hooksSource.test.tsx and rulesStore.test.ts —
 * each case loads a fresh copy under jest.isolateModules against a mocked
 * ./config and a mocked `react-native` (the real `Platform.Version` is a
 * getter that cannot be set per test).
 *
 * "Swallowed" below means the failure is reported with console.warn and the
 * test still finishes: a promise that rejected with nobody listening would fail
 * the test on its own, since jest-circus treats an unhandled rejection as an
 * error, so no assertion of ours is needed for that half.
 */

const PERMISSION = 'android.permission.POST_NOTIFICATIONS';

const permissions = {
  request: jest.fn(),
  PERMISSIONS: { POST_NOTIFICATIONS: PERMISSION },
  RESULTS: { GRANTED: 'granted', DENIED: 'denied', NEVER_ASK_AGAIN: 'never_ask_again' },
};

const fetchMock = jest.fn();

type Env = { useBackend?: boolean; os?: string; version?: number };

function load({ useBackend = true, os = 'android', version = 34 }: Env = {}) {
  let loaded!: {
    store: typeof import('../deviceStore');
    messaging: {
      getMessaging: jest.Mock;
      getToken: jest.Mock;
      onTokenRefresh: jest.Mock;
    };
  };
  jest.isolateModules(() => {
    jest.doMock('../config', () => ({
      USE_BACKEND: useBackend,
      BACKEND_BASE_URL: 'http://backend.test',
      BACKEND_TIMEOUT_MS: 1000,
    }));
    jest.doMock('react-native', () => ({
      Platform: { OS: os, Version: version },
      PermissionsAndroid: permissions,
    }));
    loaded = {
      store: require('../deviceStore'),
      // The stand-in from jest.setup.js, re-created inside this isolated
      // registry — so the fns to assert on have to be read from here, not from
      // an import at the top of this file.
      messaging: require('@react-native-firebase/messaging'),
    };
  });
  return loaded;
}

/**
 * A macrotask, so every promise the fire-and-forget registration chains
 * together has settled before an assertion reads its result — counting awaits
 * instead would assert against an implementation detail.
 */
function flush(): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, 0));
}

let warn: jest.SpyInstance;
// Every console method a failure could be reported through — the credential
// check below reads all of them, not just the one the code uses today.
let logSpies: jest.SpyInstance[];

beforeEach(() => {
  permissions.request.mockReset();
  permissions.request.mockResolvedValue('granted');
  fetchMock.mockReset();
  fetchMock.mockResolvedValue({ ok: true, status: 204 });
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  warn = jest.spyOn(console, 'warn').mockImplementation(() => {});
  logSpies = [
    warn,
    jest.spyOn(console, 'log').mockImplementation(() => {}),
    jest.spyOn(console, 'info').mockImplementation(() => {}),
    jest.spyOn(console, 'debug').mockImplementation(() => {}),
    jest.spyOn(console, 'error').mockImplementation(() => {}),
  ];
});

afterEach(() => {
  jest.useRealTimers();
  jest.dontMock('../config');
  jest.dontMock('react-native');
  jest.restoreAllMocks();
});

/** Every failure test ends here: the failure was reported, and the test got to finish. */
async function expectSwallowed(): Promise<void> {
  await flush();
  expect(warn).toHaveBeenCalled();
}

/**
 * Everything the code under test printed, as one string. Error objects are
 * expanded (name, message, stack, own properties) because JSON.stringify shows
 * them as `{}` — and an error that carries the token in its message is exactly
 * how it would leak, since the store passes the caught error through to warn.
 */
function everythingLogged(): string {
  const text = (arg: unknown): string => {
    if (arg instanceof Error) {
      return [arg.name, arg.message, arg.stack, JSON.stringify({ ...arg })].join(' ');
    }
    return typeof arg === 'string' ? arg : JSON.stringify(arg) ?? String(arg);
  };
  return logSpies.flatMap(spy => spy.mock.calls.flat()).map(text).join('\n');
}

describe('registering', () => {
  it('does nothing at all when the backend is off — no prompt, no token read, no request', async () => {
    const { store, messaging } = load({ useBackend: false });

    const stop = store.startDeviceRegistration();
    await flush();
    stop();

    expect(permissions.request).not.toHaveBeenCalled();
    expect(messaging.getMessaging).not.toHaveBeenCalled();
    expect(messaging.getToken).not.toHaveBeenCalled();
    expect(messaging.onTokenRefresh).not.toHaveBeenCalled();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('asks for POST_NOTIFICATIONS on Android 13+ and POSTs the token to the backend', async () => {
    const { store, messaging } = load({ os: 'android', version: 33 });
    messaging.getToken.mockResolvedValue('tok-1');

    store.startDeviceRegistration();
    await flush();

    expect(permissions.request).toHaveBeenCalledWith(PERMISSION);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith('http://backend.test/device-token', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: 'tok-1' }),
      signal: expect.any(AbortSignal),
    });
    expect(warn).not.toHaveBeenCalled();
  });

  it('still registers the token when the user denies the permission', async () => {
    const { store, messaging } = load({ os: 'android', version: 34 });
    messaging.getToken.mockResolvedValue('tok-1');
    permissions.request.mockResolvedValue('never_ask_again');

    store.startDeviceRegistration();
    await flush();

    expect(fetchMock).toHaveBeenCalledWith(
      'http://backend.test/device-token',
      expect.objectContaining({ body: JSON.stringify({ token: 'tok-1' }) }),
    );
    // The denial is reported, but as a warning — not as the reason to stop.
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('never_ask_again'));
  });

  it('still registers the token when the permission request itself throws', async () => {
    const { store } = load({ os: 'android', version: 34 });
    permissions.request.mockRejectedValue(new Error('no activity'));

    store.startDeviceRegistration();
    await flush();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('never prompts below Android 13, which needs no runtime permission, but still registers', async () => {
    const { store } = load({ os: 'android', version: 32 });

    store.startDeviceRegistration();
    await flush();

    expect(permissions.request).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('failures are swallowed and reported, never thrown', () => {
  it('a backend that cannot be reached', async () => {
    const { store } = load();
    fetchMock.mockRejectedValue(new TypeError('Network request failed'));

    store.startDeviceRegistration();

    await expectSwallowed();
  });

  it('a non-2xx answer', async () => {
    const { store } = load();
    fetchMock.mockResolvedValue({ ok: false, status: 400 });

    store.startDeviceRegistration();

    await expectSwallowed();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('HTTP 400'));
  });

  it('a backend that never answers, aborted at BACKEND_TIMEOUT_MS', async () => {
    jest.useFakeTimers();
    const { store } = load();
    let signal!: AbortSignal;
    fetchMock.mockImplementation(
      (_url: string, init: { signal: AbortSignal }) =>
        new Promise((_resolve, reject) => {
          signal = init.signal;
          signal.addEventListener('abort', () => reject(new Error('Aborted')));
        }),
    );

    store.startDeviceRegistration();
    await jest.advanceTimersByTimeAsync(999);
    expect(signal.aborted).toBe(false);
    await jest.advanceTimersByTimeAsync(1);

    expect(signal.aborted).toBe(true);
    // flush() waits on a real timer, so the fake ones have to go first.
    jest.useRealTimers();
    await expectSwallowed();
  });

  it('a token that cannot be read', async () => {
    const { store, messaging } = load();
    messaging.getToken.mockRejectedValue(new Error('SERVICE_NOT_AVAILABLE'));

    store.startDeviceRegistration();

    await expectSwallowed();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('a refresh listener that cannot be attached — the app still starts, and still registers', async () => {
    const { store, messaging } = load();
    messaging.onTokenRefresh.mockImplementation(() => {
      throw new Error('native module missing');
    });

    expect(() => store.startDeviceRegistration()).not.toThrow();
    await flush();

    expect(warn).toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('token refresh', () => {
  it('POSTs the rotated token, and cleanup detaches the listener', async () => {
    const { store, messaging } = load();
    messaging.getToken.mockResolvedValue('tok-1');
    const unsubscribe = jest.fn();
    messaging.onTokenRefresh.mockReturnValue(unsubscribe);

    const stop = store.startDeviceRegistration();
    await flush();
    const listener = messaging.onTokenRefresh.mock.calls[0][1];
    listener('tok-2');
    await flush();

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock).toHaveBeenLastCalledWith(
      'http://backend.test/device-token',
      expect.objectContaining({ body: JSON.stringify({ token: 'tok-2' }) }),
    );

    expect(unsubscribe).not.toHaveBeenCalled();
    stop();
    expect(unsubscribe).toHaveBeenCalledTimes(1);
  });

  it('a failed POST of the rotated token is swallowed too', async () => {
    const { store, messaging } = load();
    store.startDeviceRegistration();
    await flush();
    fetchMock.mockRejectedValue(new TypeError('Network request failed'));

    messaging.onTokenRefresh.mock.calls[0][1]('tok-2');

    await expectSwallowed();
  });
});

describe('calling it more than once', () => {
  it('registers once: one prompt, one token read, one POST, one listener', async () => {
    const { store, messaging } = load();

    store.startDeviceRegistration();
    store.startDeviceRegistration();
    await flush();

    expect(permissions.request).toHaveBeenCalledTimes(1);
    expect(messaging.getToken).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(messaging.onTokenRefresh).toHaveBeenCalledTimes(1);
  });

  it('survives an effect re-run: the listener comes back, the registration does not repeat', async () => {
    const { store, messaging } = load();
    const unsubscribe = jest.fn();
    messaging.onTokenRefresh.mockReturnValue(unsubscribe);

    // mount → cleanup → mount: what Fast Refresh or a remounted root does to the
    // effect that calls this.
    const stop = store.startDeviceRegistration();
    stop();
    store.startDeviceRegistration();
    await flush();

    expect(unsubscribe).toHaveBeenCalledTimes(1);
    expect(messaging.onTokenRefresh).toHaveBeenCalledTimes(2);
    expect(messaging.getToken).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('the token is a delivery credential and never reaches a log', () => {
  const TOKEN = 'fcm-secret-token-0123456789';

  // Each row drives one path that reports something, with the token in play
  // wherever the flow has one. The rotated token starts with TOKEN, so a single
  // substring check covers both.
  const paths: [string, (loaded: ReturnType<typeof load>) => Promise<void>][] = [
    [
      'a non-2xx answer',
      async ({ store }) => {
        fetchMock.mockResolvedValue({ ok: false, status: 500 });
        store.startDeviceRegistration();
        await flush();
      },
    ],
    [
      'a backend that cannot be reached',
      async ({ store }) => {
        fetchMock.mockRejectedValue(new TypeError('Network request failed'));
        store.startDeviceRegistration();
        await flush();
      },
    ],
    [
      'a denied permission',
      async ({ store }) => {
        permissions.request.mockResolvedValue('denied');
        store.startDeviceRegistration();
        await flush();
      },
    ],
    [
      'a token that cannot be read',
      async ({ store, messaging }) => {
        messaging.getToken.mockRejectedValue(new Error('SERVICE_NOT_AVAILABLE'));
        store.startDeviceRegistration();
        await flush();
      },
    ],
    [
      'a rotated token that fails to post',
      async ({ store, messaging }) => {
        store.startDeviceRegistration();
        await flush();
        fetchMock.mockRejectedValue(new TypeError('Network request failed'));
        messaging.onTokenRefresh.mock.calls[0][1](`${TOKEN}-rotated`);
        await flush();
      },
    ],
  ];

  it.each(paths)('%s', async (_name, run) => {
    const loaded = load({ os: 'android', version: 34 });
    loaded.messaging.getToken.mockResolvedValue(TOKEN);

    await run(loaded);

    // Not vacuous: the path did report something, and it is that report we read.
    expect(everythingLogged().length).toBeGreaterThan(0);
    expect(everythingLogged()).not.toContain(TOKEN);
  });
});
