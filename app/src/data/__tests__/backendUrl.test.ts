import { resolveBackendBaseUrl } from '../config';

describe('resolveBackendBaseUrl', () => {
  it('uses localhost in dev, whatever the local file says', () => {
    expect(resolveBackendBaseUrl(true, undefined)).toBe('http://localhost:5199');
    expect(resolveBackendBaseUrl(true, 'https://example.test')).toBe('http://localhost:5199');
  });

  it('uses the local https address in release, without a trailing slash', () => {
    expect(resolveBackendBaseUrl(false, 'https://example.test/')).toBe('https://example.test');
  });

  it.each([undefined, null, 42, '', '   ', 'http://example.test', 'example.test', 'https://'])(
    'treats %p as not configured in release',
    value => {
      expect(resolveBackendBaseUrl(false, value)).toBe('');
    },
  );
});

describe('missing backend.local file', () => {
  it('leaves a release build not configured instead of crashing', () => {
    const globals = globalThis as unknown as { __DEV__: boolean };
    const previous = globals.__DEV__;
    globals.__DEV__ = false;
    try {
      jest.isolateModules(() => {
        // Not virtual: the file exists on this machine, and a virtual mock would be
        // registered under a different key than the one config.ts resolves.
        jest.doMock('../backend.local', () => {
          throw new Error("Cannot find module './backend.local'");
        });
        const { BACKEND_BASE_URL } = require('../config') as typeof import('../config');
        expect(BACKEND_BASE_URL).toBe('');
      });
    } finally {
      globals.__DEV__ = previous;
      jest.dontMock('../backend.local');
    }
  });
});
