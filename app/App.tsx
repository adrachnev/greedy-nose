/**
 * Greedy Nose
 *
 * @format
 */

import { getMessaging, getToken } from '@react-native-firebase/messaging';
import { useEffect } from 'react';
import { NavigationContainer } from '@react-navigation/native';
import { PermissionsAndroid, Platform, StatusBar, useColorScheme } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import AppNavigator from './src/navigation/AppNavigator';

function App() {
  const isDarkMode = useColorScheme() === 'dark';

  // TRACER-BULLET (NOTIFICATION-TRACER-BULLET.md step 0): throwaway spike to get an FCM
  // registration token onto the device log, so it can be pasted into the Firebase Console's
  // "Send test message" tool. Deleted once step 2 lands `src/data/deviceStore.ts`, which does
  // this properly (persistence, retry, POSTs the token to our backend instead of logging it).
  //
  // Android 13+ (API 33) requires the POST_NOTIFICATIONS runtime permission before a
  // notification can actually display — without it the console test push sends fine and never
  // shows. `@react-native-firebase/messaging`'s own `requestPermission()` does not cover this on
  // Android: it's an iOS-only API that resolves AUTHORIZED unconditionally on Android (see its
  // deprecation notice), so `PermissionsAndroid` is what has to ask instead. This project targets
  // Android only (see NOTIFICATION-TRACER-BULLET.md), so there is no iOS branch here.
  useEffect(() => {
    async function registerForPush() {
      if (Platform.OS === 'android' && Platform.Version >= 33) {
        const result = await PermissionsAndroid.request(
          PermissionsAndroid.PERMISSIONS.POST_NOTIFICATIONS,
        );
        if (result !== PermissionsAndroid.RESULTS.GRANTED) {
          console.log('[FCM TOKEN] notification permission denied', result);
          return;
        }
      }
      const token = await getToken(getMessaging());
      console.log('[FCM TOKEN]', token);
    }
    registerForPush().catch(error => console.log('[FCM TOKEN] failed', error));
  }, []);

  return (
    <SafeAreaProvider>
      <StatusBar barStyle={isDarkMode ? 'light-content' : 'dark-content'} />
      <NavigationContainer>
        <AppNavigator />
      </NavigationContainer>
    </SafeAreaProvider>
  );
}

export default App;
