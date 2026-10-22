/**
 * Greedy Nose
 *
 * @format
 */

import { createNavigationContainerRef, NavigationContainer } from '@react-navigation/native';
import { StatusBar, useColorScheme } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { useConnectReturn, useDeviceRegistration, useRulesResync } from './src/data/hooks';
import AppNavigator from './src/navigation/AppNavigator';
import { showSettingsTab } from './src/navigation/showSettingsTab';

const navigationRef = createNavigationContainerRef();
const showSettings = () => showSettingsTab(navigationRef);

function App() {
  const isDarkMode = useColorScheme() === 'dark';

  useDeviceRegistration();
  useRulesResync();
  useConnectReturn(showSettings);

  return (
    <SafeAreaProvider>
      <StatusBar barStyle={isDarkMode ? 'light-content' : 'dark-content'} />
      <NavigationContainer ref={navigationRef}>
        <AppNavigator />
      </NavigationContainer>
    </SafeAreaProvider>
  );
}

export default App;
