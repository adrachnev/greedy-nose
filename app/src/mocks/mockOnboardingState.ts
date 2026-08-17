// TEMPORARY MOCK — replace once real persisted/backend onboarding state exists
// (i.e. once the backend + Enable Banking integration land). Defaults to
// `true` so dev builds boot straight into the main app instead of the
// connect-bank flow. See app/src/data/hooks.ts for the equivalent seam on
// the fixture-data side.

/**
 * Whether the user has already connected a bank and completed the
 * one-time bulk payee classification. Drives the root navigator's
 * choice between the onboarding stack and the main tabs.
 */
export function isOnboardingComplete(): boolean {
  return true;
}
