/* eslint-env jest */
// Runs once per test file, before the test framework itself is set up
// (setupFiles, not setupFilesAfterEach) — see the RN preset's own setup.js,
// which this array runs alongside rather than replaces.
//
// The package's own Jest mock: an in-memory implementation of the same
// AsyncStorage interface, so src/data/rulesStore.ts is exercised against
// something that behaves like the real native module instead of nothing at
// all. `/jest` (not the older `/jest/async-storage-mock` path) is this
// package's current export map entry — see its package.json's "exports".
jest.mock('@react-native-async-storage/async-storage', () =>
  require('@react-native-async-storage/async-storage/jest'),
);

// @react-native-firebase/messaging has no native module in the Jest environment (there's no
// device), so importing it unmocked throws "Native module ... is not registered" the moment
// any file requires it — including transitively, via App.tsx. Ships no jest mock of its own
// (unlike async-storage above), so this is a hand-rolled stand-in covering only the modular API
// surface src/data/deviceStore.ts calls (getMessaging, getToken, onTokenRefresh) — nothing else
// of the real package's ~30 exports exists here, so an import of anything else is undefined;
// extend it if a later step uses more of it.
jest.mock('@react-native-firebase/messaging', () => ({
  getMessaging: jest.fn(),
  getToken: jest.fn(() => Promise.resolve('test-fcm-token')),
  onTokenRefresh: jest.fn(() => () => {}),
}));

// No test may reach the network. React Native's `fetch` is XHR-backed and, in Jest, really does
// connect to localhost — a rendered <App/> once POSTed the mock token above ('test-fcm-token')
// into a developer's running dev database. So the default `fetch` fails loudly instead, and a
// test that needs one assigns its own (global.fetch = fetchMock, as backendFeed.test.ts,
// deviceStore.test.ts and hooksSource.test.tsx do). setupFiles run per test file, so a test's
// own assignment never leaks into the next file.
//
// A plain function, not jest.fn(): a test's jest.restoreAllMocks()/resetAllMocks() must not be
// able to strip the rejection and turn this back into a silent no-op. It rejects rather than
// throws, because that is how a failed fetch reaches real callers (backendFeed's and
// deviceStore's catch), so the code under test takes its ordinary failure path — and the error
// message says why.
//
// Only `fetch` is guarded. Code that opens an XMLHttpRequest or a socket directly is not; there
// is none in app/ today.
global.fetch = function fetchNotMocked(input) {
  return Promise.reject(
    new Error(
      `fetch is not mocked in this test — tests must not reach the network (called with ${String(
        input,
      )}). Assign global.fetch = jest.fn(...) in the test.`,
    ),
  );
};
