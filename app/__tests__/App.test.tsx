/**
 * @format
 */

import React from 'react';
import ReactTestRenderer from 'react-test-renderer';
import App from '../App';
import { startDeviceRegistration } from '../src/data/deviceStore';

// Mocked because the real one registers the phone with the backend, which under
// USE_BACKEND = true means a POST — and a rendered <App/> must never reach a
// developer's running backend (it once wrote the mock FCM token into the dev
// database). Its own behaviour is deviceStore.test.ts's job; what belongs to
// *this* file is that App wires it up, and that is asserted below.
jest.mock('../src/data/deviceStore');

const startMock = jest.mocked(startDeviceRegistration);

beforeEach(() => {
  startMock.mockReset();
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
