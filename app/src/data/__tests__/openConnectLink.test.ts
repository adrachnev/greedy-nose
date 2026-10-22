import { Linking } from 'react-native';
import { InAppBrowser } from 'react-native-inappbrowser-reborn';
import { CHROME_PACKAGE, dismissConnectLink, openConnectLink } from '../openConnectLink';

const url = 'https://bank.test/sign-in';
const inApp = InAppBrowser as unknown as Record<'isAvailable' | 'open' | 'close', jest.Mock>;

beforeEach(() => {
  jest.spyOn(Linking, 'openURL').mockClear();
  jest.spyOn(Linking, 'openURL').mockResolvedValue(undefined as never);
  inApp.isAvailable.mockResolvedValue(true);
  inApp.open.mockResolvedValue({ type: 'dismiss' });
  inApp.close.mockReset();
  inApp.open.mockClear();
});
afterEach(() => jest.restoreAllMocks());

it('opens a tab and reports when the user dismissed it', async () => {
  await expect(openConnectLink(url)).resolves.toBe(true);
  expect(inApp.open).toHaveBeenCalledWith(
    url,
    expect.objectContaining({ browserPackage: CHROME_PACKAGE }),
  );
  expect(Linking.openURL).not.toHaveBeenCalled();
});

it('falls back to the plain browser where Custom Tabs are missing', async () => {
  inApp.isAvailable.mockResolvedValue(false);
  await expect(openConnectLink(url)).resolves.toBe(false);
  expect(Linking.openURL).toHaveBeenCalledWith(url);
});

it('closing never throws', async () => {
  inApp.close.mockImplementation(() => {
    throw new Error('nothing open');
  });
  await expect(dismissConnectLink()).resolves.toBeUndefined();
});

it('closing never leaves an unhandled rejection', async () => {
  inApp.close.mockReturnValue(Promise.reject(new Error('nothing open')));
  await expect(dismissConnectLink()).resolves.toBeUndefined();
});

it('falls back to the default browser tab, without a package, when Chrome cannot open it', async () => {
  inApp.open.mockRejectedValueOnce(new Error('Unable to open url.'));
  await expect(openConnectLink(url)).resolves.toBe(true);
  expect(inApp.open).toHaveBeenCalledTimes(2);
  expect(inApp.open.mock.calls[1][1]).not.toHaveProperty('browserPackage');
  expect(Linking.openURL).not.toHaveBeenCalled();
});

it('uses the plain browser when no tab opens at all', async () => {
  inApp.open.mockRejectedValue(new Error('Unable to open url.'));
  await expect(openConnectLink(url)).resolves.toBe(false);
  expect(Linking.openURL).toHaveBeenCalledWith(url);
});
