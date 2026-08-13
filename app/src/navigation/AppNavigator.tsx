import { createNativeStackNavigator } from '@react-navigation/native-stack';
import { createBottomTabNavigator } from '@react-navigation/bottom-tabs';
import React, { useState } from 'react';
import { useColorScheme } from 'react-native';
import TabIcon, { TabIconName } from '../components/TabIcon';
import { isOnboardingComplete } from '../mocks/mockOnboardingState';
import ConnectBankScreen from '../screens/ConnectBankScreen';
import DebitorEditScreen from '../screens/DebitorEditScreen';
import RulesListScreen from '../screens/RulesListScreen';
import StubScreen from '../screens/StubScreen';
import TransactionDetailScreen from '../screens/TransactionDetailScreen';
import TransactionListScreen from '../screens/TransactionListScreen';
import { dark, light } from '../theme/colors';
import {
  MainTabParamList,
  OnboardingStackParamList,
  RulesStackParamList,
  TransactionsStackParamList,
} from './types';

const OnboardingStack = createNativeStackNavigator<OnboardingStackParamList>();
const TransactionsStack = createNativeStackNavigator<TransactionsStackParamList>();
const RulesStack = createNativeStackNavigator<RulesStackParamList>();
const Tab = createBottomTabNavigator<MainTabParamList>();

/**
 * Onboarding flow: connect bank -> consent -> syncing -> bulk-classify
 * existing debitors, then hands off to the main tabs. Only ConnectBank is
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
            onContinue={() => navigation.navigate('ClassifyDebitors')}
          />
        )}
      </OnboardingStack.Screen>
      <OnboardingStack.Screen name="ClassifyDebitors">
        {() => (
          <StubScreen
            title="Classify Debitors"
            mockFile="01c-classify-debitors.html"
            onContinue={onDone}
            continueLabel="Finish setup"
          />
        )}
      </OnboardingStack.Screen>
    </OnboardingStack.Navigator>
  );
}

/** Transactions tab: list + detail nested stack, so the tab bar stays visible. */
function TransactionsNavigator() {
  return (
    <TransactionsStack.Navigator screenOptions={{ headerShown: false }}>
      <TransactionsStack.Screen name="TransactionList" component={TransactionListScreen} />
      <TransactionsStack.Screen name="TransactionDetail" component={TransactionDetailScreen} />
      <TransactionsStack.Screen name="DebitorEdit" component={DebitorEditScreen} />
    </TransactionsStack.Navigator>
  );
}

/** Rules tab: list + edit nested stack, so the tab bar stays visible. */
function RulesNavigator() {
  return (
    <RulesStack.Navigator screenOptions={{ headerShown: false }}>
      <RulesStack.Screen name="RulesList" component={RulesListScreen} />
      <RulesStack.Screen name="DebitorEdit" component={DebitorEditScreen} />
    </RulesStack.Navigator>
  );
}

function SettingsStub() {
  return (
    <StubScreen title="Settings" mockFile="06-settings.html" showMockBadge />
  );
}

const TAB_ICON_BY_ROUTE: Record<keyof MainTabParamList, TabIconName> = {
  Transactions: 'transactions',
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
      <Tab.Screen name="Transactions" component={TransactionsNavigator} />
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
