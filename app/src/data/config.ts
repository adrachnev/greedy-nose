// Where the app reads payees and debits from. One flag, one file, so switching
// back to fixtures while the real feed is still being trusted is a one-line
// change with nothing else to remember (TRACER-BULLET.md step 5).
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
 * The tracer-bullet backend, run locally with
 * `dotnet run --project backend/GreedyNose.Api/GreedyNose.Api.csproj --launch-profile http`.
 *
 * `localhost` is correct on a physical Android device **because of**
 * `adb reverse tcp:5199 tcp:5199`, which forwards the device's port 5199 to
 * the laptop's. Without that command the device has no route to the backend at
 * all — and on a network with AP client isolation (public/airport WiFi) the
 * laptop's LAN address would not work either, which is exactly why the tunnel
 * is the way in rather than a hardcoded IP that has to be re-edited per WiFi.
 */
export const BACKEND_BASE_URL = 'http://localhost:5199';

/**
 * Cap on a single feed request. `fetch` on Android otherwise waits out the
 * platform's own (minutes-long) socket timeout, which on screen is
 * indistinguishable from a backend that is merely slow — and the whole point
 * of the badge is that the device says which one it is.
 */
export const BACKEND_TIMEOUT_MS = 15000;
