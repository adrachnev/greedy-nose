// The one-time offer after a bank's first sync (Story 5.9), kept in memory only.
//
// Two pieces of state, both deliberately lost with the process: the ids of the
// payees the feed held when the connect attempt started (connectStore.startConnect
// takes them), and the list built once the first sync is done. After a kill the
// user is on Settings and no offer comes, which is the same as "Keep all Bad"
// (R26a). Nothing here is ever written to storage, so the offer cannot come back.

import { buildOffer, OfferRow } from '../domain/payeeOffer';
import { getDebits, getPayees } from './backendFeed';
import { getRules } from './rulesStore';

/** `null`: no attempt, or the feed was not loaded when it started (then what was known is unknown). */
let before: ReadonlySet<string> | null = null;
let offer: OfferRow[] = [];

/**
 * Called when an attempt starts: whatever is in the feed now is not new. `null` when the feed was not
 * loaded: the payees known before are then unknown, and nothing is offered for this attempt
 * (an empty snapshot would call every payee of an older bank new).
 */
export function rememberPayeesBeforeAttempt(payeeIds: string[] | null): void {
  before = payeeIds === null ? null : new Set(payeeIds);
}

/**
 * Builds the offer from the feed as it is now and keeps it for the screen. The
 * snapshot is spent: a second call (or a later sync without a new attempt) offers nothing.
 * `feedFresh` false (the load after the sync failed): the feed may be older than the sync, so nothing is offered.
 */
export function prepareOffer(feedFresh = true): OfferRow[] {
  const snapshot = before;
  before = null;
  offer = snapshot === null || !feedFresh ? [] : buildOffer(getPayees(), getDebits(), getRules(), snapshot);
  return offer;
}

export function getOffer(): OfferRow[] {
  return offer;
}

export function clearOffer(): void {
  offer = [];
}

/** Test-only. */
export function __resetOnboardingOfferForTests(): void {
  before = null;
  offer = [];
}
