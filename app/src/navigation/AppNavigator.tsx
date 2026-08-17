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
import {
  DebitsStackParamList,
  MainTabParamList,
  OnboardingStackParamList,
  RulesStackParamList,
} from './types';

const OnboardingStack = createNativeStackNavigator<OnboardingStackParamList>();
const DebitsStack = createNativeStackNavigator<DebitsStackParamList>();
const RulesStack = createNativeStackNavigator<RulesStackParamList>();
const Tab = createBottomTabNavigator<MainTabParamList>();

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

function MainTabs() {
  const theme = useColorScheme() === 'dark' ? dark : light;

  return (
    <Tab.Navigator
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
      <Tab.Screen name="Debits" component={DebitsNavigator} />
      <Tab.Screen name="Rules" component={RulesNavigator} />
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
