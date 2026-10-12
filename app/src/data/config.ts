// Where the app reads payees and debits from. One flag, one file, so switching
// back to fixtures while the real feed is still being trusted is a one-line
// change with nothing else to remember (TRACER-01-BANK-DATA.md step 5).
//
// Rules are not covered by this flag: the backend deliberately does not send a
// classification (R6 — the client derives it), so there is nothing on the wire
// to switch between. Rules live in the local store either way.

/**
 * `true` reads payees/debits from the backend, `false` from
 * `src/mocks/data.ts`'s fixtures.
 *
 * Note what a stale rule does when this is flipped: rules are keyed by payee
 * id, and the two sources use different id schemes (`payee-netflix` vs.
 * `name:LIDL CONNECT`), so a rule saved against one source simply matches
 * nothing in the other. That is the correct reading of R4b — a payee with no
 * rule is unreviewed, therefore bad — not a bug to work around.
 */
export const USE_BACKEND = true;

/**
 * Picks the backend address. Pure, so the rule is testable without a build:
 *
 * - dev: the local backend. `localhost` is correct on a physical Android device
 *   **because of** `adb reverse tcp:5199 tcp:5199`, which forwards the device's
 *   port 5199 to the laptop's (a hardcoded LAN IP breaks on networks with AP
 *   client isolation).
 * - release: the https address from the untracked `backend.local.ts`. Anything
 *   else (no file, empty, `http://`, not a URL) yields `''`, which every caller
 *   reads as "not configured" and sends no request. https-only is deliberate:
 *   the release manifest forbids cleartext anyway, and a silent downgrade to
 *   http would carry the API key in the open.
 */
export function resolveBackendBaseUrl(isDev: boolean, localUrl: unknown): string {
  if (isDev) {
    return 'http://localhost:5199';
  }
  if (typeof localUrl !== 'string') {
    return '';
  }
  const url = localUrl.trim().replace(/\/+$/, '');
  return /^https:\/\/[^/\s]+/i.test(url) ? url : '';
}

// A guarded require, same pattern and reason as backendAuth.ts: a fresh
// checkout has no backend.local.ts, and that must mean "not configured", not a
// bundling failure (metro.config.js allows optional dependencies).
function readLocalUrl(): unknown {
  try {
    return (require('./backend.local') as { BACKEND_URL?: unknown }).BACKEND_URL;
  } catch {
    return undefined;
  }
}

/** `''` means "not configured": callers must not send a request. */
export const BACKEND_BASE_URL = resolveBackendBaseUrl(__DEV__, readLocalUrl());

/**
 * Cap on a single feed request. `fetch` on Android otherwise waits out the
 * platform's own (minutes-long) socket timeout, which on screen is
 * indistinguishable from a backend that is merely slow — and the whole point
 * of the badge is that the device says which one it is.
 */
export const BACKEND_TIMEOUT_MS = 15000;
