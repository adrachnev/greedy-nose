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
