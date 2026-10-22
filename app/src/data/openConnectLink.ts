// How the bank's sign-in page is opened (Story 5.1a, decided on the phone): in a
// Chrome Custom Tab (react-native-inappbrowser-reborn), which keeps the page
// through a password manager and the bank's app. Fallbacks, in order: the
// default browser's Custom Tab, then the plain browser (Linking.openURL).

import { Linking } from 'react-native';
import { InAppBrowser } from 'react-native-inappbrowser-reborn';

/**
 * Chrome's package. Without `browserPackage` the library picks the user's
 * default browser when it supports Custom Tabs (the owner's is DuckDuckGo), so
 * Chrome would not be used. The library has no "is this package
 * installed" call; instead, when the package is missing its native side catches
 * the ActivityNotFoundException and rejects open() ("Unable to open url."), no
 * crash, and the fallbacks below take over.
 */
export const CHROME_PACKAGE = 'com.android.chrome';

const TAB_OPTIONS = {
  showTitle: false,
  enableUrlBarHiding: true,
  // The tab survives the app being backgrounded for a password manager.
  forceCloseOnRedirection: false,
  showInRecents: true,
};

/** Chrome's tab, else the default browser's tab; rejects if neither opens. */
async function openTab(url: string) {
  try {
    return await InAppBrowser.open(url, { ...TAB_OPTIONS, browserPackage: CHROME_PACKAGE });
  } catch {
    return InAppBrowser.open(url, TAB_OPTIONS);
  }
}

/**
 * Opens the link. Resolves `true` when the user dismissed an in-app tab (so the
 * caller should look at the result at once), `false` when the page was handed
 * to the browser (the way back is the link or the foreground). Rejects when
 * nothing could be opened.
 */
export async function openConnectLink(url: string): Promise<boolean> {
  if (await InAppBrowser.isAvailable()) {
    try {
      const result = await openTab(url);
      return result.type === 'cancel' || result.type === 'dismiss';
    } catch {
      // No tab could be opened at all: the plain browser is the last resort.
    }
  }
  // No Custom Tabs on this phone: the browser is the same flow, only less tidy.
  await Linking.openURL(url);
  return false;
}

/** Closes the Custom Tab if one is open. Never throws. */
export async function dismissConnectLink(): Promise<void> {
  try {
    await Promise.resolve(InAppBrowser.close());
  } catch {
    // Nothing open, or no Custom Tabs: nothing to do.
  }
}
