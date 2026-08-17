# Greedy Nose

## Language

Always respond in English in this project, overriding any global default language setting.

A mobile app (iOS + Android) that connects to a European bank account and notifies the user
the moment a debitor they've flagged as "Bad" charges them. That's the whole product — it is
deliberately not a general finance/budgeting app.

## Requirements

**`REQUIREMENTS.md` is the single source of truth for what the app does. Read it first.**
**`TODO.md` holds the known open implementation items — read it before starting work, so a
review finding isn't rediscovered or silently re-broken.**
Mocks and implementation must follow it. Where anything in this file or `ARCHITECTURE.md`
disagrees with it, `REQUIREMENTS.md` wins — in particular, its classification model supersedes
the "Alert thresholds" and auto-flip bullets under Product decisions below, and its `R0`
terminology (payee, good/bad) supersedes the debtor/debitor/Trusted wording used throughout
this file. Settled 2026-08-14; `R3a`, `R10b` and the new `R10c` were revised 2026-08-17 on
Enable Banking's answer (see below). The mocks follow it, and `ARCHITECTURE.md` was fully
reconciled with it on 2026-08-17 — all ten `A1`–`A10` divergences closed. **`app/` still does
not** — that rework is the next piece of work.

## Status

**2026-08-17 — `ARCHITECTURE.md` is reconciled with the spec.** Both contradictions are gone
(`A1` the inverted rule engine, `A2` the missing consent-expiry push), terminology follows `R0`,
and the six designs the spec needed are written: the `DBIT` credit filter, derived-not-stored
classification, a third "reconnect" ingestion mode for `R20`, `R18`'s data lifecycle, and the
bank-agnostic consequences (per-ASPSP poll cadence, no bank hardcoded).

**Same day — Enable Banking answered the two API questions, and one answer bit.** `A6` is closed:
no SEPA creditor ID exists and none is reachable, so `R3a` is now a two-tier best-effort key
(normalized creditor account/IBAN → normalized name + creditor agent). The unasked-for correction
is bigger: **the de-duplication key was wrong**. `transaction_id` may change between fetches and
must not be used; the key is `(connected account, entry_reference)`, and since no identifier
reliably survives pending → booked, **only booked debits notify** (new `R10c`). Both keys are
built from docs plus one support answer — they get a tuning pass against real data from a live
connection later, which is why the design stores every raw string it sees.

**2026-08-17 — `app/` reworked to match the spec.** The client now follows `R0`/`R4`/`R5`/`R8`:

- **A real domain layer** (`app/src/domain/`), separate from the fixtures because it outlives
  them. `model.ts` holds `Payee`/`Debit`/`Rule`; `classification.ts` is the single place that
  decides good vs bad (`R5`'s table, `R5a`'s `>` not `>=`, `R12a`'s three reason strings).
  Classification is **derived at read time, never stored** (`R6`), which is what makes a rule
  edit re-label existing debits for free (`R7`).
- **The model moved**: good/bad lived on the payee as `trusted`, and now lives on the **rule**
  as `classification` (`R4`). "Payee has a rule" is what reviewed means (`R4b`), so no rule =
  unreviewed = bad, with no extra flag.
- **The auto-flip is gone entirely** (`R8`): no save-time flip, no passive Bad-only flip, no
  "Auto-marked Bad · Xm ago" marker, no rule-clearing on manual toggle, and none of the tests
  that pinned them. `addDebit` appends and notifies — that is all it can do now. A payee's
  classification moves only when the user moves it.
- **Renames** (`R0`): `TransactionListScreen`→`DebitListScreen`,
  `TransactionDetailScreen`→`DebitDetailScreen`, `DebitorEditScreen`→`PayeeEditScreen`; the tab
  is **Debits**; hooks are `usePayees`/`useDebits`/`useRuleForPayee`/`useSavePayeeRule` — one
  Save commits classification and limit together, so there is deliberately no per-field setter.
  `PayeeEditScreen` is a single-commit form: the Good/Bad toggle only moves draft state, Save
  writes and navigates back, Back discards. Amounts are stored **positive** and rendered without a minus sign
  (`R2a`/`R17a`).
- **`R14`**: the limit field is hidden while a payee is bad, and hidden ≠ wiped — mark them good
  again and the previous limit returns (`R8a`).
- The old array-reference discipline is unchanged and still tested by reference identity, not
  just end value: every mutation reassigns a new array so `useSyncExternalStore` sees it.

`npm test` (30 tests, 4 suites), `npx tsc --noEmit` and `npx eslint` all pass. Not yet run on
the device since the rework — worth a build before trusting the UI details.

Mocks in `mocks/` are reviewed and settled. Framework: **React Native**, bare RN app in `app/`
(Android target), toolchain validated end-to-end (build → wireless adb → physical device).
Ported: `ConnectBankScreen`, `DebitListScreen`, `DebitDetailScreen`, `RulesListScreen`,
`PayeeEditScreen`, reachable via a bottom-tab navigator (Debits/Rules/Settings, the first two
real nested stacks, Settings still a stub) plus an onboarding stack (Connect Bank + stubs for
consent/syncing/classify). **Still to port**: Settings, the onboarding screens, and the
error/empty/disconnected states.

`app/src/mocks/data.ts` supplies fixture *values* only — the types live in `app/src/domain/`.
It still has to be replaced by real data (see Open/deferred below).

## Product decisions (settled)

- **Bank connectivity**: [Enable Banking](https://enablebanking.com) — a PSD2/XS2A-licensed
  aggregator with a free tier. Chosen specifically to start with zero cost/investment. The app
  is **bank-agnostic** (`R22`): any ASPSP Enable Banking reaches is fair game, and **N26 is only
  the first one integrated**, with ING-DiBa and DKB likely next. Nothing may hardcode a bank.
  Connecting several banks at once remains deferred — see below.
- **Everything about classification lives in `REQUIREMENTS.md`, not here.** The rule model
  (`R4`), what makes a debit good or bad (`R5`), and the fact that only the user ever moves a
  payee's classification (`R8`) are specified there and implemented in
  `app/src/domain/classification.ts`. This section used to carry its own version — an amount
  threshold that auto-flipped good/bad, a passive Bad-only flip, an "Auto-marked Bad" marker —
  all of which `R8` deleted on 2026-08-14. Do not reintroduce them from memory or from an old
  commit.
- **Trust model is opt-out, not opt-in**: every payee is **bad** the first time they're seen.
  The user marks payees **good** to silence them — not the other way round. Deliberately chosen
  over a neutral "unmarked" state, since the goal is to never miss a genuinely new charge.
- **Onboarding**: after granting bank consent, a one-time bulk-review screen lets the user
  classify their existing payees before live monitoring starts — otherwise every historical
  charge would fire a notification on first connect.
- **Alert limit**: per-payee, optional "only alert if amount exceeds €X", and it only means
  anything while the payee is **good** (`R5`/`R14`). **Revised 2026-08-13**: the frequency
  variant ("only alert if charged more than N times per period") is cut — amount is the only
  limit. Saving one is validated (must be a positive number).
- **Debit Detail is informational only — no classification or rule-editing controls live
  there.** It shows the debit, why it is good or bad (`R12a`'s wording), and the payee's current
  status as a read-only pill, with a "Manage" link to the Edit Rule screen where good/bad and
  the limit are actually edited. This satisfies the notification-tap flow (one tap from
  notification → detail → Manage → classify) without duplicating editable controls.
- **Notifications do not carry quick actions.** An earlier iteration explored "Trust"/"Keep Bad"
  buttons directly on the push; deliberately removed. Tapping a notification opens the debit
  detail, which links to the Edit Rule screen where classification happens in-app.
- **No "untracked" third state.** Only good/bad exist. An earlier "reset to default / untrack"
  concept was cut as scope creep — it behaved identically to bad anyway.
- **Design language is lean and icon-driven**: no emoji, minimal decorative icons. State
  (good/bad, active tab, etc.) is communicated through **color** (green/red) and simple
  custom-drawn line-style SVG icons (`stroke="currentColor"`, `stroke-width="1.75"`,
  round caps/joins — matches the tab-bar icons in `mocks/style.css`/the HTML files), not
  through library icon sets or emoji glyphs.

## Open / deferred (explicitly not v1)

- App-level lock (Face ID/passcode) before opening the app.
- Multi-bank / multi-account differentiation in the debit list. Note this is *simultaneous*
  connections, not bank support in general — the app must work with any bank (`R22`), it just
  handles one connected account at a time in v1 (`R22a`).
- Offline state handling for the main list (only the initial-connect error state exists).
- Notification grouping/bundling (a "Group multiple alerts" toggle exists in the Settings mock
  but defaults Off — one notification per charge is the current decision).
- **`app/src/mocks/mockOnboardingState.ts` and `app/src/mocks/data.ts` are temporary
  scaffolding, not real state.** They currently drive which navigator (onboarding vs. main)
  shows and what payee/debit data renders. Both MUST be replaced with real persisted app state
  and live data from the backend/Enable Banking integration once those exist — do not let this
  quietly become permanent. Note the *types* moved out to `app/src/domain/model.ts` on
  2026-08-17: only the fixture values are scaffolding, the vocabulary is not.

## `mocks/`

Static HTML/CSS phone mockups, one file per screen, ~375×812 viewport, Flexbox layout
throughout (chosen so it maps fairly directly to React Native, the chosen stack).

- `index.html` — overview embedding every screen; open this first. Frames are grouped into six
  phases in the order the user meets them (onboarding → daily use → rules → the notification →
  settings → losing/restoring the connection), not by file number; the numbers stay on the
  labels so each frame is still easy to find on disk.
- `style.css` — shared design tokens/components (`.card`, `.pill`, `.btn`, `.tab-item`,
  `.modal-overlay`, `.toast`, `.spinner`, dark-mode variants via `prefers-color-scheme`).
- Screens are numbered by flow position (`01` connect bank → `01b` consent → `01bb` syncing →
  `01c` onboarding classify → `02` debit list → `03` debit detail → `04` rules → `04b` edit rule
  → `05` notification → `06` settings), with lettered variants for error/empty/confirm states
  (e.g. `01e` connect error, `02b` empty list, `06b`/`06c` confirm modals).
- Reworked 2026-08-14 to follow `REQUIREMENTS.md`. Renamed per `R0`: `debitor` → `payee`
  (`01c`, `04`, `04b`, `04c`) and `transaction` → `debit` (`02`, `02b`, `03`), in filenames,
  headings and copy alike. Three states were added or repurposed: `04d` edit rule for a **bad**
  payee (no amount field, R14), `05b` the reconnect summary notification (R20), and `01d` now
  shows the disconnected banner **on top of** the debit list rather than replacing it (R19).
  Comments inside the files cite the requirement they implement.
- A critical pass against the spec on 2026-08-14 added `03b` and `05c`, the good-payee-over-limit
  debit detail and its notification — the subtle half of `R5` that previously existed only as a
  list row and two HTML comments. Four known gaps were left open on purpose; they are listed at
  the bottom of `REQUIREMENTS.md`.

Iterate on mocks before touching architecture/code — that was an explicit ordering decision:
mocks first, then pick the stack, then implement and test.

## `app/`

Bare React Native project (via `@react-native-community/cli`, package `com.greedynose`),
Android target set up first. Layout:

- `src/domain/` — the permanent core: `model.ts` (`Payee`/`Debit`/`Rule`) and
  `classification.ts` (`R5`'s table, the only place good/bad is decided). No React, no
  fixtures, no I/O — so it is also the cheapest thing to test.
- `src/data/hooks.ts` — the seam screens read through. Swapping fixtures for a real API should
  touch this file and nothing else.
- `src/mocks/` — fixture *values* only, temporary (see Open/deferred).
- `src/screens/` — one component per mock screen; `src/theme/colors.ts` mirrors
  `mocks/style.css`'s light/dark tokens.

Local Windows toolchain notes (only relevant if the build breaks again):
- Requires JDK 17 (Android Gradle Plugin + a very new bundled JDK 25 from Android Studio
  trips a "restricted method" false-failure bug) — set `JAVA_HOME` to a JDK 17 install
  (e.g. Temurin) before running Gradle, not the Android Studio JBR.
- Requires the Microsoft Visual C++ Redistributable installed (CMake, used for RN's native
  build, fails with STATUS_DLL_NOT_FOUND without it).
- `npx react-native run-android` fails to invoke `gradlew.bat` from Git Bash/execa on this
  machine; run Gradle directly instead (`.\gradlew.bat app:installDebug ...`) via PowerShell.
- Device is connected via wireless adb (`adb pair`/`adb connect`), not USB.
