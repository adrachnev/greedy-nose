# Greedy Nose

## Language

Always respond in English in this project, overriding any global default language setting.

A mobile app (iOS + Android) that connects to a European bank account and notifies the user
the moment a debitor they've flagged as "Bad" charges them. That's the whole product — it is
deliberately not a general finance/budgeting app.

## Status

Mocks in `mocks/` are reviewed and settled. Framework decided: **React Native**. Bare RN app
scaffolded in `app/` (Android target). Toolchain validated end-to-end: build → wireless adb
deploy → live on physical device. First screen ported: `01-connect-bank` →
`app/src/screens/ConnectBankScreen.tsx`.

Navigation is now wired up with React Navigation (native-stack + bottom-tabs). A mock
fixture-data layer (`app/src/mocks/data.ts`, consumed via `app/src/data/hooks.ts`) now backs
the app. `TransactionListScreen`, `TransactionDetailScreen`, `RulesListScreen`, and
`DebitorEditScreen` are ported in addition to `ConnectBankScreen`, reachable via a bottom-tab
main navigator (Transactions/Rules/Settings — Rules is now a real nested stack
(`RulesListScreen` → `DebitorEditScreen`), Settings is still a stub placeholder) plus an
onboarding stack (Connect Bank + stub placeholders for consent/syncing/classify). Remaining
screens still need porting.

The Trusted/Bad toggle on `TransactionDetailScreen` is fully wired end-to-end: tapping it
calls `setDebtorTrusted` (`app/src/mocks/data.ts`), shows a confirmation toast
(`app/src/components/Toast.tsx`), and `TransactionListScreen` correctly re-renders to reflect
the change via `useSyncExternalStore` in `app/src/data/hooks.ts`. This flow hit a real bug
during development — mutating the debtor in place without a new array reference silently broke
React's change detection — since fixed and covered by regression tests
(`app/src/mocks/__tests__/data.test.ts`, `app/src/data/__tests__/hooks.test.tsx`) that assert
on reference identity, not just end value, so it can't regress unnoticed. `npm test` (Jest,
`app/jest.config.js`) passes cleanly, including the pre-existing `App.test.tsx` smoke test
(needed a `transformIgnorePatterns` fix to let `@react-navigation`'s ESM through Babel).

The Rules tab now has a real write path too: `setDebtorRule` (`app/src/mocks/data.ts`) and
`useSetDebtorRule` (`app/src/data/hooks.ts`), following the exact same "reassign to a new array,
not mutate in place" pattern as `setDebtorTrusted`/`useDebtors`, with the same reference-identity
regression tests. `rules`/`useRules`/`useRuleForDebtor`/`transactions`/`useTransactions` are all
`useSyncExternalStore`-backed now. `DebitorEditScreen` edits a single amount threshold (frequency
was cut — see "Alert thresholds" below) and hosts the Trusted/Bad toggle (moved here from
`TransactionDetailScreen`, which is now display-only — see below); `RulesListScreen` has a
working local (client-side only) name-substring search filter over Bad/Trusted sections, with
every row (Bad or Trusted) tappable, and the mock's empty-state notes rendering naturally when a
section is empty (distinguishing "genuinely empty" from "no search results"). The bidirectional
Trusted/Bad auto-flip tied to the amount threshold, the manual-toggle-clears-the-rule behavior,
and the passive-flip-is-Bad-only + "Auto-marked Bad · Xm ago" marker are all implemented — see
the "Alert thresholds" and following bullets under Product decisions below for the full behavior.

**Known issue, not yet fixed (pick this up next session):** editing the amount field on
`DebitorEditScreen` without pressing Save, then tapping the Trusted/Bad toggle, does nothing
visible to the user — the toggle only acts on the last *saved* rule state, so an unsaved edit
sitting in the text field is silently ignored by the toggle instead of prompting the user or
being accounted for. This is misleading from a user's perspective (looks like the tap didn't
register). Needs a UX decision: should tapping the toggle while there's an unsaved edit also
save that edit first, discard it with a warning, or something else? Discuss before implementing.

## Product decisions (settled)

- **Bank connectivity**: [Enable Banking](https://enablebanking.com) — a PSD2/XS2A-licensed
  aggregator with a free tier. Chosen specifically to start with zero cost/investment. First
  (and currently only) bank target: **N26**.
- **Trust model is opt-out, not opt-in**: every debitor defaults to **Bad** the first time
  they're seen. The user marks debitors **Trusted** to silence them — not the other way round.
  This was a deliberate choice over a neutral "unmarked" state, since the goal is to never miss
  a genuinely new/unknown charge.
- **Onboarding**: after granting bank consent, a one-time bulk-review screen lets the user
  classify their existing debitors before live monitoring starts — otherwise every historical
  transaction would fire a notification on first connect.
- **Alert thresholds**: per-debitor, optional "only alert if amount exceeds €X". **Revised
  2026-08-13**: the frequency variant ("only alert if charged more than N times per period")
  is cut — amount is the only threshold now. (This reverses an earlier decision, recorded
  below in a prior version of this file, that deliberately kept frequency in scope; the owner
  changed their mind after using the ported Rules tab and finding it added complexity without
  pulling its weight.) A rule is editable for **any** debitor regardless of Trusted/Bad status
  — e.g. a Trusted landlord can still get a "only alert if rent exceeds €X" rule. Saving an
  amount is validated (must be a positive number).
- **Setting/updating a rule's amount can auto-flip a debitor's Trusted/Bad status — fully
  bidirectionally, but only at explicit rule-Save time.** The debitor's most recent transaction
  is re-evaluated against the amount just saved: if it exceeds the threshold they're flipped
  (or kept) Bad, and if it doesn't they're flipped (or kept) Trusted — a charge exactly equal to
  the threshold counts as "does not exceed" (Trusted-eligible). This is bidirectional on purpose
  *for Save*: a threshold exists to express "this is fine up to €X", so a Bad debitor whose last
  charge already falls under a newly-set threshold should stop being flagged, not just a Trusted
  debitor being caught out by one. This is never silent: it's always surfaced via a toast
  explaining why (e.g. "REWE Markt's last charge exceeds €30, marked Bad" or "ScamyLoans GmbH's
  last charge is under €60, marked Trusted"). **Manually toggling Trusted/Bad always clears the
  debitor's rule, in both directions** — there's nothing left to fight the user's explicit
  choice later. **A passively-arriving new transaction (no Save, no guaranteed mounted screen)
  re-runs the same check but Bad-only**: it can still catch a Trusted debitor overcharging (the
  core safety mission), but it can never silently move a debitor back to Trusted without an
  explicit user action. Since that passive flip has no toast to rely on, it surfaces instead as
  a persistent "Auto-marked Bad · Xm ago" marker on the debitor's Rules-list row, cleared once
  the user opens that debitor's Edit screen.
- **Transaction Detail is informational only — no classification or rule-editing controls live
  there.** It shows the transaction + the debitor's current status (a read-only pill) with a
  "Manage this debitor" link to the Edit Rule screen, which is where Trusted/Bad and the amount
  threshold are actually edited. This still satisfies the notification-tap flow below (one tap
  from notification → detail → Manage this debitor → classify) without duplicating editable
  controls across two screens.
- **Notifications do not carry quick actions.** Earlier iteration explored "Trust"/"Keep Bad"
  buttons directly on the push notification; this was deliberately removed. Tapping a
  notification opens the transaction detail, which links to the Edit Rule screen where
  classification happens in-app.
- **No "untracked" third state.** Only Trusted/Bad exist. An earlier "reset to default /
  untrack" concept was cut as scope creep — it behaved identically to Bad anyway.
- **Design language is lean and icon-driven**: no emoji, minimal decorative icons. State
  (Trusted/Bad, active tab, etc.) is communicated through **color** (green/red) and simple
  custom-drawn line-style SVG icons (`stroke="currentColor"`, `stroke-width="1.75"`,
  round caps/joins — matches the tab-bar icons in `mocks/style.css`/the HTML files), not
  through library icon sets or emoji glyphs.

## Open / deferred (explicitly not v1)

- App-level lock (Face ID/passcode) before opening the app.
- Multi-bank / multi-account differentiation in the transaction list.
- Offline state handling for the main list (only the initial-connect error state exists).
- Notification grouping/bundling (a "Group multiple alerts" toggle exists in the Settings mock
  but defaults Off — one notification per charge is the current decision).
- **`app/src/mocks/mockOnboardingState.ts` and `app/src/mocks/data.ts` are temporary
  scaffolding, not real state.** They currently drive which navigator (onboarding vs. main)
  shows and what transaction/debitor data renders. Both MUST be replaced with real persisted
  app state and live data from the backend/Enable Banking integration once those exist — do
  not let this quietly become permanent.

## `mocks/`

Static HTML/CSS phone mockups, one file per screen, ~375×812 viewport, Flexbox layout
throughout (chosen so it maps fairly directly to React Native, the chosen stack).

- `index.html` — overview embedding every screen with a labelled flow order; open this first.
- `style.css` — shared design tokens/components (`.card`, `.pill`, `.btn`, `.tab-item`,
  `.modal-overlay`, `.toast`, `.spinner`, dark-mode variants via `prefers-color-scheme`).
- Screens are numbered by flow position (`01` connect bank → `01b` consent → `01bb` syncing →
  `01c` onboarding classify → `02` transaction list → `03` transaction detail → `04` rules →
  `04b` edit rule → `05` notification → `06` settings), with lettered variants for
  error/empty/confirm states (e.g. `01e` connect error, `02b` empty list, `06b`/`06c` confirm
  modals).

Iterate on mocks before touching architecture/code — that was an explicit ordering decision:
mocks first, then pick the stack, then implement and test.

## `app/`

Bare React Native project (via `@react-native-community/cli`, package `com.greedynose`),
Android target set up first. `src/theme/colors.ts` mirrors `mocks/style.css`'s light/dark
tokens; `src/screens/` holds ported screens, one component per mock screen.

Local Windows toolchain notes (only relevant if the build breaks again):
- Requires JDK 17 (Android Gradle Plugin + a very new bundled JDK 25 from Android Studio
  trips a "restricted method" false-failure bug) — set `JAVA_HOME` to a JDK 17 install
  (e.g. Temurin) before running Gradle, not the Android Studio JBR.
- Requires the Microsoft Visual C++ Redistributable installed (CMake, used for RN's native
  build, fails with STATUS_DLL_NOT_FOUND without it).
- `npx react-native run-android` fails to invoke `gradlew.bat` from Git Bash/execa on this
  machine; run Gradle directly instead (`.\gradlew.bat app:installDebug ...`) via PowerShell.
- Device is connected via wireless adb (`adb pair`/`adb connect`), not USB.
