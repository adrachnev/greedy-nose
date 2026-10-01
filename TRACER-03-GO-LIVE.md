# Tracer bullet 3 — go-live

The third tracer bullet, same discipline as `TRACER-01-BANK-DATA.md` and
`TRACER-02-NOTIFICATIONS.md`: the thinnest end-to-end slice, seen working before moving on.
This one takes Greedy Nose from "runs on the owner's laptop" to a **first productive version for
the owner only**, on Azure. Started 2026-09-29; this file is the live document — steps, Progress,
decisions, findings. The BMAD epics and stories live in
`_bmad-output/planning-artifacts/epics.md`; each step below points to its story, so there is one
source of truth per thing.

## The path

```
N26 + DKB + ING-DiBa  →  Enable Banking (Production)  →  Azure Functions + Neon Postgres  →  FCM  →  the owner's phone
```

## Scope (decided 2026-09-29, sharpened 2026-10-01)

- **Owner only.** Enable Banking's Restricted Production allows only accounts that belong to the
  Control Panel user, and forbids making the API available to third parties. Family accounts need
  a contract and a company check (KYB) — see `ARCHITECTURE.md`, "Cost".
- **All three banks, one account each: N26, DKB, ING-DiBa**, one merged debit list (`R22a`) —
  **decided 2026-10-01: go-live is not done with N26 alone.** A consent per bank, the worker walks
  all of them, one bank's failure is isolated from the others.

## Decisions (2026-10-01, from the requirements walk-through; detail in `epics.md`)

- **Hosting:** Azure Functions, isolated worker model; the worker loop becomes a 6 h timer trigger.
  Fallback: Container Apps with one always-on replica (~$1–3/month).
- **Database:** Neon free tier; migrations applied manually from the owner's machine.
- **Authentication:** one static API key over HTTPS, behind a single "current user" seam; new code
  adds no `SeedData.UserId`; `BankConsents` is keyed `(UserId, bank)`.
- **Secrets:** Key Vault (Enable Banking private key, session encryption key), app settings for
  the rest.
- **Redirect:** `https://<name>.azurewebsites.net/callback`, no custom domain; consents are
  finished in the browser (no deep link back into the app); all three consents are granted fresh.
- **First sync:** last 12 months (R25 changed), pagination with `continuation_key`.
- **Failed pushes** are retried (R11a, reverses the 2026-09-29 decision): "push owed" mark, 24 h
  limit, one email to the owner after 3 failed attempts in a row. The reconnect summary push (R20)
  is retried the same way (decided 2026-10-01).
- **Release build:** sideloaded, self-signed APK.
- **First story:** make `dotnet test` runnable (Windows Application Control blocks fresh DLLs).

## Not in this tracer

In-app onboarding (R26), the in-app expired banner (`01d`), Disconnect / Delete account (R18),
the Health Monitor job (only the failed-push email comes in), iOS, a bank-selection screen,
per-bank labels in the debit list, other users' accounts.

## Open questions

- How each bank behaves when consent is approved in its own app (DKB, ING) — recorded as a finding
  on the first real consent.
- Each bank's `maximum_consent_validity` — read with the Production application.
- Whether each fetched page counts against a bank's ~4 fetches per day.
- Whether `DELETE /sessions/{id}` also revokes the consent at a real bank.
- Behaviour of the target phone when the app is force-stopped.

## Progress

| Step | State |
|---|---|
| Slice into epics and stories (`bmad-create-epics-and-stories`) | **done 2026-10-01** — 4 epics, 30 stories in `_bmad-output/planning-artifacts/epics.md`; push retry and failure email (2.9, 2.10) come before the live proof (2.11) |
| Epic 1 — a backend I can trust to deploy (1.1–1.8) | in progress — **1.4 done 2026-10-01** (consent start protected: one-time link + state in `PendingConsents`, 398 tests, race verified live); **1.3 done 2026-10-01** (API key middleware, 346 tests, live check in a container; the backend no longer runs on the host — `tools/run-backend-container.sh`); **1.2 done 2026-10-01** (app sends `Authorization: Bearer <key>`, 211 jest tests, bundle check passed); **1.1 done 2026-10-01**: Smart App Control (not a managed policy) blocked every fresh DLL; tests now run in a Linux container (command in `AGENTS.md`), **baseline 310 passed, 0 failed**, ~11 s, repeatable; the 1.2–1.8 stories are next |
| Epic 2 — N26 live on Azure, end to end (2.1–2.11) | not started |
| Epic 3 — DKB and ING-DiBa watched too (3.1–3.8) | not started |
| Epic 4 — never silently without alerts (4.1–4.3) | not started |
