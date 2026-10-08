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

## Cloud database (Neon) — Story 2.5

- Neon free tier, region eu-central-1 (Frankfurt), database `neondb`. Use the **direct** host (no
  `-pooler`): the EF migrations take advisory locks that a pooler does not support.
- The connection string lives only as the user secret `ConnectionStrings:Neon` (format:
  `Host=…;Port=5432;Database=…;Username=…;Password=…;SSL Mode=Require;Timeout=30;Command Timeout=60`).
  In Azure it becomes a Key Vault / app setting (Story 2.6). Never in Git, logs or docs. The backend
  refuses a non-local host without `SSL Mode=Require|VerifyCA|VerifyFull`, and one with
  `Trust Server Certificate=true` (`PostgresConnectionCheck`); `UseNpgsql` retries transient errors
  (5 times, up to 15 s apart) so the first query after Neon's idle suspend (5 min) can wait for the resume.
- **Apply migrations by hand, from this machine, never at startup:**
  `DB=neon bash tools/ef-container.sh migrations list` (read-only), then
  `DB=neon bash tools/ef-container.sh database update`. The seeded user comes with the migrations.
  Run the local backend against Neon with `DB=neon bash tools/run-functions-container.sh <name>`.
- Resetting the password in the Neon console means setting the secret again.

## Azure deployment — Story 2.6

- **Address:** `https://func-greedy-nose-611f0.azurewebsites.net` (HTTPS only; `http://` answers 301 to https).
  Everything lives in resource group `rg-greedy-nose`, region Germany West Central (Frankfurt), subscription
  "Azure subscription 1" (pay-as-you-go; budget alert 5 € a month).
- **Plan:** Flex Consumption (Linux, dotnet-isolated 10.0, 512 MB instances, no always-ready instances) —
  .NET 10 runs on Linux only there. Free grant per subscription: 250,000 executions and 100,000 GB-s a month.
  Storage `stgreedynose611f0` (Standard_LRS); the app reaches it through its own identity
  (`AzureWebJobsStorage__accountName`, roles Blob Data Owner / Queue Data Contributor / Table Data
  Contributor) — no storage key in any setting. Monitoring: Application Insights `appi-greedy-nose` in
  Log Analytics `log-greedy-nose`.
- **Secrets:** Key Vault `kv-greedynose-611f0` (RBAC). The Function App's system identity has "Key Vault
  Secrets User"; six settings are Key Vault references (`Api__Key`, `Consent__EncryptionKey`,
  `ConnectionStrings__Postgres` = Neon, `Brevo__ApiKey`, `EnableBanking__PrivateKey` = the PEM text,
  `Firebase__ServiceAccountJson` = the JSON text). Plain settings: `IngestionSchedule` (`0 0 */6 * * *`),
  `Brevo__SenderEmail/SenderName`, `Alerts__RecipientEmail`, `EnableBanking__ApplicationId` and
  `EnableBanking__RedirectUrl`. **Both Enable Banking values are still the Sandbox ones — Story 2.7 switches them
  to the Production application and the Azure redirect URL.** `AzureWebJobsStorage` is never a Key Vault reference.
- **Scripts** (all use the Azure CLI in a container, `tools/az.sh`, nothing to install; `bash tools/az.sh login
  --use-device-code` once):
  - `tools/azure-setup.sh` — creates everything above; safe to run again.
  - `tools/azure-secret.sh <vault> <name> secret:<UserSecretKey>|file:<UserSecretKey>` — copies one local value
    into the Key Vault without printing it.
  - `tools/azure-deploy.sh` — publishes in a container, zips, deploys (`config-zip`; `az functionapp deploy`
    answers 415 on Flex).
  - Check the settings: `bash tools/az.sh rest --method get --url "https://management.azure.com<app id>/config/configreferences/appsettings?api-version=2023-12-01"`
    (every reference must say `Resolved`).
  - Trigger the timer by hand: `POST https://<host>/admin/functions/IngestionTick` with `x-functions-key`
    (master key from `az functionapp keys list`), body `{}` → 202.
- **Logs:** Application Insights; query with `bash tools/az.sh monitor app-insights query --app appi-greedy-nose -g
  rg-greedy-nose --analytics-query "traces | where timestamp > ago(30m) | project timestamp, message"`.
  They hold SQL text and the fixed owner id, no secrets. Logs arrive a few minutes late.
- **Cold start (measured 2026-10-08):** the first `GET /debits` after 12 minutes idle answered 200 in 4.6 s
  (right after the deploy: 6.5 s); the next call 0.17 s. Pushes are not affected (the timer runs the tick).
  Cost check in Azure Cost Analysis is still to do after 2–3 days.
- **Left as they are on purpose (owner's decision 2026-10-08):** the Neon password and the Brevo key were
  pasted into the chat once and are not rotated.

## Progress

| Step | State |
|---|---|
| Slice into epics and stories (`bmad-create-epics-and-stories`) | **done 2026-10-01** — 4 epics, 30 stories in `_bmad-output/planning-artifacts/epics.md`; push retry and failure email (2.9, 2.10) come before the live proof (2.11) |
| Epic 1 — a backend I can trust to deploy (1.1–1.8) | in progress — **1.4 done 2026-10-01** (consent start protected: one-time link + state in `PendingConsents`, 398 tests, race verified live); **1.3 done 2026-10-01** (API key middleware, 346 tests, live check in a container; the backend no longer runs on the host — `tools/run-backend-container.sh`); **1.2 done 2026-10-01** (app sends `Authorization: Bearer <key>`, 211 jest tests, bundle check passed); **1.1 done 2026-10-01**: Smart App Control (not a managed policy) blocked every fresh DLL; tests now run in a Linux container (command in `AGENTS.md`), **baseline 310 passed, 0 failed**, ~11 s, repeatable; the 1.2–1.8 stories are next |
| Epic 2 — N26 live on Azure, end to end (2.1–2.11) | in progress — 2.1–2.4, 2.9, 2.10 done (2026-10-07/08); **2.5 done 2026-10-08** (Neon project in eu-central-1, all 12 migrations applied, seed user present; see "Cloud database (Neon)" above); **2.6 done 2026-10-08** (Function App on Flex Consumption in Frankfurt, Key Vault references resolved, https only, 401/200 checked, tick without consent harmless; see "Azure deployment" above) |
| Epic 3 — DKB and ING-DiBa watched too (3.1–3.8) | not started |
| Epic 4 — never silently without alerts (4.1–4.3) | not started |
