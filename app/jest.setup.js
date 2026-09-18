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
// surface App.tsx currently calls; extend it if a later step (device token registration) uses
// more of it.
jest.mock('@react-native-firebase/messaging', () => ({
  AuthorizationStatus: { NOT_DETERMINED: -1, DENIED: 0, AUTHORIZED: 1, PROVISIONAL: 2 },
  getMessaging: jest.fn(),
  getToken: jest.fn(() => Promise.resolve('test-fcm-token')),
  requestPermission: jest.fn(() => Promise.resolve(1)),
}));
