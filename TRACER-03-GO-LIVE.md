# Tracer bullet 3 — go-live

The third tracer bullet, same discipline as `TRACER-01-BANK-DATA.md` and
`TRACER-02-NOTIFICATIONS.md`: the thinnest end-to-end slice, seen working before moving on.
This one takes Greedy Nose from "runs on the owner's laptop" to a **first productive version for
the owner only**, on Azure. Started 2026-09-29; this file is the live document — steps, Progress,
decisions, findings. The BMAD epics and stories stay in `_bmad-output/`; each step below points to
its story, so there is one source of truth per thing.

## The path

```
N26 + DKB + ING-DiBa  →  Enable Banking (Production)  →  backend on Azure + cloud Postgres  →  FCM  →  the owner's phone
```

## Scope (decided 2026-09-29)

- **Owner only.** Enable Banking's Restricted Production allows only accounts that belong to the
  Control Panel user, and forbids making the API available to third parties. Family accounts need
  a contract and a company check (KYB) — see `ARCHITECTURE.md`, "Cost".
- **Three banks, one account each: N26, DKB, ING-DiBa**, one merged debit list (`R22a`). Go-live
  may start with N26 alone, but the model is multi-bank from the start: a consent per bank, the
  worker walks all of them, one bank's failure is isolated from the others.

## Blockers to deploying (rough order)

1. The debit list comes from the database, not live from the bank (`/debits` calls the bank on
   every request; N26 allows ~4 fetches a day).
2. Authentication on every endpoint; remove the debug endpoints (`/raw`, `/aspsps`, an open
   `/connect`).
3. The consent moves from a local file into an encrypted table; secrets into Key Vault / app
   settings.
4. Hosting decision: the worker is an in-process `BackgroundService` and needs an always-on host,
   or must be rebuilt as a timer Function (isolated worker model). Plus cloud Postgres and its
   migrations.
5. An HTTPS address registered as the redirect URL in the Enable Banking *production* application
   (Sandbox and Production differ).
6. Switch back to the Production application (the N26 consent backup is valid to 2026-12-15).
7. A release build of the app pointing at the cloud address.

Can wait: in-app onboarding (connect N26 once through the backend page, classify payees through
the Rules screen), the Health Monitor mail, iOS, the in-app banner.

## Open questions

- The redirect path back into the app (DKB and ING switch into their own banking apps) — the
  biggest unknown of the onboarding work.
- Hosting: timer Function vs a small always-on host.
- Each bank's `maximum_consent_validity` — read `GET /aspsps?country=DE` with the Production
  application.

## Progress

| Step | State |
|---|---|
| Slice into epics and stories (`bmad-create-epics-and-stories`) | **not started** |
