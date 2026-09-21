// Device registration: tells our backend which FCM token this phone can be
// reached on, so the server can push a bad-payee alert to it (the whole product,
// see NOTIFICATION-TRACER-BULLET.md step 2). Until the backend holds a token it
// has nowhere to send anything, however correct the rule engine gets.
//
// Module-level like backendFeed.ts and rulesStore.ts, and with the same
// posture: nothing here throws and nothing rejects unhandled. A failed
// registration is a `console.warn`, never an error on screen — the app is fully
// usable without push, and a phone that could not register this launch simply
// tries again on the next one.
//
// Deliberately NO retry loop and NO persisted "already sent" flag. The server
// upserts on the token, so posting once per launch is idempotent and cheap, and
// a backend that was down at start is covered by the next launch. State kept on
// disk would only be one more thing that can disagree with the server (a
// wiped database, a restored backup) and leave a phone silently unreachable.
//
// The token is a delivery credential for this phone, so it is never logged
// here. The backend keeps the same rule on its side.
//
// Android 13+ (API 33) will not display a notification until the user has
// granted the POST_NOTIFICATIONS runtime permission, so this asks for it first.
// `@react-native-firebase/messaging`'s own `requestPermission()` cannot do that
// job: it is an iOS-only API that resolves AUTHORIZED unconditionally on
// Android without showing any dialog — found the hard way in step 0, where a
// console test push was sent fine and never appeared. `PermissionsAndroid` is
// what has to ask. This project targets Android only, so there is no iOS branch.
//
// A *denied* permission does not stop the registration. The token is valid
// either way, and a user who later enables notifications in system settings
// starts receiving pushes with no change to the app.

import { getMessaging, getToken, onTokenRefresh } from '@react-native-firebase/messaging';
import { PermissionsAndroid, Platform } from 'react-native';
import { BACKEND_BASE_URL, BACKEND_TIMEOUT_MS, USE_BACKEND } from './config';

/** POST_NOTIFICATIONS is a runtime permission from Android 13 (API 33) on. */
const ANDROID_RUNTIME_NOTIFICATION_API = 33;

/**
 * Guards the one-shot part (permission prompt, first token read, first POST):
 * once per process, and never reset — a second call must not prompt or POST
 * again. Kept apart from `refreshSubscription` below because the two have
 * different lifetimes; see startDeviceRegistration().
 */
let registrationStarted = false;

/** The live token-refresh listener's unsubscribe, or null while none is attached. */
let refreshSubscription: (() => void) | null = null;

/**
 * Registers this device with the backend, and keeps doing so if Firebase
 * rotates the token. Returns the cleanup that detaches the refresh listener.
 *
 * Idempotent in both halves, because the effect that calls this can run more
 * than once in one process — Fast Refresh re-runs effects in development, and
 * any remount of the root component does the same (the app does not use
 * StrictMode):
 * - the one-shot registration runs once per process, whatever the cleanup does;
 * - the listener is a subscription, so cleanup detaches it and the next call
 *   attaches it again. Resetting the one-shot flag on cleanup instead would
 *   prompt for permission and POST twice on every re-run; not resetting the
 *   listener would leave the re-run app deaf to token rotation.
 *
 * There is one shared listener, so the returned cleanup stops it for every
 * caller. Only App.tsx calls this, through useDeviceRegistration().
 */
export function startDeviceRegistration(): () => void {
  if (!USE_BACKEND) {
    // Fixture mode is offline by design: no prompt, no token read, no request.
    return stopRefreshListener;
  }

  if (refreshSubscription == null) {
    refreshSubscription = attachRefreshListener();
  }

  if (!registrationStarted) {
    registrationStarted = true;
    // Not awaited: register() reports through console.warn, and the catch is
    // for the one thing it cannot do itself — a throw from getMessaging() or
    // getToken().
    register().catch(error => console.warn('[deviceStore] could not read the FCM token', error));
  }

  return stopRefreshListener;
}

function stopRefreshListener(): void {
  refreshSubscription?.();
  refreshSubscription = null;
}

function attachRefreshListener(): () => void {
  try {
    return onTokenRefresh(getMessaging(), token => {
      // Tell the backend immediately rather than at the next launch, so it can
      // reach the phone on the new token. Under the contract the backend
      // only upserts per token — it never replaces or deletes — so every
      // rotation leaves the previous token behind as a stale row, and whatever
      // sends pushes (step 3+) has to cope with tokens FCM no longer accepts.
      postToken(token);
    });
  } catch (error) {
    // A synchronous throw inside the effect that calls this would take the
    // whole app down over a feature the app does not need to be usable.
    console.warn('[deviceStore] could not listen for FCM token refreshes', error);
    return () => {};
  }
}

async function register(): Promise<void> {
  await askForNotificationPermission();
  const token = await getToken(getMessaging());
  await postToken(token);
}

/** Never throws: whatever happens here, the token is still worth registering. */
async function askForNotificationPermission(): Promise<void> {
  if (Platform.OS !== 'android' || Platform.Version < ANDROID_RUNTIME_NOTIFICATION_API) {
    return;
  }
  try {
    const result = await PermissionsAndroid.request(PermissionsAndroid.PERMISSIONS.POST_NOTIFICATIONS);
    if (result !== PermissionsAndroid.RESULTS.GRANTED) {
      console.warn(
        `[deviceStore] notification permission ${result}; registering the token anyway`,
      );
    }
  } catch (error) {
    console.warn('[deviceStore] notification permission request failed; registering anyway', error);
  }
}

/**
 * The contract (NOTIFICATION-TRACER-BULLET.md step 2): `POST /device-token`,
 * `{ "token": "<fcm token>" }`, any 2xx is stored. Everything else is a warning.
 * Never throws — it runs from a Firebase listener as well as from register().
 *
 * Same AbortController + timeout as backendFeed.ts: `fetch` on Android
 * otherwise waits out the platform's own minutes-long socket timeout.
 */
async function postToken(token: string): Promise<void> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), BACKEND_TIMEOUT_MS);
  try {
    const response = await fetch(`${BACKEND_BASE_URL}/device-token`, {
      method: 'POST',
      signal: controller.signal,
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token }),
    });
    if (!response.ok) {
      console.warn(`[deviceStore] backend did not store the device token: HTTP ${response.status}`);
    }
  } catch (error) {
    console.warn('[deviceStore] could not reach the backend to register the device token', error);
  } finally {
    clearTimeout(timeout);
  }
}
