import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import React, { useState } from 'react';
import { useColorScheme } from 'react-native';
import TabIcon, { TabIconName } from '../components/TabIcon';
import { isOnboardingComplete } from '../mocks/mockOnboardingState';
import ConnectBankScreen from '../screens/ConnectBankScreen';
import DebitDetailScreen from '../screens/DebitDetailScreen';
import DebitListScreen from '../screens/DebitListScreen';
import PayeeEditScreen from '../screens/PayeeEditScreen';
import RulesListScreen from '../screens/RulesListScreen';
import StubScreen from '../screens/StubScreen';
import { dark, light } from '../theme/colors';
import { MAIN_TAB_NAVIGATOR_ID } from './ids';
import {
  DebitsStackParamList,
  MainTabParamList,
  OnboardingStackParamList,
  RulesStackParamList,
} from './types';

const OnboardingStack = createNativeStackNavigator<OnboardingStackParamList>();
const DebitsStack = createNativeStackNavigator<DebitsStackParamList>();
const RulesStack = createNativeStackNavigator<RulesStackParamList>();
// The id type argument is what makes `id` mandatory below: drop the prop and
// this stops compiling, rather than stopping R23b at runtime.
const Tab = createBottomTabNavigator<MainTabParamList, typeof MAIN_TAB_NAVIGATOR_ID>();

/**
 * Onboarding flow: connect bank -> consent -> syncing -> bulk-classify
 * existing payees, then hands off to the main tabs. Only ConnectBank is
 * fully ported today; the rest are navigable stubs (see StubScreen).
 */
function OnboardingNavigator({ onDone }: { onDone: () => void }) {
  return (
    <OnboardingStack.Navigator screenOptions={{ headerShown: false }}>
      <OnboardingStack.Screen name="ConnectBank">
        {({ navigation }) => (
          <ConnectBankScreen onContinue={() => navigation.navigate('ConnectConsent')} />
        )}
      </OnboardingStack.Screen>
      <OnboardingStack.Screen name="ConnectConsent">
        {({ navigation }) => (
          <StubScreen
            title="Bank Consent"
            mockFile="01b-connect-consent.html"
            onContinue={() => navigation.navigate('Syncing')}
          />
        )}
      </OnboardingStack.Screen>
      <OnboardingStack.Screen name="Syncing">
        {({ navigation }) => (
          <StubScreen
            title="Syncing Account"
            mockFile="01bb-syncing.html"
            onContinue={() => navigation.navigate('ClassifyPayees')}
          />
        )}
      </OnboardingStack.Screen>
      <OnboardingStack.Screen name="ClassifyPayees">
        {() => (
          <StubScreen
            title="Classify Payees"
            mockFile="01c-classify-payees.html"
            onContinue={onDone}
            continueLabel="Finish setup"
          />
        )}
      </OnboardingStack.Screen>
    </OnboardingStack.Navigator>
  );
}

/** Debits tab: list + detail nested stack, so the tab bar stays visible. */
function DebitsNavigator() {
  return (
    <DebitsStack.Navigator screenOptions={{ headerShown: false }}>
      <DebitsStack.Screen name="DebitList" component={DebitListScreen} />
      <DebitsStack.Screen name="DebitDetail" component={DebitDetailScreen} />
      <DebitsStack.Screen name="PayeeEdit" component={PayeeEditScreen} />
    </DebitsStack.Navigator>
  );
}

/** Rules tab: list + edit nested stack, so the tab bar stays visible. */
function RulesNavigator() {
  return (
    <RulesStack.Navigator screenOptions={{ headerShown: false }}>
      <RulesStack.Screen name="RulesList" component={RulesListScreen} />
      <RulesStack.Screen name="PayeeEdit" component={PayeeEditScreen} />
    </RulesStack.Navigator>
  );
}

function SettingsStub() {
  return <StubScreen title="Settings" mockFile="06-settings.html" showMockBadge />;
}

const TAB_ICON_BY_ROUTE: Record<keyof MainTabParamList, TabIconName> = {
  Debits: 'debits',
  Rules: 'rules',
  Settings: 'settings',
};

function MainTabBarIcon({
  routeName,
  color,
  size,
}: {
  routeName: keyof MainTabParamList;
  color: string;
  size: number;
}) {
  return <TabIcon name={TAB_ICON_BY_ROUTE[routeName]} color={color} size={size} />;
}

/**
 * R24, second half: **re-tapping the tab you are already on does nothing.**
 *
 * Be clear about what this listener is and is not. Bottom-tabs already ignores
 * a press on the focused tab: `BottomTabBar` guards its navigate with
 * `if (!focused && !event.defaultPrevented)`, so on today's navigator R24
 * holds with or without this code. The listener is a
 * **deliberate redundant guard**: it states the requirement in the app instead
 * of leaving it resting on a library default, and it gives the requirement a
 * unit test, which a library default cannot have.
 *
 * The migration trap worth writing down: the *unstable native* tab navigator
 * (`@react-navigation/bottom-tabs/unstable`) hardcodes
 * `specialEffects.repeatedTabSelection.popToRoot` and emits `tabPress`
 * **without** `canPreventDefault`. There, a re-tap really does pop the nested
 * stack, and this listener would **not** stop it — `preventDefault` has
 * nothing to cancel. Switching navigators means re-solving R24, not moving
 * this function.
 *
 * Structurally typed rather than imported: only `isFocused` and
 * `preventDefault` are used here, and naming the full react-navigation event
 * types would couple this file to them for no gain.
 *
 * Exported for its test — the behaviour is one `if` away from inverting into
 * "swallow every tab switch", and nothing else in the suite would notice.
 */
export function listTabListeners({ navigation }: { navigation: { isFocused: () => boolean } }) {
  return {
    tabPress: (e: { preventDefault: () => void }) => {
      if (navigation.isFocused()) {
        e.preventDefault();
      }
    },
  };
}

/**
 * R24, first half: arriving at a tab always lands on its list, never on a
 * detail or edit screen left open from a previous visit. Popping on *blur*
 * rather than on focus is what also delivers R24a for free — an unsaved rule
 * draft is discarded silently on the way out, exactly as Back already discards
 * it (R4's single-commit form), with no dialog code at all.
 */
const LIST_TAB_OPTIONS = { popToTopOnBlur: true } as const;

function MainTabs() {
  const theme = useColorScheme() === 'dark' ? dark : light;

  return (
    <Tab.Navigator
      id={MAIN_TAB_NAVIGATOR_ID}
      screenOptions={({ route }) => ({
        headerShown: false,
        tabBarActiveTintColor: theme.accent,
        tabBarInactiveTintColor: theme.textMuted,
        tabBarStyle: { backgroundColor: theme.surface, borderTopColor: theme.border },
        tabBarShowLabel: false,
        // react-navigation's per-route screenOptions requires this render-prop shape;
        // MainTabBarIcon itself is a stable, module-level component, so this is safe.
        // eslint-disable-next-line react/no-unstable-nested-components
        tabBarIcon: ({ color, size }) => (
          <MainTabBarIcon
            routeName={route.name as keyof MainTabParamList}
            color={color}
            size={size}
          />
        ),
      })}
    >
      <Tab.Screen
        name="Debits"
        component={DebitsNavigator}
        options={LIST_TAB_OPTIONS}
        listeners={listTabListeners}
      />
      <Tab.Screen
        name="Rules"
        component={RulesNavigator}
        options={LIST_TAB_OPTIONS}
        listeners={listTabListeners}
      />
      {/* Settings has no nested stack, so neither option has anything to do
          there — there is no stack to pop and nothing to blur away. */}
      <Tab.Screen name="Settings" component={SettingsStub} />
    </Tab.Navigator>
  );
}

/**
 * Root navigator: "has the user finished onboarding?" gates between the
 * onboarding stack and the main app tabs. Onboarding-complete state is a
 * mock today (see src/mocks/mockOnboardingState.ts) but the branch itself
 * is the real, permanent shape of this decision.
 */
export default function AppNavigator() {
  const [onboardingComplete, setOnboardingComplete] = useState(isOnboardingComplete());

  if (!onboardingComplete) {
    return <OnboardingNavigator onDone={() => setOnboardingComplete(true)} />;
  }

  return <MainTabs />;
}
