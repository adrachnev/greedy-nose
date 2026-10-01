---
stepsCompleted: [1]
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

{{requirements_coverage_map}}

## Epic List

{{epics_list}}
