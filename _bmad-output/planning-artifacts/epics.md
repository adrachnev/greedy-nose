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
the Health Monitor job (only a small operator-email slice for failed pushes comes in, see R11a), iOS, a bank-selection screen, per-bank labels in the
debit list, other users' accounts (Restricted Production is owner-only).

**What was built, measured or decided** while doing a story is not kept here: it is in the notes of the story's
bead (`bd show <id>`, the bead's `--spec-id` points back to its section) and in `TRACER-03-GO-LIVE.md`. This file is the plan.
**Outcome of the tracer (2026-10-19):** closed with N26 and DKB; ING-DiBa was dropped by the owner (Story 3.6 not done,
3.8 judged on two banks).

**Process reminder:** each story is built exactly as before (plan mode, `coder-backend`/
`coder-mobile`, `coder-reviewer` started by hand, findings back to the same coder). Never
`bmad-build`.

## Requirements Inventory

### Functional, non-functional and architecture requirements

These are no longer numbered here. The stories cite **R numbers** (`REQUIREMENTS.md`) and **section titles of
`ARCHITECTURE.md`** (written `A: "Title"`). The Tracer 3 numbers FR1–FR10, NFR1–NFR10 and AR1–AR18 survive only in
bead notes, `TRACER-03-GO-LIVE.md` and old commits; `git show 28e4587:_bmad-output/planning-artifacts/epics.md` has
the lists they stood for.

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
  Android channel (A: "Client") the look is checked on the device, not assumed.

### Coverage Map (Tracer 3)

Which epic delivered which requirements. This is the map of the finished Tracer 3, kept as history. There is no full
table per R: each story names the R it delivers, and the other direction is checked by search at every planning round
(`AGENTS.md`, "Work after Tracer 3").

R22a: Epic 3 (Epic 2 for the N26 part) — several banks at once, one consent each, one merged list
R25, R20b: Epic 1 (first sync: 90 days, all pages), verified live in Epic 2 (N26) and Epic 3 (DKB)
R9, R2a: Epic 1 — debit list from stored data
R10, R10a, R10b, R10c, R11: Epic 2 — new booked bad debit → one push, verified live (baseline, built)
R12a, R17a: Epic 2 — push wording, plus the dedicated Android channel (baseline, built)
R4–R7, R13, R14, R21, R35: Epic 2 — rules reach the backend (resync at app start)
R19: Epic 4 — dead-consent push, including the "dies before the first sync" gap
R20, R20a: Epic 3 — reconnect summary per bank (baseline, built)
R15, R16: Epic 3 — EUR check against the real banks (baseline, built)
R22: Epic 3 — no literal bank name in app copy (UX-DR1)
R11a: Epic 2 — a failed push is retried (Stories 2.9, 2.10)
R27–R34: apply across all epics and become acceptance criteria

Architecture decisions are carried by the stories that deliver them (see `A:` in each story's head).
UX-DR1 → Epic 3, UX-DR2 → Epic 2, UX-DR3 → Epic 2.

## Epic List

Approved 2026-10-01. Order matters: Epic 2 is the thin end-to-end slice with N26 only (the
proven bank); Epic 3 adds the two unknown banks, still inside Tracer 3.

### Epic 1: A backend I can trust to deploy
The owner can rely on the backend to guard his data and to serve the debit list from stored data;
the tests run, so every later story is verifiable. Epic 1 is the most technical epic: a backend
that must be safe before deployment is the real value here.
**Delivers:** R25, R9, R2a, R29, R33
**Architecture:** A: "Authentication and secrets", "Data model", "Transaction ingestion"; tests on this machine: `AGENTS.md`, "Running and verifying"

### Epic 2: N26 live on Azure, end to end
A real N26 bad charge becomes a push on the owner's phone, and a failed push is never lost — with the backend on Azure and the phone
not attached to the laptop.
**Delivers:** R10, R10a, R10b, R10c, R11, R11a, R12a, R17a, R4–R7, R13, R14, R21, R35 (and R22a, R25, R28 for N26)
**Architecture:** A: "Hosting, as built", "Cost", "Authentication and secrets", "Client", "Reliability"; UX-DR2, UX-DR3

### Epic 3: DKB and ING-DiBa watched too
All three banks feed one debit list, and one bank failing never stops the others.
**Delivers:** R22a, R20, R20a, R15, R16, R22, R31
**Architecture:** A: "Hosting, as built", "Data model", "Cost", "Consent lifecycle"; UX-DR1

### Epic 4: I'm never silently left without alerts
A dead connection is noticed, not silent, and each bank's state can be seen.
**Delivers:** R19
**Architecture:** A: "Reliability", "Consent lifecycle", "Data model"

Planned 2026-10-21 (after Tracer 3), in this order:

### Epic 5: Connect a bank from the app
Search a bank, approve at the bank, choose the account, watch the first sync, sort the payees once. (Stories 5.1–5.9.)
**Delivers:** R36, R25, R26, R26a, R22a

### Epic 6: Delete a bank
Remove a bank for good, here and at the bank; the rules stay. (Stories 6.1–6.2.)
**Delivers:** R18, R36, R20b

### Epic 7: Reconnect an expired bank
One tap on the card or the banner brings an expired bank back. (Stories 7.1–7.3.)
**Delivers:** R36, R19, R20, R20a, R21

## Epic 1: A backend I can trust to deploy

The owner can rely on the backend to guard his data and to serve the debit list from stored data;
the tests run, so every later story is verifiable. (R25, R9, R2a; A: "Authentication and secrets", "Data model", "Transaction ingestion".)
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
**And** tests cover 401 for every other endpoint and success with the key (R32, R33)


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
happy path (R32, R33)


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
**And** the ingestion worker's own `SeedData.UserId` uses stay until Epic 3 (R22a, R31); no new
`SeedData.UserId` appears anywhere


### Story 1.6: The debit list comes from stored data

As the owner,
I want the debit list to come from what the backend has already stored,
So that opening the app never spends the bank's few daily fetches (R9, R29).

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


### Story 1.7: Consents live in an encrypted database table

As the owner,
I want my bank consents kept in the database, encrypted,
So that the backend can run in the cloud and no consent sits in a loose file (A: "Data model").

**Implementation note:** the encryption key comes from configuration here (user secrets
locally); Key Vault is wired in Epic 2.

**Acceptance Criteria:**

**Given** the migration is applied
**Then** a `BankConsents` table exists, one row per `(UserId, bank)`
**And** the session id and consent payload are encrypted at rest — never plaintext in the
database or in logs (R32)

**Given** consents for two banks
**Then** there are two rows and reading or replacing one never touches the other

**Given** a consent already exists for a bank
**When** `/callback` completes a new consent for it
**Then** the row is replaced (the reconnect path); the old session is *not* closed here (Epic 4,
A: "Data model")

**Given** the backend restarts
**Then** the consent state — including its expiry, and `Expired` for a dead one — is restored
from the database
**And** `consent.local.json` is no longer read or written

**Given** the wrong encryption key
**Then** the failure is loud and clear, never a silently empty store

**And** the ingestion worker behaves as before with one consent, and all ingestion tests pass
(multi-consent is Epic 3)

> **Changed 2026-10-09 (Story 2.6a):** the window is now **90 days** (`IngestionRunner.FirstSyncWindowDays`), not 12 months — real N26 answered 422 `WRONG_TRANSACTIONS_PERIOD` to 12 months and to 180 days. The acceptance criteria below record what was built on 2026-10-05.

### Story 1.8: The first sync pulls 12 months, all pages

As the owner,
I want the first sync to pull the last 12 months completely,
So that the history is whole and the same for every bank (R25, R20b; A: "Transaction ingestion").

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


## Epic 2: N26 live on Azure, end to end

A real N26 bad charge becomes a push on the owner's phone, and a failed push is never lost — with the backend on Azure and the phone
not attached to the laptop. (R10, R10a, R10b, R10c, R11, R11a, R12a, R17a, R4–R7, R13, R14, R21, R35, and R22a,
R25 for N26; A: "Hosting, as built", "Cost", "Authentication and secrets", "Client", "Reliability"; UX-DR2, UX-DR3.) Stories 2.1–2.4, 2.9 and 2.10 are code and can be built by coders
(good candidates for an automated run; 2.10 also needs a Brevo account from the owner); 2.5–2.8
and 2.11 involve the owner's Azure account, secrets, real bank data and phone, so they run with
the owner. **Order decision (2026-10-01):** the push retry (2.9) and the failure email (2.10)
come before the live proof (2.11), so N26 only counts as live once a failed push is no longer
lost. As code-only stories they can also be built earlier, any time after Epic 1.

### Story 2.1: The app resends my rules at start

As the owner,
I want the app to resend all my rules to the backend when it starts,
So that a rule I saved never stays missing on the backend, where the push decision is made (R35).

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
So that I notice it the moment it arrives (R12a, R17a; A: "Client"; UX-DR3).

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
So that it runs on the free Functions plan without an always-on server (A: "Hosting, as built").

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
So that one deployment serves the app and the poller (A: "Hosting, as built").

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
So that the deployed backend has somewhere to keep its data (A: "Hosting, as built", "Cost").

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


### Story 2.6: The backend is deployed to Azure

As the owner,
I want the backend running in Azure on free resources,
So that it polls and serves the app without my laptop (R27, R32, R33; A: "Hosting, as built").

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


### Story 2.6a: The first sync is tried against the real N26, locally

As the owner,
I want to see what a first sync does against my real N26 account, on my own machine,
So that I know the page count and the quota behaviour before the cloud connection (R22a, R29).

**Owner step:** real bank data and real quota — not for an unattended run. No code change.

**Acceptance Criteria:**

**Given** the local backend runs on the Production application and the dev database
**When** I connect N26 with `tools/connect-bank.sh N26 DE` and approve it in N26
**Then** the consent is stored encrypted in the dev database and the first sync runs once, silently — no pushes

**Given** the first sync has run
**Then** the number of pages, the number of stored debits, the duration and whether a 429 occurred are recorded
as counts only (answers `gn-xeq`, and the page-quota part of `gn-6k5`)

**Given** a 429 stops the sync
**Then** nothing is stored, the number of pages that succeeded is recorded, and no manual retry is made inside 6 hours

**And** no PEM, IBAN, session id or raw bank data appears in logs or docs
**And** the local secrets are set back to the Sandbox values and the container is removed afterwards


### Story 2.7: N26 is connected through the cloud

As the owner,
I want my real N26 account connected to the cloud backend,
So that my real debits are watched (R22a, R25, R28; A: "Cost").

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
**Then** the first sync runs once and stores up to 90 days of N26 debits silently — no pushes —
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
So that the app works without my laptop (A: "Authentication and secrets"; UX-DR2).

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
So that a bad charge never goes unannounced (R10, R11, R11a; A: "Reliability").

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
So that a broken push channel is noticed within hours, not days (R11a; A: "Reliability").

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


### Story 2.11: A real charge produces a push on my phone

As the owner,
I want a real bad charge to reach my phone as a push,
So that I know the whole chain works without my laptop (R10, R10a, R10b, R10c, R11, R12a; the tracer's definition of done
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
**Then** whether the push still arrives is recorded as a finding (A: "Client")

**And** if the 6-hour wait is too long, one tick may be triggered by hand once, only after counting
the day's N26 fetches so the quota is not exceeded
**And** `TRACER-03-GO-LIVE.md` records the result, and its Progress table is updated

## Epic 3: DKB and ING-DiBa watched too

All three banks feed one debit list, and one bank failing never stops the others. (R22a, R20, R20a, R15, R16, R22, R31;
A: "Hosting, as built", "Data model", "Cost", "Consent lifecycle"; UX-DR1.) Stories 3.1–3.4 are code and can be built by coders; 3.5–3.8
involve the owner's banks and real data, so they run with the owner.

### Story 3.1: The worker takes the user from the consent

As the owner,
I want the ingestion worker to use the user that owns each consent,
So that no part of the worker is tied to one hardcoded user (R22a, multi-user preparation).

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
So that a problem at one bank never silences alerts from the others (R22a, R31).

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
So that DKB and ING-DiBa work without being hardcoded (R22; A: "Cost").

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
So that the app is right for every bank I connect (R22; UX-DR1).

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
So that its charges are watched too (R22a, R25; A: "Consent lifecycle").

> **Dropped 2026-10-13:** the owner decided two banks (N26, DKB) are enough. Nothing prevents adding ING-DiBa later.

**Owner step:** real bank data and real quota — not for an unattended run.

**Acceptance Criteria:**

**Given** the DKB account is linked in the Enable Banking Control Panel
**When** I start a consent for DKB from a browser and approve it in DKB's own banking app
**Then** the consent is stored encrypted, with `valid_until` per Story 3.3
**And** how the approval flowed — whether the browser came back on its own, and what I had to do
by hand — is recorded as a finding (A: "Consent lifecycle")

**Given** the browser does not return and the consent cannot be completed
**Then** the story stops and we decide (for example a deep link), instead of working around it

**Given** the consent is stored
**Then** the first sync stores up to 90 days of DKB debits silently, with no pushes
**And** the number of debits and of bank requests is recorded

**Given** `/debits`
**Then** the DKB debits appear merged with N26's

**And** the share of DKB debits that carry an `entry_reference`, and of payees with a creditor
IBAN, is recorded as tuning data for R10b and R3a — counts only, no raw bank data in logs or docs

### Story 3.6: ING-DiBa is connected through the cloud

As the owner,
I want my ING-DiBa account connected to the cloud backend,
So that its charges are watched too (R22a, R25; A: "Consent lifecycle").

**Owner step:** real bank data and real quota — not for an unattended run.

**Acceptance Criteria:**

**Given** the ING-DiBa account is linked in the Enable Banking Control Panel
**When** I start a consent for ING-DiBa from a browser and approve it in ING's own banking app
**Then** the consent is stored encrypted, with `valid_until` per Story 3.3
**And** how the approval flowed is recorded as a finding (A: "Consent lifecycle")

**Given** the browser does not return and the consent cannot be completed
**Then** the story stops and we decide, instead of working around it

**Given** the consent is stored
**Then** the first sync stores up to 90 days of ING-DiBa debits silently, with no pushes
**And** the number of debits and of bank requests is recorded

**Given** `/debits`
**Then** the ING-DiBa debits appear merged with the other banks'

**And** the `entry_reference` and creditor-IBAN shares are recorded as tuning data, counts only

### Story 3.7: Each bank reconnects and alerts on its own

As the owner,
I want a reconnect or a dead consent at one bank to affect only that bank,
So that the other banks' alerts carry on (R20, R20a, R15, R16, R31).

**Acceptance Criteria:**

**Given** bank A is reconnected after a gap (a new session)
**Then** the reconnect summary push fires for A only if A has bad new debits (R20, R20a), and bank
B's session hash, debits and sync state are untouched

**Given** bank A's consent dies
**Then** A gets its one expiry push per session, and bank B keeps being polled

**Given** a charge in a currency other than EUR at any bank
**Then** it is skipped and logged, never compared with a EUR limit (R15, R16)
**And** the per-bank count of skipped charges from the real first syncs is recorded; if there
were none, the finding says no foreign-currency charge was observed

**And** the reconnect and expiry cases are proven with two sandbox consents and two-bank fakes —
a real reconnect only happens when a real consent expires

### Story 3.8: Three banks, one list, one alert path

As the owner,
I want to see all three banks on one list and be alerted from each,
So that I know the whole go-live works (R22a; the tracer's definition of done for all three banks).

> **Done 2026-10-13 for two banks** (ING-DiBa dropped, see Story 3.6).

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

A dead connection is noticed, not silent, and the owner can see each bank's state. (R19; A: "Reliability", "Consent lifecycle", "Data model".) The push retry and failure email (R11a) moved into Epic 2 (Stories 2.9 and 2.10).
All three stories are backend code and good candidates for an automated run.

### Story 4.1: A consent that dies during the first sync still alerts me

As the owner,
I want a consent that dies before its first sync finishes to alert me too,
So that no dead connection is ever silent (R19; A: "Reliability", "Consent lifecycle").

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
So that I can tell which bank works without opening logs (A: "Reliability").

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
So that stale sessions do not pile up and a dead session stops costing calls (A: "Data model").

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

## Epic 5: Connect a bank from the app

The owner connects a bank in the app: search it by name, approve at the bank, choose the account if there are several,
and watch the first sync run. Connecting and the first sync are two separate steps; neither undoes the other
(decided 2026-10-21). The logic lives in the backend; the app asks and shows.
**Delivers:** R36 (connect, bank states in Settings), R25, R26, R26a, R22a (choice of the account)
**Architecture:** A: "Consent lifecycle", "Ingestion modes", "Transaction ingestion", "Data model", "Bank-agnostic design"

### Story 5.1: Spike — the way back from the bank's app

As the owner,
I want to know where my phone lands after I approve at my bank,
So that the connect flow in the app is built on what really happens and not on a guess
(R36; A: "Consent lifecycle").

**Acceptance Criteria:**

**Given** a one-time link from `POST /connect-links`, opened in the browser of the owner's phone, for DKB and for N26
**When** the owner approves at the bank
**Then** it is written down for each bank where the phone lands (browser, bank app, Greedy Nose), whether the
"Connected" page shows, and what the owner has to do by hand to get back to Greedy Nose

**Given** the measurements
**Then** `ARCHITECTURE.md`, "Consent lifecycle", step 2, states the decision with its reason: (A) the owner switches
back by hand and the app reads the state of the connection when it comes to the foreground, or (B) the "Connected" page
opens the app by a link — B only if it works every time at both banks, and A stays the fallback either way

**Given** the decision
**Then** the notes of the bead for this story and of `gn-hkx` carry the measurements (no session id, IBAN or account
name in them)

**And** no app or backend code is written in this story; the reconnect it causes at DKB and N26 may send one summary
push (R20), which the owner has accepted

### Story 5.2: Search for my bank by name

As the owner,
I want to type part of my bank's name and see the matching banks with their country,
So that I can pick my bank without the app knowing any bank in advance (R36, R22; A: "Bank-agnostic design",
"Consent lifecycle").

**Acceptance Criteria:**

**Given** a search text of at least 2 characters
**When** the app sends it with the API key
**Then** the answer lists up to 20 banks from Enable Banking's list, sorted by name, each with name, country and
whether it is already connected; without the key the answer is 401

**Given** a search text shorter than 2 characters
**Then** it is refused with a clear message

**Given** no bank matches
**Then** the list is empty, which is not an error

**Given** Enable Banking's list cannot be read
**Then** the answer is its own error (502), so the app can say "couldn't load banks" and not "no bank found"
(`01-select-bank#failed`)

**Given** a bank that has a card in Settings (connected, syncing, sync failed or expired)
**Then** it is marked as connected in the hits

**Given** a bank whose connection is not expired
**When** a consent link is requested for it
**Then** `POST /connect-links` refuses it (`gn-4b3`); a bank whose connection is expired is still allowed, because that
is the reconnect (Epic 7 adds the check of the stored account)

**Given** the search text, the answer and the logs
**Then** the answer holds only name, country and the connected mark, and the search text is not logged

**And** the bank list is kept in memory for about 10 minutes for the search only; starting a consent still reads it
fresh, as today

**And** tests cover the hits, the minimum length, the empty list, the unreadable list, the connected mark, the refused
second consent, the allowed reconnect of an expired bank and the 401

### Story 5.3: Start a consent from the app and follow it

As the owner,
I want to start the bank's consent from the app and see how far it is,
So that I do not need a laptop or a script to connect a bank (R36, R25, R22a; A: "Consent lifecycle", "Data model").

**Acceptance Criteria:**

**Given** a bank from the search (Story 5.2)
**When** the app asks for a consent link
**Then** the answer holds the one-time URL and an id of this attempt, which the app uses to ask for its state

**Given** the owner approved and the bank offers exactly one account in euro (R15)
**When** the callback is handled
**Then** the bank is saved with that account (kept by IBAN), the first sync is queued (R25), and the state of the
attempt is "saved"

**Given** the bank offers several accounts in euro
**When** the callback is handled
**Then** nothing is saved as a connection yet; the session and the list of accounts wait encrypted on the attempt with
a short deadline from the callback (15 minutes), and the state is "choose account"

**Given** the bank offers no account in euro
**Then** the session is closed at Enable Banking (best effort), nothing is stored, and the state is "failed" with the
reason "no supported account" (`01ba-select-account#failed`)

**Given** the bank reports an error or the owner cancelled, or the code cannot be traded
**Then** the state is "failed" with a short reason; nothing of the bank's text is shown or logged

**Given** the app asks for the state of an attempt
**Then** the answer is one of: waiting for the bank, choose account, saved, failed (with reason), expired; it needs the
API key, an unknown id answers 404, and it never holds a session id, IBAN, account name or account list

**Given** the deadline passes without a choice
**Then** the state is "expired", the waiting session is closed and the stored list is deleted

**Given** the rule-based account choice (`AccountSelector`, type `CACC` and product "Girokonto")
**Then** it is removed; the owner chooses, and `AGENTS.md` notes that `tools/connect-bank.sh` cannot finish a choice
between several accounts

**Given** a bank that had an older session (a reconnect)
**Then** the old session is closed after the new one is stored, as in Story 4.3

**And** tests cover one account, several accounts, none, a bank error, the state answers (including 404 and 401), the
deadline, and that no log line or answer holds an id, IBAN or account name

### Story 5.4: Choose the account

As the owner,
I want to pick the account to watch when my bank has several,
So that Greedy Nose watches the account I pay with (R36, R22a; A: "Consent lifecycle", "Data model").

**Acceptance Criteria:**

**Given** an attempt in the state "choose account" (Story 5.3)
**When** the app asks for the accounts
**Then** each row holds an id of the row, the account's name (else its product, else "Account"), its kind and the last
four digits of its IBAN — never the full IBAN, the uid or anything else of the bank's answer

**Given** the list
**Then** the first current account the bank offered carries the mark "suggested"; it is only a hint

**Given** the app sends the id of a row
**Then** the bank is saved with that account (kept by IBAN, not by product text), the first sync is queued (R25), the
waiting list is deleted and the state of the attempt is "saved"

**Given** the owner cancels (or leaves the choice)
**Then** the session is closed at Enable Banking (best effort), the waiting list is deleted and nothing is saved

**Given** an id that is not in the list, a choice for an attempt that is not in the state "choose account", or a second
choice
**Then** it is refused and nothing changes

**Given** any of these calls
**Then** they need the API key, and no log line holds an IBAN, an account name or an id of the bank

**And** tests cover the list, the mark, the choice, the cancel, the refusals and the 401

### Story 5.5: My banks with their state, and try the first sync again

As the owner,
I want to see my banks and their state, and to repeat a first sync that failed,
So that I know what each bank is doing and can fix a failed start without connecting again
(R36, R25, R29; A: "Transaction ingestion", "Reliability", "Data model").

**Acceptance Criteria:**

**Given** the API key
**When** the app asks for my banks
**Then** there is one entry per saved bank with its name, country, account name, valid-until date and one state:
connected, syncing, sync failed or expired; without the key the answer is 401

**Given** a bank whose first sync is not finished and has not failed
**Then** its state is "syncing"; when the sync has failed (or its queue message was given up) the state is "sync failed"
with a short reason, among them "the bank's daily limit is used up"

**Given** the list
**Then** it holds no session id, IBAN, token, payee or amount, and answering it never calls the bank (it reads stored
data only, so the app may ask for it every few seconds)

**Given** a bank in the state "sync failed"
**When** the app asks to try again
**Then** the first sync is queued again and no new consent is asked of the bank

**Given** a bank whose daily fetch limit is used up (R29)
**When** the app asks to try again
**Then** it is refused with the message to try again tomorrow and no call is made to the bank

**Given** a bank in any other state, or an unknown bank
**When** the app asks to try again
**Then** it is refused and nothing is queued

**And** tests cover each state, the 401, the repeat, the refusal at the limit, the refusal in other states, and that the
list never calls the bank

### Story 5.6: Settings shows my banks, and the search finds a bank

As the owner,
I want Settings to list my banks with their state and to let me search for a new bank,
So that I see what is connected and can start connecting another (R36, R22; mocks `06`, `01`, `01a`, `02b`, `04c`).

**Acceptance Criteria:**

**Given** saved banks
**When** I open Settings
**Then** each bank has a card with its name, account name and one state: connected, syncing, sync failed or expired
(Story 5.5), as in `06-settings`

**Given** a bank in the state "syncing"
**Then** its card asks the backend again every few seconds and changes to its new state by itself; the asking stops
when no bank is syncing or the screen is left

**Given** no bank is connected (first start, or after the last bank is gone)
**Then** the app opens on Settings with one sentence about what it does and "Connect a bank" as the main action
(`06-settings#no-bank`); the empty debit list and the empty rule list point to it too (`02b`, `04c`)

**Given** I tap "Connect a bank"
**Then** I see the search (`01-select-bank`), with the field empty and no list until I type

**Given** I type at least 2 characters
**Then** the app asks the backend after a short pause, not on every key, and shows name and country of each hit; a bank
marked as connected shows "Connected" and cannot be tapped

**Given** no bank matches
**Then** the screen of `01a-select-bank-no-match` shows; **given** the list could not be loaded, it says
"Couldn't load banks" and never "No bank found" (`01-select-bank#failed`)

**Given** a bank in the hits that is not connected
**When** I tap it
**Then** the next step is Story 5.7; until it is built, the tap leads nowhere yet

**Given** the screens
**Then** no literal bank name is in the app's own words (R22), amounts and dates follow the device locale (R17), and the
cards carry no delete or reconnect action yet (Epics 6 and 7)

**And** the three checks of `AGENTS.md` ("Running and verifying") pass, and tests cover the cards for each state, the
polling stopping, the first-start state, the search with its pause and minimum length, and the three search results

### Story 5.7: Approve at the bank, come back, choose the account

As the owner,
I want to approve at my bank and, if there are several accounts, pick the one to watch,
So that my bank is connected without leaving anything half done (R36, R22a, R15; A: "Consent lifecycle"; mocks `01ba`,
`01e`).

**Acceptance Criteria:**

**Given** a bank I tapped in the search (Story 5.6)
**When** the app has asked the backend for the consent link (Story 5.3)
**Then** the link opens in the phone's browser and the bank's own screen takes over; the app builds nothing of the
bank's consent screen (`01b` only shows what the bank shows)

**Given** I am back in the app (on my own, or by the way the spike chose, Story 5.1)
**Then** the app asks the backend for the state of the attempt as soon as it is in the foreground, and shows a waiting
screen while the state is "waiting for the bank"

**Given** the state "saved"
**Then** the app goes on to the first sync (Story 5.8)

**Given** the state "choose account"
**Then** the app shows the accounts as in `01ba-select-account`: name, kind, last four digits, "Suggested" on one row;
tapping a row saves the choice and goes on to the first sync

**Given** I tap Cancel (or go back) on the account choice
**Then** the attempt is cancelled at the backend, nothing is saved, and the app is back on Settings

**Given** the state "failed"
**Then** the screen of `01e-connect-error` shows with "Try Again" (a new consent for the same bank) and "Choose a
different bank"; the reason "no supported account" shows the text of `01ba-select-account#failed`

**Given** the state "expired", or no answer from the backend
**Then** the app says so plainly and offers to start again; it never shows a spinner without end

**Given** the screens
**Then** no literal bank name is in the app's own words (R22), and no IBAN is ever shown in full

**And** the three checks of `AGENTS.md` pass, and tests cover each state of the attempt, the cancel, the return to the
foreground, and the retry from the error screen

### Story 5.8: Watch the first sync, and try it again

As the owner,
I want to see my first sync run and to repeat it if it failed,
So that a connected bank is never left in an unknown state (R25, R36, R29; A: "Transaction ingestion"; mock `01bb`).

**Acceptance Criteria:**

**Given** a bank was just saved (Story 5.7)
**Then** the app shows "Fetching your debits" with a spinner (`01bb-syncing`) and asks the backend for the bank's state
every few seconds

**Given** I tap "Settings", go back or close the app
**Then** nothing is undone: the sync runs on in the backend and the card in Settings shows its state

**Given** the sync is done
**Then** the app goes on to the onboarding offer (Story 5.9) when payees without a rule were found, otherwise to the
debit list

**Given** the sync has failed
**Then** the screen shows "Couldn't fetch your debits" (`01bb-syncing#failed`), the bank stays connected, and "Try
again" repeats only the pull (Story 5.5)

**Given** the bank's daily limit is used up
**Then** the screen says to try again tomorrow and "Try again" is not offered

**Given** the sync is still running after 2 minutes
**Then** the screen stops waiting and points to Settings, where the card shows the state; the sync is not stopped

**Given** a card in Settings with the state "sync failed"
**Then** it offers "Try again" with the same effect as on this screen

**Given** the screens
**Then** nothing fetched here can notify (R25), no literal bank name is in the app's own words (R22), and the polling
stops when the screen is left

**And** the three checks of `AGENTS.md` pass, and tests cover running, done (with and without payees to review),
failed, the limit, the 2-minute stop, leaving the screen, and "Try again" on the screen and on the card

### Story 5.9: Sort the payees once, or leave them Bad

As the owner,
I want to mark all payees of my history Good in one tap, or leave them all Bad,
So that a new bank does not start with a list of payees I have to sort one by one (R26, R26a, R35; mock `01c`).

**Acceptance Criteria:**

**Given** the first sync is done and I am still in the connect flow
**Then** the app lists the payees of that sync that have no rule, as in `01c-classify-payees`: name, amount, number of
charges, with no control per payee

**Given** the list
**When** I tap "Mark all Good and start"
**Then** a Good rule without a limit is written for every payee in the list, the rules reach the backend (R35), and
the debit list opens

**Given** the list
**When** I tap "Keep all Bad and start"
**Then** no rule is written (a payee without a rule is Bad, R5) and the debit list opens

**Given** I tap Skip, go back or close the app
**Then** no rule is written, the bank stays connected, it counts as "Keep all Bad" (R26a), and the offer does not come
back

**Given** the rules cannot be saved (no network)
**Then** I stay on the list with "Couldn't save", and no rule is half written

**Given** no payee is left without a rule, or the sync found no debits
**Then** the list is skipped and the debit list opens at once

**Given** the history of the sync
**Then** none of it notifies (R25); only a charge that arrives after the sync does (R26a)

**Given** the build of this story
**Then** it is checked whether writing and sending all rules at once is atomic at the backend (R35); if it is not,
that gap becomes a backend bead before this story is built

**And** the three checks of `AGENTS.md` pass, and tests cover both buttons, Skip and back, the failed save, the
skipped list, and that a payee with a rule is not in the list

## Epic 6: Delete a bank

The owner removes a bank for good: its connection ends here and at the bank, its debits go, his rules stay. Together with
Epic 5 this closes the loop of adding and removing a bank from the app. (Epic 7, reconnecting an expired bank, follows.)
**Delivers:** R18 (delete), R36 (delete on the card), R20b (connect again after a delete)
**Architecture:** A: "Data lifecycle", "Data model", "Consent lifecycle"

### Story 6.1: Delete a bank in the backend

As the owner,
I want one call that removes a bank and everything that belongs only to it,
So that no access stays open at the bank and no debit of it stays behind, while my rules are kept
(R18, R20b; A: "Data lifecycle", "Data model").

**Acceptance Criteria:**

**Given** the API key and a saved bank
**When** the app asks to delete it
**Then** the access is revoked at Enable Banking first; a session that is already closed, expired or unknown there
counts as done (as in Story 4.3)

**Given** the revoke fails in any other way (no answer, an error from the bank's side)
**Then** the delete is refused with a clear message, the bank and all its data stay, and the app may try again

**Given** the revoke worked
**Then**, in one database change, the bank's consent, its sync state, its debits, their notification records and its
pushes still owed (including a reconnect summary) are deleted

**Given** the delete
**Then** payees and rules stay (R3a, R18), and no other bank's data, and not the device token, is touched

**Given** a tick or a retried push for that bank is running while it is deleted (`gn-8j5`)
**Then** it writes nothing for the deleted bank afterwards

**Given** the last bank is deleted
**Then** the backend simply has nothing to poll; rules and device token stay

**Given** the same bank is connected again after a delete
**Then** it starts as a first sync with no push and no summary (R20b), and the onboarding offer lists only payees
without a rule

**Given** an unknown bank, or no API key
**Then** the answers are 404 and 401

**Given** the logs and the answers
**Then** they hold no session id, IBAN, payee or amount

**And** tests cover the order (revoke before delete), the tolerated answers, the refusal with nothing changed, what is
deleted and what stays, the race with a tick, the last bank, and connecting again

### Story 6.2: Delete a bank from Settings

As the owner,
I want to delete a bank from its card after a clear warning,
So that I never remove a bank by accident and know what is kept (R18, R36; mock `06c`).

**Acceptance Criteria:**

**Given** a card in Settings, in any state (connected, syncing, sync failed, expired)
**Then** it has "Delete"

**Given** I tap "Delete"
**Then** a confirmation names the bank and says that the connection and its debit history are deleted for good, that the
access at the bank ends, that my rules are kept, and that it cannot be undone (`06c-delete-confirm`)

**Given** it is my last bank
**Then** the confirmation also says that no alert comes until a bank is connected (`06c-delete-confirm#last-bank`)

**Given** I confirm
**Then** the app asks the backend (Story 6.1) and "Delete" cannot be tapped twice while it waits

**Given** the delete worked
**Then** the card is gone, the debits of that bank are gone from the debit list without restarting the app, the rules
are unchanged, and after the last bank the app shows Settings with no bank (`06-settings#no-bank`)

**Given** the delete is refused or there is no network
**Then** the bank stays and a message says that it could not be deleted and that I can try again; nothing is half done

**Given** I tap Cancel
**Then** nothing changes

**Given** the screens
**Then** no literal bank name is in the app's own words (R22) except the bank's own name from the data

**And** the three checks of `AGENTS.md` pass, and tests cover the dialog (normal and last bank), the confirm, the
cancel, the failed delete, and the screens after the last bank

## Epic 7: Reconnect an expired bank

The owner brings an expired bank back with one tap on its card or on the banner, and sees at once which bank is not
being watched. Rules and debits are kept; the gap is told as one summary push.
**Delivers:** R36 (reconnect), R19 (banner per bank), R20, R20a, R21
**Architecture:** A: "Consent lifecycle", "Ingestion modes", "Data lifecycle"

### Story 7.1: Reconnect an expired bank in the backend

As the owner,
I want to reconnect an expired bank to the same account,
So that alerts resume without a new first sync and without losing rules or debits
(R36, R20, R20a; A: "Consent lifecycle", "Ingestion modes").

**Acceptance Criteria:**

**Given** a bank whose connection is expired
**When** the app asks for a reconnect link for it
**Then** the answer holds the one-time URL and an id of the attempt; for a bank that is not expired, or an unknown bank,
it is refused

**Given** the owner approved and the bank offers the stored account (found by its IBAN)
**When** the callback is handled
**Then** the new session replaces the old one, the old one is closed after the new one is stored (Story 4.3), no
account choice is asked and no first sync is started

**Given** the next tick after a reconnect
**Then** it runs in reconnect mode: the gap goes through the rules and, if at least one debit in it is bad, one summary
push is sent (R20), otherwise none (R20a)

**Given** the bank does not offer the stored IBAN
**Then** the reconnect is refused with a clear reason, the new session is closed at Enable Banking (best effort), and the
old connection, rules and debits stay as they were

**Given** the app asks for the state of a reconnect attempt
**Then** the answer is one of: waiting for the bank, saved, failed (with reason), expired — with the same rules as in
Story 5.3 (API key, 404 for an unknown id, no session id, IBAN or account name)

**Given** the reconnect worked
**Then** the bank's state is "connected" again and the expiry push for the old session is not repeated

**Given** the logs and the answers
**Then** they hold no session id, IBAN, payee or amount

**And** tests cover the refused link, the stored account found, the account missing (old data untouched), the mode and
the summary of the next tick, the closed old session, and the state answers

### Story 7.2: Reconnect from the bank's card

As the owner,
I want a "Reconnect" button on the card of an expired bank,
So that I get my alerts back in a few taps (R36, R19; mocks `01b#reconnect`, `01e#reconnect`).

**Acceptance Criteria:**

**Given** a card in the state "expired"
**Then** it has "Reconnect" next to "Delete"; no other state has it

**Given** I tap "Reconnect"
**Then** the app asks for the link of Story 7.1 and opens it at once in the browser — no bank search, no account choice
and no sync screen follow

**Given** I am back in the app (way and wait as in Story 5.7)
**When** the state is "saved"
**Then** Settings shows the bank as connected and I need to do nothing more

**Given** the state is "failed" (the login was cancelled, it timed out, or the bank did not offer my account)
**Then** the screen of `01e-connect-error#reconnect` shows: "Couldn't reconnect …", the bank stays expired, rules and
debits are kept, with "Try Again" (a new link for the same bank) and "Back to Settings"

**Given** the state "expired", or no answer from the backend
**Then** the app says so plainly and offers to start again; it never shows a spinner without end

**Given** the screens
**Then** no literal bank name is in the app's own words (R22), and no IBAN is ever shown in full

**And** the three checks of `AGENTS.md` pass, and tests cover the button only on an expired card, the way back, the
success, each failure, and the retry

### Story 7.3: A banner on the debit list for each expired bank

As the owner,
I want the debit list to tell me loudly which bank is not being watched,
So that silence is never mistaken for "nothing bad happened" (R19, R21, R22a; mock `01d`).

**Acceptance Criteria:**

**Given** a bank in the state "expired"
**Then** the debit list shows a banner for it above the list, naming the bank ("«Bank»: connection expired"), with the
text of `01d-connection-expired` and a "Reconnect «Bank»" button

**Given** two banks are expired
**Then** there is one banner for each, each naming its bank

**Given** I tap the button on a banner
**Then** the reconnect of Story 7.2 starts for that bank

**Given** the bank is connected again
**Then** its banner is gone

**Given** the banner
**Then** the list below it stays visible and searchable, the history is kept, and rules stay editable (R21); the
"Last seen" header shows only when no other bank is still live

**Given** the app is opened, or comes to the foreground
**Then** the banners follow the real states of the banks (Story 5.5) and do not need a restart

**Given** the screens
**Then** no literal bank name is in the app's own words (R22)

**And** the three checks of `AGENTS.md` pass, and tests cover no banner, one, two, the button, the banner going away
after a reconnect, and the "Last seen" header
