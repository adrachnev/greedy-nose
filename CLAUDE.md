# Greedy Nose

@AGENTS.md

## Status

**2026-09-29 — first-sync mode signal built (backend), uncommitted at time of writing.** The
ingestion worker's "first sync or steady state" decision no longer asks "do any `Debits` rows
exist?" — it reads a new `AccountSyncStates` row, keyed `(UserId, AccountKey)` (IBAN, never `uid`),
which exists only once that account's first sync has finished. The first sync is **all-or-nothing**:
every new debit plus the row are committed in one `SaveChangesAsync`, no resume logic — an
interrupted first sync restarts from scratch and the R10b dedup key makes that safe. This closes
the `TODO.md` gap against R25 *and* a second one found while designing it: an empty account never
wrote a row, so it would have stayed "first sync" forever and swallowed its first real charge.
Migration `AddAccountSyncState` backfills a row for every account that already has debits.
Built by `coder-backend`, two `coder-reviewer` passes (no MUST FIX; two test-strength findings
fixed — the silence tests now register a device token so they can really fail). 242 tests pass.
Verified live: migration applied to the dev DB, backfill gave one row; deleting that row and
starting the backend re-ran a silent bootstrap — 0 pushes, no duplicate debits, row restored.
**Decided but not built (each needs its own plan):** Reconnect mode (derive it by comparing the
current consent session with a `LastSessionId` on that row, no flag; send the summary first, then
commit); runtime consent-death detection (`ExpiresAt` per tick plus a bank "session invalid"
error); the client-facing "first sync done" contract and the onboarding classify screen (R26).
v1 is one account per bank, but the schema deliberately doesn't block several accounts later.

**2026-09-28 — `ARCHITECTURE.md` validated against `REQUIREMENTS.md` via `bmad-architecture`, all
findings fixed and committed (`b844bb7`).** Two independent reviewers found ARCHITECTURE.md had
drifted from the 2026-09-24 REQUIREMENTS.md revision (stale R12a wording, R20a/R20b uncited, R25's
first-sync signal unspecified in a way that could misfire) and that three component boundaries
were described inconsistently enough for two builders to diverge (payee-resolution-vs-dedup
ordering, the composite dedup key's ambiguous fields, reconnect-summary ownership). All fixed: one
canonical pipeline stated once, the composite key's `day`/`payee`/`ordinal` fields pinned down, the
reconnect summary now explicitly owned by the Ingestion Worker, and `FirstSyncCompletedAt`
replaces "do any Debits rows exist" as the real mode signal — that row-existence check's gap
against a partial-first-sync-resume is now tracked in `TODO.md` against the real running code, not
just the doc.

Also folded in the owner's polling-quota proposal: real-device testing confirmed on-demand "app is
open" fetch is **not** exempt from the bank's 4x/day cap (hit `ASPSP_RATE_LIMIT_EXCEEDED` through
the real PSU-header path), closing an open question flagged since 2026-09-16. It's removed
entirely except one narrow exception — the first sync, still triggered immediately by the API
right after consent. Swipe-to-refresh needed no new backend contract, just a client-side re-read
of the existing debit list.

Five tech-currency claims web-verified: Azure Functions' in-process model retires 10 Nov 2026
(isolated worker forced anyway on net10.0); Postgres free tiers pause/suspend on idle but never
lose data, and aren't a practical risk given the worker's own cadence; SendGrid's free tier ended
May 2025 — swapped for Brevo (300/day, free forever).

**`NOTIFICATION-TRACER-BULLET.md` is the live document for the notification work; read its
Progress section first.** Scoped and started 2026-09-17, right after the first tracer bullet
closed — Postgres, FCM push, the polling worker, the rule engine and rules-sync are all new
ground it covers step by step. **All steps 0–7 are done (0 Firebase 2026-09-18, 1 Postgres schema
2026-09-20, 2 device token registration and 3 the backend sends a push both 2026-09-21, 4 rules
sync, 5 the rule engine port and 6 the ingestion worker all 2026-09-22, 7 record findings
2026-09-23) — this tracer bullet is complete.** **The backend is still on the Sandbox application**
(to spare N26's daily quota); the switch-back procedure is at the end of that file, and switching
back is the one remaining real-world step, deliberately not done inside this docs-only pass (see
`AUTOMATED-RUN.md`'s stop conditions — it spends real N26 quota).

**2026-09-24 — `REQUIREMENTS.md` validated and revised, docs-only.** `bmad-prd`'s validate intent
(rubric walker) plus `bmad-review` (edge-case-hunter, verification-gap) plus a manual first-use/
normal-use walkthrough — three independent passes, cross-converging on several findings — found
the doc's one real hole: the onboarding classify flow was load-bearing for R4b/R10c but had no
requirement of its own, which had already caused the step-6 divergence CLAUDE.md's 2026-09-22
entry records. Fixed by adding **R25** (a connection starts with a first sync; an interrupted
pull is still the same first sync) and **R26/R26a** (onboarding, sourced from the already-settled
`01c-classify-payees` mock: not gated on full review — an unreviewed payee stays bad and alerts
normally on its next charge). Also fixed: **R20a/R20b** (the reconnect summary now has a stated
zero-bad-debit floor, so it can't fire a no-op push that would violate R1, and post-deletion
reconnect is explicitly routed to R25's silent bootstrap, not R20); **R13a** (the Save-commits/
Back-discards mechanic R24a always assumed but that no requirement actually stated); R12a's "no
rule" wording (no longer claims an unreviewed-but-seen payee is unfamiliar); R23b extended to the
Rules-list search round trip; R10b/R10c tightened (the composite fallback key is now stated as
the common case, not the exception; provisional rows classify and get replaced in place once
booked). Full detail and the validation report (with three code-level findings intentionally left
out of `REQUIREMENTS.md` — they're implementation gaps against already-correct requirements, not
doc issues) are in `_bmad-output/planning-artifacts/prds/prd-greedy-nose-2026-09-24/`.

**2026-09-23 — step 7 (record findings) done, docs-only.** No `REQUIREMENTS.md` change; three
additions to `ARCHITECTURE.md`, each checked against code or the dev database rather than
asserted: the Ingestion Worker's component-table row still names Azure Functions, but what
actually runs is an in-process `BackgroundService` in the API host (deliberate, deferred); the
"First run" ingestion-mode row promises onboarding-classify routing that step 6 deliberately
didn't build (silent bootstrap only), confirmed by a mid-session backend restart to still read
real state, not a flag; and Mock ASPSP's control-panel-added test transactions do carry
`entry_reference` (verified by querying the dev `Debits` table directly), sandbox-only evidence
that doesn't reopen the already-answered real-N26 question. Full detail in
`NOTIFICATION-TRACER-BULLET.md`'s step 7.

**2026-09-22 — step 6 (the ingestion worker) done, verified on the device.** A bootstrap tick
silently seeded 92 historical debits with no notifications; two later mock transactions for
unclassified payees each produced exactly one real push, a following poll did not resend either,
and marking one of those payees Good in the app silenced its next debit. Built by the main
session directly, not `coder-backend` — agent-spawning was blocked by a permission classifier for
this whole step (both a resume and a fresh spawn were denied). This is the exact process gap
`CLAUDE.md` already names as dangerous (main-session code with no automatic review), compensated
by starting `coder-reviewer` by hand three times regardless, since that agent type was not
blocked. Two real MUST FIX findings came back, both about the one failure this product cannot
have — a charge that silently never notifies — and both fixed and verified by reproduction: a
bulk-insert-then-classify shape that could strand an already-committed debit if a tick was cut
short, and a payee-upsert race that poisoned the rest of a tick's writes. See
`NOTIFICATION-TRACER-BULLET.md`'s step 6 "as built" for the full detail, including the
`IDebitsFetcher` seam added beyond the plan (mirrors `INotificationSender`, needed so the new
tests exercise real mapping logic without a socket).

**2026-09-22 — steps 4 and 5 done, as an owner-approved "automated run" (first use of that
mode).** Step 5: `RuleEngine.cs` (backend), a line-for-line port of `classification.ts`'s R5
table — 210/210 tests pass, no review findings. Step 4: `POST /rules` (`Rules/RulesEndpoint.cs`)
plus `rulesStore.ts`'s sync call, built by `coder-backend`/`coder-mobile` in parallel against a
contract settled in plan mode (the request carries the payee's name/initials/iban so the endpoint
can upsert `Payees` before `Rules`, satisfying the composite FK). Verified on the device: marking
a payee creates both the `Payees` and `Rules` row. Two `coder-reviewer` passes on the backend
half found and fixed an unhandled concurrent-insert race; a narrower race (the loser of that race
can silently lose its own data) and a few test-coverage gaps were judged low-severity and
deferred to `TODO.md` rather than spent on a third fix round. One coder agent got stuck in an
infinite loop after finishing its own work — see `TODO.md`'s "Process" item on the `SubagentStop`
review hook; worked around by verifying its (correct, already-saved) output directly rather than
waiting on it. Also found on the device, not a regression: rules saved before this session have
no retry mechanism and never reached the backend — expected given the step's scope, fix deferred.
The decisions left open for step 6 (notably: its own `Payees` upsert must overwrite
`FirstSeenAt`, which step 4 may have already set to a synthetic "now") are in
`NOTIFICATION-TRACER-BULLET.md`.

Step 3, verified on the phone: our own backend put a real banner on it, a high-priority push woke
the screen while Dozing, and FCM's answers to bad tokens matched the sender's four outcomes
(`Sent` / `TokenNoLongerValid` / `Rejected` / `Transient` — what step 6 may do with each is in the
doc). Step 1: local Docker Postgres (`backend/docker-compose.yml`, loopback only), EF Core schema
and two migrations in `backend/GreedyNose.Api/Data/`, one seeded user. Step 2: `POST /device-token`
(printable ASCII, ≤1024 chars) plus `app/src/data/deviceStore.ts`; verified on the phone — the real
FCM token lands in `DeviceTokens` within seconds of launch, survives relaunch and a backend outage.
Two review passes per half each time.

**2026-09-17 — tracer bullet complete: step 6 confirmed on the device, all steps 0–7 done.** The
backend was switched back to the **Production** application (`ApplicationId`/`PrivateKeyPath`/
`RedirectUrl` restored, see the note below) and the real N26 consent restored from
`consent.local.json.n26-bak` — still valid (expires 2026-12-15), so no fresh browser consent was
needed. `adb reverse` plus a fresh `installDebug` put the app on the device: the Debits tab showed
real N26 charges, and the rules created the previous session against the backend's payee ids were
still there and matched correctly — confirming rules persistence and the payee-id scheme survive a
Sandbox↔Production secret switch and a reinstall, not only a restart. One gap found along the way,
now recorded in `REQUIREMENTS.md`: the `Subscription` payment type is confirmed unreachable from
any real bank data seen — the owner's own Netflix and Anthropic charges both arrive as plain card
payments. Full detail in `TRACER-BULLET.md`'s "Step 6 as built".

**2026-09-16 — tracer-bullet steps 6 and 7 are done at the backend, verified against the real N26
account (91 debits, 52 payees) through the Production application; device confirmation of step 6
is the one thing still open.** It's blocked by N26 itself, not by code: Enable Banking rate-limits
background fetches (`ASPSP_RATE_LIMIT_EXCEEDED`, ~4/day per their own FAQ), and a burst of calls
while wiring up USB access used up the day's quota — their guidance is to wait ~6h before
retrying, no workaround exists. Full findings in `TRACER-BULLET.md`. For unrelated testing in the
meantime, the backend was switched back to the **Sandbox** application (Mock ASPSP):
`dotnet user-secrets` currently hold the sandbox `ApplicationId`/`PrivateKeyPath`, and the real N26
consent was moved aside as `consent.local.json.n26-bak` (not deleted) under
`backend/GreedyNose.Api/`. Switching back to Production needs both application ids restored (see
`TRACER-BULLET.md`'s Findings) **and** the `RedirectUrl` secret set back to
`https://localhost:5199/callback` — the two applications are registered with different redirect
URIs in Enable Banking's console (Sandbox: plain `http://`; Production: `https://`), discovered the
hard way this session when the wrong one produced a `WRONG_ASPSP_PROVIDED` / `REDIRECT_URI_NOT_ALLOWED`
error depending on which secret was stale.

**Same day — rules now persist (`app/src/data/rulesStore.ts`, AsyncStorage).** The single
piece of in-memory-only state flagged in `TODO.md` since step 5 — mark a payee good, reload the JS
context, the rule is gone — is fixed: rules hydrate from `AsyncStorage` on the first `subscribe()`
(mirroring `backendFeed.ts`'s lazy-load pattern), seed from the fixture set on a fresh install only
in fixture mode (empty in backend mode, which is correct per `R4b`), and persist through a write
queue so one failed native write can't silently swallow every write after it. Two `coder-reviewer`
passes: the first found one MUST FIX (an unhandled promise rejection in the write queue could
permanently wedge all future writes after a single transient failure) and one SHOULD FIX (no
regression test for it) — both fixed by `coder-mobile` and re-verified mergeable. Confirmed
on-device: mark a payee, force-close the app, reopen — the rule survives.

**The tracer bullet reached the device on 2026-08-19: every layer is wired end to end, and the
app shows bank data fetched through our own backend. `TRACER-BULLET.md` is the live document;
read its "Progress" section first.** Started 2026-08-17. The account behind it is still the
**sandbox** Mock ASPSP (holding an import of real German account data, which is why its findings
are worth something) — firing the same code at the real N26 account is step 6, and it waits on
Restricted Production approval. The plan, settled the same day: an end-to-end slice
(sandbox ASPSP → local ASP.NET Core minimal API → the device's debit list), sandbox first and the
real N26 account after, with no DB, push, polling or Azure in the first shot.

**`backend/GreedyNose.Api` now exists** — a bare minimal API (net10.0) holding the Enable Banking
client, the RS256 JWT signer, a file-backed consent store, the domain mapper and the endpoints for
steps 1–4, in step order.

**2026-08-18 — steps 2, 3 and 4 are done and verified against a live connection.** The Mock ASPSP
consent completed in a browser; `/raw` returned 100 real transactions (kept in `raw/`, gitignored);
`/debits` maps them to 92 debits and 45 payees in exactly the shape `app/src/domain/model.ts`
declares.

**2026-08-19 — step 5 is done and verified on the device: the app runs on real bank data.** The
Debits tab lists the sandbox charges through `adb reverse tcp:5199 tcp:5199`. The seam held —
no screen learned where its data comes from. New under `app/src/data/`: `config.ts` (the
`USE_BACKEND` flag, so fixtures stay one line away) and `backendFeed.ts` (fetch, validation and
the `useSyncExternalStore` store); `hooks.ts` binds the source **once at module level**, and
rules stay local under both flags because `R6` means the backend sends no classification.
`MockDataBadge` became `DataSourceBadge` — once the feed is real, an empty list could mean the
backend is down, `adb reverse` is missing, the consent expired, or the account is genuinely
empty, and the device is the worst place to guess. Full detail in `TRACER-BULLET.md`'s
"Step 5 as built". **Steps 0–5 are done; step 6 waits on Restricted Production approval.**

Three things step 5 exposed, all in `TODO.md`: **rules are in-memory only** and die with the JS
context (the one piece of state the user creates by hand, and the only unpersisted one — hidden
while rules and payees shared a fixture file); booking timestamps are **midnight UTC**, which
groups correctly only in a timezone ahead of UTC; and **45 payees with no rule** make the
unclassified state real for the first time, which is the onboarding bulk review's whole purpose.

**Real data broke three assumptions**, all recorded with evidence in `TRACER-BULLET.md`'s Findings:

- **`entry_reference` was null on all 100 rows**, so `R10b`'s de-duplication key did not exist at
  all. The mapper falls back to a composite of `(account, booking date, amount, payee key,
  ordinal)`, documented as a stopgap, not a design.
- **A creditor IBAN was present on 1 of 92 debits** and a creditor agent on none, so `R3a`'s
  tier 1 is dead for card payments and the normalized name carries almost everything.
- **The account `uid` changes on every consent** (seen three times, same IBAN). Keying debits on
  it would have re-alerted the entire history on every reconnect — the storm `R20` exists to
  prevent. `ConnectedAccount.Key` is the IBAN for this reason.

`credit_debit_indicator` is confirmed **`DBIT`**/`CRDT`: `ARCHITECTURE.md` was right and the API
reference's `DBTR` was wrong. Two honest gaps remain in the mapping, both logged for step 7:
`Subscription` is unreachable from bank data (no code means "recurring"), and aggregator prefixes
(`PAYPAL *…`, `SumUp *…`) key as the aggregator rather than the shop behind it — the merge
direction `R3b` warns against.

**The consent now survives restarts** (`consent.local.json`, gitignored — it holds a session id
that reads a real account). Step 2 had chosen memory-only; that was revised on 2026-08-18 once it
became clear the restart cost was paid per code change, not per session. It is still not the
database — one consent, no users, no history.

**Same day — reviewed against Enable Banking's own C# sample.** The **JWT is a clean bill**:
header, claims, algorithm, padding and encoding all match `cs_example`, so authentication is not
the place to look when step 6 misbehaves. Nine of thirteen findings were fixed immediately; the
one that mattered is that **currency was read and thrown away** while the DTO field is `amountEUR`,
so a foreign-currency charge would have been limit-checked as euros — a false or missed alert
under `R5`, invisible in a 100/100 EUR dump. Non-EUR charges are now skipped **and logged**. The
four deferred findings are in `TODO.md`; the one that blocks step 6 is that `valid_until` asks for
a flat 90 days and never consults the ASPSP's `maximum_consent_validity`.

Credentials: sandbox application `007a8a74-7a48-4213-82e1-d017d44b81b0`, private key at the repo
root as `<application-id>.pem` and **gitignored** by a new root `.gitignore` — it is the whole
credential (Enable Banking has no token endpoint), so it stays server-side forever and never goes
near `app/`. Config lives in `dotnet user-secrets`, not `appsettings.json`. .NET SDK 10.0.400 was
installed on this machine this session.

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

**2026-08-17 — `R23`/`R24` implemented in `app/`: search, and tabs that always land on their
list.** The mocks got the screens (`02c`, `02d`, `.search-field`); the client got the behaviour:

- **Search is one shared matcher, not two** (`app/src/utils/search.ts`, pure and tested). The
  debit list matches payee name *or* amount, the rules list name only, both substring and both
  accent-tolerant (`R23a`). Umlauts are folded with an **explicit table**, not
  `normalize('NFD')` — Hermes' Unicode surface is a build option, and a search that quietly
  stops folding on one platform is a bug nobody reports. A name is matched in **both** spellings
  (`müller` → `muller` *and* `mueller`), in both directions, because the bank's SEPA field says
  `BAECKEREI MUELLER` while the user types the umlaut.
- **Amounts are matched unformatted** (`toFixed(2)`), which is what lets `R17` land at the same
  time without breaking search: `formatCurrencyEUR` now uses a module-cached
  `Intl.NumberFormat` in the **device** locale (`49,00 €` on a German phone). Dates stay
  English on purpose — `R17` is about money, and a French month name under the literal `Today`
  would be worse than consistency.
- **The query is scoped to the visit** (`R23b`): it survives list → detail → Back, and is
  cleared on the **tab's** blur, not the screen's — the screen also blurs on the way into a
  detail screen, and clearing there would break the first half of the requirement.
- **`R24` is two navigator options, no dialog**: `popToTopOnBlur` on both list tabs, plus a
  `tabPress` listener that `preventDefault()`s when the tab is already focused. Together they
  also deliver `R24a` (an unsaved rule draft is discarded silently on tab switch) with no
  confirm-dialog code at all.
- **Fixtures were replaced wholesale**: six payees, ~71 debits, a full year of history, still
  computed relative to *now*. Four `TODO.md` items closed alongside (locale amounts, the memo
  that froze the Today/Yesterday labels, 16px row spacing, the per-row `usePayee` subscription).
  The set is built so all three of `R12a`'s bad-debit reasons are reachable **by hand on the
  device**, not just in unit tests: FitLine Gym has no rule ("New payee…"), ScamyLoans GmbH is
  explicitly marked bad ("You marked this payee as bad."), and Bäckerei Müller is good with a
  €30.00 limit that two charges exceed ("Over your limit of…"). A review pass caught the middle
  one going missing when the set was first rewritten; `data.test.ts` now pins all three.

`npm test` (99 tests, 10 suites), `npx tsc --noEmit` and `npx eslint .` all pass. Not yet run on
the device since either rework — worth a build before trusting the UI details, and note the
device is now the only place `Intl` (new in `formatCurrencyEUR`) has never been exercised.

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
- **Onboarding**: after granting bank consent, a one-time bulk-review screen lets the user
  classify their existing payees before live monitoring starts — otherwise every historical
  charge would fire a notification on first connect.
- Classification (`R4`/`R5`/`R6`/`R8`) is specified in full in `REQUIREMENTS.md` and implemented
  in `app/src/domain/classification.ts`. This section no longer carries its own copy — see
  `AGENTS.md`'s Known pitfalls for the one thing worth restating (the deleted auto-flip).

## Open / deferred (explicitly not v1)

- App-level lock (Face ID/passcode) before opening the app.
- Multi-bank / multi-account differentiation in the debit list. Note this is *simultaneous*
  connections, not bank support in general — the app must work with any bank (`R22`), it just
  handles one connected account at a time in v1 (`R22a`).
- Offline state handling for the main list (only the initial-connect error state exists).
- Notification grouping/bundling (a "Group multiple alerts" toggle exists in the Settings mock
  but defaults Off — one notification per charge is the current decision).
- Non-EUR bank accounts: `R15` hard-assumes EUR ("no currency picker anywhere"), which
  contradicts `R22`'s bank-agnostic promise the moment a non-eurozone ASPSP is connected. Needs
  a currency model (store and compare per-currency) that doesn't exist yet — found by
  `bmad-review` 2026-09-23, not yet decided.
