# Open items

Code-level work that is known, deliberately deferred, and easy to forget. Product-level
deferrals live in `REQUIREMENTS.md` (its "Refining these from real data" section and the mock
gaps) and `CLAUDE.md`'s "Open / deferred" — this file is for *implementation* items, mostly
review findings.

Rules for this file: an item is either done and deleted, or it says why it is still here. Do not
let it become a graveyard — anything nobody has touched in months is not a TODO, it is a decision
to not do it, and should be recorded as such or dropped.

## From the `app/` rework review — 2026-08-17

Two review passes over the R0/R4/R5/R8 rework. The first found a critical inverted-copy bug
(fixed); these are what survived the second, independent pass. **No MUST FIX findings remain.**

### Worth doing next

- [ ] **Bad debit's amount is not red on the detail screen**
      (`app/src/screens/DebitDetailScreen.tsx:66`). `mocks/03` and `03b` both paint it, and
      `DebitListScreen` already does — so today the list and the detail screen disagree about the
      same charge.

### Needs a decision before coding

- [ ] **A typed limit is silently dropped when you toggle to Bad and save**
      (`app/src/screens/PayeeEditScreen.tsx:95`). Save while bad writes the last *saved* limit, so
      an amount typed just before toggling disappears with no warning. This is the last remnant of
      the old "unsaved edit vs. toggle" question: the single-commit form removed most of it, but
      not this corner. Options: keep the draft amount on save, warn, or leave as-is and accept it.
      Decide before touching the screen.

### Smaller, no decision needed

- [ ] Reseed effect keys on `rule`, not `route.params.payeeId`
      (`app/src/screens/PayeeEditScreen.tsx:64`) — harmless with fixtures, but once real data
      refreshes in the background it can wipe a half-typed limit mid-edit.
- [ ] Good/Bad toggle has no `accessibilityRole` or selected state
      (`app/src/screens/PayeeEditScreen.tsx:162`) — only colour marks the active half, which
      leaves screen-reader and colour-blind users with nothing.
- [ ] **Debounce the debit-list search once the history is real**
      (`app/src/screens/DebitListScreen.tsx:121`). Every keystroke filters, sorts and groups the
      whole list. Deliberately not done now: at ~70 fixture rows it buys nothing measurable and
      costs a small state machine, and the rows themselves no longer re-render (memoized row +
      hoisted `renderItem`). Revisit with the paged history from the backend, where the sort is
      the part that will hurt.
- ~~`useAddDebit` has no caller~~ — **done 2026-08-19.** Removed in step 5. The shape the backend
      landed with was identical, but the hook wrote to the *fixture* store, so under the live feed
      it would have been a silent no-op named after the thing that adds a charge. `addDebit`
      itself stays in `src/mocks/data.ts`, where its tests are; `backendFeed.refresh()` is how a
      charge arrives now.

## From the R23/R24 review — 2026-08-17

Deliberate non-fixes. Both are real, both were understood, and neither is worth code today —
recorded here so they are decisions rather than things nobody noticed.

- [ ] **A lone thousands separator in search reads as a decimal point**
      (`app/src/utils/search.ts`, `normalizeAmountQuery`). Typing `1.234` looks for `1.234` and
      therefore misses a €1,234.00 charge; `1.234,56` works, because a second separator proves
      the first one grouped. The input is genuinely ambiguous — `1.234` is one thousand in
      Germany and one-and-a-bit in Britain — and guessing is how a search box starts lying.
      `parseAmount.ts` already refuses the same ambiguity by design rather than picking a side,
      so search agreeing with it is at least consistent. Invisible below €1000, which is most
      charges. Revisit if real balances make four-figure debits common, and if so solve it once
      for both files, not twice.
- [ ] **The `Intl.NumberFormat` cache lives as long as the JS context**
      (`app/src/utils/format.ts:17`). Change the phone's language while the app is warm and
      amounts keep rendering in the old locale — the one thing R17 promises not to do. The fix
      would be an `AppState`/locale-change invalidation hook inside what is otherwise a pure,
      dependency-free util, which is a real cost for a case Android mostly closes on its own: a
      system language change restarts the activity in practice, and the cache dies with it.
      Revisit if the device says otherwise — this is on the list of things only a device shows
      (see Standing, below).

## Investigate first — seen on device 2026-08-17

- [ ] **Two screens disagreed about the good-payee-with-a-limit.** On the device, the Rules tab
      listed that payee under **Bad** with "Alerts on every charge", while Debit Detail for the
      same payee showed **Good · limit €30.00**, and the debit list painted the avatar red where
      the detail screen painted it green. The fixtures said `{ classification: 'good',
      amountEUR: 30 }`, so the Rules tab was the one that was wrong.

      **Still open, and now re-pointed**: the payee it was seen on (REWE Markt) no longer exists —
      the 2026-08-17 fixture rework replaced it with **Bäckerei Müller**, which holds the same
      role (good, €30.00 limit, charges on both sides of it). Re-test there. Nothing about the
      suspected cause was proven or fixed, so this is a re-target, not a resolution.

      Rule out the boring cause first: this machine has hit stale-Metro state before (full kill +
      `--reset-cache` + uninstall/reinstall). The app was launched over a Fast-Refreshed bundle,
      so module state in `src/mocks/data.ts` may have been left over from an earlier build rather
      than re-initialised.

      If a clean reinstall still shows it, it is a real bug, and the likely area is
      `useRuleByPayeeId` / `useRules` snapshot identity — the same `useSyncExternalStore` class of
      failure this codebase already shipped once. Note both screens were verified correct by unit
      tests, so whatever this is, the tests do not cover it.

## From the backend review against Enable Banking's own C# sample — 2026-08-18

Reviewed `backend/GreedyNose.Api` against
`https://github.com/enablebanking/enablebanking-api-samples/tree/master/cs_example`. **The JWT is
a clean bill** — header, claims, algorithm, padding and encoding all match the sample, and the key
is loaded once and the clock injected, which the sample does not do. Nothing about our
authentication should be suspected when step 6 misbehaves.

Thirteen findings came back. **Nine were fixed on the spot**: the currency assumption, silent `0m`
amounts, unreadable dates, the granted-vs-requested consent expiry, the `identification_hash`
fallback, the reference echoing the payee name, unescaped `date_from`, `GetProperty` throwing on
an unexpected 200 body, and a doc comment that named the wrong weakness in the fallback debit id.
The four below are left open on purpose.

### Blocks step 6, not step 5

- [ ] **`valid_until` is a fixed 90 days and never consults `maximum_consent_validity`**
      (`backend/GreedyNose.Api/Program.cs`, `/connect`). Mock ASPSP allows 180 days so it has never
      bitten, but a production ASPSP with a shorter cap rejects `POST /auth` outright and step 6
      stops at the consent screen. The value is already in `GET /aspsps` per ASPSP; the fix is to
      read it before authorizing, which also needs a decision about caching that list.

### Needed before pagination is turned on

- [ ] **`/debits` reads one page and ignores `continuation_key`**
      (`backend/GreedyNose.Api/Program.cs`). `/raw` accepts it, `/debits` does not, so history
      silently stops at whatever one page holds — 100 transactions in the 2026-08-18 dump, about
      three months. Fine for step 5's list; wrong for onboarding, which is supposed to see the
      whole history before deciding what counts as new.
- [ ] **The ordinal in the fallback debit id collides across pages**
      (`EnableBanking/TransactionMapper.cs`, `ResolveDebitId`). It counts occurrences within one
      response, so a same-day group split by a page boundary restarts at `#0`. Harmless today
      because there is exactly one page — and precisely why it must be solved *with* pagination,
      not after. Documented at the method.
- [ ] **`/debits` sends no `date_from`**, so the window is whatever each ASPSP defaults to. Under
      `R22` that means different banks return different amounts of history for no reason the user
      can see.

### Worth a decision, no urgency

- [ ] **The payee key is part of the fallback debit id** (`ResolveDebitId`). One spelling change at
      the bank re-keys the payee *and* every debit under it at once, which R10b reads as an
      entirely new history. Only reachable while `entry_reference` is missing — step 6 decides
      whether that is ever the case in production.
- ~~**91 of 92 debits now have an empty `reference`**~~ — **done 2026-08-19.** Checked when step 5
      landed, and it did render an empty labelled row. Both the Reference *and* the Payee IBAN row
      on `DebitDetailScreen` now appear only when the bank sent the field, and the card they sit
      in disappears when neither did. The IBAN half was the worse of the two and was not in the
      original item: it is empty on 44 of 45 payees.
- [ ] **The granted-expiry path is coded but unverified.** `ConsentStore.ExpiresAt` reads
      `access.valid_until` from the session response; the consent currently on disk predates the
      field, so `/health` reports `expiresAt: null`. The next fresh consent exercises it.

## From step 5 — the app on the live feed, 2026-08-19

### Blocks anyone actually using the app

- ~~**Rules live in memory and die with the JS context.**~~ — **done 2026-09-16.** Rules now
      persist to `AsyncStorage` (`app/src/data/rulesStore.ts`): hydrated on first `subscribe()`,
      seeded from the fixture set only in fixture mode, written through a queue so an out-of-order
      native write can't silently revert a save. Two review passes (one MUST FIX — an unhandled
      promise rejection could permanently wedge future writes — fixed and pinned by a regression
      test). Verified on-device: mark a payee, force-close, reopen — the rule survives. The
      payee-id-scheme note below is unaffected; a rule still keys on whatever id scheme was active
      when it was saved.

### Worth knowing, no action decided

- [ ] **The two data sources use different payee id schemes** — `payee-netflix` in the fixtures,
      `name:LIDL CONNECT` from the backend — so a rule saved under one matches nothing under the
      other. Correct behaviour under `R4b` (no rule = unreviewed = bad) rather than a bug, and
      flipping `USE_BACKEND` therefore reads as "every payee is new again". Recorded because it
      will look like data loss the first time someone hits it.
- [ ] **Booking timestamps are midnight UTC**, because `transaction_date` was null on every row
      (see `TRACER-BULLET.md`). Germany is UTC+1/+2 so the date sections land on the right day,
      but in any timezone behind UTC the same charge would group under the previous day, and
      `DebitDetailScreen` prints a meaningless `01:00`/`02:00` for every debit. `R22` says nothing
      may assume a bank; nothing should assume a timezone either.
- [ ] **The unreviewed-payee flood is now real**: 45 payees, none with a rule, so the Rules tab is
      45 rows under Bad and every debit is bad. This is what onboarding's one-time bulk review
      exists for (`mocks/01c`, the flow `R4b` calls the only user of the has-a-rule distinction),
      and it is not ported yet — the fixtures' six payees hid how bad the unclassified state
      looks at real scale.

## Process

- [ ] **The `SubagentStop` review hook did not request a review after `coder-backend` stopped**
      (seen again 2026-09-20, notification tracer-bullet step 1). The hook does run —
      `.claude/hooks/last-subagent-stop.json` was rewritten when the reviewer stopped — but no
      "requesting an automatic code review" instruction reached the session after the coder's
      stop, so the main session had to start `coder-reviewer` by hand. Not yet known whether the
      hook exited early (the size check or the `coder-reviewer` text match), or whether
      `additionalContext` from a `SubagentStop` hook is simply not surfaced. To find out: log the
      hook's decision (skip reason or "requesting") to a file, then run one more coder agent.
      Until then, treat the hook as unreliable and start the review by hand.

## Standing

- [ ] **Run the reworked app on the device.** Partly done 2026-08-17 — it builds, installs and
      renders, but see the discrepancy above; no screen is signed off yet. The R23/R24 pass on
      the same day added things only a device shows: the search field's keyboard behaviour, the
      44px tap target on the ✕, `Intl` under Hermes (amount formatting is now the platform's job,
      not ours), and the tab-press/`popToTopOnBlur` gestures.
- [ ] Port the remaining screens: Settings, the onboarding flow (consent/syncing/classify), and
      the error/empty/disconnected states.
