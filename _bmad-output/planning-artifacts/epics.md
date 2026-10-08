---
stepsCompleted: [1, 2, 3, 4]
inputDocuments:
  - REQUIREMENTS.md
  - ARCHITECTURE.md
  - TRACER-03-GO-LIVE.md
  - TODO.md
  - mocks/ (index.html, style.css, 01*–06* screens)
  - _bmad-output/planning-artifacts/prds/prd-greedy-nose-2026-09-24/validation-report.md
  - _bmad-output/planning-artifacts/ux-designs/ux-greedy-nose-2026-09-28/validation-report.md
---

# Greedy Nose — Tracer 3 (Go-live) - Epic Breakdown

## Overview

This document provides the epic and story breakdown for **Tracer 3, the go-live** of Greedy Nose
(`TRACER-03-GO-LIVE.md`): a first productive version **for the owner only**, on Azure, connecting the
owner's three banks (N26, DKB, ING-DiBa). It decomposes the requirements from `REQUIREMENTS.md`
(the PRD), `ARCHITECTURE.md`, and the mocks (UX) into implementable stories.

**Scope decision (2026-10-01):** only what Tracer 3 needs. Everything already built and verified in
Tracers 1 and 2 is *baseline*, not new work (it appears below only where go-live changes or depends
on it). No `DESIGN.md`/`EXPERIENCE.md` UX pair exists; `mocks/` is the UX source.

**Explicitly out of Tracer 3** (kept here so nobody re-adds them): in-app onboarding (R26/R26a —
the owner connects N26 once through the backend page and classifies payees on the Rules screen),
the in-app connection-expired banner (`01d`), Disconnect / Delete account (R18, `06b`/`06c`),
the Health Monitor job (only a small operator-email slice for failed pushes comes in, see AR18), iOS, a bank-selection screen, per-bank labels in the
debit list, other users' accounts (Restricted Production is owner-only).

**Process reminder:** each story is built exactly as before (plan mode, `coder-backend`/
`coder-mobile`, `coder-reviewer` started by hand, findings back to the same coder). Never
`bmad-build`.

## Requirements Inventory

### Functional Requirements

Only the requirements Tracer 3 must deliver or newly depends on. "Baseline" = already built and
verified against the sandbox/N26; go-live must keep it true on the cloud and across several banks.

- FR1 (R22a): The app connects **one account per bank, several banks at once** — N26, DKB and
  ING-DiBa — and the debit list shows all of them together. Each bank is its own consent; a
  connection that ends, fails or reconnects affects only its own bank's debits and alerts.
  **Decided 2026-10-01: all three banks are in Tracer 3** — go-live is not done with N26 alone.
- FR2 (R25, R20b): Granting consent to a bank starts a **first sync** of that account's available
  history, stored as already-seen; none of it notifies. It is all-or-nothing; an interrupted first
  sync restarts and is still a first sync. Must work for each of the three banks.
- FR3 (R9, R2a): The debit list shows debits newest first, each marked good or bad, and **comes
  from stored data** (what the worker has pulled), not from a live bank call. Incoming money never
  appears.
- FR4 (R10, R10a, R10b, R10c, R11): A new, **booked**, bad debit triggers exactly one push;
  "new" means an unseen identifier `(account, entry_reference | composite fallback)`, never a
  recent date. Baseline — must hold unchanged for every bank.
- FR5 (R12a): The push carries the payee and amount as title and one of three reasons (no rule /
  marked bad / over limit) as body; no minus sign (R17a). Baseline.
- FR6 (R4–R7, R13, R14, R21): The user's rules decide good/bad; a rule edit re-labels existing
  debits and never notifies for them; rules stay editable while disconnected. **Go-live addition:**
  because the backend classifies when it sends a push, a rule saved in the app must reliably reach
  the backend.
- FR7 (R19): A dead connection is never silent. The **one push** (`Bank connection expired` /
  `Reconnect to keep getting alerts.`) when a consent dies, once per session, per bank. Baseline
  (built). The in-app banner half is out of scope.
- FR8 (R20, R20a): Reconnecting a bank after a gap sends **one summary push** (only if at least one
  bad debit), never a burst. Baseline (built) — per bank.
- FR9 (R15, R16): Amounts use the account currency (EUR); a foreign-currency charge is never
  compared to a EUR limit — non-EUR charges are skipped and logged. Baseline.
- FR10 (R22): Nothing hardcodes a bank — copy, data model and rule logic stay bank-agnostic.

### NonFunctional Requirements

- NFR1 (R1, R3b, R10b): **Never miss a genuinely new bad charge.** Where identity or de-duplication
  is uncertain: split rather than merge for payees, risk the duplicate alert rather than the missed
  one.
- NFR2 (Cost): The whole deployment stays at **zero cost** — Azure consumption/free tiers
  (Functions or equivalent, storage, Key Vault), a free Postgres tier (Supabase/Neon), Enable
  Banking Restricted Production, Firebase, Brevo for operator mail.
- NFR3 (Cost, Enable Banking Terms): **Owner's accounts only.** Every account must be linked in the
  Enable Banking Control Panel by the owner first; no third party may be given access to the API;
  family accounts need a contract and KYB and are out of scope.
- NFR4 (Transaction ingestion): Respect the bank's background-fetch cap (~4 per day per bank).
  Poll cadence is **configuration per ASPSP** (default 6 h); `ASPSP_RATE_LIMIT_EXCEEDED` is a normal
  backoff signal; the app never triggers a fresh bank fetch; the one exception is the first sync
  right after consent. The push retry (AR18) works from stored data and never calls the bank.
- NFR5 (Latency): Same-day notification is acceptable; worst case ≈ one poll interval (6 h) plus
  booking delay.
- NFR6 (Isolation): One bank's failure (error, quota, dead consent) must not stop ingestion or
  alerts for the other banks.
- NFR7 (Secrets): The Enable Banking `.pem`, the Firebase Admin SDK key, the live consent and bank
  data are never committed, printed or logged; configuration lives in a secrets store (user
  secrets locally, Key Vault / app settings in Azure), never `appsettings.json`. Secrets in
  Tracer 3: the Enable Banking key, the Firebase Admin key, the API key (AR2), the APK signing
  key, the Neon connection string, the Brevo key. The consent session id reads the real account:
  it may live only **encrypted** in `BankConsents` (Key Vault key, AR3); anywhere else in the
  database only its SHA-256 hash; never logged. **Decided 2026-10-01: Key Vault** holds the
  Enable Banking private key (one per application, shared by all banks and users) and the
  encryption key for the sessions (one session per user and bank); plain settings (and Key Vault
  references) go in app settings; locally everything stays in `dotnet user-secrets`.
- NFR8 (Security): No endpoint is reachable unauthenticated once deployed; no debug endpoint ships.
- NFR9 (Data safety): Free-tier Postgres may pause or cold-start but never loses data; the worker's
  cadence keeps it awake; the worst case is one slow request, never a missed alert.
- NFR10 (Platform): Backend targets net10.0; if the worker becomes an Azure Function it must use
  the **isolated worker model** (in-process retires 10 Nov 2026). Client target: Android.

### Additional Requirements

No starter template (brownfield). The blockers from `TRACER-03-GO-LIVE.md` are AR1–AR9; the rest
come from `ARCHITECTURE.md` and `TODO.md` findings that go-live makes real.

- AR1 (blocker 1): **Debit list from the database.** `GET /debits` must read stored debits and
  payees, never call the bank per request (N26 allows ~4 fetches/day). The worker is the only
  thing that fetches.
- AR2 (blocker 2): **Authentication on every endpoint**, including `POST /rules` and
  `POST /device-token` (both documented as open "before deployed anywhere"). Remove `/raw`,
  `/aspsps` and the open `/connect`. Owner-only; no multi-user login needed, but a phone handed
  to another account must not keep the old user's alerts.
  **Decided 2026-10-01: a single static API key** (long random string, HTTPS only, kept in Key
  Vault / app settings, never in Git; rotating = change on the server and ship a new app), with
  **one "current user" seam** — all endpoints get the user from one place, so a later move to
  per-user sign-in (OAuth/OIDC) touches that seam plus the app's sign-in, not every endpoint.
  The browser pages (`/connect`, `/callback`) cannot send a header; they need their own
  protection (e.g. a one-time `state`), to be settled in the story.
  **Multi-user preparation, decided 2026-10-01 (cheap now, costly to retrofit):** (1) the seam
  replaces the hardcoded `SeedData.UserId` in the endpoints (`/rules`, `/device-token`) as they are
  touched — for now it always returns the owner; (2) the new consent table carries a `UserId`
  (see AR3); (3) the worker takes the user from the consent, not from the seed constant (see AR4);
  (4) **rule for all new code: no new `SeedData.UserId`.** Deliberately *not* done now: sign-in /
  identity provider, per-user app storage, account creation and management. Before real
  multi-user, still to fix: the payee key is not scoped per user (`TODO.md`).
- AR3 (blocker 3): **Consent store in the database.** A `BankConsents` table replaces
  `consent.local.json`: one row per bank **per user** (key `(UserId, bank)`; today's file has no
  user at all), tokens encrypted with a Key Vault key, session id kept only as a hash elsewhere. A repeat `/connect` for a bank that already has a consent
  **replaces** it — that is the reconnect path (decided 2026-10-01).
- AR4: **Multi-consent worker.** The Ingestion Worker walks every consent each tick, isolates
  per-bank failures, and reads the poll interval per ASPSP. `AccountSyncStates` is already keyed
  `(UserId, AccountKey)`. The worker takes the user from each consent row, not from
  `SeedData.UserId` (about 25 hardcoded uses today in `IngestionRunner`).
- AR5 (blocker 4): **Hosting: Azure Functions, isolated worker model** (decided 2026-10-01; free
  at this scale — Consumption grant 1M executions / 400k GB-s, Flex 250k / 100k; Functions plan
  Consumption vs Flex to be picked in the story). The in-process `BackgroundService` loop becomes
  a **timer trigger** (every 6 h) that runs one existing `IngestionRunner` tick; the API runs in
  the same Functions host (ASP.NET Core integration) or separately — settle in the story. The
  first sync stays API-triggered right after the consent callback. Accepted cost: a cold start of
  roughly 2–7 s on the first request after a quiet period (pushes are not affected, they come
  from the worker). Rejected: App Service F1 (60 CPU min/day, no Always On, unloads after ~20 min
  idle); Container Apps with one always-on replica (~$1–3/month, kept as the fallback).
- AR6 (blocker 4): **Cloud Postgres: Neon free tier** (decided 2026-10-01; compute suspends after
  5 min idle and resumes on its own, no data loss), with the six EF migrations applied
  **manually from the owner's machine at deploy time**, not automatically at startup (a Function
  app can start several instances). Connection string in secrets.
- AR7 (blocker 5): An **HTTPS address** registered as the redirect URL in the Enable Banking
  *Production* application (Sandbox and Production differ), and `/callback` served on it.
  **Decided 2026-10-01: the default `https://<name>.azurewebsites.net/callback`, no custom domain**
  (free, HTTPS included); the exact URL must be fixed before registering — Enable Banking checks
  it strictly (`REDIRECT_URI_NOT_ALLOWED` on a mismatch).
- AR8 (blocker 6): **Switch to the Production application** (`ApplicationId`, `PrivateKeyPath`,
  `RedirectUrl`) in the cloud config. **Decided 2026-10-01: all three consents (N26, DKB,
  ING-DiBa) are granted fresh through the cloud callback** — the old local N26 consent
  (valid to 2026-12-15) is not restored, so the full cloud path (callback, encrypted storage,
  first sync) is exercised for every bank. Each account must first be linked in the Enable
  Banking Control Panel.
- AR9: **Consent validity.** `valid_until` is a flat 90 days today; read each ASPSP's
  `maximum_consent_validity` (`GET /aspsps?country=DE` with the Production application) before
  authorizing, or a bank with a shorter cap rejects `POST /auth`. **Decided 2026-10-01: read the
  list fresh at every `/connect`, no cache** (`/connect` is rare); use the smaller of 90 days and
  the bank's maximum.
- AR10: **Pagination.** Follow `continuation_key`; the fetcher must return the **complete**
  history or throw (first sync is all-or-nothing); fix the fallback-id ordinal colliding across
  pages; send `date_from` so banks do not return arbitrary history lengths. **Decided 2026-10-01:
  the first sync pulls a fixed window of the last 12 months** (an upper limit — a bank may return
  less). This **changes R25** ("as much as the ASPSP returns, no how-far-back setting"), so
  `REQUIREMENTS.md` must be updated first. To check while building: whether each page counts
  against the bank's ~4 fetches/day. Needed before DKB and
  ING first syncs are trustworthy; also fixes `FirstSeenAt` and the Reconnect N/M undercount.
- AR11: **Return path into the app after consent.** DKB and ING switch into their own banking
  apps for authorization; how the user lands back in Greedy Nose (redirect/deep link) is the
  biggest open unknown. **Decided 2026-10-01: option 1 for Tracer 3 — finish in the browser.** The
  owner starts each consent from a browser, approves in the bank's own app, and the `/callback`
  page just says "done"; the owner opens Greedy Nose afterwards. No app work, no deep link (later).
  Each bank's real behaviour is recorded as a finding in `TRACER-03-GO-LIVE.md` on the first
  real consent.
- AR12 (blocker 7): **Release build** of the Android app pointing at the cloud address (no
  `adb reverse`, `USE_BACKEND` on). **Decided 2026-10-01: a sideloaded, self-signed release APK
  installed by the owner, no Play Store.** The signing key stays out of Git like every other
  secret; the API key (AR2) is baked into the build.
- AR13: **Rules reach the backend reliably.** A failed first sync of a rule is never retried and
  rules saved before sync existed never arrived; add a per-launch resync of local rules (the
  backend upsert is idempotent). **Decided 2026-10-01: resend all local rules at app start, plus
  the existing send on every create/edit; no extra resend when the app returns to the
  foreground.** Also: `POST /rules` race and the "Unknown payee" placeholder.
- AR14: **Push quality before shipping.** FCM's default channel shows no heads-up pop-up and a
  generic icon; add a dedicated high-importance channel and a proper small icon. **Decided
  2026-10-01: in Tracer 3** (the alert is the whole product); the force-stop behaviour is a test on
  the owner's phone, recorded as a finding. Check what the
  target phone does when the app is force-stopped.
- AR15: **First-sync visibility.** A consent that dies before the first sync finishes stays
  silent (only a log line). The app also cannot read the connection state at all yet
  (`ConsentStore.GetState` has no endpoint; `/health` still says `connected: true`).
  **Decided 2026-10-01:** (1) a backend status endpoint, protected by the API key, listing per
  bank: connected/expired, valid until, last successful poll — read in a browser or with `curl`,
  no app screen (banner/Settings stay later); (2) fix the silent "dies before the first sync
  finishes" case so it still sends the expiry push.
- AR16: **Hygiene.** Close the old session when a reconnect completes (`DELETE /sessions/{id}`);
  a dead session keeps costing calls every tick. **Decided 2026-10-01: in Tracer 3.** Caveat:
  `DELETE` was only tested in the sandbox; whether it also revokes the consent at a real bank is
  unchecked — record as a finding.
- AR17: **Verification environment.** `dotnet test` is blocked here by a Windows Application
  Control policy (`0x800711C7`) — find a way to run the backend tests before the go-live stories
  rely on them. **Decided 2026-10-01: fix this first — an early story in the first epic.** It is
  the owner's own machine (not managed), so a local fix (adjust the Application Control policy /
  allow the repo build output) should be possible; CI or a container is the fallback.
- AR18: **A failed push is retried, never silently lost.** Today a failed send (`Rejected`/
  `Transient`/no token) still commits the debit as seen, so the alert is never retried (accepted
  on 2026-09-29; **reversed 2026-10-01**). Decided 2026-10-01:
  - The bad debit is **saved and shown in the list** even if its push failed.
  - It gets an explicit **"push owed" mark**, set when the first send fails. It cannot be inferred
    from "bad debit with no `NotificationLog` row" — a debit that turns bad later through a rule
    edit has none either and must never notify (R10a).
  - Every tick, besides fetching, the worker **retries all debits with the mark**, in addition to
    new ones; it never blocks ingestion of other debits.
  - **Failed attempts are logged** (per debit: attempts, last outcome, time).
  - **Limit:** retry for at most 24 h, then give up and log it. *(confirmed 2026-10-01)*
  - **Operator email:** when pushes fail several times in a row (3 attempts, *confirmed
    2026-10-01*), one email goes to the owner (once per incident, not every tick), so a broken push channel is
    noticed. This pulls a small slice of the Health Monitor email (Brevo, free) into Tracer 3; the
    rest of the Health Monitor stays out.
  - Only `TokenNoLongerValid` may prune a token; `Rejected` means a human must look.

### UX Design Requirements

Source is `mocks/` and `REQUIREMENTS.md` ("Known mock gaps"); there is no `DESIGN.md`/
`EXPERIENCE.md`. Go-live touches the UI very little.

- UX-DR1 (mock gap 4, R22): **No literal bank name in app copy.** N26 is hardcoded in eight mocks
  (`01`, `01b`, `01bb`, `01d`, `01e`, `02b`, `06`, `06b`); wherever the connected institution is
  mentioned, use a placeholder. Needed as soon as DKB and ING-DiBa are connected. Applies to the
  ported screens' copy, not only the mocks.
- UX-DR2 (CLAUDE.md, `DataSourceBadge`): In the release build against the cloud backend, the debit
  list must keep telling apart "backend unreachable / not signed in" from "genuinely empty" —
  the device is the worst place to guess.
- UX-DR3 (R12, R17): The push title/body follow R12a and device-locale amounts; with a dedicated
  Android channel (AR14) the look is checked on the device, not assumed.

### FR Coverage Map

FR1: Epic 3 (Epic 2 for the N26 part) — several banks at once, one consent each, one merged list
FR2: Epic 1 (first sync: 12 months, all pages), verified live in Epic 2 (N26) and Epic 3 (DKB, ING)
FR3: Epic 1 — debit list from stored data
FR4: Epic 2 — new booked bad debit → one push, verified live (baseline, built)
FR5: Epic 2 — push wording, plus the dedicated Android channel (baseline, built)
FR6: Epic 2 — rules reach the backend (resync at app start)
FR7: Epic 4 — dead-consent push, including the "dies before the first sync" gap
FR8: Epic 3 — reconnect summary per bank (baseline, built)
FR9: Epic 3 — EUR check against the real banks (baseline, built)
FR10: Epic 3 — no literal bank name in app copy (UX-DR1)

Technical (AR) and non-functional (NFR) requirements are carried by the stories that deliver them:
AR1, AR2, AR3, AR10, AR17 → Epic 1; AR5, AR6, AR7, AR8, AR12, AR13, AR14, AR18 → Epic 2;
AR4, AR9, AR11 → Epic 3; AR15, AR16 → Epic 4. UX-DR1 → Epic 3, UX-DR2 → Epic 2,
UX-DR3 → Epic 2. NFR1–NFR10 apply across all epics and become acceptance criteria.

## Epic List

Approved 2026-10-01. Order matters: Epic 2 is the thin end-to-end slice with N26 only (the
proven bank); Epic 3 adds the two unknown banks, still inside Tracer 3.

### Epic 1: A backend I can trust to deploy
The owner can rely on the backend to guard his data and to serve the debit list from stored data;
the tests run, so every later story is verifiable. Epic 1 is the most technical epic: a backend
that must be safe before deployment is the real value here.
**FRs covered:** FR2 (first sync: 12 months, all pages), FR3
**Also:** AR1, AR2, AR3, AR10, AR17

### Epic 2: N26 live on Azure, end to end
A real N26 bad charge becomes a push on the owner's phone, and a failed push is never lost — with the backend on Azure and the phone
not attached to the laptop.
**FRs covered:** FR4, FR5, FR6 (and FR1/FR2 for N26)
**Also:** AR5, AR6, AR7, AR8, AR12, AR13, AR14, AR18, UX-DR2, UX-DR3

### Epic 3: DKB and ING-DiBa watched too
All three banks feed one debit list, and one bank failing never stops the others.
**FRs covered:** FR1, FR8, FR9, FR10
**Also:** AR4, AR9, AR11, UX-DR1

### Epic 4: I'm never silently left without alerts
A dead connection is noticed, not silent, and each bank's state can be seen.
**FRs covered:** FR7
**Also:** AR15, AR16

## Epic 1: A backend I can trust to deploy

The owner can rely on the backend to guard his data and to serve the debit list from stored data;
the tests run, so every later story is verifiable. (FR2, FR3; AR1, AR2, AR3, AR10, AR17.)
Epic 1 deliberately adds nothing for the app's user interface beyond the API key header.

### Story 1.1: Backend tests run on my machine

As the owner,
I want `dotnet test` to run on my machine,
So that every later backend story is verified by real tests.

**Implementation note:** this is a machine/toolchain fix, not product code — the main session
does it (see `AGENTS.md`, Policy), not `coder-backend`.

**Acceptance Criteria:**

**Given** a freshly built backend
**When** I run `dotnet test GreedyNose.Api.Tests`
**Then** the tests load and execute — no `FileLoadException` / `0x800711C7` — and the result is
pass/fail per test
**And** the baseline (how many tests pass today) is recorded in `TRACER-03-GO-LIVE.md`

**Given** I change code and rebuild
**When** I run the tests again
**Then** they still run (the fix does not depend on a one-time exception for one specific DLL)

**Given** the fix is a Windows Application Control setting or an allowed path
**Then** it is limited to what the repository's build output needs, not a global weakening
**And** it is documented in `AGENTS.md` ("Running and verifying") so a new machine can repeat it

**Given** no local fix works
**Then** the fallback (CI or a container) is set up and documented instead

**As built (2026-10-01):** the block is Smart App Control (state 1), not a managed policy, and
the owner chose not to switch it off. The tests run in a Linux container (`dotnet/sdk:10.0`, the
repo mounted read-only, NuGet cached in a named volume): **310 passed, 0 failed, about 11 s**,
repeatable after every change. The command is in `AGENTS.md`; `dotnet build` still works on the
host. Coders and reviewers use the same command for backend tests.

### Story 1.2: The app sends the API key with every backend call

As the owner,
I want the app to send my API key with every backend call,
So that it keeps working once the backend starts requiring the key (Story 1.3).

**Acceptance Criteria:**

**Given** backend mode is on and the key is in a local, gitignored config file
**When** the app calls `/debits`, `/rules` or `/device-token`
**Then** each request carries the key in a header

**Given** backend mode is on and the key is missing
**When** the app starts
**Then** the data-source badge says the backend is not configured — never a silently empty list
(UX-DR2)

**Given** the repository
**Then** the key is in no tracked file; only a template file names the setting
**And** fixture mode behaves exactly as before

**And** jest tests cover the header on each call and the missing-key state; `tsc`, `eslint` and
`jest` pass

*(The backend ignores the header until Story 1.3, so nothing breaks in between.)*

**As built (2026-10-01):** header `Authorization: Bearer <key>` (Story 1.3 must accept exactly
this; it also lets a later per-user token reuse the header). The key lives in the gitignored
`app/src/data/apiKey.local.ts`, with the committed template `apiKey.local.example.ts`; no new
dependency. `backendAuth.ts` reads it through a guarded `require`, and `metro.config.js` sets
`allowOptionalDependencies: true` — verified with a real `react-native bundle`: it builds without
the file, and with a throwaway key file the key is in the bundle (file deleted afterwards).
Missing key: no request is sent, the feed shows "API key not configured", the rules and
device-token syncs warn and skip, and a rule is still saved locally. 211 jest tests (was 200),
`tsc` clean, `eslint` only the 3 old warnings. One review pass, no MUST/SHOULD FIX. Known and
accepted: the key sits in the JS bundle, so a release APK carries it (rotate by shipping a new
APK). Not verified end to end against the backend — Story 1.3 is not built yet.

### Story 1.3: The backend requires the API key and drops the debug endpoints

As the owner,
I want every backend data endpoint to require my API key,
So that nobody else can read my debits or change my rules.

**Acceptance Criteria:**

**Given** a request to any endpoint except `/connect` and `/callback` (Story 1.4), including `/health`, with no key or a wrong key
**When** it is handled
**Then** the answer is 401 and no data or connection state is revealed

**Given** a request with the right key
**When** it is handled
**Then** the endpoint behaves exactly as before

**Given** `/raw` and `/aspsps`
**Then** they no longer exist (404) and their tests are removed

**Given** `/connect` and `/callback`
**Then** they are handled by Story 1.4; until then they keep working as today (the one
unprotected pair, closed right after)

**Given** the key
**Then** it is compared in constant time, never logged, never in `appsettings.json`, and read
from configuration (user secrets locally)
**And** tests cover 401 for every other endpoint and success with the key (NFR7, NFR8)

**As built (2026-10-01):** `Auth/ApiKeyMiddleware` (registered first in `Program.cs`) +
`Auth/ApiKeyOptions` (`Api:Key`, fail closed: missing, shorter than 32 characters or edge
whitespace stops the startup). `Authorization: Bearer <key>` (scheme case-insensitive), SHA-256 +
`FixedTimeEquals`, 401 with `WWW-Authenticate: Bearer` and an empty body; unknown paths also 401.
Exempt only `/connect` and `/callback` (exact segment match, so `/connectx` is not; removed in
Story 1.4). `/raw` and `/aspsps` are gone (and `EnableBankingOptions.RawDumpDirectory`, whose only
reader was `/raw`); `/health` needs the key. A rejection logs only a sanitized, truncated path.
346 tests (310 + 36 new, in the container), host `dotnet build` clean, one review pass (no MUST
FIX; the three cheap items fixed). **Verified live** in a container against the dev Postgres: 18
curl checks (no/wrong/right key, lower-case `bearer`, key without scheme, `/raw` and `/aspsps` →
404, `/callback` exempt, `/CONNECTX` 401, POST `/rules` and `/device-token` with an empty body →
400 so nothing was written), the key in no log and no tracked file, and the backend refusing to
start with a short key. **Finding:** Smart App Control also blocks *running* the backend on the
host, so it now runs in a container (`tools/run-backend-container.sh`, see `AGENTS.md`).

### Story 1.4: Starting a consent is protected

As the owner,
I want starting and finishing a bank consent to be protected too,
So that nobody else can connect a bank account to my backend or replay a consent link.

**Acceptance Criteria:**

**Given** the owner starts a consent from a browser
**When** `/connect` is opened
**Then** it works without putting the API key into a URL (the mechanism — e.g. a one-time link
issued by an authenticated call — is settled in plan mode)
**And** it issues a one-time `state` bound to that bank

**Given** `/callback` is called
**When** the `state` is missing, unknown, already used or expired
**Then** it is rejected and no consent is stored
**And** a valid `state` completes the consent once; replaying it is rejected

**Given** the backend restarts, or several instances run (Azure Functions can stop when idle and
scale out)
**Then** an issued `state` is still valid and a used one is still refused — it is stored in the
database, not in memory

**Given** a consent request without the key and without a valid one-time link
**Then** it is rejected and nothing is started at the bank

**And** the `state` is never logged; tests cover missing, wrong, used and expired `state`, and the
happy path (NFR7, NFR8)

**As built (2026-10-01):** `POST /connect-links` (behind the API key; `/connect-links` is a
different path segment, so the `/connect` exemption does not cover it) returns `{ url }` built from
the origin of `RedirectUrl`; `GET /connect?token=` spends the link once, mints the `state` in the
same write and redirects to the bank; `GET /callback` spends the `state` *before* trading the code.
New table `PendingConsents` (migration `AddPendingConsents`, applied manually to the dev DB): only
SHA-256 hashes of token and state (unique indexes), `Version` concurrency token, 30 minute lifetime,
cascade FK to `Users`; expired rows are deleted when a new link is created. Failures: 400 (bad,
used, expired or lost-race secret — indistinguishable), 502 (Enable Banking failed, a network
fault, or the consent could not be stored); the bank body, session id, IBANs and any `?error=`
text are never shown; both pages send `Cache-Control: no-store` and `Referrer-Policy: no-referrer`.
`ConsentStore` lost its in-memory pending state and the premature `AspspName` overwrite;
`Complete(...)` takes the bank from the record. Endpoint code moved to `Consent/ConsentEndpoints.cs`.
398 tests (346 + 52 new, in the container), two review passes (no MUST FIX; both SHOULD FIX
rounds applied). **Verified live** against the dev Postgres: no key → 401, link → 302 once then 400,
no/garbage token → 400, unknown state → 400, `?error=<script>` not echoed, headers present, raw
token not in the DB, and **a real race — two parallel requests on one link gave exactly one 302 and
one 400**. Not run live: a completed callback (needs a real bank code) and the log grep (my check
failed; the unit tests assert it). The helper is `tools/connect-bank.sh` (`GN_NO_OPEN=1` skips the
browser). Deliberately not done: rate limiting, and an `https` check on the bank's redirect URL
(see `TODO.md`).

### Story 1.5: One "current user" lookup

As the owner,
I want every endpoint to get the user from one place,
So that moving to per-user sign-in later changes one spot, not every endpoint.

**Acceptance Criteria:**

**Given** `/rules`, `/device-token` and `/debits`
**When** they need the user
**Then** they ask one current-user service, which returns the owner for now
**And** none of them references `SeedData.UserId` directly

**Given** a test double that returns a different user id
**When** a rule is saved and a device token registered
**Then** both rows belong to that user (proves the seam is real)

**And** the existing tests still pass; behaviour is unchanged for the owner
**And** the ingestion worker's own `SeedData.UserId` uses stay until Epic 3 (AR4); no new
`SeedData.UserId` appears anywhere

**As built (2026-10-05):** `Auth/ICurrentUser` (`Guid UserId`) and `Auth/OwnerCurrentUser`
(returns `SeedData.UserId`), registered scoped in `Program.cs`. `RulesEndpoint` and
`DeviceTokenEndpoint` take it as a parameter and no longer name `SeedData.UserId`. **Deviation
from the AC wording:** `/debits` is untouched — it uses no user today (it still calls the bank
live); Story 1.6 injects the service when it reads stored data (noted as a comment on bead
`gn-ywx.6`). Also still on `SeedData.UserId`, deliberately: `ConsentEndpoints` (browser pages, no
request user), `IngestionRunner` (Epic 3), the model seed. 400 tests (398 + 2 new, in the
container), one review pass, no MUST FIX. **Gap, accepted:** the "different user" test covers
`/rules` only — the device-token refresh uses `ExecuteUpdateAsync`, which EF InMemory lacks, so
that endpoint needs a Postgres-backed test (bead `gn-vgm`); the refresh is keyed on `Token` only
(bead `gn-oue`). Not run live: no behaviour change.

### Story 1.6: The debit list comes from stored data

As the owner,
I want the debit list to come from what the backend has already stored,
So that opening the app never spends the bank's few daily fetches (FR3, AR1).

**Acceptance Criteria:**

**Given** stored debits and payees
**When** the app calls `GET /debits`
**Then** it gets them in the existing JSON shape (the contract `app/src/domain/model.ts` and
`backendFeed.ts` validate today), newest first, with no good/bad classification (R6)

**Given** a fetcher that throws if it is ever called
**When** `GET /debits` is served
**Then** the request still succeeds — the endpoint makes no bank call

**Given** the worker stores a new debit
**When** `GET /debits` is called next
**Then** the new debit appears without any bank call

**Given** no debits have been stored yet (before the first sync)
**When** `GET /debits` is called
**Then** it answers 200 with an empty list, not an error

**Given** accounts of several banks (R22a)
**Then** the list merges all of the user's accounts, ordered by date
**And** incoming money is never in it (R2a)
**And** the app's pull-to-refresh only re-reads this endpoint

**As built (2026-10-05):** `Debits/DebitsEndpoint.HandleAsync(ICurrentUser, GreedyNoseDbContext,
CancellationToken)` replaces the inline lambda in `Program.cs`. It reads the user's stored debits
(all accounts, newest first, `Id` as tie-break) and the payees they reference, and returns the
unchanged `DebitsDto` shape; the timestamp format is one shared constant
(`TransactionMapper.TimestampFormat`). No bank, consent or fetcher dependency; nothing stored → 200
with empty lists (the old 409 is gone). 409 tests (400 + 9 new, in the container), one review pass,
no MUST FIX. **Verified live** (container backend, dev Postgres, Sandbox application): `/debits` →
200, 94 debits and 44 payees with the same field names as before, **zero** Enable Banking requests
caused by it (request count 1 → 1 around the call; the one request is the worker's startup tick),
401 without the key. **Not done, recorded as beads:** the app's pull-to-refresh (the app has no
`RefreshControl` yet, `gn-hw1`), `skipped` is always 0 because stored data cannot recount refused
charges (`gn-dyv`), no paging (`gn-w67`).

### Story 1.7: Consents live in an encrypted database table

As the owner,
I want my bank consents kept in the database, encrypted,
So that the backend can run in the cloud and no consent sits in a loose file (AR3).

**Implementation note:** the encryption key comes from configuration here (user secrets
locally); Key Vault is wired in Epic 2.

**Acceptance Criteria:**

**Given** the migration is applied
**Then** a `BankConsents` table exists, one row per `(UserId, bank)`
**And** the session id and consent payload are encrypted at rest — never plaintext in the
database or in logs (NFR7)

**Given** consents for two banks
**Then** there are two rows and reading or replacing one never touches the other

**Given** a consent already exists for a bank
**When** `/callback` completes a new consent for it
**Then** the row is replaced (the reconnect path); the old session is *not* closed here (Epic 4,
AR16)

**Given** the backend restarts
**Then** the consent state — including its expiry, and `Expired` for a dead one — is restored
from the database
**And** `consent.local.json` is no longer read or written

**Given** the wrong encryption key
**Then** the failure is loud and clear, never a silently empty store

**And** the ingestion worker behaves as before with one consent, and all ingestion tests pass
(multi-consent is Epic 3)

**As built (2026-10-07):** table `BankConsents` (PK `UserId` + `AspspName`, FK to `Users` with cascade),
`SessionIdCipher` and `PayloadCipher` as `bytea`. `ConsentCipher` = AES-256-GCM, random 12-byte nonce
per value, layout `[version][nonce][tag][ciphertext]`, associated data `{user}|{bank}|{column}` (a value
moved to another row or column fails to decrypt). Key = `Consent:EncryptionKey` (base64, 32 bytes),
validated at startup; Key Vault comes in Epic 2. `ConsentStore` keeps its in-memory surface but persists
through `IConsentPersistence`/`DbConsentPersistence`: `CompleteAsync` saves first and updates memory only if
the save worked (a failed save throws and the callback shows its generic 502 page, nothing leaked);
`RestoreAsync` loads the most recently connected row, an expired consent still loads as `Expired`, and a
wrong key stops the backend with a clear `ConsentDecryptionException` — never a silently empty store.
`consent.local.json` is no longer read or written and `ConsentFilePath` is gone. **Decision:** the old
consent file is not migrated (N26 is connected fresh through the cloud in 2.7; the local Sandbox consent
was redone once). 465 tests (430 + 35), one review pass, no MUST FIX. **Verified live** (Sandbox, dev
Postgres, container): migration applied; the backend starts with no consent from the empty table; after a
real Mock ASPSP consent in the browser there is one row with 65-byte and 445-byte ciphertexts and no
plaintext markers in them, the old file untouched; a restart restores the consent as Active with the
expiry the bank gave; a start with a wrong key fails loudly at startup; the following Reconnect tick found
0 new debits and sent no push. **Open (beads):** `gn-7h4` (Epic 2: startup retry on transient DB errors,
run migrations before the first start, key rotation needs a re-consent), `gn-uxv` (Epic 3: normalize the
bank name, walk all rows).

### Story 1.8: The first sync pulls 12 months, all pages

As the owner,
I want the first sync to pull the last 12 months completely,
So that the history is whole and the same for every bank (FR2, AR10, R25).

**Acceptance Criteria:**

**Given** a first sync
**When** the fetcher runs
**Then** it sends `date_from` = today minus 12 months and follows `continuation_key` until no page
is left

**Given** a page fails midway
**Then** the fetcher throws and nothing is stored (the first sync stays all-or-nothing); the next
tick starts it again

**Given** a same-day, same-amount, same-payee group split across a page boundary
**Then** the fallback debit ids do not collide and no debit is lost or duplicated (ordinal across
the whole result)

**Given** a bank returns less than 12 months
**Then** the sync succeeds with what it returned

**Given** a bank answers `ASPSP_RATE_LIMIT_EXCEEDED` mid-sync
**Then** the sync fails as a whole and retries at the next tick

**Given** any sync
**Then** it logs how many bank requests it made (answers the open question whether each page
counts against the ~4 fetches per day)
**And** `Payee.FirstSeenAt` uses the earliest date across all pages
**And** steady-state and reconnect ticks also follow pagination, so a long gap is not undercounted
(the exact date window for those is settled in plan mode)

**As built (2026-10-05):** `IDebitsFetcher.FetchAsync(account, dateFrom, ct)`. The runner
decides the mode **once** before fetching (`DetermineMode`) and computes `dateFrom` with the pure
`IngestionRunner.ComputeDateFrom`: first sync = UTC today minus 12 months; steady and reconnect =
the date of the newest stored debit of the account (else the first-sync date) minus 7 days of
overlap, never later than today and never earlier than 12 months back. **Every tick** — not only the
first sync — sends `date_from`, follows `continuation_key` (escaped) to the last page and maps all
pages in **one** `TransactionMapper.Map` call (ordinals run across pages); a repeated-key guard and
a 200-page cap stop a runaway loop, and any failed page, incl. `ASPSP_RATE_LIMIT_EXCEEDED`, throws
so nothing is stored. Every tick logs its page count. The overlap re-fetches known debits; the id
dedup makes that harmless (tests: one push, N/M over the new debits only). `Payee.FirstSeenAt`
already takes the earliest date of the batch. 430 tests (400 + 30), three review passes, no MUST FIX.
**Verified live** (Sandbox / Mock ASPSP, dev Postgres): first sync = 4 pages from 2025-10-05, stored
198 further debits silently (94 → 292); a second first sync (row removed again) fetched the same 4
pages and stored 0 (ids stable, no duplicates); a normal tick = 1 page from the newest debit minus 7
days, no push, no new rows. **History of the design:** an earlier build paged only on the first
sync (to spare the ~4 fetches/day quota); the owner decided on 2026-10-05 that every tick pages from
the last fetch on, so a backlog is never undercounted. **Open before go-live (blocks 2.11, 3.5, 3.6):**
`gn-xeq` and `gn-6k5` — does each page count against the bank quota, does a bank reject an old
`date_from`, does the bank filter on the same day as the mapper.

## Epic 2: N26 live on Azure, end to end
A real N26 bad charge becomes a push on the owner's phone, and a failed push is never lost — with the backend on Azure and the phone
not attached to the laptop.
**FRs covered:** FR4, FR5, FR6 (and FR1/FR2 for N26)
**Also:** AR5, AR6, AR7, AR8, AR12, AR13, AR14, AR18, UX-DR2, UX-DR3

### Epic 3: DKB and ING-DiBa watched too
All three banks feed one debit list, and one bank failing never stops the others.
**FRs covered:** FR1, FR8, FR9, FR10
**Also:** AR4, AR9, AR11, UX-DR1

### Epic 4: I'm never silently left without alerts
A dead connection is noticed, not silent, and each bank's state can be seen.
**FRs covered:** FR7
**Also:** AR15, AR16

## Epic 1: A backend I can trust to deploy

The owner can rely on the backend to guard his data and to serve the debit list from stored data;
the tests run, so every later story is verifiable. (FR2, FR3; AR1, AR2, AR3, AR10, AR17.)
Epic 1 deliberately adds nothing for the app's user interface beyond the API key header.

### Story 1.1: Backend tests run on my machine

As the owner,
I want `dotnet test` to run on my machine,
So that every later backend story is verified by real tests.

**Implementation note:** this is a machine/toolchain fix, not product code — the main session
does it (see `AGENTS.md`, Policy), not `coder-backend`.

**Acceptance Criteria:**

**Given** a freshly built backend
**When** I run `dotnet test GreedyNose.Api.Tests`
**Then** the tests load and execute — no `FileLoadException` / `0x800711C7` — and the result is
pass/fail per test
**And** the baseline (how many tests pass today) is recorded in `TRACER-03-GO-LIVE.md`

**Given** I change code and rebuild
**When** I run the tests again
**Then** they still run (the fix does not depend on a one-time exception for one specific DLL)

**Given** the fix is a Windows Application Control setting or an allowed path
**Then** it is limited to what the repository's build output needs, not a global weakening
**And** it is documented in `AGENTS.md` ("Running and verifying") so a new machine can repeat it

**Given** no local fix works
**Then** the fallback (CI or a container) is set up and documented instead

**As built (2026-10-01):** the block is Smart App Control (state 1), not a managed policy, and
the owner chose not to switch it off. The tests run in a Linux container (`dotnet/sdk:10.0`, the
repo mounted read-only, NuGet cached in a named volume): **310 passed, 0 failed, about 11 s**,
repeatable after every change. The command is in `AGENTS.md`; `dotnet build` still works on the
host. Coders and reviewers use the same command for backend tests.

### Story 1.2: The app sends the API key with every backend call

As the owner,
I want the app to send my API key with every backend call,
So that it keeps working once the backend starts requiring the key (Story 1.3).

**Acceptance Criteria:**

**Given** backend mode is on and the key is in a local, gitignored config file
**When** the app calls `/debits`, `/rules` or `/device-token`
**Then** each request carries the key in a header

**Given** backend mode is on and the key is missing
**When** the app starts
**Then** the data-source badge says the backend is not configured — never a silently empty list
(UX-DR2)

**Given** the repository
**Then** the key is in no tracked file; only a template file names the setting
**And** fixture mode behaves exactly as before

**And** jest tests cover the header on each call and the missing-key state; `tsc`, `eslint` and
`jest` pass

*(The backend ignores the header until Story 1.3, so nothing breaks in between.)*

**As built (2026-10-01):** header `Authorization: Bearer <key>` (Story 1.3 must accept exactly
this; it also lets a later per-user token reuse the header). The key lives in the gitignored
`app/src/data/apiKey.local.ts`, with the committed template `apiKey.local.example.ts`; no new
dependency. `backendAuth.ts` reads it through a guarded `require`, and `metro.config.js` sets
`allowOptionalDependencies: true` — verified with a real `react-native bundle`: it builds without
the file, and with a throwaway key file the key is in the bundle (file deleted afterwards).
Missing key: no request is sent, the feed shows "API key not configured", the rules and
device-token syncs warn and skip, and a rule is still saved locally. 211 jest tests (was 200),
`tsc` clean, `eslint` only the 3 old warnings. One review pass, no MUST/SHOULD FIX. Known and
accepted: the key sits in the JS bundle, so a release APK carries it (rotate by shipping a new
APK). Not verified end to end against the backend — Story 1.3 is not built yet.

### Story 1.3: The backend requires the API key and drops the debug endpoints

As the owner,
I want every backend data endpoint to require my API key,
So that nobody else can read my debits or change my rules.

**Acceptance Criteria:**

**Given** a request to any endpoint except `/connect` and `/callback` (Story 1.4), including `/health`, with no key or a wrong key
**When** it is handled
**Then** the answer is 401 and no data or connection state is revealed

**Given** a request with the right key
**When** it is handled
**Then** the endpoint behaves exactly as before

**Given** `/raw` and `/aspsps`
**Then** they no longer exist (404) and their tests are removed

**Given** `/connect` and `/callback`
**Then** they are handled by Story 1.4; until then they keep working as today (the one
unprotected pair, closed right after)

**Given** the key
**Then** it is compared in constant time, never logged, never in `appsettings.json`, and read
from configuration (user secrets locally)
**And** tests cover 401 for every other endpoint and success with the key (NFR7, NFR8)

**As built (2026-10-01):** `Auth/ApiKeyMiddleware` (registered first in `Program.cs`) +
`Auth/ApiKeyOptions` (`Api:Key`, fail closed: missing, shorter than 32 characters or edge
whitespace stops the startup). `Authorization: Bearer <key>` (scheme case-insensitive), SHA-256 +
`FixedTimeEquals`, 401 with `WWW-Authenticate: Bearer` and an empty body; unknown paths also 401.
Exempt only `/connect` and `/callback` (exact segment match, so `/connectx` is not; removed in
Story 1.4). `/raw` and `/aspsps` are gone (and `EnableBankingOptions.RawDumpDirectory`, whose only
reader was `/raw`); `/health` needs the key. A rejection logs only a sanitized, truncated path.
346 tests (310 + 36 new, in the container), host `dotnet build` clean, one review pass (no MUST
FIX; the three cheap items fixed). **Verified live** in a container against the dev Postgres: 18
curl checks (no/wrong/right key, lower-case `bearer`, key without scheme, `/raw` and `/aspsps` →
404, `/callback` exempt, `/CONNECTX` 401, POST `/rules` and `/device-token` with an empty body →
400 so nothing was written), the key in no log and no tracked file, and the backend refusing to
start with a short key. **Finding:** Smart App Control also blocks *running* the backend on the
host, so it now runs in a container (`tools/run-backend-container.sh`, see `AGENTS.md`).

### Story 1.4: Starting a consent is protected

As the owner,
I want starting and finishing a bank consent to be protected too,
So that nobody else can connect a bank account to my backend or replay a consent link.

**Acceptance Criteria:**

**Given** the owner starts a consent from a browser
**When** `/connect` is opened
**Then** it works without putting the API key into a URL (the mechanism — e.g. a one-time link
issued by an authenticated call — is settled in plan mode)
**And** it issues a one-time `state` bound to that bank

**Given** `/callback` is called
**When** the `state` is missing, unknown, already used or expired
**Then** it is rejected and no consent is stored
**And** a valid `state` completes the consent once; replaying it is rejected

**Given** the backend restarts, or several instances run (Azure Functions can stop when idle and
scale out)
**Then** an issued `state` is still valid and a used one is still refused — it is stored in the
database, not in memory

**Given** a consent request without the key and without a valid one-time link
**Then** it is rejected and nothing is started at the bank

**And** the `state` is never logged; tests cover missing, wrong, used and expired `state`, and the
happy path (NFR7, NFR8)

**As built (2026-10-01):** `POST /connect-links` (behind the API key; `/connect-links` is a
different path segment, so the `/connect` exemption does not cover it) returns `{ url }` built from
the origin of `RedirectUrl`; `GET /connect?token=` spends the link once, mints the `state` in the
same write and redirects to the bank; `GET /callback` spends the `state` *before* trading the code.
New table `PendingConsents` (migration `AddPendingConsents`, applied manually to the dev DB): only
SHA-256 hashes of token and state (unique indexes), `Version` concurrency token, 30 minute lifetime,
cascade FK to `Users`; expired rows are deleted when a new link is created. Failures: 400 (bad,
used, expired or lost-race secret — indistinguishable), 502 (Enable Banking failed, a network
fault, or the consent could not be stored); the bank body, session id, IBANs and any `?error=`
text are never shown; both pages send `Cache-Control: no-store` and `Referrer-Policy: no-referrer`.
`ConsentStore` lost its in-memory pending state and the premature `AspspName` overwrite;
`Complete(...)` takes the bank from the record. Endpoint code moved to `Consent/ConsentEndpoints.cs`.
398 tests (346 + 52 new, in the container), two review passes (no MUST FIX; both SHOULD FIX
rounds applied). **Verified live** against the dev Postgres: no key → 401, link → 302 once then 400,
no/garbage token → 400, unknown state → 400, `?error=<script>` not echoed, headers present, raw
token not in the DB, and **a real race — two parallel requests on one link gave exactly one 302 and
one 400**. Not run live: a completed callback (needs a real bank code) and the log grep (my check
failed; the unit tests assert it). The helper is `tools/connect-bank.sh` (`GN_NO_OPEN=1` skips the
browser). Deliberately not done: rate limiting, and an `https` check on the bank's redirect URL
(see `TODO.md`).

### Story 1.5: One "current user" lookup

As the owner,
I want every endpoint to get the user from one place,
So that moving to per-user sign-in later changes one spot, not every endpoint.

**Acceptance Criteria:**

**Given** `/rules`, `/device-token` and `/debits`
**When** they need the user
**Then** they ask one current-user service, which returns the owner for now
**And** none of them references `SeedData.UserId` directly

**Given** a test double that returns a different user id
**When** a rule is saved and a device token registered
**Then** both rows belong to that user (proves the seam is real)

**And** the existing tests still pass; behaviour is unchanged for the owner
**And** the ingestion worker's own `SeedData.UserId` uses stay until Epic 3 (AR4); no new
`SeedData.UserId` appears anywhere

**As built (2026-10-05):** `Auth/ICurrentUser` (`Guid UserId`) and `Auth/OwnerCurrentUser`
(returns `SeedData.UserId`), registered scoped in `Program.cs`. `RulesEndpoint` and
`DeviceTokenEndpoint` take it as a parameter and no longer name `SeedData.UserId`. **Deviation
from the AC wording:** `/debits` is untouched — it uses no user today (it still calls the bank
live); Story 1.6 injects the service when it reads stored data (noted as a comment on bead
`gn-ywx.6`). Also still on `SeedData.UserId`, deliberately: `ConsentEndpoints` (browser pages, no
request user), `IngestionRunner` (Epic 3), the model seed. 400 tests (398 + 2 new, in the
container), one review pass, no MUST FIX. **Gap, accepted:** the "different user" test covers
`/rules` only — the device-token refresh uses `ExecuteUpdateAsync`, which EF InMemory lacks, so
that endpoint needs a Postgres-backed test (bead `gn-vgm`); the refresh is keyed on `Token` only
(bead `gn-oue`). Not run live: no behaviour change.

### Story 1.6: The debit list comes from stored data

As the owner,
I want the debit list to come from what the backend has already stored,
So that opening the app never spends the bank's few daily fetches (FR3, AR1).

**Acceptance Criteria:**

**Given** stored debits and payees
**When** the app calls `GET /debits`
**Then** it gets them in the existing JSON shape (the contract `app/src/domain/model.ts` and
`backendFeed.ts` validate today), newest first, with no good/bad classification (R6)

**Given** a fetcher that throws if it is ever called
**When** `GET /debits` is served
**Then** the request still succeeds — the endpoint makes no bank call

**Given** the worker stores a new debit
**When** `GET /debits` is called next
**Then** the new debit appears without any bank call

**Given** no debits have been stored yet (before the first sync)
**When** `GET /debits` is called
**Then** it answers 200 with an empty list, not an error

**Given** accounts of several banks (R22a)
**Then** the list merges all of the user's accounts, ordered by date
**And** incoming money is never in it (R2a)
**And** the app's pull-to-refresh only re-reads this endpoint

**As built (2026-10-05):** `Debits/DebitsEndpoint.HandleAsync(ICurrentUser, GreedyNoseDbContext,
CancellationToken)` replaces the inline lambda in `Program.cs`. It reads the user's stored debits
(all accounts, newest first, `Id` as tie-break) and the payees they reference, and returns the
unchanged `DebitsDto` shape; the timestamp format is one shared constant
(`TransactionMapper.TimestampFormat`). No bank, consent or fetcher dependency; nothing stored → 200
with empty lists (the old 409 is gone). 409 tests (400 + 9 new, in the container), one review pass,
no MUST FIX. **Verified live** (container backend, dev Postgres, Sandbox application): `/debits` →
200, 94 debits and 44 payees with the same field names as before, **zero** Enable Banking requests
caused by it (request count 1 → 1 around the call; the one request is the worker's startup tick),
401 without the key. **Not done, recorded as beads:** the app's pull-to-refresh (the app has no
`RefreshControl` yet, `gn-hw1`), `skipped` is always 0 because stored data cannot recount refused
charges (`gn-dyv`), no paging (`gn-w67`).

### Story 1.7: Consents live in an encrypted database table

As the owner,
I want my bank consents kept in the database, encrypted,
So that the backend can run in the cloud and no consent sits in a loose file (AR3).

**Implementation note:** the encryption key comes from configuration here (user secrets
locally); Key Vault is wired in Epic 2.

**Acceptance Criteria:**

**Given** the migration is applied
**Then** a `BankConsents` table exists, one row per `(UserId, bank)`
**And** the session id and consent payload are encrypted at rest — never plaintext in the
database or in logs (NFR7)

**Given** consents for two banks
**Then** there are two rows and reading or replacing one never touches the other

**Given** a consent already exists for a bank
**When** `/callback` completes a new consent for it
**Then** the row is replaced (the reconnect path); the old session is *not* closed here (Epic 4,
AR16)

**Given** the backend restarts
**Then** the consent state — including its expiry, and `Expired` for a dead one — is restored
from the database
**And** `consent.local.json` is no longer read or written

**Given** the wrong encryption key
**Then** the failure is loud and clear, never a silently empty store

**And** the ingestion worker behaves as before with one consent, and all ingestion tests pass
(multi-consent is Epic 3)

### Story 1.8: The first sync pulls 12 months, all pages

As the owner,
I want the first sync to pull the last 12 months completely,
So that the history is whole and the same for every bank (FR2, AR10, R25).

**Acceptance Criteria:**

**Given** a first sync
**When** the fetcher runs
**Then** it sends `date_from` = today minus 12 months and follows `continuation_key` until no page
is left

**Given** a page fails midway
**Then** the fetcher throws and nothing is stored (the first sync stays all-or-nothing); the next
tick starts it again

**Given** a same-day, same-amount, same-payee group split across a page boundary
**Then** the fallback debit ids do not collide and no debit is lost or duplicated (ordinal across
the whole result)

**Given** a bank returns less than 12 months
**Then** the sync succeeds with what it returned

**Given** a bank answers `ASPSP_RATE_LIMIT_EXCEEDED` mid-sync
**Then** the sync fails as a whole and retries at the next tick

**Given** any sync
**Then** it logs how many bank requests it made (answers the open question whether each page
counts against the ~4 fetches per day)
**And** `Payee.FirstSeenAt` uses the earliest date across all pages
**And** steady-state and reconnect ticks also follow pagination, so a long gap is not undercounted
(the exact date window for those is settled in plan mode)

**As built (2026-10-05):** `IDebitsFetcher.FetchAsync(account, firstSync, ct)`; the runner decides
the mode **once** before fetching (`DetermineMode`, same rule as before) and uses that value for
the fetch and the commit. `EnableBankingDebitsFetcher`: a first sync sends `date_from` = UTC date
minus 12 months, follows `continuation_key` (escaped) until none, merges all pages and maps them in
**one** `TransactionMapper.Map` call (ordinals run across pages), with a repeated-key guard and a
200-page cap; any failed page, incl. `ASPSP_RATE_LIMIT_EXCEEDED`, throws so nothing is stored. Every
tick logs how many pages it fetched. `Payee.FirstSeenAt` already takes the earliest date of the
batch, so it covers all pages. 424 tests (409 + 15), two review passes (the second only on the
mode change), no MUST FIX. **Verified live** (Sandbox / Mock ASPSP, dev Postgres): with the
account's sync row removed the first sync fetched 4 pages from 2025-10-05, stored 198 further
debits silently (94 → 292, no duplicates), 0 pushes, the row was recreated (original row restored
afterwards). **Deviation from the AC, decided in plan mode and now to be confirmed by the owner:**
steady and reconnect ticks do **not** page — they stay one request with no `date_from`, because the
bank allows ~4 fetches a day and each page is a request; a reconnect after a long gap therefore
still undercounts (see `gn-mm1`, `gn-40m`). **Open before go-live:** `gn-xeq` (P1, blocks Story
2.11) — does a real 12-month first sync (here 4 pages) fit N26's daily quota?

## Epic 2: N26 live on Azure, end to end

A real N26 bad charge becomes a push on the owner's phone, and a failed push is never lost — with the backend on Azure and the phone
not attached to the laptop. (FR4, FR5, FR6, and FR1/FR2 for N26; AR5, AR6, AR7, AR8, AR12, AR13,
AR14, AR18, UX-DR2, UX-DR3.) Stories 2.1–2.4, 2.9 and 2.10 are code and can be built by coders
(good candidates for an automated run; 2.10 also needs a Brevo account from the owner); 2.5–2.8
and 2.11 involve the owner's Azure account, secrets, real bank data and phone, so they run with
the owner. **Order decision (2026-10-01):** the push retry (2.9) and the failure email (2.10)
come before the live proof (2.11), so N26 only counts as live once a failed push is no longer
lost. As code-only stories they can also be built earlier, any time after Epic 1.

### Story 2.1: The app resends my rules at start

As the owner,
I want the app to resend all my rules to the backend when it starts,
So that a rule I saved never stays missing on the backend, where the push decision is made (FR6,
AR13).

**Acceptance Criteria:**

**Given** local rules exist and backend mode is on
**When** the app starts
**Then** it sends every local rule to `POST /rules`; the backend upsert is idempotent, and no
"already synced" flag is persisted

**Given** the backend is unreachable at start
**Then** the app keeps working, the local rules stay intact, no unhandled promise rejection
occurs, and the next start tries again

**Given** a rule is created or edited
**Then** it is sent immediately, as today
**And** there is no extra resend when the app returns to the foreground

**Given** the backend already knows a payee by its real name
**When** a resync sends a rule for that payee
**Then** the real name is never replaced by the "Unknown payee" placeholder

**And** fixture mode behaves as before; jest tests cover resync, offline start and the placeholder
case; `tsc`, `eslint` and `jest` pass
**And** the `POST /rules` insert-race and placeholder items in `TODO.md` are handled or explicitly
deferred in plan mode

### Story 2.2: A bad-debit push shows as a heads-up with a proper icon

As the owner,
I want a bad-debit push to pop up on the screen with a clear icon,
So that I notice it the moment it arrives (FR5, AR14, UX-DR3).

**Acceptance Criteria:**

**Given** the app installed
**When** it starts
**Then** a dedicated high-importance notification channel exists (created before the first push,
also on an existing install)

**Given** a bad-debit push sent by the backend
**When** it arrives
**Then** it uses that channel, shows as a heads-up pop-up, and carries a monochrome small icon, not
the generic square

**Given** the push
**Then** title and body are unchanged from R12a, with device-locale amounts (R17) and no minus sign

**Given** the push is tapped
**Then** the app opens as today (tap-to-open stays deferred)

**And** backend tests pin the channel id on the message; the look is checked on the owner's phone
with a Mock ASPSP debit and without screenshots of the notification shade
(`adb shell dumpsys notification --noredact` instead)

### Story 2.3: The ingestion tick runs on a 6-hour timer trigger

As the owner,
I want the bank polling to run as a timer-triggered Azure Function,
So that it runs on the free Functions plan without an always-on server (AR5).

**Acceptance Criteria:**

**Given** the backend runs in the Azure Functions isolated worker model on net10.0 (no
in-process packages)
**When** the timer fires (default every 6 hours; shortened by a setting for local testing)
**Then** exactly one existing `IngestionRunner` tick runs

**Given** the tick throws
**Then** the error is logged without secrets, and the next trigger runs normally

**Given** a tick is still running when the next trigger is due
**Then** they do not overlap

**Given** any host
**Then** the old `BackgroundService` loop does not run next to the timer (no double polling)

**And** the function keeps no state between runs; the existing ingestion tests pass; a test proves
the trigger calls the runner once; it runs locally against the dev database

### Story 2.4: The API runs in the same Functions host

As the owner,
I want the API and the first sync to work inside the Functions host,
So that one deployment serves the app and the poller (AR5).

**Acceptance Criteria:**

**Given** the Functions host running locally
**When** the app calls `/debits`, `/rules` and `/device-token` with the key
**Then** they answer exactly as before (same contract), and the key rules of Epic 1 still hold

**Given** the consent callback completes
**Then** the first sync is triggered once, immediately, and its result is stored

**Given** a first sync that takes longer than the HTTP time limit (about 4 minutes)
**Then** the callback still answers promptly, and the sync continues or is retried by the next tick
(background vs synchronous is settled in plan mode)

**Given** the host restarts mid first sync
**Then** nothing is stored and the next tick starts the first sync again (all-or-nothing)

**And** how to start the host locally is documented

### Story 2.5: The cloud database is ready

As the owner,
I want a cloud Postgres with my schema,
So that the deployed backend has somewhere to keep its data (AR6, NFR9).

**Acceptance Criteria:**

**Given** a Neon free-tier project
**When** I apply the EF migrations manually from my machine
**Then** the schema matches the dev database, including the `BankConsents` table, and the seeded
user exists

**Given** the connection string
**Then** it requires SSL and is stored only as a secret (user secrets locally; Key Vault or an app
setting in the cloud) — never in Git, logs or docs

**Given** the compute has suspended after idle
**When** the backend queries it
**Then** the first query succeeds after the short resume (timeouts and retry settings allow it)

**Given** the backend running locally against Neon
**Then** a read of an empty `Debits` table works

**And** the migration command is written down in `TRACER-03-GO-LIVE.md`

**As built (2026-10-08):** Neon project in eu-central-1 (free tier, database `neondb`, direct host,
no pooler). The connection string is the user secret `ConnectionStrings:Neon`; the tools take it with
`DB=neon` (`tools/ef-container.sh`, `tools/run-functions-container.sh`), print nothing, and the dev
database stays the default. All 12 migrations applied from this machine; 12 tables + history, the seed
user exists, `Debits` empty. Code: `UseNpgsql(..., EnableRetryOnFailure(5, 15 s))`, and
`Data/PostgresConnectionCheck.RequireTlsForRemoteHost` (a non-local host needs `SSL Mode`
Require/VerifyCA/VerifyFull and no `Trust Server Certificate=true`; messages never carry a value),
called by `AddGreedyNoseCore` and by the design-time factory. 576 backend tests (557 + 19), one review
pass, no MUST FIX; three findings fixed (the migration path runs the check too, the trust-certificate
hole, a test that pins the wiring). **Verified live:** after 6 minutes of idle the first `GET /debits`
against Neon answered 200 with an empty list in 2.4 s (warm: 0.08 s); no key → 401; the log holds
neither host nor password. Retry covers the resume, so point (1) of bead `gn-7h4` (a transient DB
failure killing the consent restore) is handled for EF queries. Not done on purpose: running the
migrations from the deployed app; the connection string goes to Key Vault in Story 2.6.

### Story 2.6: The backend is deployed to Azure

As the owner,
I want the backend running in Azure on free resources,
So that it polls and serves the app without my laptop (AR5, NFR2, NFR7, NFR8).

**Acceptance Criteria:**

**Given** the Azure subscription
**When** I deploy
**Then** the Function app runs on the chosen free plan (Consumption or Flex, decided in plan mode)
with its storage account, and the cost stays within the free grants (checked in Azure cost
analysis after the first days)

**Given** the cloud address
**Then** it is HTTPS only — plain `http://` is refused or redirected before any key could be
sent

**Given** the API key is configured in Azure
**When** a request has no key or a wrong key
**Then** it gets 401, and with the right key it succeeds; no endpoint is open

**Given** Key Vault
**Then** the Enable Banking private key and the session encryption key are read from it through
the Function's own identity, and the Neon connection string comes from a Key Vault reference or
an app setting — nothing secret in the repository or in plain logs

**Given** the timer schedule
**Then** the tick runs every 6 hours, and its logs can be read for diagnosis without secrets

**Given** the deployed backend with no consent yet
**Then** a tick does nothing harmful

**And** the cold start is measured and recorded; the deploy steps and the cloud address are
written in `TRACER-03-GO-LIVE.md`

### Story 2.7: N26 is connected through the cloud

As the owner,
I want my real N26 account connected to the cloud backend,
So that my real debits are watched (FR1, FR2, AR7, AR8).

**Owner step:** real bank data and real quota — not for an unattended run.

**Acceptance Criteria:**

**Given** the Production application
**When** I register exactly `https://<name>.azurewebsites.net/callback` as its redirect URL
**Then** Enable Banking accepts it (no `REDIRECT_URI_NOT_ALLOWED`)

**Given** the N26 account is linked in the Enable Banking Control Panel
**When** I start a consent from a browser (the bank is the configured one, N26, until Story 3.3
makes the bank a parameter) and approve it in N26
**Then** the callback stores the consent encrypted in `BankConsents`, with the 90-day request N26
accepted before

**Given** the consent is stored
**Then** the first sync runs once and stores up to 12 months of N26 debits silently — no pushes —
and the count is recorded

**Given** the app's key
**When** `/debits` is read
**Then** the real debits come from the database

**And** the old sandbox consent and `consent.local.json` are not reused
**And** no raw bank data appears in logs or docs, only counts; the number of bank requests the
first sync made is recorded as a finding

### Story 2.8: The release app is on my phone and talks to the cloud

As the owner,
I want a signed release APK on my phone that talks to the cloud backend,
So that the app works without my laptop (AR12, UX-DR2).

**Acceptance Criteria:**

**Given** the release build
**Then** it is signed with a key that is kept out of Git, and the cloud address and API key come
from build configuration, not from tracked source

**Given** the APK is installed on my phone (sideloaded)
**When** the app starts
**Then** it talks to the cloud over HTTPS — no `adb reverse`, no cleartext traffic in release —
and the Debits tab shows my real N26 debits

**Given** the backend is unreachable
**Then** the badge says so; **given** a wrong or missing key it says "not authorized"; **given** an
empty list it says there are no debits — three different states (UX-DR2)

**Given** my rules were made earlier
**Then** they still match the backend's payee ids after the reinstall

**And** the build is made through PowerShell and Gradle as `AGENTS.md` describes

### Story 2.9: A failed push is remembered and retried

As the owner,
I want a push that could not be sent to be remembered and tried again,
So that a bad charge never goes unannounced (FR4, AR18, R11a).

**Acceptance Criteria:**

**Given** a new, booked, bad debit whose push fails (`Rejected`, `Transient`, or no device token)
**Then** the debit is still stored and shown in the list
**And** it carries an explicit **push owed** mark — never inferred from "bad and no
`NotificationLog` row"

**Given** a debit with the mark
**When** the next tick runs
**Then** the worker retries its push from stored data, with no bank call, alongside the normal
processing of new debits
**And** a failing retry never blocks the ingestion of other debits

**Given** a retry succeeds
**Then** a `NotificationLog` row is written, the mark is cleared, and the debit never pushes
again (R11)

**Given** a debit with the mark is older than 24 hours
**Then** the worker gives up, logs it, and ends the mark; the debit stays in the list

**Given** a failed attempt
**Then** it is logged per debit with attempt count, last outcome and time — no secrets, no payee
name, no amount

**Given** a debit turns bad later because of a rule edit (R10a)
**Then** it gets no mark and never pushes

**Given** a Reconnect tick whose summary push (R20) fails to send
**Then** the summary is remembered the same way, with its counts N and M frozen at creation, and
retried for up to 24 hours; a retried summary is never sent twice

**Given** the outcomes
**Then** only `TokenNoLongerValid` prunes a device token; `Rejected` keeps the push owed

**And** a migration adds the mark and attempt fields; tests with a failing sender fake cover:
stays marked, retried next tick, succeeds once, gives up after 24 hours (fake clock), the
rule-edit case, the summary retry, and that other debits are unaffected

### Story 2.10: I get an email when pushes keep failing

As the owner and operator,
I want one email when pushes keep failing,
So that a broken push channel is noticed within hours, not days (AR18).

**Owner step:** create a Brevo account, a sender and an API key (free tier, 300 mails a day).

**Acceptance Criteria:**

**Given** three failed push attempts in a row (across ticks, per-debit or summary pushes; a
success resets the count)
**Then** one email goes to the owner through Brevo, once per incident

**Given** the email
**Then** its subject names the reason (e.g. `Greedy Nose: push alerts are failing (Rejected)`)
**And** its body gives: the reason; since when and the last try; how many bad debits are waiting
and the age of the oldest; that retries continue until 24 hours old; which banks are affected; and
a "check first" line that depends on the reason — `Rejected`: the Firebase Admin key in Key Vault
and its project, and the function logs; `Transient`: likely a temporary FCM or network problem,
check Google's status page; no device token or `TokenNoLongerValid`: open the app once so it
registers again
**And** it contains no payee names, amounts, debit ids, tokens, keys or account numbers — counts,
ages and the reason only

**Given** the problem continues
**Then** no further email is sent for it
**And** when pushes work again, one short follow-up says so and how many waiting debits were sent

**Given** the email cannot be sent
**Then** it is logged, ingestion and the push retry carry on, and the email is retried at the next
tick (the once-per-incident flag is set only after Brevo accepted the mail)

**Given** the Brevo key and the recipient address
**Then** they are configuration (secret and setting), never in Git or logs
**And** the sender sits behind an interface; tests use a fake and cover the three-in-a-row rule,
once per incident, the follow-up and a failing sender

**As built (2026-10-08):** `Alerts/` holds `IAlertMailSender`, `BrevoAlertMailSender` (POST
`/v3/smtp/email`, failures are results, never exceptions), `PushFailureMail` (the texts as pure
functions: counts, ages, outcome names and the bank name only) and `PushFailureWatch`. State lives
in the new `PushHealth` table (one row per user; migration `AddPushHealth`). `IngestionRunner` calls
`RecordAttemptAsync` after each debit push, summary push and retry, and `EvaluateAsync` once per tick
in the `finally` after the retry stage; both have their own budget and never throw. The expiry push
(R19) does not count. Config is mandatory at startup: `Brevo:ApiKey`, `Brevo:SenderEmail`,
`Brevo:SenderName`, `Alerts:RecipientEmail` (user secrets; Key Vault / app settings in Azure). 557
backend tests (536 + 21), one review pass, no MUST FIX. **Decisions and gaps:** every push attempt
counts, so three failing pushes inside one tick already mail (bead for per-tick counting); failing
again between the failure mail and the follow-up is the same incident (no second mail); the banks
line shows the active consent's bank only (Epic 3 makes it multi-bank); if Brevo accepts the mail but
the flag save then fails, the next tick mails again (at-least-once, bead). **Verified live:** with the
device token removed and one owed mark, three ticks gave exactly one failure mail, further failures
no second one; after the token came back the push was sent and exactly one follow-up arrived; both
mails read correctly. The Brevo IP block ("Authorized IPs") has to stay off — home and Azure
addresses are not fixed.

### Story 2.11: A real charge produces a push on my phone

As the owner,
I want a real bad charge to reach my phone as a push,
So that I know the whole chain works without my laptop (FR4, FR5; the tracer's definition of done
for N26).

**Owner step:** a real charge, the real phone.

**Acceptance Criteria:**

**Given** the cloud backend runs, N26 is connected, and a payee has no rule or is marked bad
**When** a new real booked charge from that payee arrives and the next tick runs
**Then** one push arrives on my phone, as a heads-up with R12a's wording, with the phone away from
the laptop

**Given** the next tick
**Then** no second push arrives for the same charge (R10b, R11)

**Given** a charge from a good payee
**Then** no push arrives

**Given** the app is force-stopped in Android settings
**Then** whether the push still arrives is recorded as a finding (AR14)

**And** if the 6-hour wait is too long, one tick may be triggered by hand once, only after counting
the day's N26 fetches so the quota is not exceeded
**And** `TRACER-03-GO-LIVE.md` records the result, and its Progress table is updated

## Epic 3: DKB and ING-DiBa watched too

All three banks feed one debit list, and one bank failing never stops the others. (FR1, FR8, FR9,
FR10; AR4, AR9, AR11, UX-DR1.) Stories 3.1–3.4 are code and can be built by coders; 3.5–3.8
involve the owner's banks and real data, so they run with the owner.

### Story 3.1: The worker takes the user from the consent

As the owner,
I want the ingestion worker to use the user that owns each consent,
So that no part of the worker is tied to one hardcoded user (AR4, multi-user preparation).

**Acceptance Criteria:**

**Given** a consent row for `(user, bank)`
**When** a tick runs
**Then** every read and write in `IngestionRunner` — debits, payees, sync state, notification log,
device tokens — uses that consent's user

**Given** a test consent owned by a user other than the seeded owner
**When** a tick runs
**Then** all of its rows, and the device-token lookup, belong to that user

**Given** the seeded owner with one consent
**Then** behaviour is exactly as before and all ingestion tests pass

**And** no `SeedData.UserId` remains in the ingestion code (checked in review)

### Story 3.2: The worker walks every bank

As the owner,
I want one tick to cover all my banks, with one bank's failure never stopping the others,
So that a problem at one bank never silences alerts from the others (FR1, AR4, NFR6).

**Acceptance Criteria:**

**Given** consents for two banks
**When** a tick runs
**Then** each bank is handled in turn with its own fetch, its own `AccountSyncStates` row and its
own mode (first sync, steady state or reconnect)

**Given** bank A's fetch fails (error, rate limit or dead consent)
**Then** bank B is still processed in the same tick, and A's failure is logged with the bank's name
and without secrets

**Given** a poll interval set per bank (default 6 hours for every bank)
**When** a bank was polled more recently than its interval
**Then** the tick skips that bank
**And** `LastAttemptAt`, `LastSuccessAt` and `LastError` are recorded per consent (Epic 4's status
endpoint reads them)

**Given** two banks whose first syncs are due in the same tick
**Then** each first sync is all-or-nothing on its own

**Given** the same merchant appears at two banks
**Then** it is one payee with one rule, and each debit keeps its own account key

**And** tests with two fake banks cover isolation, interval skipping and the shared payee

### Story 3.3: Connecting a bank uses that bank's own limits

As the owner,
I want to connect any bank by name, with that bank's own consent limit,
So that DKB and ING-DiBa work without being hardcoded (AR9, FR10).

**Acceptance Criteria:**

**Given** `/connect` is started for a bank by name and country
**When** the bank list is read fresh from Enable Banking (no cache)
**Then** the matching bank is used; no bank is hardcoded in the backend

**Given** the name matches more than one bank, or none
**Then** it is refused with a clear message and nothing is started — never guessed

**Given** the bank states a `maximum_consent_validity`
**Then** `valid_until` is the smaller of 90 days and that maximum; with no stated maximum it is 90
days

**Given** the bank list cannot be read
**Then** `/connect` fails clearly and starts nothing

**And** the Mock ASPSP stays selectable for tests; the real name and country used for N26, DKB and
ING-DiBa are recorded in `TRACER-03-GO-LIVE.md`; tests use a stubbed list

### Story 3.4: The app never names a bank in its own words

As the owner,
I want no literal bank name in the app's text,
So that the app is right for every bank I connect (FR10, UX-DR1).

**Acceptance Criteria:**

**Given** the screens that mention the connected bank (connect, consent, syncing, expired banner,
connect error, empty list, settings, disconnect confirmation)
**When** they are shown
**Then** they use neutral wording or a placeholder — never a literal "N26"

**Given** the mocks `01`, `01b`, `01bb`, `01d`, `01e`, `02b`, `06` and `06b`
**Then** they follow the same wording (the mocks are the UI ground truth)

**Given** a search of the app's UI copy for "N26"
**Then** it finds nothing

**And** the jest tests that pin copy are updated; `tsc`, `eslint` and `jest` pass

### Story 3.5: DKB is connected through the cloud

As the owner,
I want my DKB account connected to the cloud backend,
So that its charges are watched too (FR1, FR2, AR11).

**Owner step:** real bank data and real quota — not for an unattended run.

**Acceptance Criteria:**

**Given** the DKB account is linked in the Enable Banking Control Panel
**When** I start a consent for DKB from a browser and approve it in DKB's own banking app
**Then** the consent is stored encrypted, with `valid_until` per Story 3.3
**And** how the approval flowed — whether the browser came back on its own, and what I had to do
by hand — is recorded as a finding (AR11)

**Given** the browser does not return and the consent cannot be completed
**Then** the story stops and we decide (for example a deep link), instead of working around it

**Given** the consent is stored
**Then** the first sync stores up to 12 months of DKB debits silently, with no pushes
**And** the number of debits and of bank requests is recorded

**Given** `/debits`
**Then** the DKB debits appear merged with N26's

**And** the share of DKB debits that carry an `entry_reference`, and of payees with a creditor
IBAN, is recorded as tuning data for R10b and R3a — counts only, no raw bank data in logs or docs

### Story 3.6: ING-DiBa is connected through the cloud

As the owner,
I want my ING-DiBa account connected to the cloud backend,
So that its charges are watched too (FR1, FR2, AR11).

**Owner step:** real bank data and real quota — not for an unattended run.

**Acceptance Criteria:**

**Given** the ING-DiBa account is linked in the Enable Banking Control Panel
**When** I start a consent for ING-DiBa from a browser and approve it in ING's own banking app
**Then** the consent is stored encrypted, with `valid_until` per Story 3.3
**And** how the approval flowed is recorded as a finding (AR11)

**Given** the browser does not return and the consent cannot be completed
**Then** the story stops and we decide, instead of working around it

**Given** the consent is stored
**Then** the first sync stores up to 12 months of ING-DiBa debits silently, with no pushes
**And** the number of debits and of bank requests is recorded

**Given** `/debits`
**Then** the ING-DiBa debits appear merged with the other banks'

**And** the `entry_reference` and creditor-IBAN shares are recorded as tuning data, counts only

### Story 3.7: Each bank reconnects and alerts on its own

As the owner,
I want a reconnect or a dead consent at one bank to affect only that bank,
So that the other banks' alerts carry on (FR8, FR9, NFR6).

**Acceptance Criteria:**

**Given** bank A is reconnected after a gap (a new session)
**Then** the reconnect summary push fires for A only if A has bad new debits (R20, R20a), and bank
B's session hash, debits and sync state are untouched

**Given** bank A's consent dies
**Then** A gets its one expiry push per session, and bank B keeps being polled

**Given** a charge in a currency other than EUR at any bank
**Then** it is skipped and logged, never compared with a EUR limit (FR9)
**And** the per-bank count of skipped charges from the real first syncs is recorded; if there
were none, the finding says no foreign-currency charge was observed

**And** the reconnect and expiry cases are proven with two sandbox consents and two-bank fakes —
a real reconnect only happens when a real consent expires

### Story 3.8: Three banks, one list, one alert path

As the owner,
I want to see all three banks on one list and be alerted from each,
So that I know the whole go-live works (FR1; the tracer's definition of done for all three banks).

**Owner step:** real banks, real charges, the real phone.

**Acceptance Criteria:**

**Given** N26, DKB and ING-DiBa are connected
**When** I open the app
**Then** one merged debit list shows debits from all three banks, newest first

**Given** a new real bad charge at each bank (as they happen over the first days)
**Then** each produces exactly one push, with no duplicates

**Given** the tick logs
**Then** each bank's result is visible separately, and no bank hit `ASPSP_RATE_LIMIT_EXCEEDED`
during the first week

**Given** a failing bank simulated locally with sandbox consents
**Then** the other banks still alert (the isolation of Story 3.2, seen end to end)

**And** `TRACER-03-GO-LIVE.md` records the findings of Epic 3 and its Progress table is updated

## Epic 4: I'm never silently left without alerts

A dead connection is noticed, not silent, and the owner can see each bank's state. (FR7; AR15,
AR16.) The push retry and failure email (AR18, R11a) moved into Epic 2 (Stories 2.9 and 2.10).
All three stories are backend code and good candidates for an automated run.

### Story 4.1: A consent that dies during the first sync still alerts me

As the owner,
I want a consent that dies before its first sync finishes to alert me too,
So that no dead connection is ever silent (FR7, AR15, R19).

**Acceptance Criteria:**

**Given** a consent whose first sync has not finished
**When** it expires by time, or the bank ends it (401 with an `error` field, confirmed by a
session status other than `AUTHORIZED`)
**Then** the expiry push is sent once per session, with the same text as today

**Given** the push cannot be sent
**Then** it is retried at the next tick, and counts as "told" only after a successful send

**Given** a reconnect (a new session)
**Then** the "already told" memory resets on its own, and the first sync starts again silently

**Given** a consent that dies after its first sync finished
**Then** behaviour is exactly as today; the "already told" memory now lives on the consent row,
and a migration carries over existing values

**And** tests cover: dies before the first sync, push once only, retry after a failed send, and
reconnect resets

### Story 4.2: I can see my connection status

As the owner,
I want a status page for my bank connections,
So that I can tell which bank works without opening logs (AR15).

**Acceptance Criteria:**

**Given** the API key
**When** I open the status endpoint
**Then** it lists one entry per connected bank, and without the key it answers 401

**Given** a bank's consent
**Then** its entry shows the state — connected, expired, dead, or first sync pending — and the
valid-until date

**Given** the fields recorded per consent (Story 3.2)
**Then** each entry shows the last successful poll, the last attempt and a short last error
**And** the number of debits waiting for a push (Story 2.9)

**Given** the output
**Then** it never contains a session id, token, account number, IBAN, payee name or amount —
states, dates and counts only

**Given** a dead consent
**Then** `/health` no longer says `connected: true`; it reports the real state or is folded into
this endpoint

**And** tests cover each state and the 401

### Story 4.3: A reconnect closes the old session

As the owner,
I want the old session closed when I reconnect a bank,
So that stale sessions do not pile up and a dead session stops costing calls (AR16).

**Acceptance Criteria:**

**Given** a bank has an old session and a new consent completes
**Then** the old session is closed with `DELETE /sessions/{id}` after the new one is stored
**And** a failure to close it never breaks the reconnect — it is logged and the new consent stays

**Given** the old session was already closed or has expired
**Then** the answer from Enable Banking counts as fine, not an error

**Given** a dead session that was already announced
**Then** the tick makes no fetch and no status call for it until the bank is reconnected

**Given** one bank's session is closed
**Then** no other bank's session is touched

**Given** the first real reconnect at a real bank
**Then** whether `DELETE` also revokes the consent there is recorded as a finding (so far tested
only in the sandbox)

**And** tests cover close after reconnect, a failing close, the already-closed case, and the
skipped calls for a dead session
