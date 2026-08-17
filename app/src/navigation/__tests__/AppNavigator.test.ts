/**
 * R24: **re-tapping the tab you are already on does nothing.**
 *
 * The listener under test is a redundant guard, not the mechanism:
 * `@react-navigation/bottom-tabs` already ignores a press on the focused tab
 * (`BottomTabBar` guards with `if (!focused && !event.defaultPrevented)`).
 * What this test buys is that the requirement is asserted somewhere rather
 * than assumed of a library — and that the guard cannot invert into
 * "swallow every tab switch", which would strand the user with no error and
 * nothing rendered differently.
 *
 * It does **not** cover the unstable native tab navigator, which pops to root
 * via `repeatedTabSelection` and emits `tabPress` without `canPreventDefault`;
 * see the note on `listTabListeners`.
 */

import { listTabListeners } from '../AppNavigator';

function tabPressWith(isFocused: boolean) {
  const preventDefault = jest.fn();
  listTabListeners({ navigation: { isFocused: () => isFocused } }).tabPress({ preventDefault });
  return preventDefault;
}

describe('listTabListeners — R24', () => {
  it('swallows the press when the tab is already focused', () => {
    // Belt and braces over the library's own `!focused` check: R24 should not
    // be one upstream refactor away from re-tap popping the nested stack.
    expect(tabPressWith(true)).toHaveBeenCalled();
  });

  it('lets the press through when the tab is not focused', () => {
    // This is an ordinary tab switch; blocking it would strand the user.
    expect(tabPressWith(false)).not.toHaveBeenCalled();
  });
});
