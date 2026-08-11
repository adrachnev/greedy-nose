# Greedy Nose

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
the app. `TransactionListScreen` and `TransactionDetailScreen` are ported in addition to
`ConnectBankScreen`, reachable via a bottom-tab main navigator (Transactions/Rules/Settings,
with Rules/Settings as stub placeholders for now) plus an onboarding stack (Connect Bank +
stub placeholders for consent/syncing/classify). Remaining screens still need porting.

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
- **Alert thresholds**: per-debitor, optional "only alert if amount exceeds €X" and/or "only
  alert if charged more than N times per period." If both are set, they combine with **AND**
  logic. Explicitly kept in scope after a lean-scope review suggested cutting them — the owner
  wants this.
- **Notifications do not carry quick actions.** Earlier iteration explored "Trust"/"Keep Bad"
  buttons directly on the push notification; this was deliberately removed. Tapping a
  notification opens the transaction detail, where classification happens in-app.
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
