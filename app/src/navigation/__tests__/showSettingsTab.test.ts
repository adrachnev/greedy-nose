import { showSettingsTab } from '../showSettingsTab';

const ref = (over: { ready?: boolean; routes?: string[] | undefined } = {}) => ({
  isReady: () => over.ready ?? true,
  getRootState: () => (over.routes ? { routeNames: over.routes } : undefined),
  navigate: jest.fn(),
});

it('navigates to the Settings tab when the main tabs are showing', () => {
  const r = ref({ routes: ['Debits', 'Rules', 'Settings'] });
  showSettingsTab(r);
  expect(r.navigate).toHaveBeenCalledWith('Settings');
});

it('does nothing during onboarding, before the container is ready, or without a state', () => {
  const onboarding = ref({ routes: ['ConnectBank', 'Syncing'] });
  showSettingsTab(onboarding);
  const notReady = ref({ ready: false, routes: ['Settings'] });
  showSettingsTab(notReady);
  const noState = ref({});
  showSettingsTab(noState);
  expect(onboarding.navigate).not.toHaveBeenCalled();
  expect(notReady.navigate).not.toHaveBeenCalled();
  expect(noState.navigate).not.toHaveBeenCalled();
});
