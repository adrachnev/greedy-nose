<!-- bmad:context -->
<!-- Verified 2026-09-23 against 9acae0f. Managed by bmad-project-context; edits inside this block are replaced on refresh. Keep anything you want preserved outside the markers. -->

## Greedy Nose

A mobile app (iOS + Android) that connects one European bank account and notifies the user the
moment a payee flagged "Bad" charges them — deliberately not a general finance app. React Native
client in `app/`, ASP.NET Core / Azure Functions (C#) backend in `backend/`. `REQUIREMENTS.md` is
the single source of truth for product behavior; where anything else disagrees, it wins.

## Policy

- Always respond in English in this project, regardless of any global default language.
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

## Where things are

- `REQUIREMENTS.md` — read first. `ARCHITECTURE.md` — the design doc, reconciled with it.
  `NOTIFICATION-TRACER-BULLET.md` — the live step-by-step doc for current work; read its Progress
  section first. `TODO.md` — known implementation gaps and review findings; check before starting
  so a finding isn't rediscovered.
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
