import { MAIN_TAB_NAVIGATOR_ID } from './ids';

type TabNavigation = { navigate: (name: string, params: object) => void };

/**
 * What is used of a navigation prop. Screens pass their own typed prop, whose
 * `getParent` only accepts the ids its param list knows; hence the cast below.
 */
export type SelectBankOpener = {
  getParent: (id: string) => TabNavigation | undefined;
};

/**
 * Opens the bank search (Story 5.6) from a screen of any tab: the Settings tab
 * with SelectBank on top of its home. `initial: false` keeps SettingsHome as
 * the first route of that stack, so Cancel has somewhere to go back to.
 */
export function openSelectBank(navigation: object): void {
  (navigation as SelectBankOpener).getParent(MAIN_TAB_NAVIGATOR_ID)?.navigate('Settings', {
    screen: 'SelectBank',
    initial: false,
  });
}
