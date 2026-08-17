import { useNavigation } from '@react-navigation/native';
import { useEffect, useState } from 'react';
import { MAIN_TAB_NAVIGATOR_ID } from '../navigation/ids';

/**
 * Search state scoped to a **visit to the tab**, not to the screen (R23b).
 *
 * The query has to survive list -> debit detail -> Back, so several hits can be
 * worked through without retyping, and it has to be gone when the user comes
 * back to the tab later, so a tab always hands back the full list. A short list
 * with no visible cause is the failure this prevents — it looks exactly like a
 * dead bank connection (R19 exists for the same reason).
 *
 * Hence the **tab's** blur, not this screen's: the screen blurs on the way
 * into a detail screen too, and clearing there would break the first half of
 * R23b. useFocusEffect has the same problem.
 *
 * The tab is looked up **by id**, not as `getParent()`. Bare "one level up"
 * happens to be right for a list screen inside a tab's stack today, and would
 * go on being green in every test while pointing at the wrong navigator the
 * moment a screen is nested one level differently. Asking for
 * MAIN_TAB_NAVIGATOR_ID says what this hook actually needs, and keeps being
 * correct at any depth.
 */
export function useTabScopedSearch(): {
  search: string;
  setSearch: (value: string) => void;
} {
  const navigation = useNavigation();
  const [search, setSearch] = useState('');

  useEffect(() => {
    // Undefined when this screen is not under the tabs at all — a bare render
    // in tests or Storybook, or the onboarding stack. Nothing to clear on, and
    // R23b has nothing to say there, so this is a no-op rather than a throw.
    const tab = navigation.getParent(MAIN_TAB_NAVIGATOR_ID);
    if (!tab) {
      return;
    }
    return tab.addListener('blur', () => setSearch(''));
  }, [navigation]);

  return { search, setSearch };
}
