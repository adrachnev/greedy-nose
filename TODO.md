# Open items

Code-level work that is known, deliberately deferred, and easy to forget. Product-level
deferrals live in `REQUIREMENTS.md` (its "Refining these from real data" section and the mock
gaps) and `CLAUDE.md`'s "Open / deferred" — this file is for *implementation* items, mostly
review findings.

Rules for this file: an item is either done and deleted, or it says why it is still here. Do not
let it become a graveyard — anything nobody has touched in months is not a TODO, it is a decision
to not do it, and should be recorded as such or dropped.

## From the `REQUIREMENTS.md` validation pass — 2026-09-24

`bmad-review`'s verification-gap lens, run against `REQUIREMENTS.md` adapted to check the actual
`app/` and `backend/GreedyNose.Api` code against what each `R`-numbered requirement specifies.
All three are implementation gaps against requirements that are already worded correctly — the
doc needed no change for any of these; the code does.

- [ ] **`R4a`'s "at most two decimal places" is not enforced by the backend rule-amount validator**
      (`backend/GreedyNose.Api/Rules/RulesValidation.cs:62-65`). `TryValidate` only checks
      `AmountEUR <= 0`; `POST /rules` accepts e.g. `amountEUR: 12.999`, which Postgres then
      silently rounds via the `numeric(12,2)` column rather than rejecting as `R4a` implies. Add a
      decimal-scale check next to the existing positivity check.
- [ ] **`R3a`'s "raw strings stored alongside the resolved key" is asserted by the doc and
      `ARCHITECTURE.md` but not implemented** (`backend/GreedyNose.Api/Data/Payee.cs:15`). `Payee`
      has one `Name` field, unconditionally overwritten — not appended to — on every ingestion
      tick (`IngestionRunner.cs`'s `UpsertPayeesAsync`) and every `POST /rules` save
      (`RulesEndpoint.cs`'s `HandleAsync`). Once a real bank sends two spellings of the same
      creditor over time (already observed in `TransactionMapperTests.cs`'s branch-number and
      aggregator-prefix cases), every spelling but the most recent is permanently discarded — the
      exact data `R3a` says is needed for "a future payee-key re-tuning pass." Either add raw-name
      history or strike the claim from `R3a`/`ARCHITECTURE.md`.
- [ ] **`R24`/`R24a`'s entire tab-navigation behavior rides on one untested navigator option**
      (`app/src/navigation/AppNavigator.tsx:160`, `popToTopOnBlur: true`, applied to the Debits and
      Rules `Tab.Screen` entries at lines 189/195). No render/integration test exercises it —
      `AppNavigator.test.ts` only unit-tests `listTabListeners` against a hand-built `{ isFocused }`
      stub and never mounts `AppNavigator`/`MainTabs`. Dropping the option, or a react-navigation
      upgrade changing its semantics, would silently break both requirements (tab always lands on
      its list; an unsaved rule draft discards on tab switch) while `npx jest` stays green — the
      `tabPress`/`listTabListeners` re-tap guard is tested separately and would still pass, masking
      the regression. Add a render test that mounts the tab navigator, pushes DebitList → PayeeEdit
      with draft state, fires the Debits tab's blur, and asserts the reset + discarded draft.

## From a `bmad-review` pass over REQUIREMENTS.md/ARCHITECTURE.md — 2026-09-23

Adversarial + Edge-Case Hunter lenses, run together over both documents. All real, all about
components this project has explicitly not built yet — no code exists for any of these today, so
nothing here is urgent; each is a design decision to make when that piece is actually built.

### Health Monitor (not built — deliberately out of scope for the notification bullet)

- [ ] **R19's expiry-push latency has no stated bound.** Composing the actual mechanism (a poll
      only fails after expiry, the Health Monitor only flags staleness past 20h, the Health
      Monitor itself runs daily) gives a worst case around 44h — unlike every other latency claim
      in the docs, this one isn't written down anywhere. Decide the bound when the Health Monitor
      is built, and state it.
- [ ] **Nothing monitors notification *dispatch* failure, only ingestion staleness.** A correctly
      detected bad debit whose FCM send keeps coming back `Transient`/`Rejected` has no alerting
      path — though it isn't silent forever: the real `IngestionRunner` only writes
      `NotificationLog` on `Sent`, so an unset row means the debit is retried next poll by
      construction. What's missing is *alerting a human* if that never succeeds, not a missing
      retry.
- [ ] **The Health Monitor's staleness query needs to exclude user-initiated Disconnect**, or
      Disconnect's "no expiry push" promise breaks the first time the daily job runs against a
      disconnected consent.
- [ ] **Expiry-push dedup should key on `(consent id, expiry occurrence)`, not consent id alone**
      — otherwise a consent that expires, gets reconnected, and expires again later has its
      second expiry silently suppressed as "already sent," recreating the exact silence R19
      exists to prevent.

### Account deletion (not built)

- [ ] **No stated fallback if the Enable Banking revoke call fails during deletion.** Decide
      between a bounded retry-then-proceed-anyway vs. blocking deletion — the reliable-deletion
      promise (`06c`, "gone, not undoable") argues for the former.
- [ ] **Deletion racing an in-flight ingestion tick or Health Monitor push for the same user**
      needs a lock/transaction that also cancels or waits on the in-flight job, or a concurrent
      job could write orphaned rows after "deletion" completes.

### Reconnect mode (not built for this bullet)

- [ ] Reconnect summary push behavior on a **zero-new-debits gap** (reconnected within minutes) is
      unspecified — likely just suppress the push.
- [ ] **Reconnect-mode's trigger only names explicit re-auth/reconnect events**, not a plain
      multi-day technical-outage gap. Without an explicit "gap exceeds N hours" trigger too, a
      backlog from an outage could run as steady-state and fire the exact per-charge burst R20
      exists to prevent.

### Tap-to-open (not built — `R12`'s navigation half, deliberately deferred)

- [ ] **No fallback screen specified for a deep link to a debit/payee that no longer exists**
      (tapped an old push after deleting the account). Needs a generic "no longer available"
      screen once tap-to-open is built.

### Multi-account / multi-user (v1 is single-account, single-user)

- [ ] **The payee identity key (`R3a`) is never scoped to an account or user.** Harmless today
      (one seeded user, one connected account); once multi-user exists, two users' identically
      named merchants would resolve to the same `Payee` row — a cross-tenant classification leak.
      Scope the key to `(user, resolved key)` before multi-user lands, not after.
- [ ] **No server-side guard against starting a second consent while one is already active**
      (`R22a`) — enforced today only by the mobile client having no UI path to it. Any second path
      to the API (retry, future admin tool, bug) has undefined behavior. Add the guard when real
      auth/multi-account work starts.
- [ ] **Two ingestion paths can race on the same consent** once the on-demand "app is open" fetch
      is actually built alongside the 6h timer poll — a per-consent lock/lease is needed so a
      concurrent fetch is a no-op, or the same new bad debit could be evaluated twice (breaking
      `R11`).

### Payee identity, no decision needed yet

- [ ] **`R3b`'s "split rather than merge" has no way to fix a bad split later.** A merchant whose
      charges inconsistently carry an IBAN (plausible per the aggregator-prefix findings) could
      re-split and re-notify every time its matching tier flips, with no way for the user to
      silence it permanently. No merge/consolidation UI exists; note as deferred, not a bug.

### Pending debits (zero observed so far, sandbox or production)

- [ ] **Whether a pending (not-yet-booked) debit is classified and shown like any other row, or
      held back until booking, isn't stated anywhere** — `R9` implies every listed debit is
      labeled good/bad unconditionally, while "Debit identity" separately excludes pending items
      from identifier matching. Resolve once a real pending transaction is actually observed.
- [ ] **A debit pending at onboarding (first-run) time has no reliable key yet; if it books later
      under a different key, first-run's "never notifies" guarantee may not hold for it.** Same
      reason as above — untested because unobserved. State it as an accepted exception once it's
      confirmed to actually happen.

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
      entirely new history. Only reachable while `entry_reference` is missing — confirmed a live
      concern, not a hypothetical: 63 of 91 real debits still needed the fallback despite the
      field existing (`ARCHITECTURE.md`'s "Refining this from real data", settled 2026-09-16).
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

## From notification tracer-bullet step 6 — the ingestion worker, 2026-09-22

- [ ] **`Payee.FirstSeenAt` is only as accurate as one page of `/debits`.** `IngestionRunner`
      sets it to the earliest `Timestamp` seen in the current fetch, which is correct today but is
      the earliest date visible in one page, not necessarily the payee's true first-ever charge —
      `/debits` still reads a single page (`continuation_key` is ignored, tracked separately above).
      Revisit once pagination is turned on.
- [ ] **`ClassifyInsertAndNotifyAsync`'s `NotificationLog` race-catch can silently drop a token
      prune from the same iteration** (`IngestionRunner.cs`, the `IX_NotificationLog_UserId_DebitId`
      catch). `db.ChangeTracker.Clear()` — needed so the poisoned insert doesn't abort the rest of
      the tick, same fix as the payee-upsert race — also discards any `DeviceTokens.Remove(token)`
      queued earlier in that same call. Only reachable with two processes concurrently ingesting the
      same account, which this tracer bullet does not deploy (`IngestionWorker` runs one tick at a
      time, one process). Self-heals: FCM keeps answering `TokenNoLongerValid` for that token on
      every later bad debit until it is actually pruned. Third review pass, 2026-09-22.

## From notification tracer-bullet step 4 — rules sync, 2026-09-22

- [ ] **A failed first sync of a rule is never retried** (`app/src/data/rulesStore.ts`,
      `syncRuleToBackend`). Same shape as step 2's device-token finding below, and not fixed for
      the same reason: the owner's decision there was "retry on the next launch", but this step's
      plan only called for a fire-and-forget call whose failure must not block the local save —
      not a retry mechanism. If the one sync attempt fails (offline, backend down), the rule stays
      correct in `AsyncStorage` (R6) but never reaches Postgres, so that payee can never trigger a
      push until the user re-saves the rule by hand. Likely fix: a per-launch resync of every local
      rule, mirroring `deviceStore.ts`'s pattern (attempt once per launch, no persisted "already
      synced" flag, safe because the backend's upsert is idempotent).
- [ ] **Saving a rule for an unknown payee syncs a placeholder name/initials to the backend**
      (`app/src/screens/PayeeEditScreen.tsx` via `domain/payees.ts`'s `unknownPayee` fallback,
      through `saveRule`). The `Payees` row the backend upserts gets `"Unknown payee"`/`"?"`
      instead of the real name. Low priority: step 4's contract sets `FirstSeenAt` only on first
      insert and never touches it on update, so the row self-heals once step 6's ingestion worker
      upserts the real payee data from a later fetch — the placeholder is a brief, cosmetic
      mismatch, not a permanent one.
- [ ] **`POST /rules` has no authentication** (`backend/GreedyNose.Api/Rules/RulesEndpoint.cs`).
      Same posture and same reason as `POST /device-token`'s finding below: deliberate while the
      backend only runs on the owner's machine and there is one seeded user; whoever can reach it
      can rewrite that user's payees and rules. **Close before the backend is deployed anywhere,**
      ideally in the same pass that closes `POST /device-token`.
- [ ] **On the insert race (`RulesEndpoint.cs`'s `catch` on `PayeePrimaryKeyName`/
      `RulePrimaryKeyName`), the loser's own content can be silently discarded.** Two concurrent
      `POST /rules` for the same brand-new payeeId with genuinely different classification/amount:
      the winner's write persists, and the loser gets the same `204` the winner does — unlike
      `DeviceTokenEndpoint`'s equivalent race (where the raced content *is* the key, so there is
      nothing to lose), here the loser has no signal that its own values didn't land, so it does
      not know to re-POST. Narrow and self-healing today — only reachable on a payee's very
      first-ever sync, and `AsyncStorage` stays the source of truth (R6) with nothing downstream
      reading this table yet — but revisit before step 6 starts trusting `Rules` as authoritative.
      Second review pass, 2026-09-22.
- [ ] **The race catch above is untestable against the InMemory provider, and `RulesTests.cs`
      doesn't say so** (unlike `DeviceTokenTests.cs`, which does). `PostgresException` never
      surfaces from `UseInMemoryDatabase`, so the `catch`'s pattern match can only be proven
      against real Postgres. Also missing: a test pinning `RulesEndpoint.PayeePrimaryKeyName`/
      `RulePrimaryKeyName` against the actual EF model, the way `DataModelTests.cs` pins
      `DeviceTokenEndpoint.TokenIndexName` — without one, a future migration renaming either
      constraint would make the catch silently stop matching. Second review pass, 2026-09-22.
- [ ] **`RulesEndpoint.PayeeIdPreview` shows a short payee id (≤10 chars) completely unredacted**,
      unlike `TokenPreview.Of`'s more conservative "too short → show nothing". Harmless today —
      every real payee id (`TransactionMapper`'s `iban:…`/`name:…` keys) is well over 10 chars —
      but inconsistent. Second review pass, 2026-09-22.
- [ ] **`RulesEndpoint`'s handled insert race logs two Error-level EF entries with a stack trace**,
      same noise as the already-tracked `DeviceTokenEndpoint` finding below — not re-explained
      here, just not previously written down for this endpoint too. Second review pass,
      2026-09-22.

## From notification tracer-bullet step 3 — the backend sends a push, 2026-09-21

- [ ] **`Message.Token` is `[Obsolete]` in FirebaseAdmin 3.6.0** ("Deprecated. Use `Fid` instead",
      per the package's own XML docs). A Firebase Installation ID is a different identifier from
      the FCM registration token `getToken()` returns, so moving to it means an app change and a
      different `DeviceTokens` value. Registration tokens still work today (a real push landed on
      the phone), so `FirebaseNotificationSender.BuildMessage` carries a
      `#pragma warning disable CS0618` tagged `// PRAGMATIC:`. Revisit when the SDK announces the
      removal of `Token` — pin the package version until then rather than upgrading blind.
- [ ] **Pushes arrive on FCM's default channel and look plain.** Verified on the phone: the
      notification sits on `fcm_fallback_notification_channel` at importance 3 — sound, shade
      entry, **no heads-up pop-up** — with a generic square small icon. For "the moment a bad
      payee charges you" a dedicated high-importance channel is the likely answer, plus a proper
      monochrome small icon (`com.google.firebase.messaging.default_notification_icon` in the
      manifest) and `channel_id` on the message. It needs a native channel in the app, so it is
      its own decision before shipping — not step 3, not step 6.
- [ ] **Step 6 must use the sender's four outcomes correctly** (`INotificationSender`,
      `FirebaseNotificationSender.Classify`/`SendAsync`; written after both step 3 reviews):
      - **`Rejected` means "needs a human — keep the alert pending", not "discard it".** It now
        includes a revoked or wrong-project key (`TokenResponseException`, `SenderIdMismatch`),
        a malformed token/request (`InvalidArgument`) and any bare 401/403/404 with no messaging
        code. If the worker gave up on the alert there, a fixable outage would silently lose it.
      - **Only `TokenNoLongerValid` (`Unregistered`) may prune a token.** A wrong-project key must
        never empty the token list.
      - **`Transient` needs a bound.** A natural one: stop retrying once the alert is older than
        its 1-hour TTL. It also catches a network failure, a timeout, a Google token-endpoint
        429/5xx, and the SDK's `NullReferenceException` on **any empty-bodied error response**
        (the HTTP status is lost there, so an empty-bodied permanent 401/403/404 — a proxy, say —
        also lands as `Transient`, logged at Error).
      - **Time budget per send.** FirebaseAdmin retries a failing send 4 times with back-off
        internally (~15 s for a 503/network error, not configurable through `AppOptions`), the
        credential path adds ~3 s, and a black-holed connection waits the 100 s `HttpClient`
        timeout. The poll loop needs its own budget.
      - **Deadline vs. shutdown.** Any caller cancellation is rethrown as
        `OperationCanceledException`, including the worker's own per-send `CancelAfter` — the
        worker must tell its deadline from a host shutdown.
      - Also: `SendResult` has a public constructor that can bypass its factories, and carries no
        `Retry-After`; `catch (Exception)` also turns a future SDK validation failure (once a data
        payload exists) into "Transient".
- [ ] FCM does not deliver to a **force-stopped** app (Settings → Force stop; on some OEMs a swipe
      from recents behaves the same). The whole product rests on delivery, so before shipping,
      check what the target phones do and what the app should tell the user (`R19`-style: never
      silently dead).

## From notification tracer-bullet step 2 — device token registration, 2026-09-21

- [ ] **`POST /device-token` has no authentication** (`backend/GreedyNose.Api/Notifications/DeviceTokenEndpoint.cs`).
      Deliberate while the backend only runs on the owner's machine and there is one seeded user;
      whoever can reach it can register a token. **Close before the backend is deployed anywhere.**
      Also the moment real login exists: an existing token's row keeps its old `UserId` on
      re-registration ("last registrant wins" is not implemented), so a phone handed to another
      account would keep receiving the previous owner's alerts.
- [ ] **A failed first registration is not retried until the process restarts.** The owner's
      decision was "retry on the next launch", but Android can keep a cached process alive for
      days, so a warm resume never retries (`app/src/data/deviceStore.ts`). Cheap fix if wanted:
      re-attempt on `AppState` → `active` while no attempt has succeeded.
- [ ] **Every token rotation leaves a stale row** — the backend upserts per token and never deletes
      one. Step 6's dispatcher must prune a token when FCM answers `UNREGISTERED` — the sender's
      `TokenNoLongerValid` outcome, and the only one that may prune (a bare 404 with no messaging
      code is `Rejected`, see above).
- [ ] Minor: the token read waits for the notification-permission dialog
      (`deviceStore.ts`, `register()`), though the token does not depend on it — a user who kills
      the app while the dialog is open stays unregistered for that launch.
- [ ] **A handled insert race still logs two Error-level EF entries with a stack trace**
      (`DeviceTokenEndpoint.cs`, the `catch` on the unique index). The race is real, not
      theoretical: the app posts once after `getToken` and again on `onTokenRefresh` at first
      launch (249 pairs in 320 concurrent posts in the review). The lines look like failures and
      train people to ignore the log. A single `INSERT ... ON CONFLICT ("Token") DO UPDATE` would
      remove the catch, the index-name coupling and the noise — at the price of raw SQL that the
      no-database test project cannot check. Left as is by decision; revisit if the noise bites.
- [ ] A rejected token (400) leaves no trace on the server (`Microsoft.AspNetCore` is at
      Warning). If FCM ever changes its token format, the only sign is a warning in the phone's
      logcat. A server warning with the length and the problem sentence (never the token) would
      surface it.
- [ ] Nothing in the automated suite pins `[RequestSizeLimit(8192)]` on the route — the manual
      curl checks are the only coverage; deleting the attribute keeps all tests green.
- [ ] `deviceStore.test.ts` leak tests: the "token that cannot be read" row never has the token in
      play (vacuous), and no row's error carries the token, so the Error-expansion helper is never
      shown to actually find one. Add a positive control; `{...arg}` also misses a non-enumerable
      `cause`.
- [ ] Comment drift: `app/jest.setup.js` says RN's `fetch` is "XHR-backed" in Jest, which it is
      not (there `XMLHttpRequest` is undefined and `fetch` is Node's own), and
      `backendFeed.test.ts:35` says Jest has no `fetch` at all. Reconcile the two. Also
      `deviceStore.ts` says "there is no iOS branch" while `Platform.OS !== 'android'` is one
      (on iOS it would register a token without asking permission); and `DeviceTokenEndpoint.cs`
      says a maximum-length token needs "~1.1 KB" — as `\uXXXX` escapes it is ~6 KB (still
      inside the 8 KB limit).

## Process

- [ ] **The `SubagentStop` review hook's request goes to the wrong recipient.** Seen at every
      coder stop in the notification tracer bullet (steps 1–3). The hook does run and does decide
      to request a review (`.claude/hooks/last-subagent-stop.json` is rewritten), but its
      `additionalContext` is delivered to **the subagent that just stopped** — on 2026-09-21 the
      step 3 coder reported, in its own final message, that "the hook asks for `coder-reviewer`
      to be launched" and that it could not do that (no agent-spawning tool). The main session
      never sees it, so nothing starts the review. Consequence: the process step 3 of `CLAUDE.md`
      ("this half is already automated") is not automated. Until a hook mechanism that reaches the
      *main* session is found (a `SubagentStop` hook cannot launch an agent itself, and its
      context lands in the stopped agent), the main session starts `coder-reviewer` by hand after
      every coder stops — `CLAUDE.md` now says so (2026-09-21). **Still open** — the wrong
      recipient itself is not fixed, only its worst consequence (below). Idea, untested: a `Stop`
      hook on the *main* session that blocks finishing while unreviewed code exists.
- [x] **The wrong-recipient bug could make the stopped coder loop infinitely, not just miss the
      review.** Hit for real on 2026-09-22: a coder-backend agent, told by the hook to "launch
      coder-reviewer" (impossible), tried, failed, stopped again — which re-matched
      `coder-mobile|coder-backend` and re-fired the same hook, forever. It got stuck over an hour,
      100+ tool calls, after its actual file edits were already correct and complete on disk.
      **Fixed 2026-09-22** in `.claude/hooks/review-after-coder.sh`: it now checks the payload's
      `stop_hook_active` field (the harness's own guard for exactly this — true means this stop is
      already a continuation of a previous stop-hook's `additionalContext`) and exits immediately
      without re-injecting. Also softened the injected text itself so even the *first* fire tells
      the coder plainly not to attempt the impossible task or retry, rather than instructing it to
      try. Verified with hand-built payloads for all four branches (looping/not, coder-reviewer
      exclusion, coder-mobile/backend match). Not yet proven against a real stuck-loop scenario in
      the wild — worth re-checking after the next coder that would have looped.

## Standing

- [ ] **Run the reworked app on the device.** Partly done 2026-08-17 — it builds, installs and
      renders, but see the discrepancy above; no screen is signed off yet. The R23/R24 pass on
      the same day added things only a device shows: the search field's keyboard behaviour, the
      44px tap target on the ✕, `Intl` under Hermes (amount formatting is now the platform's job,
      not ours), and the tab-press/`popToTopOnBlur` gestures.
- [ ] Port the remaining screens: Settings, the onboarding flow (consent/syncing/classify), and
      the error/empty/disconnected states.
