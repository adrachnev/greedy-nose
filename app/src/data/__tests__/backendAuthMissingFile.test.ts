/**
 * A fresh checkout has no apiKey.local.ts. That has to read as "not
 * configured", not crash the app at import time. Its own file because Jest
 * hands an isolated registry any module the *main* registry already loaded,
 * so this must be the first thing in its registry to touch the key file.
 */

it('treats a missing local file as an empty key', () => {
  jest.doMock(
    '../apiKey.local',
    () => {
      throw new Error("Cannot find module './apiKey.local'");
    },
    { virtual: true },
  );
  const { hasApiKey } = require('../backendAuth') as typeof import('../backendAuth');

  expect(hasApiKey()).toBe(false);
});
