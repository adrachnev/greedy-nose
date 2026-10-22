/**
 * Story 5.7: where the attempt screen leaves to, with a REAL navigator (a stub
 * cannot show that a pop lands on the right screen). Stack as in the app:
 * SettingsHome, SelectBank, ConnectAttempt.
 */

import {
  createNavigationContainerRef,
  NavigationContainer,
} from '@react-navigation/native';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import React from 'react';
import { Text } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import ReactTestRenderer, { act } from 'react-test-renderer';
import {
  __resetConnectStoreForTests,
  getConnectFlow,
  startConnect,
  subscribeToConnectFlow,
} from '../../data/connectStore';
import { SettingsStackParamList } from '../../navigation/types';
import ConnectAttemptScreen from '../ConnectAttemptScreen';

jest.mock('../../data/config', () => ({
  USE_BACKEND: true,
  BACKEND_BASE_URL: 'http://backend.test',
  BACKEND_TIMEOUT_MS: 1000,
}));
jest.mock('../../data/backendAuth', () => ({
  hasApiKey: () => true,
  backendHeaders: () => ({ Authorization: 'Bearer test' }),
}));
jest.mock('../../data/openConnectLink', () => ({
  openConnectLink: jest.fn(() => Promise.resolve(false)),
  dismissConnectLink: jest.fn(() => Promise.resolve()),
}));

const Stack = createNativeStackNavigator<SettingsStackParamList>();
const ref = createNavigationContainerRef<SettingsStackParamList>();
const fetchMock = jest.fn();
let reason = 'bank-error';
let mounted: ReactTestRenderer.ReactTestRenderer | undefined;

const METRICS = {
  frame: { x: 0, y: 0, width: 375, height: 812 },
  insets: { top: 0, left: 0, right: 0, bottom: 0 },
};

const Home = () => <Text>home screen</Text>;
const Search = () => <Text>search screen</Text>;

function reply(status: number, body: unknown = {}) {
  return Promise.resolve({
    ok: status < 300,
    status,
    json: () => Promise.resolve(body),
  });
}

beforeEach(async () => {
  jest.spyOn(console, 'warn').mockImplementation(() => {});
  reason = 'bank-error';
  fetchMock.mockReset();
  fetchMock.mockImplementation((url: string) =>
    url.endsWith('/connect-links')
      ? reply(200, { url: 'https://example.test/c', attemptId: 'a1' })
      : reply(200, { state: 'failed', reason }),
  );
  (globalThis as unknown as { fetch: unknown }).fetch = fetchMock;
  __resetConnectStoreForTests();
  await act(async () => {
    mounted = ReactTestRenderer.create(
      <SafeAreaProvider initialMetrics={METRICS}>
        <NavigationContainer ref={ref}>
          <Stack.Navigator
            initialRouteName="SettingsHome"
            screenOptions={{ headerShown: false }}
          >
            <Stack.Screen name="SettingsHome" component={Home} />
            <Stack.Screen name="SelectBank" component={Search} />
            <Stack.Screen
              name="ConnectAttempt"
              component={ConnectAttemptScreen}
            />
          </Stack.Navigator>
        </NavigationContainer>
      </SafeAreaProvider>,
    );
  });
  // Settings > search > the attempt, which the read turns into "failed".
  await act(async () => {
    ref.navigate('SelectBank');
  });
  await act(async () => {
    await startConnect('Example Bank', 'DE');
    ref.navigate('ConnectAttempt');
  });
});

afterEach(() => {
  act(() => mounted?.unmount());
  mounted = undefined;
  jest.restoreAllMocks();
});

/** The state read says "failed": the screen shows the error. */
const fail = () => press('Check again');

const press = async (label: string) => {
  await act(async () => {
    const buttons = mounted!.root.findAll(
      n => n.props.accessibilityRole === 'button' && n.props.onPress,
    );
    buttons
      .find(b => b.findAllByType(Text).some(t => t.props.children === label))!
      .props.onPress();
  });
};

it('starts on the attempt screen with SelectBank beneath', () => {
  expect(ref.getCurrentRoute()?.name).toBe('ConnectAttempt');
  expect(JSON.stringify(ref.getRootState().routes.map(r => r.name))).toBe(
    '["SettingsHome","SelectBank","ConnectAttempt"]',
  );
});

it('"Choose a different bank" lands on the search, not on Settings', async () => {
  await fail();
  // On a device the screen stays mounted while it pops. An idle flow at that moment would send it on to Settings
  // (the idle effect), so the ended attempt may only be forgotten once the screen is gone from the stack.
  let screenStillThereWhenForgotten: boolean | undefined;
  const unsubscribe = subscribeToConnectFlow(() => {
    if (getConnectFlow().step === 'idle') {
      screenStillThereWhenForgotten = ref
        .getRootState()
        .routes.some(r => r.name === 'ConnectAttempt');
    }
  });
  await press('Choose a different bank');
  unsubscribe();
  expect(ref.getCurrentRoute()?.name).toBe('SelectBank');
  expect(screenStillThereWhenForgotten).toBe(false);
});

it('"Cancel" on an ended attempt lands on Settings', async () => {
  await fail();
  await press('Cancel');
  expect(ref.getCurrentRoute()?.name).toBe('SettingsHome');
});

it('"Back to Settings" lands on Settings', async () => {
  reason = 'no-supported-account';
  await fail();
  await press('Back to Settings');
  expect(ref.getCurrentRoute()?.name).toBe('SettingsHome');
});

describe('after a restart the stack holds only Settings home and the attempt', () => {
  beforeEach(async () => {
    await act(async () => {
      ref.resetRoot({
        index: 1,
        routes: [{ name: 'SettingsHome' }, { name: 'ConnectAttempt' }],
      });
    });
  });

  const names = () => ref.getRootState().routes.map(r => r.name);

  it('"Choose a different bank" replaces the ended attempt with the search', async () => {
    await fail();
    await press('Choose a different bank');
    expect(names()).toEqual(['SettingsHome', 'SelectBank']);
    expect(ref.getCurrentRoute()?.name).toBe('SelectBank');
    expect(getConnectFlow().step).toBe('idle');
  });

  it('"Back to Settings" leaves only Settings home', async () => {
    reason = 'no-supported-account';
    await fail();
    await press('Back to Settings');
    expect(names()).toEqual(['SettingsHome']);
  });
});
