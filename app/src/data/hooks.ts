// Thin data-access seam. Screens consume payees/debits/rules only through
// these hooks — never by importing app/src/mocks/data.ts or backendFeed.ts
// directly. Which source is behind them is decided once, here, from
// USE_BACKEND in ./config.
//
// Payees and debits come from whichever source that flag names. **Rules do
// not**: the backend deliberately sends no classification (R6 — the client
// derives it from the current rule), so there is nothing on the wire to
// switch, and rules stay in the local store under both flags.

import { NavigationContext } from '@react-navigation/native';
import {
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from 'react';
import { AppState, Linking } from 'react-native';
import { Debit, Payee, Rule, RuleDraft } from '../domain/model';
import { debits as fixtureDebits, payees as fixturePayees, subscribeToDataChanges } from '../mocks/data';
import {
  FeedState,
  getDebits,
  getPayees,
  getState,
  refresh,
  subscribe as subscribeToFeed,
} from './backendFeed';
import { USE_BACKEND } from './config';
import {
  cancelAttempt,
  checkAttempt,
  chooseAccount,
  CONNECT_POLL_MS,
  ConnectFlow,
  forgetEndedAttempt,
  getConnectFlow,
  isUsableBankName,
  returnToApp,
  startConnect,
  subscribeToConnectFlow,
} from './connectStore';
import {
  BankHit,
  getMyBanks,
  MyBanksState,
  peekFirstStartNoBank,
  refreshMyBanks,
  searchBanks,
  SEARCH_PAUSE_MS,
  startMyBanksPolling,
  subscribeToMyBanks,
  takeFirstStartNoBank,
} from './myBanksStore';
import {
  getRegistrationState,
  RegistrationState,
  retryRegistration,
  startDeviceRegistration,
  subscribeToRegistration,
} from './deviceStore';
import { getRules, saveRule, startRulesResync, subscribe as subscribeToRules } from './rulesStore';

// Every collection is read through useSyncExternalStore, so screens re-render
// when one changes elsewhere — e.g. navigating back to the debit list after
// editing a rule, which must re-label that payee's debits immediately (R7).
//
// The source is bound at module level rather than branched inside each hook.
// USE_BACKEND cannot change while the process lives, so this is not a
// conditional hook — and binding it per call would mean subscribing to *both*
// stores, which would start a backend fetch even in fixture mode.

const subscribeToPayees = USE_BACKEND ? subscribeToFeed : subscribeToDataChanges;
const payeesSnapshot = USE_BACKEND ? getPayees : () => fixturePayees;
const subscribeToDebits = USE_BACKEND ? subscribeToFeed : subscribeToDataChanges;
const debitsSnapshot = USE_BACKEND ? getDebits : () => fixtureDebits;

export function usePayees(): Payee[] {
  return useSyncExternalStore(subscribeToPayees, payeesSnapshot);
}

export function usePayee(payeeId: string): Payee | undefined {
  const all = usePayees();
  return useMemo(() => all.find(p => p.id === payeeId), [all, payeeId]);
}

export function useDebits(): Debit[] {
  return useSyncExternalStore(subscribeToDebits, debitsSnapshot);
}

export function useDebit(debitId: string): Debit | undefined {
  const all = useDebits();
  return useMemo(() => all.find(d => d.id === debitId), [all, debitId]);
}

export function useRules(): Rule[] {
  return useSyncExternalStore(subscribeToRules, getRules);
}

export function useRuleForPayee(payeeId: string): Rule | undefined {
  const all = useRules();
  return useMemo(() => all.find(r => r.payeeId === payeeId), [all, payeeId]);
}

/**
 * Rules keyed by payee, for screens that classify a whole list at once (the
 * debit list, the rules list) and would otherwise do a linear scan per row.
 */
export function useRuleByPayeeId(): Map<string, Rule> {
  const all = useRules();
  return useMemo(() => new Map(all.map(rule => [rule.payeeId, rule])), [all]);
}

/**
 * Exposes the single write path for a rule (PayeeEditScreen's Save and Clear).
 * Classification and amount go together because one Save commits both — see
 * saveRule in src/data/rulesStore.ts.
 *
 * Takes the whole `payee`, not just its id: saveRule() needs `name`/
 * `initials`/`iban` too, to upsert the backend's `Payees` row alongside the
 * rule (TRACER-02-NOTIFICATIONS.md step 4).
 */
export function useSavePayeeRule(): (payee: Payee, draft: RuleDraft) => void {
  return useCallback((payee: Payee, draft: RuleDraft) => {
    saveRule(payee, draft);
  }, []);
}

// --- Where the data came from -----------------------------------------------

export type DataSource = {
  /** `true` when payees/debits are the backend's, `false` when they are fixtures. */
  live: boolean;
  /** Meaningful only while `live`; the fixture source never loads or fails. */
  feed: FeedState;
  /** Re-reads the feed. A no-op against fixtures, so callers need no branch. */
  refresh: () => void;
};

// Nothing loads, nothing fails and nothing is refused on the way in, so both
// counters are 0 by construction rather than by luck.
const FIXTURE_FEED: FeedState = { status: 'ready', dropped: 0, skipped: 0 };
const noopRefresh = () => {};
// Not awaited: refresh() reports failure through the feed's own state, which is
// what `feed` above carries to the badge.
const feedRefresh = () => {
  // A refresh is also the retry for a device token the backend never confirmed.
  retryRegistration();
  refresh();
};

/**
 * What the on-device badge reports. It exists because a tracer bullet has to be
 * *seen* working: an empty list means "the backend is unreachable", "the
 * consent expired" and "this account genuinely has no debits" all at once, and
 * the device is the one place where guessing between them is expensive.
 */
export function useDataSource(): DataSource {
  const feed = useSyncExternalStore(
    USE_BACKEND ? subscribeToFeed : subscribeToDataChanges,
    USE_BACKEND ? getState : () => FIXTURE_FEED,
  );
  return {
    live: USE_BACKEND,
    feed,
    refresh: USE_BACKEND ? feedRefresh : noopRefresh,
  };
}

/** Whether the backend has this phone's push token; `failed` means alerts cannot arrive. */
export function useRegistrationState(): RegistrationState {
  return useSyncExternalStore(subscribeToRegistration, getRegistrationState);
}

// --- Push registration --------------------------------------------------------

/**
 * Keeps this phone's FCM token registered with the backend for as long as the
 * app is mounted — call it once, from the root component. It reads nothing, so
 * nothing re-renders on its account. It is gated on USE_BACKEND inside the
 * store (fixture mode makes no prompt and no request), and the store's own
 * idempotence is what makes a strict-mode remount safe; see deviceStore.ts.
 */
export function useDeviceRegistration(): void {
  useEffect(() => startDeviceRegistration(), []);
}

// --- Rules resync ---------------------------------------------------------------

/**
 * Resends every local rule to the backend once per app start — call it once,
 * from the root component, next to useDeviceRegistration(). Reads nothing, so
 * nothing re-renders; the store's own once-per-process guard makes a remount
 * safe, and there is deliberately no foreground trigger. See startRulesResync.
 */
export function useRulesResync(): void {
  useEffect(() => {
    startRulesResync();
  }, []);
}

// --- Connect a bank -------------------------------------------------------------

/** The link the backend's callback page opens (ARCHITECTURE.md, "Consent lifecycle"). */
export const CONNECTED_LINK_PREFIX = 'greedynose://connected';

export type ConnectAttempt = {
  flow: ConnectFlow;
  /** Starts an attempt for a hit of the bank search. */
  start: (bankName: string, country: string) => void;
  /** Reads the state now ("Check again"). */
  checkNow: () => void;
  /** Sends the chosen account row. */
  choose: (accountId: string) => void;
  /** Cancel or back while the attempt runs: leaves at once, tells the backend. */
  cancel: () => void;
  /** Forgets an ended attempt (the screen is left); does nothing for a running one. */
  reset: () => void;
};

/**
 * The ways back from the bank's pages, for as long as the app runs — call it
 * once from the root component (the Settings tab is not mounted until opened,
 * and the app may have been killed and restarted while the user was at the
 * bank): the app link, the app coming to the foreground, and the app start
 * itself. All three only ask the store to look; it resumes a stored attempt
 * after a restart and ignores the call when there is none. `onResumed` runs
 * when an attempt is running or was just ended by the look: the place to show
 * its screen.
 */
export function useConnectReturn(onResumed?: () => void): void {
  // Latest callback without re-attaching the listeners when its identity changes.
  const onResumedRef = useRef(onResumed);
  onResumedRef.current = onResumed;

  useEffect(() => {
    const onUrl = (url: string | null) => {
      if (typeof url === 'string' && url.startsWith(CONNECTED_LINK_PREFIX)) {
        resume(true);
      }
    };
    const resume = (viaLink: boolean) => {
      returnToApp(viaLink).then(
        checked => {
          if (checked) {
            onResumedRef.current?.();
          }
        },
        () => {},
      );
    };
    resume(false);
    const linkSubscription = Linking.addEventListener('url', event => onUrl(event.url));
    Linking.getInitialURL().then(onUrl, () => {});
    const stateSubscription = AppState.addEventListener('change', state => {
      if (state === 'active') {
        resume(false);
      }
    });
    return () => {
      linkSubscription.remove();
      stateSubscription.remove();
    };
  }, []);
}

/**
 * Reads the state of a waiting attempt every few seconds while the calling
 * screen is visible (stops at any other state, when the screen loses focus and
 * on unmount). After repeated reads without an answer it stops too, and the
 * screen offers "Check again". Without a navigator (a bare render) "visible"
 * means "mounted".
 */
export function useConnectPolling(): void {
  const flow = useSyncExternalStore(subscribeToConnectFlow, getConnectFlow);
  const navigation = useContext(NavigationContext);
  const pollable = flow.step === 'waiting' && !flow.noAnswer;

  useEffect(() => {
    if (!pollable) {
      return;
    }
    // Polls only while the screen is focused AND the app is in the foreground: with the user in the
    // browser the foreground return reads the state anyway (useConnectReturn).
    let focused = !navigation || navigation.isFocused();
    // Only an explicit background/inactive counts as away: the state can be unknown right at the start.
    const isAway = (state: string | null) => state === 'background' || state === 'inactive';
    let active = !isAway(AppState.currentState);
    let timer: ReturnType<typeof setInterval> | null = null;
    const update = () => {
      if (focused && active) {
        timer ??= setInterval(() => {
          checkAttempt();
        }, CONNECT_POLL_MS);
      } else if (timer !== null) {
        clearInterval(timer);
        timer = null;
      }
    };
    update();
    const subscriptions = navigation
      ? [
          navigation.addListener('focus', () => {
            focused = true;
            update();
          }),
          navigation.addListener('blur', () => {
            focused = false;
            update();
          }),
        ]
      : [];
    const appState = AppState.addEventListener('change', next => {
      active = !isAway(next);
      update();
    });
    return () => {
      focused = false;
      update();
      subscriptions.forEach(unsubscribe => unsubscribe());
      appState.remove();
    };
  }, [pollable, navigation]);
}

/** The flow of connecting one bank, for the screens that start it and show it. */
export function useConnectAttempt(): ConnectAttempt {
  const flow = useSyncExternalStore(subscribeToConnectFlow, getConnectFlow);

  return useMemo(
    () => ({
      flow,
      start: (bankName: string, country: string) => {
        startConnect(bankName, country);
      },
      checkNow: () => {
        checkAttempt();
      },
      choose: (accountId: string) => {
        chooseAccount(accountId);
      },
      cancel: () => {
        cancelAttempt();
      },
      reset: forgetEndedAttempt,
    }),
    [flow],
  );
}

// --- My banks ---------------------------------------------------------------------

/** The saved banks and their state; only reads, the Settings screen and the root ask the backend. */
export function useMyBanks(): MyBanksState {
  return useSyncExternalStore(subscribeToMyBanks, getMyBanks);
}

/**
 * Keeps the banks fresh while the calling screen is on screen: one read when it
 * gets focus and when the app comes to the foreground, and a read every few
 * seconds while a bank is syncing (see startMyBanksPolling). Without a
 * navigator (a bare render) "focused" means "mounted".
 */
export function useMyBanksPolling(): void {
  const navigation = useContext(NavigationContext);

  useEffect(() => {
    let stop: (() => void) | null = null;
    const begin = () => {
      stop ??= startMyBanksPolling();
    };
    const end = () => {
      stop?.();
      stop = null;
    };
    if (!navigation || navigation.isFocused()) {
      begin();
    }
    const subscriptions = navigation
      ? [navigation.addListener('focus', begin), navigation.addListener('blur', end)]
      : [];
    const appState = AppState.addEventListener('change', next => {
      if (next === 'active' && stop !== null) {
        refreshMyBanks();
      }
    });
    return () => {
      end();
      subscriptions.forEach(unsubscribe => unsubscribe());
      appState.remove();
    };
  }, [navigation]);
}

/** How often and how long the jump is retried while the navigator cannot take it yet. */
const NO_BANK_RETRY_MS = 500;
const NO_BANK_RETRY_LIMIT = 20;

/**
 * First start (R36): when the first good answer of the process says "no bank",
 * calls `onNoBank` until it reports that the jump happened (`true`), then never
 * again. The navigator may not be ready, or may still show onboarding, when the
 * answer arrives; the flag is spent only by a jump that was made. Call it once
 * from the root component; it renders nothing. A failed or slow read never
 * counts as "no bank".
 */
export function useNoBankStartup(onNoBank: () => boolean): void {
  const onNoBankRef = useRef(onNoBank);
  onNoBankRef.current = onNoBank;

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | null = null;
    let retries = 0;
    let cancelled = false;
    const check = () => {
      // One chain only: this is also the store listener, so a publish can arrive while a retry is pending.
      if (timer !== null) {
        clearTimeout(timer);
        timer = null;
      }
      if (cancelled || getMyBanks().status !== 'ready' || !peekFirstStartNoBank()) {
        return;
      }
      if (onNoBankRef.current()) {
        takeFirstStartNoBank();
      } else if (retries < NO_BANK_RETRY_LIMIT) {
        retries += 1;
        timer = setTimeout(check, NO_BANK_RETRY_MS);
      }
    };
    const unsubscribe = subscribeToMyBanks(check);
    refreshMyBanks();
    return () => {
      cancelled = true;
      unsubscribe();
      if (timer !== null) {
        clearTimeout(timer);
      }
    };
  }, []);
}

// --- Bank search ------------------------------------------------------------------

export type BankSearch =
  | { phase: 'idle' }
  | { phase: 'loading' }
  | { phase: 'ready'; banks: BankHit[] }
  | { phase: 'failed' };

/**
 * The search of Story 5.6 for the text typed so far: nothing below the minimum
 * length, otherwise one request after a pause in typing. An answer for text that
 * has since changed is dropped (`cancelled`), so a slow old answer never
 * overwrites the newer one. `retry` asks again at once.
 */
export function useBankSearch(typed: string): { search: BankSearch; retry: () => void } {
  // Trimmed, so a trailing space does not fire the same search again.
  const text = typed.trim();
  const [search, setSearch] = useState<BankSearch>({ phase: 'idle' });
  const [attempt, setAttempt] = useState(0);
  const retryingRef = useRef(false);

  useEffect(() => {
    if (!isUsableBankName(text)) {
      setSearch({ phase: 'idle' });
      return;
    }
    let cancelled = false;
    // The previous hits stay on screen while the new ones load (no flicker per key).
    setSearch(previous => (previous.phase === 'ready' ? previous : { phase: 'loading' }));
    const wait = retryingRef.current ? 0 : SEARCH_PAUSE_MS;
    retryingRef.current = false;
    const timer = setTimeout(() => {
      searchBanks(text).then(result => {
        if (!cancelled) {
          setSearch(result.ok ? { phase: 'ready', banks: result.banks } : { phase: 'failed' });
        }
      });
    }, wait);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [text, attempt]);

  const retry = useCallback(() => {
    retryingRef.current = true;
    setAttempt(n => n + 1);
  }, []);

  return { search, retry };
}
