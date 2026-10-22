import { MAIN_TAB_NAVIGATOR_ID } from './ids';

type TabNavigation = { navigate: (name: string) => void };

/** What is used of a navigation prop; the cast below is the same one as in openSelectBank. */
export type DebitListOpener = {
  getParent: (id: string) => TabNavigation | undefined;
};

/** Leaves the Settings tab for the debit list. The Debits tab always lands on its list (R24). */
export function openDebitList(navigation: object): void {
  (navigation as DebitListOpener).getParent(MAIN_TAB_NAVIGATOR_ID)?.navigate('Debits');
}

/**
 * The end of the connect flow (Stories 5.8 and 5.9): the Settings stack is cut back to its home, so coming
 * back to the tab finds neither the sync screen nor the offer, and the debit list opens.
 */
export function leaveSettingsForDebitList(navigation: object): void {
  (navigation as { reset: (state: object) => void }).reset({ index: 0, routes: [{ name: 'SettingsHome' }] });
  openDebitList(navigation);
}
