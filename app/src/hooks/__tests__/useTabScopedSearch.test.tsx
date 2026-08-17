/**
 * R23b: the query is scoped to the **visit to the tab**, not to the screen.
 *
 * Both halves matter and they pull in opposite directions:
 *
 * - it must survive list -> debit detail -> Back, so several hits can be
 *   worked through without retyping;
 * - it must be gone when the user comes back to the tab later, so a tab always
 *   hands back the full list.
 *
 * That is why the hook listens on the **tab's** blur. The screen's own blur
 * fires on the way into a detail screen too, so wiring it there — or to
 * useFocusEffect, which has the same problem — looks correct in a click-through
 * and quietly breaks the first half. The second test below is what pins that:
 * it fails against the obvious implementation.
 *
 * The double below only answers to MAIN_TAB_NAVIGATOR_ID, so "listens to the
 * tab" is asserted rather than assumed: a hook that reverted to a bare
 * `getParent()` would find nothing here and fail loudly.
 */

import React from 'react';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { useNavigation } from '@react-navigation/native';
import { MAIN_TAB_NAVIGATOR_ID } from '../../navigation/ids';
import { useTabScopedSearch } from '../useTabScopedSearch';

jest.mock('@react-navigation/native', () => ({
  useNavigation: jest.fn(),
}));

type Listeners = { [event: string]: Array<() => void> };

/**
 * A navigation double with two separately addressable blur channels: the
 * screen's own, and the tab's. Telling them apart is the entire point.
 */
function mockNavigation() {
  const own: Listeners = {};
  const parent: Listeners = {};

  const addListener = (store: Listeners) => (event: string, callback: () => void) => {
    store[event] = [...(store[event] ?? []), callback];
    return () => {
      store[event] = (store[event] ?? []).filter(c => c !== callback);
    };
  };

  const tabNavigation = { addListener: addListener(parent) };
  const navigation = {
    addListener: addListener(own),
    // Only the tab answers, and only to its own id — exactly like
    // react-navigation, which walks up until it finds a navigator with that
    // id and returns undefined if there is none.
    getParent: (id?: string) => (id === MAIN_TAB_NAVIGATOR_ID ? tabNavigation : undefined),
  };

  (useNavigation as jest.Mock).mockReturnValue(navigation);

  const emit = (store: Listeners, event: string) => {
    act(() => {
      (store[event] ?? []).forEach(callback => callback());
    });
  };

  return {
    blurScreen: () => emit(own, 'blur'),
    blurTab: () => emit(parent, 'blur'),
    tabListenerCount: () => (parent.blur ?? []).length,
  };
}

let api: ReturnType<typeof useTabScopedSearch>;

function Probe() {
  api = useTabScopedSearch();
  return null;
}

function renderProbe() {
  let renderer!: ReactTestRenderer.ReactTestRenderer;
  act(() => {
    renderer = ReactTestRenderer.create(<Probe />);
  });
  return renderer;
}

describe('useTabScopedSearch — R23b', () => {
  it('clears the query when the tab is left', () => {
    const nav = mockNavigation();
    renderProbe();

    act(() => api.setSearch('müller'));
    expect(api.search).toBe('müller');

    nav.blurTab();

    expect(api.search).toBe('');
  });

  it('keeps the query when only the screen blurs — going into a debit detail', () => {
    const nav = mockNavigation();
    renderProbe();

    act(() => api.setSearch('müller'));
    nav.blurScreen();

    // The half that is easy to lose: Back from the detail screen has to land
    // on the same filtered list the user left.
    expect(api.search).toBe('müller');
  });

  it('unsubscribes from the tab on unmount', () => {
    const nav = mockNavigation();
    const renderer = renderProbe();
    expect(nav.tabListenerCount()).toBe(1);

    act(() => renderer.unmount());

    expect(nav.tabListenerCount()).toBe(0);
  });

  it('asks for the tab by id, not for whatever happens to be one level up', () => {
    // `getParent()` with no argument means "one level up", which is only
    // right by coincidence of the current nesting. Pinning the id here is what
    // makes a re-nested screen a compile/test problem instead of a silent one.
    const getParent = jest.fn(() => undefined);
    (useNavigation as jest.Mock).mockReturnValue({ getParent });

    renderProbe();

    expect(getParent).toHaveBeenCalledWith(MAIN_TAB_NAVIGATOR_ID);
  });

  it('survives a screen rendered outside a tab navigator', () => {
    // Bare renders (tests, a screen reused elsewhere) sit under no tab; the
    // hook must not throw, it simply has nothing to clear on.
    (useNavigation as jest.Mock).mockReturnValue({ getParent: () => undefined });

    expect(() => renderProbe()).not.toThrow();
  });
});
