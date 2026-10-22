/**
 * Brings the user to the Settings tab, where the result of connecting a bank is
 * shown. Used when the app is resumed from the bank's pages (by link, by hand,
 * or after Android restarted it), which can happen while any screen is open.
 *
 * Does nothing while onboarding is not complete: that navigator has no
 * Settings route, and navigating to one would only log an unhandled action.
 * Structurally typed (only what is used), like listTabListeners in AppNavigator.
 */
export type RootNavigationRef = {
  isReady: () => boolean;
  getRootState: () => { routeNames?: string[] } | undefined;
  navigate: (name: never) => void;
};

/** `true` when the jump was made, `false` when it could not be (not ready, onboarding). */
export function showSettingsTab(ref: RootNavigationRef): boolean {
  if (!ref.isReady() || !ref.getRootState()?.routeNames?.includes('Settings')) {
    return false;
  }
  (ref.navigate as (name: string) => void)('Settings');
  return true;
}
