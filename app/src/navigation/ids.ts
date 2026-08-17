/**
 * Navigator ids — runtime values, which is why they do not live in `types.ts`.
 *
 * Its own module rather than `AppNavigator.tsx` for a concrete reason: screens
 * import this to find their tab, and `AppNavigator` imports the screens. Any
 * id living there would close that loop.
 */

/**
 * The bottom-tab navigator, so a screen can reach it by name instead of by
 * counting levels (see useTabScopedSearch / R23b).
 *
 * `getParent()` with no argument means "one level up", which silently depends
 * on how deep the screen happens to sit. `getParent(MAIN_TAB_NAVIGATOR_ID)`
 * walks the chain until it finds *this* navigator, so re-nesting a screen —
 * promoting a list out of its stack, or wrapping one in another — cannot
 * quietly hand back the wrong navigation object.
 */
export const MAIN_TAB_NAVIGATOR_ID = 'MainTabs' as const;
