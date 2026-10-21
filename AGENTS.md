<!-- bmad:context -->
<!-- Verified 2026-09-23 against 9acae0f. Managed by bmad-project-context; edits inside this block are replaced on refresh. Keep anything you want preserved outside the markers. -->

## Greedy Nose

A mobile app (Android in v1, R34) that connects one European bank account and notifies the user the
moment a payee flagged "Bad" charges them — deliberately not a general finance app. React Native
client in `app/`, ASP.NET Core / Azure Functions (C#) backend in `backend/`. `REQUIREMENTS.md` is
the single source of truth for product behavior; where anything else disagrees, it wins.

## Policy

- The console language follows the owner (German at the moment, as set in the global settings). Documents, code,
  comments and commit messages stay English.
- Code under `app/` is written only by the `coder-mobile` subagent; code under `backend/` only
  by `coder-backend`. Never write it in the main session, even for something that looks small —
  that also skips the automatic review trigger, since review fires on a subagent stopping, not on
  an edit. Exception: changes of 10 lines or fewer, or docs-only changes (`.md`/`.txt`) — edit
  those directly.
- After every `coder-mobile`/`coder-backend` run, start one `coder-reviewer` per coder by hand, in
  parallel — the `SubagentStop` hook that requests this lands in the stopped coder, not the main
  session, so it does not happen on its own. Send findings back to the *same* coder agent via
  SendMessage, not a fresh one and not the main session.
- If agent-spawning itself is ever blocked, say so plainly rather than silently writing `app/` or
  `backend/` code yourself — and still start `coder-reviewer` by hand if that type isn't blocked.
- The device toolchain, Gradle builds, `adb`, logcat, and running the local backend/Postgres are
  the main session's own job — no subagent can do them — but that is not licence to write code.
- Secrets (the Enable Banking `.pem`, the Firebase Admin SDK key, bank exports, the live consent)
  are never committed, printed, or logged — configuration lives in `dotnet user-secrets`, never
  `appsettings.json`. `.gitignore` already covers the filenames; don't rename around it.
- Commit directly to `main` (no feature branches); commit only when asked; never push unless asked.
- When the user says "automated run" plus named steps: read `AUTOMATED-RUN.md` in full before
  doing anything else — it replaces plan mode and the rules above with its own opt-in ones.
- BMAD skills (decided 2026-09-29): use `bmad-create-epics-and-stories` to slice larger work into
  epics and stories (the input is `REQUIREMENTS.md`, `ARCHITECTURE.md` and the UX documents in
  `_bmad-output/`). Each story is then built **exactly as before** — plan mode, `coder-backend`/
  `coder-mobile`, `coder-reviewer` started by hand, findings back to the same coder. Do **not** use
  `bmad-build` (or any other BMAD skill) to write or review code: its own implementation and review
  loop would bypass the rules above. Other BMAD skills stay available for documents (`bmad-prd`,
  `bmad-architecture`, `bmad-review`, `bmad-ux`) as they were used so far.

## Where things are

- `REQUIREMENTS.md` — read first. `ARCHITECTURE.md` — the design doc, reconciled with it.
  `TODO.md` — known implementation gaps and review findings; check before starting so a finding
  isn't rediscovered.
- The tracer bullets, in order — each doc has a Progress section; the live one is
  `TRACER-03-GO-LIVE.md`, read its Progress first:
  - `TRACER-01-BANK-DATA.md` (2026-08-17 → 09-17) — real Enable Banking data end to end into the
    app; done. Real data broke three assumptions (missing `entry_reference`, almost no creditor
    IBAN, `uid` changing per consent).
  - `TRACER-02-NOTIFICATIONS.md` (2026-09-17 → 09-23) — Postgres, FCM push, rules sync, rule
    engine, ingestion worker; done, verified on the device.
  - `TRACER-03-GO-LIVE.md` (from 2026-09-29) — the owner-only productive version on Azure with
    N26 and DKB (ING-DiBa dropped); closed 2026-10-19.
- `.claude/agents/coder-mobile.md` / `coder-backend.md` / `coder-reviewer.md` — the three subagent
  definitions Policy refers to above.
- `mocks/index.html` — the UI ground truth for `coder-mobile`; static HTML/CSS phone mockups, one
  file per screen. `mocks/style.css` holds the shared design tokens.
- `app/src/domain/` — the permanent core (`model.ts`, `classification.ts`); no React, no
  fixtures. `app/src/data/hooks.ts` — the one seam screens read data through; swapping data
  sources touches this file and nothing else. `app/src/mocks/` — fixture values, temporary
  scaffolding (see Known pitfalls).

## Running and verifying

- Mobile: `cd app && npx tsc --noEmit && npx eslint . && npx jest` — the three checks run after
  every mobile fix round.
- Backend: `dotnet build`, `dotnet test`, `dotnet ef` — run `dotnet tool restore` first; a
  globally installed `dotnet-ef` may not match the version `dotnet-tools.json` pins.
- Backend tests: `dotnet test` does not run on this machine — Smart App Control blocks every
  freshly built, unsigned DLL (`0x800711C7`, code-integrity event 3077). Run the tests in a Linux
  container instead (Docker running; the mount is read-only, so the Windows build output is
  untouched; the named volume caches NuGet; about 11 s):
  `MSYS_NO_PATHCONV=1 docker run --rm -v "C:/Workspace/greedy-nose/backend:/src:ro" -v greedy-nose-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test GreedyNose.Api.Tests --artifacts-path /tmp/artifacts`
  (the `MSYS_NO_PATHCONV` prefix is for Git Bash; drop it in PowerShell). `dotnet build` still
  works on the host. Do not switch Smart App Control off — that was ruled out on 2026-10-01.
- Running the backend: `dotnet run` does not work on the host either — Smart App Control blocks
  the freshly built `GreedyNose.Api.exe` (same cause as the tests). Start it in a container with
  `bash tools/run-backend-container.sh <name>` (Docker and the dev Postgres up; port 5199 on
  127.0.0.1, so `adb reverse tcp:5199 tcp:5199` still works; run it in the background and stop it
  with `docker rm -f <name>`). It mounts the user-secrets file and the Firebase / Enable Banking
  key files read-only and prints no secret. Verified live 2026-10-01 (Story 1.3).
- Host (Stories 2.3 + 2.4): there is **one** host, `backend/GreedyNose.Functions` — the API (HTTP
  functions, same paths, no `/api` prefix, key checked by Functions middleware), the first-sync queue
  function (the consent callback enqueues a message) and the ingestion timer. `GreedyNose.Api` is only
  a library now. Start it with `bash tools/run-functions-container.sh <name>` (or its alias
  `tools/run-backend-container.sh`; Docker, dev Postgres and Azurite up:
  `docker compose -f backend/docker-compose.yml up -d`); the API answers on 127.0.0.1:5199, so
  `adb reverse` and `tools/connect-bank.sh` work as before. Migrations: `tools/ef-container.sh`. The
  first run of a started app after a code change needs a hard restart (`adb shell am force-stop
  com.greedynose`) — a backgrounded app keeps its old state and sends no request. Default schedule:
  every 6 h (`IngestionSchedule`, a flat name on purpose —
  the host resolves `%name%` through configuration). Schedules under 6 h are refused unless
  `Ingestion__AllowShortInterval=true`; for local tests pass
  `-e "IngestionSchedule=*/30 * * * * *" -e Ingestion__AllowShortInterval=true` (Sandbox only — N26
  allows ~4 fetches a day). Verified live 2026-10-07.
- Backend API key (Story 1.3): the backend refuses to start without `Api:Key` (at least 32
  characters, no leading/trailing whitespace) — `dotnet user-secrets set "Api:Key" "<random>"`.
  Put the same value into `app/src/data/apiKey.local.ts`. Every endpoint except `/connect` and
  `/callback` (closed in Story 1.4) answers 401 without `Authorization: Bearer <key>`.
- Connecting a bank (Story 1.4): `/connect` and `/callback` are browser pages and need no header;
  each is protected by its own one-time secret. Start with `bash tools/connect-bank.sh [bank]
  [country] [base-url]` — it asks `POST /connect-links` (API key) for a one-time link and opens it in
  the browser. The link works once and expires after 30 minutes; the callback state is single use too;
  a failed or interrupted attempt needs a new link. The callback page shows no session id or IBAN.
  Since Story 3.3 the bank name and country are checked against Enable Banking's own list (an unknown or
  ambiguous bank is refused with a clear message and nothing is created) and `valid_until` follows the bank's
  own maximum.
- Connection status (Story 4.2): `bash tools/status.sh [base-url]` prints `GET /status` (API key, read with
  `curl`): per bank the state (connected, expired, dead, first sync pending), valid-until, last success, last
  attempt, a short last error, and the number of debits waiting for a push — no session id, IBAN or amount.
  `GET /health` is only liveness plus the worst real state.
- App backend key (Story 1.2): in backend mode the app sends `Authorization: Bearer <key>`. Copy
  `app/src/data/apiKey.local.example.ts` to `apiKey.local.ts` (gitignored) and put the key in it;
  without it the app sends no backend request and the dev badge says "API key not configured".
  After changing the file, restart Metro with `--reset-cache`. The key ends up inside the bundle,
  so a release APK carries it (accepted for one owner; rotate by shipping a new APK).
- Local Postgres: `docker compose -f backend/docker-compose.yml up -d` (loopback-only, dev creds,
  throwaway data) before running the backend.
- Stop any locally-running backend before a coder agent builds it — a running process locks the
  build's DLLs.
- Android: build with `.\gradlew.bat app:installDebug ...` via PowerShell, not
  `npx react-native run-android` — it fails to invoke `gradlew.bat` from Git Bash/execa on this
  machine. Requires `JAVA_HOME` set to a JDK 17 install (not the Android Studio JBR or a bundled
  JDK 25 — both trip a build failure) and the Microsoft Visual C++ Redistributable installed (RN's
  native build uses CMake, which fails with `STATUS_DLL_NOT_FOUND` without it). The device
  connects over wireless `adb` (`adb pair`/`adb connect`), not USB.
- Wireless `adb` pairing lapses and needs redoing periodically. Separately, a missing
  `adb reverse` tunnel for Metro's port (8081) looks identical on the device to "Metro isn't
  running" even when Metro is healthy — check the tunnel before assuming the bundler is down.

## Cloud and release operations

Names, settings and findings: `TRACER-03-GO-LIVE.md`, "Azure deployment". Only the working rules are here.

- Azure CLI: `bash tools/az.sh <args>` (container; the login lives in the Docker volume `greedy-nose-azure`). The
  login is the service principal `sp-greedy-nose-tools` (`bash tools/az-sp-login.sh`, Contributor on `rg-greedy-nose`
  only, secret renewal: bead `gn-4dy`). Entra security defaults are on, so never plan a device-code login for Graph
  or Log Analytics work.
- Deploy: migrations on Neon first (`DB=neon bash tools/ef-container.sh database update`), then
  `bash tools/azure-deploy.sh`; afterwards `bash tools/status.sh https://func-greedy-nose-611f0.azurewebsites.net`.
  Code that is already running does not need a redeploy for a migration that only drops a dead column.
- The permission classifier blocks a production deploy, a migration on Neon and the creation of credentials when
  Claude runs them. Do not work around it: prepare everything, then ask the owner to run the command themselves
  in the session with `! <command>`, and verify the result afterwards.
- A manual ingestion tick costs one of a bank's ~4 fetches of the UTC day: reset `BankConsents.LastAttemptAt` of that
  bank in Neon first (the poll interval skips a bank polled within 6 h), then `POST /admin/functions/IngestionTick`
  (TRACER-03 has the call). Check the count of today's `Enable Banking GET … transactions` log lines before doing it.
- Logs: Application Insights / Log Analytics, query examples in TRACER-03. Our own lines carry no ids; keep it that way
  (a new log line never prints a session id, account uid, IBAN, payee or amount).
- Release APK (phone build): `cd app/android; .\gradlew.bat app:assembleRelease -PreactNativeArchitectures=arm64-v8a`.
  The signing key (`C:\Users\Nutzer\.greedy-nose\release.jks`, the owner backs it up), `app/android/keystore.properties`
  and `app/src/data/backend.local.ts` are untracked on purpose. The device token must reach the cloud before real
  data flows, or pushes are lost.

## Conventions that differ from defaults

- Terminology follows `R0`: `payee`/`debit`/`good`/`bad` — never `debtor`/`debitor`/`transaction`/
  `trusted`. Renamed across the codebase.
- Classification is derived at read time from the payee's rule, never stored on the debit (`R6`)
  — editing a rule must re-label existing debits for free.
- Amounts are stored positive and rendered without a minus sign (`R17a`).
- Debit Detail is informational only — no classification or rule-editing controls; editing
  happens only on the Edit Rule screen, one commit (Save writes classification and limit together).
- No emoji, no icon-library glyphs — line-style SVGs (`stroke="currentColor"`,
  `stroke-width="1.75"`, round caps/joins); state is color (green/red), not iconography.

## Known pitfalls

- `R8`'s auto-flip (a save-time flip, a passive Bad-only flip, an "Auto-marked Bad" marker) was
  deleted in full — a past commit and this session's own training data both still contain it;
  don't restore it from either.
- A neutral "unmarked"/"untracked" third state was proposed and explicitly rejected as scope
  creep (it behaved identically to bad) — don't re-suggest it.
- `app/src/mocks/data.ts` and `mockOnboardingState.ts` are temporary scaffolding, not real state —
  don't let either quietly become permanent as real persistence/backend data lands.
- Real Enable Banking data broke assumptions the API reference implied: `entry_reference` is
  missing on a meaningful share of real debits (composite fallback key, not just a stopgap); a
  creditor IBAN appears on almost no card payments, so the normalized payee name carries most of
  the matching weight; the connected account's `uid` changes on every consent — key debits on the
  IBAN, never on `uid`. `credit_debit_indicator` is confirmed `DBIT`/`CRDT` — the API reference's
  `DBTR` was wrong.
- A charge's currency must be checked, not assumed EUR — comparing a foreign-currency debit
  against a EUR limit is a false or missed alert; non-EUR charges are skipped and logged instead.
- The `Subscription` payment type is unreachable from any real bank data seen so far — real
  recurring charges (Netflix, etc.) arrive as plain card payments.
- FCM's `Message.Token` is deprecated in FirebaseAdmin 3.6.0 in favor of `Fid`, a different
  identifier — pinned around deliberately (`// PRAGMATIC`), not "fixed" by switching blind. Only
  the `TokenNoLongerValid` outcome may prune a device token; `Rejected` means a human needs to
  look, not that the alert can be silently discarded.

<!-- /bmad:context -->

## Work tracking with Beads (decided 2026-10-05)

Beads (`bd`, issue prefix `gn-`, data in `.beads/`) holds the **state and order** of work. BMAD
keeps the **what and why**: `epics.md` and `REQUIREMENTS.md` stay the only place for acceptance
criteria — a story bead carries only a title and `--spec-id` pointing at its `epics.md` section,
never a copy.

- **Start of a session:** `bd ready` (what is unblocked), `bd show <id>` for one item. The state of the work is
  never written into `CLAUDE.md`; its Status section is a few lines that point here.
- **Per story:** pick the bead, plan in plan mode as before, `bd update <id> --claim`, give the coder
  the bead id. Never change the build/review process — Beads only tracks it.
- **Review findings:** a MUST/SHOULD FIX goes back to the same coder as before. Anything deferred
  becomes a new bead (`bd create ... -l review-finding`, then `bd dep relate` to its story) —
  **not a new `TODO.md` entry**. `TODO.md` is frozen; its 74 open items were imported as beads on
  2026-10-05 (label `todo-md`) and the file stays only as archive.
- **Done:** `bd close <id> -r "<what> — commit <hash>"` in the same pass as the commit.
- **Dependencies:** `bd dep add <blocked> <blocker>`; check `bd dep cycles` after bulk changes.
- **Do not:** use `bmad-sprint-planning` (a second tracker), run `bd setup claude` or
  `bd init` again (they rewrite `AGENTS.md`/`settings.json`; init was run with `--skip-agents
  --skip-hooks`), install Beads git hooks, or push the Dolt remote (`bd dolt push`) without being asked.
- **Git:** `.beads/issues.jsonl` is the committed export (`export.auto` is on); the Dolt database
  itself (`.beads/embeddeddolt/`) is git-ignored. After bead changes run
  `bd export > .beads/issues.jsonl` if the file is stale, and commit it only when asked, like
  everything else. Usage metrics are switched off (`bd metrics off`).
- The `bd` CLI was installed with `npm install -g @beads/bd`; it runs on this machine despite
  Smart App Control (checked 2026-10-05).

## Work after Tracer 3: plan with BMAD, record in the right place (decided 2026-10-16)

The three tracer documents are history and stay as they are; there is no fourth tracer. New work is planned with BMAD
and built with the process in Policy.

- **Where what is written** (one place per thing):
  - what the app does: `REQUIREMENTS.md`; design decisions: `ARCHITECTURE.md`; plan of a story (description,
    acceptance criteria): `_bmad-output/planning-artifacts/epics.md` — **a plan, no "As built" blocks any more**;
  - what was built, measured or decided while doing a story (numbers, findings, deviations): the **notes of its
    bead** (`bd update <id> --append-notes`) and the commit message; a decision that changes the design also goes into
    `ARCHITECTURE.md`; operating knowledge goes into `AGENTS.md`;
  - state and order of work: Beads. `CLAUDE.md` Status stays a few lines.
- **Done 2026-10-19** (bead `gn-tn2`): `ARCHITECTURE.md` matches the as-built system again, and the "As built" blocks of
  `epics.md` (13 stories) were moved into the notes of their beads and removed (the file also held a stale second copy of
  Epic 1, now gone). BMAD plans from these documents; keep them this way: no history in `epics.md`, no stale design in
  `ARCHITECTURE.md`.
- **How the documents refer to each other** (decided 2026-10-19). The chain is `REQUIREMENTS.md` (R: what must hold, and
  why) → epic → story → bead; `ARCHITECTURE.md` (A: how it is built) is a side input to the stories. A reference points to
  the place where a thing is written; it never copies the text.
  - **Epic:** one goal for the owner in a sentence, and the R numbers it delivers (one line). It does not list its
    stories; they belong to it through their number (story 3.2 is in epic 3, bead `gn-yhb.2` is a child of `gn-yhb`).
  - **Story:** its head names the R it delivers and the A it must respect. A is the **section title** in
    `ARCHITECTURE.md` (for example `ARCHITECTURE.md, "Ingestion modes"`), not a number. The acceptance criteria are
    testable behaviour (Given/When/Then); they may be concrete but do not repeat a rule's wording or its numbers
    ("at the pace of R29", not "every 6 h").
  - **No coverage table** (decided 2026-10-21): the stories already name the R they deliver, so no second list is kept.
    At every planning round, check the other direction by search: for each R that changed or is touched, `grep` its
    number in `epics.md`. An R that is neither named by a story nor already built (baseline) nor deferred is a gap.
  - **A story needs behaviour no R states?** First write the R in `REQUIREMENTS.md`, then the story. A design decision
    made while building goes into `ARCHITECTURE.md`; if the title of an A section changes, search `epics.md` for it.
  - **State:** the stories of Tracer 3 cite R numbers and A section titles (the old FR/NFR/AR numbers are gone from
    `epics.md`; `git show 28e4587:_bmad-output/planning-artifacts/epics.md` has the lists). The three UX-DR were left
    as they were.
- **Planned 2026-10-21** (`bmad-create-epics-and-stories`): Epic 5 (connect a bank from the app, `gn-hkx`), Epic 6
  (delete a bank), Epic 7 (reconnect an expired bank), in that order; each story has a bead. Epic 7 should be built
  before the DKB and N26 consents run out (valid until 2027-01-19); until then the shell tool reconnects them. Still to
  plan: the resilience beads (`gn-emh`, `gn-a64`, `gn-ftd`) and any further screens.
- BMAD plans only. Building and reviewing stays as in Policy (plan mode, `coder-*`, `coder-reviewer`); do not use
  `bmad-build`.

## Local backend setup notes (outside the managed block; added 2026-10-07)

- Story 2.10: the backend also refuses to start without `Brevo:ApiKey`, `Brevo:SenderEmail`,
  `Brevo:SenderName` and `Alerts:RecipientEmail` (user secrets, project `backend/GreedyNose.Api`; never
  print or commit the key). The failure mail is sent through Brevo; keep Brevo's "Authorized IPs"
  blocking off. Apply migration `AddPushHealth` (`bash tools/ef-container.sh database update`).

- Story 1.7: the backend also refuses to start without `Consent:EncryptionKey` (base64 of exactly 32
  random bytes, e.g. `dotnet user-secrets set "Consent:EncryptionKey" "$(openssl rand -base64 32)"
  --project backend/GreedyNose.Api` — never print or commit the value). Consents live encrypted in the
  `BankConsents` table; `consent.local.json` is no longer used. Apply the migration before the first
  start (container recipe: `bash tools/ef-container.sh database update`; `migrations list` is
  read-only). A wrong key stops the
  backend at startup on purpose. After switching, connect a bank again with `bash tools/connect-bank.sh`.
