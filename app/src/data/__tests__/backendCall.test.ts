/**
 * The shared request helper: with the backend switched on but no key or address,
 * it says so once (no value in the line) and answers "no answer".
 */

export {};

function load(config: { USE_BACKEND: boolean; BACKEND_BASE_URL: string }, key: boolean) {
  let module!: typeof import('../backendCall');
  jest.isolateModules(() => {
    jest.doMock('../config', () => ({ ...config, BACKEND_TIMEOUT_MS: 1000 }));
    jest.doMock('../backendAuth', () => ({
      hasApiKey: () => key,
      backendHeaders: () => ({ Authorization: 'Bearer secret-key' }),
    }));
    module = require('../backendCall');
  });
  return module;
}

let warn: jest.SpyInstance;
beforeEach(() => {
  warn = jest.spyOn(console, 'warn').mockImplementation(() => {});
  (globalThis as unknown as { fetch: unknown }).fetch = jest.fn();
});
afterEach(() => jest.restoreAllMocks());

it('warns once, without any value, when the key or the address is missing', async () => {
  const { backendCall } = load({ USE_BACKEND: true, BACKEND_BASE_URL: 'http://backend.test' }, false);
  await expect(backendCall('tag', '/x', '/x', { method: 'GET' })).resolves.toEqual({ ok: false, status: null });
  await backendCall('tag', '/x', '/x', { method: 'GET' });
  expect(warn).toHaveBeenCalledTimes(1);
  expect(warn).toHaveBeenCalledWith('[tag] API key or backend address not configured');
  expect(fetch).not.toHaveBeenCalled();

  const noAddress = load({ USE_BACKEND: true, BACKEND_BASE_URL: '' }, true);
  await noAddress.backendCall('tag', '/x', '/x', { method: 'GET' });
  expect(warn).toHaveBeenCalledTimes(2);
});

it('says nothing when the backend is switched off on purpose', async () => {
  const { backendCall } = load({ USE_BACKEND: false, BACKEND_BASE_URL: '' }, false);
  await expect(backendCall('tag', '/x', '/x', { method: 'GET' })).resolves.toEqual({ ok: false, status: null });
  expect(warn).not.toHaveBeenCalled();
});
