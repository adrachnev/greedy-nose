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

export function showSettingsTab(ref: RootNavigationRef): void {
  if (!ref.isReady()) {
    return;
  }
  if (ref.getRootState()?.routeNames?.includes('Settings')) {
    (ref.navigate as (name: string) => void)('Settings');
  }
}
