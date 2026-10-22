import { RootNavigationRef } from './showSettingsTab';

/**
 * Brings the user to the screen of the running bank attempt (Story 5.7), in the
 * Settings tab. Used when the app is resumed from the bank's pages (by link, by
 * hand, or after Android restarted it), which can happen while any screen is
 * open. `initial: false` keeps SettingsHome as the first route of that stack, so
 * leaving the attempt has somewhere to go back to, also after a restart.
 *
 * Does nothing while onboarding is not complete: that navigator has no Settings
 * route, and navigating to one would only log an unhandled action.
 * `RootNavigationRef` is the structural type of showSettingsTab.
 */
export function showConnectAttempt(ref: RootNavigationRef): boolean {
  if (!ref.isReady() || !ref.getRootState()?.routeNames?.includes('Settings')) {
    return false;
  }
  (ref.navigate as unknown as (name: string, params: object) => void)('Settings', {
    screen: 'ConnectAttempt',
    initial: false,
  });
  return true;
}
