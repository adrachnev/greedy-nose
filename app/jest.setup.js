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
