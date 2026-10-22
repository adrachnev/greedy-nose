import { showConnectAttempt } from '../showConnectAttempt';

const ref = (over: { ready?: boolean; routes?: string[] | undefined } = {}) => ({
  isReady: () => over.ready ?? true,
  getRootState: () => (over.routes ? { routeNames: over.routes } : undefined),
  navigate: jest.fn(),
});

it('opens the attempt screen on top of Settings home when the main tabs are showing', () => {
  const r = ref({ routes: ['Debits', 'Rules', 'Settings'] });
  expect(showConnectAttempt(r)).toBe(true);
  expect(r.navigate).toHaveBeenCalledWith('Settings', { screen: 'ConnectAttempt', initial: false });
});

it('does nothing during onboarding, before the container is ready, or without a state', () => {
  const onboarding = ref({ routes: ['ConnectBank', 'Syncing'] });
  const notReady = ref({ ready: false, routes: ['Settings'] });
  const noState = ref({});
  expect(showConnectAttempt(onboarding)).toBe(false);
  expect(showConnectAttempt(notReady)).toBe(false);
  expect(showConnectAttempt(noState)).toBe(false);
  for (const r of [onboarding, notReady, noState]) {
    expect(r.navigate).not.toHaveBeenCalled();
  }
});
