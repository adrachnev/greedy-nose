/**
 * The API key is a credential: these pin what must hold whatever the key is —
 * the header shape every backend call relies on, that an empty key reads as
 * "not configured", and that the key is only ever read from the local file
 * (mocked in jest.setup.js; no test sees a real key).
 */

import { __setApiKeyForTests, backendHeaders, hasApiKey } from '../backendAuth';

afterEach(() => {
  __setApiKeyForTests('test-api-key');
});

describe('backendAuth', () => {
  it('reads the key from the (mocked) local file', () => {
    expect(hasApiKey()).toBe(true);
    expect(backendHeaders()).toEqual({ Authorization: 'Bearer test-api-key' });
  });

  it('merges the caller headers next to Authorization', () => {
    expect(backendHeaders({ Accept: 'application/json' })).toEqual({
      Authorization: 'Bearer test-api-key',
      Accept: 'application/json',
    });
  });

  it('reports an empty key as not configured', () => {
    __setApiKeyForTests('');
    expect(hasApiKey()).toBe(false);
  });
});
