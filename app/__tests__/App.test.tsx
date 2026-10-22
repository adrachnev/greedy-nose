/**
 * @format
 */

import React from 'react';
import ReactTestRenderer from 'react-test-renderer';
import App from '../App';
import { returnToApp } from '../src/data/connectStore';
import { startDeviceRegistration } from '../src/data/deviceStore';
import { showSettingsTab } from '../src/navigation/showSettingsTab';

// Mocked because the real one registers the phone with the backend, which under
// USE_BACKEND = true means a POST — and a rendered <App/> must never reach a
// developer's running backend (it once wrote the mock FCM token into the dev
// database). Its own behaviour is deviceStore.test.ts's job; what belongs to
// *this* file is that App wires it up, and that is asserted below.
jest.mock('../src/data/deviceStore');

// The real store would read AsyncStorage and /status; what belongs here is the wiring: App runs
// useConnectReturn, and a resumed attempt brings the user to Settings through showSettingsTab.
jest.mock('../src/data/connectStore', () => ({
  ...jest.requireActual('../src/data/connectStore'),
  returnToApp: jest.fn(),
}));
jest.mock('../src/navigation/showSettingsTab', () => ({ showSettingsTab: jest.fn() }));

const startMock = jest.mocked(startDeviceRegistration);

const returnMock = jest.mocked(returnToApp);
const showSettingsMock = jest.mocked(showSettingsTab);

beforeEach(() => {
  startMock.mockReset();
  returnMock.mockReset();
  returnMock.mockResolvedValue(false);
  showSettingsMock.mockReset();
});

test('renders correctly', async () => {
  await ReactTestRenderer.act(() => {
    ReactTestRenderer.create(<App />);
  });
});

test('registers the device once when the app mounts, and detaches on unmount', async () => {
  const stop = jest.fn();
  startMock.mockReturnValue(stop);

  let renderer!: ReactTestRenderer.ReactTestRenderer;
  await ReactTestRenderer.act(() => {
    renderer = ReactTestRenderer.create(<App />);
  });

  expect(startMock).toHaveBeenCalledTimes(1);
  expect(stop).not.toHaveBeenCalled();

  await ReactTestRenderer.act(() => {
    renderer.unmount();
  });

  expect(stop).toHaveBeenCalledTimes(1);
});

test('looks for a bank attempt to resume at start, and a resumed one opens Settings', async () => {
  returnMock.mockResolvedValue(true);
  await ReactTestRenderer.act(async () => {
    ReactTestRenderer.create(<App />);
  });
  expect(returnMock).toHaveBeenCalled();
  expect(showSettingsMock).toHaveBeenCalledTimes(1);
});

test('does not open Settings when there is nothing to resume', async () => {
  await ReactTestRenderer.act(async () => {
    ReactTestRenderer.create(<App />);
  });
  expect(returnMock).toHaveBeenCalled();
  expect(showSettingsMock).not.toHaveBeenCalled();
});
