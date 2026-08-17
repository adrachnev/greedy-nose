/**
 * The one claim `useTabScopedSearch`'s own tests cannot make.
 *
 * Those tests drive a hand-written navigation double, so they pin *that the
 * hook asks for the tab by id* — not that react-navigation answers. This one
 * stands up the real navigators in the real nesting (tab -> stack -> screen)
 * and checks that `getParent(MAIN_TAB_NAVIGATOR_ID)` hands back the object
 * whose `blur` fires when the tab is left. If that ever stops being true, R23b
 * dies silently and every other test stays green.
 */

import { NavigationContainer } from '@react-navigation/native';
import type { NavigationContainerRef, ParamListBase } from '@react-navigation/native';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import { createNativeStackNavigator } from '@react-navigation/native-stack';
import React from 'react';
import ReactTestRenderer, { act } from 'react-test-renderer';
import { MAIN_TAB_NAVIGATOR_ID } from '../ids';

const Tab = createBottomTabNavigator<
  { Debits: undefined; Rules: undefined },
  typeof MAIN_TAB_NAVIGATOR_ID
>();
const Stack = createNativeStackNavigator<{ DebitList: undefined }>();

type Probe = {
  tabBlurs: number;
  foundTab: boolean;
  foundWrongId: boolean;
};

/**
 * Module-level because the screen component is what has the navigation object,
 * and there is no other way out of it. Reset per test.
 */
let probe: Probe;

type ScreenNavigation = {
  getParent: (id?: string) => { addListener: (event: string, cb: () => void) => () => void } | undefined;
};

function DebitListProbe({ navigation }: { navigation: ScreenNavigation }) {
  React.useEffect(() => {
    // The wrong id must not resolve to *something* — that is the whole reason
    // for asking by id rather than taking whatever is one level up.
    probe.foundWrongId = navigation.getParent('NotTheTabNavigator') != null;

    const tab = navigation.getParent(MAIN_TAB_NAVIGATOR_ID);
    probe.foundTab = tab != null;
    if (!tab) {
      return;
    }
    return tab.addListener('blur', () => {
      probe.tabBlurs += 1;
    });
  }, [navigation]);
  return null;
}

function DebitsNavigator() {
  return (
    <Stack.Navigator screenOptions={{ headerShown: false }}>
      <Stack.Screen name="DebitList" component={DebitListProbe} />
    </Stack.Navigator>
  );
}

function RulesPlaceholder() {
  return null;
}

describe('getParent(MAIN_TAB_NAVIGATOR_ID) against the real navigators', () => {
  it('finds the tab from a screen nested one stack deep, and blurs with it', () => {
    probe = { tabBlurs: 0, foundTab: false, foundWrongId: false };
    const container = React.createRef<NavigationContainerRef<ParamListBase>>();
    let renderer!: ReactTestRenderer.ReactTestRenderer;

    act(() => {
      renderer = ReactTestRenderer.create(
        <NavigationContainer ref={container}>
          <Tab.Navigator id={MAIN_TAB_NAVIGATOR_ID} screenOptions={{ headerShown: false }}>
            <Tab.Screen name="Debits" component={DebitsNavigator} />
            <Tab.Screen name="Rules" component={RulesPlaceholder} />
          </Tab.Navigator>
        </NavigationContainer>,
      );
    });

    // The lookup resolved through two navigators, and only for the right id.
    expect(probe.foundTab).toBe(true);
    expect(probe.foundWrongId).toBe(false);
    expect(probe.tabBlurs).toBe(0);

    act(() => container.current?.navigate('Rules'));

    // Leaving the tab is what clears the query in the real hook.
    expect(probe.tabBlurs).toBe(1);

    act(() => renderer.unmount());
  });
});
