# Notification tracer bullet

The second tracer bullet, same discipline as `TRACER-BULLET.md`: the thinnest end-to-end slice
through every new layer, seen working before moving on, not a prototype to throw away.
`TRACER-BULLET.md` proved the app can read real bank data end to end; it deliberately stopped
short of everything in `ARCHITECTURE.md`'s "Deliberately out of scope" list — Postgres, FCM push,
the polling worker, the rule engine, the three ingestion modes. Notifications are the actual
product (`CLAUDE.md`: "the whole product"), so this is the next slice. Written 2026-09-17, scoped
in conversation before any code — see "Decisions" below.

**This will span several sessions.** Update the Progress table before ending each one, same habit
as `TRACER-BULLET.md`.

## The path

```
Mock ASPSP (sandbox)  →  local C# API (poll worker + rule engine)  →  Postgres  →  FCM  →  device
```

Testing happens against **Mock ASPSP, not real N26** — same reasoning as the first tracer bullet:
the consent/data loop can be re-run endlessly while wiring this up, without touching the
rate-limited production quota (`TRACER-BULLET.md`'s Findings).

## Decisions taken before writing this (2026-09-17)

| Question | Decision |
|---|---|
| Definition of done | A real push lands on the phone, not just a logged decision |
| Ingestion modes | Steady-state only — first-run and reconnect (`R20`) deferred |
| Seen-debits storage | Postgres, not a flat file |
| Timer location | Inside the local backend process (`dotnet run`), not Azure Functions |
| Postgres scope | Full `ARCHITECTURE.md` schema shape (minus one trim, see below) |
| Rules sync | Real client→server sync (new endpoint), not seeded by hand |
| Firebase/FCM setup | Done live together in session, when implementation starts |
| Tap-to-open (`R12`) | Deferred — prove the banner first |
| Auth | Tables only, one seeded user row, no login screen |
| Dev database | Local Docker Postgres |

**Scope trim, flagged rather than silently decided:** the full schema includes `BankConsents`,
but `ConsentStore` (file-backed `consent.local.json`) already works and is proven against real
N26 data. This bullet creates `Users`/`Payees`/`Rules`/`Debits`/`NotificationLog`/`DeviceTokens` —
everything it actually reads or writes — and leaves `BankConsents` and migrating `ConsentStore`
for a later, separate piece of work.

**Formatting default:** `R17` formats amounts by device locale, but the server has no device
locale to format push text with. Push titles/bodies use a fixed `de-DE`-style format ("49,00 €"),
matching the mocks and the target market. In-app screens the push eventually links to (once `R12`
lands) still use `formatCurrencyEUR` and the real device locale — this only affects the transient
OS notification text.

## What already exists and gets reused, not rebuilt

- `EnableBankingClient` + `TransactionMapper` (`backend/GreedyNose.Api/EnableBanking/`) — the
  ingestion worker calls the same mapping path `/debits` already uses, refactored into a shared
  method rather than duplicated.
- `ConsentStore`'s shape (singleton, in-memory state + `Lock`, restore-on-startup) is the pattern
  new stateful services should follow structurally, even though most of them move to EF Core.
- `app/src/data/rulesStore.ts`'s single write path, `saveRule(payeeId, draft)` — the one place a
  backend sync call hooks in, after the existing `persist(nextRules)` call.
- `app/src/data/backendFeed.ts`'s fetch pattern (`AbortController`, timeout, error-to-state
  rather than throw) — the template for the two new mobile→backend calls.
- `app/src/domain/classification.ts`'s decision table — ported line-for-line to C#, not
  redesigned.
- `app/src/data/hooks.ts`'s module-level singleton binding — the pattern a new device-token store
  follows (mirrors `rulesStore.ts`, exposed the same way).

## Progress

| Step | State |
|---|---|
| 0 Firebase project | **done, verified 2026-09-18** — real push sent from Firebase Console reached the device |
| 1 Postgres schema | **done, verified 2026-09-20** — both migrations applied to the local Docker Postgres, six tables + one seeded user; two `coder-reviewer` passes, no MUST FIX left |
| 2 Device token registration | **done, verified on the device 2026-09-21** — the phone's real token lands in `DeviceTokens` within seconds of launch; two `coder-reviewer` passes per half, no MUST FIX left |
| 3 Backend can send, proven in isolation | not started |
| 4 Rules sync | not started |
| 5 Rule engine, ported and tested | not started |
| 6 Ingestion worker — steady state | not started |
| 7 Record findings | not started |

## Steps

Each ends in something visible.

### 0. Firebase project — owner's task, done live in session

Create the Firebase project, add the Android app (package `com.greedynose`), download
`google-services.json` into `app/android/app/`, add the `com.google.gms.google-services` Gradle
plugin, generate a service-account JSON key for server-side sending (gitignored, same treatment
as the Enable Banking `.pem`).

**Done when:** a test push sent from the Firebase console's own "Compose notification" tool
reaches the physical device — before any of our code sends anything. Isolates "is Firebase wired
up at all" before building on top of it, same reasoning as the first tracer bullet's step 1
keeping the JWT alone.

**Done, 2026-09-18.** Firebase project `greedy-nose` created, Android app registered under
`com.greedynose`, `google-services.json` in place (gitignored), Google Services Gradle plugin
wired into both `app/android/build.gradle` (classpath, `4.5.0`) and `app/android/app/build.gradle`
(apply plugin). Service-account key downloaded and gitignored (`*firebase-adminsdk*.json`, repo
root) for step 3.

A small, deliberately temporary probe (`coder-mobile`, not reviewed per instruction — throwaway
code, superseded by step 2) was needed to actually get a token to test with: `@react-native-firebase/app`
+ `@react-native-firebase/messaging` installed, a `useEffect` in `App.tsx` requests notification
permission and logs the FCM token. One real gotcha found and fixed in the same pass: **Firebase's
own `requestPermission()` is an iOS-only API that resolves `AUTHORIZED` unconditionally on
Android** — it does not trigger the OS permission dialog there at all. `PermissionsAndroid.request`
is what actually has to ask, plus a `POST_NOTIFICATIONS` entry in `AndroidManifest.xml`. Both
missing at first; the temporary probe now does it correctly, and step 2's real implementation
needs to as well.

**Second gotcha, worth remembering for every future test on this project:** a push sent while the
app is in the **foreground** is delivered silently to the app's own message handler — Android
only auto-shows the OS banner when the app is backgrounded or killed. The app has no message
handler yet (deferred to a later step), so a foreground test looks like nothing happened. Always
background the app before sending a test push.

Confirmed end to end: real FCM token logged from the device → pasted into Firebase Console's
"Send test message" → banner appeared on the physical device with the app backgrounded.

### 1. Postgres schema

Docker Compose (`backend/docker-compose.yml`, one `postgres:16` service) for local dev. Add
`Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL`,
`Microsoft.EntityFrameworkCore.Design` to `GreedyNose.Api.csproj`. New `GreedyNoseDbContext` with
`Users`, `Payees`, `Rules`, `Debits`, `NotificationLog`, `DeviceTokens`. One seeded `User` row
(fixed id, no auth fields beyond what anchors the foreign keys). Connection string via
`dotnet user-secrets` (`ConnectionStrings:Postgres`), same pattern as the Enable Banking config.

**Done when:** `dotnet ef database update` succeeds and the tables exist.

**Done, 2026-09-20.** Built by `coder-backend`, reviewed twice by `coder-reviewer` (the first pass
found two SHOULD FIX and five CONSIDER items, the second found two SHOULD FIX and a few nits; all
the ones the owner picked are fixed).

- **Local setup (main session):** `backend/docker-compose.yml` runs `postgres:16` as
  `greedy-nose-postgres`, named volume `greedy-nose-pgdata`, **bound to `127.0.0.1` only** (the dev
  password is in the repo, and from step 6 the DB holds real bank data). `dotnet-ef` 10.0.12 is a
  **local tool** (`dotnet-tools.json` at the repo root, so `dotnet ef` works from `backend/`).
  Connection string is the `ConnectionStrings:Postgres` user-secret.
- **Code:** `backend/GreedyNose.Api/Data/` — `GreedyNoseDbContext`, one entity class per table,
  `SeedData.UserId` (the fixed seeded user, `5f0d7c3a-8b1e-4d6a-9a52-3c7e1b2f4a90`), `Migrations/`
  (`InitialCreate`, then `TightenDeleteBehaviorAndRuleLimit`). The `Classification` enum lives in
  `GreedyNose.Api.Domain` (not `Data`), so step 5's `RuleEngine` depends on the domain, not on
  persistence; the lowercase `good`/`bad` converter stays in `Data`.
- **Startup:** a missing `ConnectionStrings:Postgres` fails at startup with the exact
  `dotnet user-secrets set` command in the message. There is **no** migration on startup and no
  connectivity check — `/health` still answers with Postgres down. `database update` is a manual step.
- **Delete behaviour:** the five `UserId → Users` FKs cascade (account deletion is a real delete).
  The three cross-table FKs (`Rules→Payees`, `Debits→Payees`, `NotificationLog→Debits`) are
  `NoAction`, so deleting a lone debit cannot erase its `R11` guard row and a delete-and-reinsert
  payee "upsert" cannot silently wipe a rule. Tested against real Postgres in a rolled-back
  transaction, both directions.
- **Constraints:** `CK_Rules_Classification` (`good`/`bad`), `CK_Rules_AmountEUR_Positive`
  (null or > 0), unique `IX_DeviceTokens_Token`, unique `IX_NotificationLog_UserId_DebitId` (the
  DB-level `R11` guard).

**Carried into later steps — decisions the review surfaced, not yet made:**

- **Step 2/6:** the step 6 `BackgroundService` is a singleton and must inject
  `IDbContextFactory<GreedyNoseDbContext>` (registered with `AddDbContextFactory`), **never** the
  context itself. Endpoints keep taking the scoped context.
- **Step 4:** `Rules` has a composite FK to `Payees` (owner's choice), and nothing writes `Payees`
  before step 6. `POST /rules` therefore cannot save a rule for a payee the DB has never seen, and
  its planned body carries no name/initials/iban. Step 4 must either add those fields to the
  request (the app has them) or upsert the payee from it — decide there, or step 4's "done when"
  (mark a payee, see a `Rules` row) fails on the FK.
- **Step 4:** `ClassificationText.Parse` throws `FormatException` — right for a corrupt DB value,
  wrong for a client's bad `POST /rules` body. The endpoint needs a `TryParse` or a 400 mapping,
  and should validate the limit is positive itself (the DB check is only the last line of defence).
- **Step 6:** decide the order of "insert `NotificationLog` row" vs. "send the push". `SentAt` is
  non-null and there is no status column, so a row can only mean "sent". If step 6 inserts the row
  first to use the unique index as the race guard, a failed FCM send leaves a row for a push that
  never went out — the missed-alert direction, the worst failure for this product. Likely answer:
  a nullable `SentAt`, or send first and insert after; decide before writing the worker.
- **Step 6:** add a `(UserId, AccountKey)` index on `Debits` when the bootstrap check ("any debits
  for this account?") is written. Parse the mapper's UTC timestamps so the offset stays zero
  (`AssumeUniversal`) — Npgsql throws on a non-zero offset for `timestamptz`.
- **Later:** `NotificationLog.DebitId` is a required FK, so the table cannot record the
  consent-expiry push (`ARCHITECTURE.md`) or the `R20` summary push, neither of which has a debit.
  Out of scope now; revisit when either is built.

### 2. Device token registration

New `POST /device-token` (`{ token }`, upserts `DeviceTokens` for the seeded user). New
`app/src/data/deviceStore.ts` mirroring `rulesStore.ts`'s shape: requests Android notification
permission (`POST_NOTIFICATIONS`, required from Android 13), reads the FCM token via
`@react-native-firebase/messaging` (new dependency, needs a native rebuild), POSTs it once. Wired
into `hooks.ts` alongside the existing stores.

**Done when:** the token row appears in Postgres after installing the app.

**Done, 2026-09-21.** Backend by `coder-backend`, app by `coder-mobile`, in parallel against a
contract settled in the plan; each half reviewed twice by `coder-reviewer`.

- **Contract, as built:** `POST /device-token`, `{ "token": "…" }` → `204` (stored or already
  stored, idempotent), `400` problem details otherwise, `413` above an 8 KB body. A token is
  **printable ASCII only (0x21–0x7E) and at most 1024 characters** — the plan said 4096, and the
  first review found that a 3000-character token hits Postgres's btree row limit and a NUL
  character fails with `22021`, both as a 500. ASCII-only makes length equal UTF-8 bytes, so 1024
  sits about 2.6× under the index limit. Real FCM tokens are ~140–200 characters of
  `[A-Za-z0-9:_-]`. The token is stored exactly as sent, never trimmed.
- **Backend:** `backend/GreedyNose.Api/Notifications/DeviceTokenEndpoint.cs` and
  `DeviceTokenValidation.cs`. Update-first upsert on the unique `Token`; a lost insert race (two
  requests for the same new token) is caught on that one index name and answered `204` — a real
  case, since the app posts once after `getToken` and again on `onTokenRefresh` at first launch.
  The full token is never logged (a 6-character preview at most, and none for short tokens). No
  auth, tagged `// PRAGMATIC:` with a `TODO.md` item to close it before any deploy.
- **App:** `app/src/data/deviceStore.ts` (`startDeviceRegistration()`), reached through
  `useDeviceRegistration()` in `hooks.ts`, called from `App.tsx`; the step 0 probe is gone. Asks for
  `POST_NOTIFICATIONS` on Android 13+, reads the token, POSTs it, and POSTs again on
  `onTokenRefresh`. Only in backend mode. **A denied permission still registers the token** (it is
  valid regardless). No retry loop and no persisted flag — the upsert is idempotent and the next
  launch retries. Never throws; a failure is a `console.warn` and the app is unaffected.
- **A test bug the review missed, found by the database:** a jest run POSTed the fake
  `test-fcm-token` into the dev database, because `App.test.tsx` rendered `App` and RN's `fetch`
  really does reach `localhost` inside Jest. Fixed twice over: `App.test.tsx` now mocks
  `deviceStore` and asserts the hook is wired (deleting the call from `App.tsx` fails a test), and
  `jest.setup.js` installs a global `fetch` that rejects loudly ("tests must not reach the
  network"). Proven with the backend listening: a control POST created a row, then the full suite
  left none. (An earlier "proof" of this was worthless — the backend had failed to start — which is
  why the control POST is part of the check.)
- **Verified on the device (Sandbox backend, `adb reverse`, JS-only reload — no native rebuild was
  needed):** first launch → a 142-character token for the seeded user appeared 6 s after start;
  force-close and reopen → still one row, `UpdatedAt` moved (3 s); backend stopped, app launched →
  opens normally, one `[deviceStore] could not reach the backend…` warning 3 s in, no crash; backend
  restarted, relaunch → `UpdatedAt` moved again (7 s). **Not verified:** that the stored token is
  the one the step 0 probe logged — the new code deliberately never logs it. Step 3, sending to it,
  is the real proof.
- **Phone gotchas found:** a screen-off (doze) phone freezes the app's network — one relaunch
  logged `Network request failed` 43 s after start while the backend was up, and the screen had
  gone to sleep; keep the screen awake (`adb shell input keyevent KEYCODE_WAKEUP`) during timed
  tests. That one failure is a likely-but-unproven doze effect, not a bug found. Also: the
  adb daemon was not running at session start and wireless pairing had lapsed (already in the
  toolchain notes); when grepping `adb logcat -v time`, the level marker is `W/ReactNativeJS(…)`, not
  ` W ` — the first backend-down check "found nothing" only because of that.
- **Deferred, in `TODO.md`:** no auth, no retry on warm resume, stale rows after token rotation
  (step 3+ must prune on FCM `UNREGISTERED`), the Error-level log noise from a handled race, no
  server-side trace of a rejected token, and a few test/comment gaps.

### 3. Backend can send, proven in isolation

Add the `FirebaseAdmin` NuGet package. A small `NotificationSender` wrapping
`FirebaseMessaging.SendAsync`. A temporary `POST /debug/send-test-push` endpoint that reads the
stored token and sends a fixed "hello" message.

**Done when:** curling that endpoint puts a real push on the phone, sent by our backend code
rather than the Firebase console. Isolates "can we send" before the rule engine sits on top of
it. Endpoint gets deleted once step 6 proves the real path.

### 4. Rules sync

New `POST /rules` (`{ payeeId, classification: 'good' | 'bad', amountEUR?: number }`, upserts
`Rules` for the seeded user). `rulesStore.ts`'s `saveRule()` gets a sibling fire-and-forget call
to it, following `backendFeed.ts`'s error-swallowing pattern — a failed sync must not block the
local save, since AsyncStorage stays the in-app source of truth (`R6`) and Postgres's copy is
only what the server-side rule engine reads.

**Done when:** marking a payee in the app shows the row in `Rules`.

### 5. Rule engine, ported and tested

New static `RuleEngine` class (backend), porting `classification.ts`'s table exactly:

| Rule state | Result |
|---|---|
| No rule | bad, `no-rule` → "New payee — you haven't seen this one before." |
| `classification: bad` | bad, `marked-bad` → "You marked this payee as bad." |
| `classification: good`, no `amountEUR` | good |
| `classification: good`, `amountEUR` set | good if `debit.amountEUR <= amountEUR`, else bad, `over-limit` → "Over your limit of «amount»." |

New `RuleEngineTests.cs` in the existing `GreedyNose.Api.Tests` project, mirroring
`classification.test.ts`'s cases and the existing requirement-id-in-test-name convention.

**Done when:** tests pass.

### 6. The ingestion worker — steady state, configurable interval

A `BackgroundService` using `PeriodicTimer`, interval from config
(`Ingestion:PollIntervalSeconds`) — a short value in dev (e.g. 30s, via user-secrets), the real
6h default in `appsettings.json`. Each tick, per active consent:

1. Fetch + map debits (the shared method from "What already exists").
2. Upsert `Payees`.
3. **Bootstrap case** (no `Debits` rows yet for this account): insert everything as seen, no rule
   evaluation, no notification — a minimal stand-in for `R10b`'s "first sync" consequence,
   without building the full onboarding classify-screen mode (deferred, see below).
4. **Steady-state case**: any mapped debit not already in `Debits` is new → insert it → look up
   its `Rules` row → `RuleEngine.Classify` → if bad, check `NotificationLog` for that debit id
   (guards against a duplicate send across restarts, `R11`) → if not sent, `NotificationSender`
   fires and a `NotificationLog` row is written in the same step.

**Done when:** add a mock transaction for a bad-rule payee in Mock ASPSP's control panel, wait
one poll interval, a real push lands on the device — title `«Payee» · «amount»`, correct `R12a`
body. A good-and-under-limit payee's new debit produces zero pushes across the same wait.

### 7. Record findings

Same spirit as the first tracer bullet's step 7 — update `REQUIREMENTS.md`/`ARCHITECTURE.md`
with anything the sandbox run actually taught (e.g., whether Mock ASPSP's added transactions
carry `entry_reference`, timing behavior of the configurable poll, anything about the
bootstrap/steady-state boundary that surprised us).

## Deliberately out of scope

- First-run onboarding classify screen UX (the bootstrap step above is silent, no UI).
- Reconnect summary push (`R20`).
- Health Monitor (staleness checks, operator email alerts).
- Real user auth/login.
- Tap-to-open → debit detail (`R12`'s navigation half).
- `BankConsents` table / migrating `ConsentStore` off its file.
- Azure deployment — the worker stays in-process for this bullet.
- iOS/APNs — Android device only, same as the rest of this project so far.

## Critical files

- `backend/GreedyNose.Api/Program.cs` — wiring for the new DbContext, `NotificationSender`,
  `BackgroundService`, and the two new endpoints.
- `backend/GreedyNose.Api/EnableBanking/TransactionMapper.cs` — the fetch+map call the worker
  reuses; may need a small refactor to expose it as a callable method rather than only living
  inside the `/debits` endpoint handler.
- New: `backend/GreedyNose.Api/Data/GreedyNoseDbContext.cs` and entity classes.
- New: `backend/GreedyNose.Api/Notifications/RuleEngine.cs`, `NotificationSender.cs`,
  `IngestionWorker.cs`.
- `backend/GreedyNose.Api.Tests/` — new `RuleEngineTests.cs` alongside the existing
  `TransactionMapperTests.cs`.
- `app/src/data/rulesStore.ts` — the sync call added to `saveRule()`.
- New: `app/src/data/deviceStore.ts`, following `rulesStore.ts`'s shape.
- `app/src/data/hooks.ts` — wiring the new device-token store in.
- `app/android/app/build.gradle`, `app/android/build.gradle` — Google Services plugin.
- `app/package.json` — `@react-native-firebase/app`, `@react-native-firebase/messaging`.

## Verification

- Backend: `dotnet test` (existing 41 + new `RuleEngineTests`) and `dotnet ef database update`
  against the local Docker Postgres.
- Mobile: `npx tsc --noEmit`, `npx eslint .`, `npx jest`.
- End to end, on the physical device, against Mock ASPSP: mark a payee bad, add a mock
  transaction for it, confirm exactly one real push arrives within one (shortened, dev-config)
  poll interval; confirm a second poll tick does not re-send it.

## Process note

Per `CLAUDE.md`'s delegation rule, step 0 (Firebase) is done live together since it's a
browser/console task, not code. Steps 1–6 are backend-only, mobile-only, or split at the
`POST /rules` / `POST /device-token` contracts above (already settled here, so `coder-backend`
and `coder-mobile` can work the two sides in parallel once step 1's schema exists) — each step
goes through `coder-backend`/`coder-mobile` then `coder-reviewer`, same as every other piece of
work in this repo.

## Picking this up next session

Steps 0–2 are done. Start with **step 3** (the backend sends a push to the stored token) — it
needs the Firebase service-account key at the repo root (gitignored, `*firebase-adminsdk*.json`).
Step 4's `POST /rules` contract question (the `Rules→Payees` foreign key) needs an answer before
its coder agent starts. Postgres must be running: `docker compose up -d` in `backend/` (the
container restarts on its own after a reboot once Docker Desktop is up).

**The backend is on the Sandbox application for this whole bullet** (switched 2026-09-21, so app
launches — which call `/debits` — do not spend N26's ~4/day quota). Switch back to Production when
the bullet ends: `ApplicationId`, `PrivateKeyPath`, `RedirectUrl` (`https://…`) and copy
`consent.local.json.n26-bak` over `consent.local.json`.
