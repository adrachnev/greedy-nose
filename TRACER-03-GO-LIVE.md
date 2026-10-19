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
- **First sync:** last 90 days (R25 changed twice: 12 months on 2026-10-01, 90 days on 2026-10-09 after real N26
  refused 12 months with 422 `WRONG_TRANSACTIONS_PERIOD`), pagination with `continuation_key`.
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
- Whether each fetched page counts against a bank's ~4 fetches per day. **Evidence 2026-10-16 (Application Insights,
  beads `gn-xeq`, `gn-6k5` closed):** every tick at N26 and DKB fetched exactly 1 page (first syncs 108 and 185
  transactions, normal ticks 11–19), 4 ticks a day = 4 transaction requests per bank per day, no 429 in 30 days. The
  bank's own counting of a second page is **not proven** (the Enable Banking FAQ says nothing on `continuation_key`;
  only their support can answer) and never occurs with a 90-day window. The real risk is a fifth fetch in a UTC day
  (a manual tick), not paging.
- ~~Whether `DELETE /sessions/{id}` also revokes the consent at a real bank.~~ Answered for DKB on 2026-10-13: the new
  session kept fetching after the old one was closed.
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
- **Rotating `Consent__EncryptionKey` means re-consent:** the stored consents carry a version byte but no key id,
  so a new key cannot read the old rows — change the Key Vault secret, then connect each bank again.
  Migrations are never applied by the app: after a schema change run `DB=neon bash tools/ef-container.sh database
  update` before `bash tools/azure-deploy.sh`.
- **Azure login (changed 2026-10-16, bead `gn-7n2` closed):** Entra security defaults are **on** (MFA for people).
  The CLI on this machine is logged in as the service principal `sp-greedy-nose-tools` (role Contributor on
  `rg-greedy-nose` only; client id `2bb8a88e-c51a-48cf-a5a4-12d1787f4525`, tenant `19ca364c-1284-42e1-85b5-bfb61dee02a3`),
  so deploy, migrations' Azure side and log queries need no code and no MFA. It was created by hand in the portal
  (App registration, secret, role assignment); the login is `bash tools/az-sp-login.sh <client-id> <tenant-id>
  <secret-file>` (the file lies outside the repo and is deleted afterwards). **The secret expires after 12 months —
  bead `gn-4dy`.** A device-code login (`bash tools/az.sh login --use-device-code`) still works for Azure Resource
  Manager, but Graph and Log Analytics need an extra MFA, so use the service principal.
- **Scripts** (all use the Azure CLI in a container, `tools/az.sh`, nothing to install):
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
  Logs arrive a few minutes late. Since the log privacy fixes (`gn-6yh`, Deploy 2/4) our own lines carry no session id,
  account uid, IBAN, payee or amount; checked 2026-10-16 with regex counts on the message text. From 2026-10-16
  `host.json` sets `Azure.Core` and `Azure.Identity` to Warning (they were about 80 % of the lines). Query as the
  service principal: `WS=$(bash tools/az.sh monitor log-analytics workspace show -g rg-greedy-nose -n log-greedy-nose
  --query customerId -o tsv)`, then `bash tools/az.sh monitor log-analytics query -w "$WS" --analytics-query
  "AppTraces | where TimeGenerated > ago(1d) | where Message startswith 'Tick fetched'" -o tsv` (the app-insights
  query command above needs an extra MFA under security defaults). One request line per Enable Banking call:
  `Enable Banking GET /accounts/{id}/transactions -> 200` (count them for the ~4/day check).
- **Cold start (measured 2026-10-08):** the first `GET /debits` after 12 minutes idle answered 200 in 4.6 s
  (right after the deploy: 6.5 s); the next call 0.17 s. Pushes are not affected (the timer runs the tick).
  Cost check in Azure Cost Analysis is still to do after 2–3 days.
- **Left as they are on purpose (owner's decision 2026-10-08):** the Neon password and the Brevo key were
  pasted into the chat once and are not rotated.

## Findings: first sync at N26 (Story 2.6a, 2026-10-09)

Local host on the Production application, dev database, real N26. Counts only:

- **History window:** `date_from` 12 months back → 422 `WRONG_TRANSACTIONS_PERIOD`; 180 days → 422; **90 days → 200**.
  The limit lies between 90 and 180 days (not narrowed further). Both 422 answers came within ~2 s from Enable
  Banking; whether a 422 costs quota is unknown. **Decision: the first-sync window is 90 days for every bank**
  (R25; `IngestionRunner.FirstSyncWindowDays`).
- **Pages:** the 90-day sync took **1 page, 108 transactions**, oldest booking date 12 July, newest 8 October,
  3 s, no 429, no push. A second page was never seen, so **whether a further page counts against N26's ~4 fetches
  a day is still unmeasured** (`gn-xeq`); with 90 days one page is the normal case. Enable Banking's support agent
  could not say either, and PSU headers do not help at N26.
- **Reconnect vs. first sync:** a database that already knows the account runs the consent's sync as a Reconnect
  (from the last fetch, plus one summary push). To try a real first sync again, delete the account's row in
  `AccountSyncStates` (by hand) and let a tick run.
- **Production needs `https` for the redirect:** only `https://localhost:5199/callback` is registered. Locally a
  Caddy container (`caddy reverse-proxy --from https://localhost:5199 --to http://<host container>:80`, self-signed
  certificate, accept the browser warning) in front of the host on another port
  (`HOST_PORT=5198 bash tools/run-functions-container.sh …`; `connect-bank.sh N26 DE http://127.0.0.1:5198`).
  The one-time link takes its address from the redirect URL, so it already points to the https address.
- **One-off tick:** a host schedule like `0 <minute> <hour> * * *` (daily, so the 6 h floor accepts it) fires once;
  `/admin/functions/…` answers 401 locally.
- **Logs now say more:** "Tick fetched N page(s), M transaction(s) from <date>; oldest booking date …, newest …",
  and a 422/429 from Enable Banking carries its own `message` in the exception text.

## Findings: N26 through the cloud (Story 2.7, 2026-10-09)

- **Switch to Production in Azure:** the Production key went into the Key Vault secret `enablebanking-private-key`
  (a new version; the reference has no version, so the restart picked it up), `EnableBanking__ApplicationId` =
  the Production application, `EnableBanking__RedirectUrl` = `https://func-greedy-nose-611f0.azurewebsites.net/callback`
  (registered in the Control Panel first). All references `Resolved`. The Production key path for the copy script
  lives in the unused user secret `EnableBanking:ProductionPrivateKeyPath`.
- **Consent and first sync:** `bash tools/connect-bank.sh N26 DE https://func-greedy-nose-611f0.azurewebsites.net`,
  approved in N26. The first sync ran one minute later through the queue: **1 page, 108 transactions** from 2026-07-11,
  **101 debits** stored in Neon (booking days 12 July – 8 October), 55 payees, one consent, one sync-state row,
  **0 pushes, 0 notifications**, no 429. `GET /debits` with the key answers 200. Neon was empty before (no Sandbox data).
- **Bank requests of the whole first sync: 1.** Together with the earlier local tests N26 saw about 3 fetches that day
  and answered all of them.
- **Azure CLI blocked by Entra security defaults (error 530035):** after the first deploy the CLI could not sign in
  any more (also the Zip deploy asks for the extra scope `https://appservice.azure.com/.default`); a personal
  Microsoft account cannot use `aka.ms/mfasetup`. Fix used: the owner switched **Security defaults to Disabled**
  (Entra ID → Properties → Manage security defaults), then `bash tools/az.sh login --tenant <tenant id>
  --use-device-code` and once more with `--scope https://appservice.azure.com/.default`, in a private browser window
  at `microsoft.com/devicelogin`. Security defaults are **still disabled** — see the bead for re-enabling or a
  service principal.

## Findings: DKB through the cloud (Story 3.5, 2026-10-13)

- **Owner decision 2026-10-13: ING-DiBa is not connected; two banks (N26, DKB) are enough** for this tracer. Story 3.6 is
  dropped, 3.8 is judged on two banks.
- **Approval flow (AR11):** consent started with `tools/connect-bank.sh DKB DE <cloud url>` on the laptop, "Authentifizierung"
  clicked in the browser, confirmed in the DKB app on the phone; the laptop browser moved on by itself to the callback page
  "Connected". Nothing by hand. Browser and banking app were on two devices; the same-device path (phone only) is untested
  (bead `gn-hkx`).
- **Several accounts:** the consent returned **5 accounts**, all `CACC`/`PRIV`/`EUR`; the products are Tagesgeld (2),
  "Girokonto u18" (2) and "Girokonto" (1). The type alone does not separate them, and the first account (`Accounts[0]`)
  was not the current account: the first sync stored 0 debits. Rule now (commits `c197408`, `ef0b08e`): with several
  accounts only an account typed `CACC` with the exact product "Girokonto" is chosen; zero or several matches choose
  nothing, store nothing and close the new session (a `Pick` result of null). One account is taken as it is. Only the
  chosen account is stored. Docs: `cash_account_type` is a required field, values CACC/CARD/CASH/LOAN/OTHR/SVGS.
  The choice in the app is bead `gn-hkx` (after Tracer 3).
- **First sync (after the rule):** **1 bank request, 1 page, 185 transactions** from 2026-07-15 (90 days), **168 debits**
  stored silently (**0 pushes, 0 notifications**), 40 payees. `/debits` now lists 279 debits and 97 payees, N26 and DKB merged.
  N26 was skipped in that tick (polled within its interval). The old DKB session was closed with `DELETE` (HTTP 200) and the
  new one fetches, so a reconnect does not end the new consent at DKB (Story 4.3's open question, answered for DKB).
- **Tuning data (counts only, Neon, 2026-10-13):**

  | | debits | with `entry_reference` | composite fallback | payees | payees with a creditor IBAN |
  |---|---|---|---|---|---|
  | N26 | 111 | 65 | 46 | 57 | 0 |
  | DKB | 168 | 168 | 0 | 40 | 39 |

  DKB sends `entry_reference` on every debit and a creditor IBAN on almost every payee, so R10b's key and R3a's tier 1
  work there; N26 stays the weaker case (composite key for 41 % of debits, no creditor IBAN).
- **New beads:** `gn-hkx` (connect from the app and choose the account there), the HTTP-client logging of session id and
  account uid in Application Insights (P2), the empty accounts list on a reconnect (P3).

## As built: Enable Banking URLs out of the logs (bead `gn-6yh`, autorun 2026-10-13)

- **Problem:** the default HttpClient logging wrote `Sending HTTP request DELETE .../sessions/<session id>` and `GET .../accounts/<uid>/transactions` into Application Insights (found in Story 3.5). Ids are no credential (every call needs our signed JWT), but a session id is never logged by rule.
- **Fix (code only, no migration, `host.json` unchanged):** `ConfigureHttpClientDefaults(RemoveAllLoggers)` removes the default logging of every client; `RedactedRequestLogHandler` on the one Enable Banking client registration logs one line `Enable Banking {Method} {PathTemplate} -> {StatusCode}`. The path keeps only the fixed segments `auth`, `sessions`, `accounts`, `transactions`, `balances`, `aspsps`, `application`; every other segment becomes `{id}` (fails closed); no query, header, body or exception message (a transport failure logs method, template and exception type at Warning). The same line gives the fetch count against the ~4/day quota and a `-> 429` for the week check `gn-06m`. Brevo gets no URL line any more.
- **Verified:** 794 backend tests in the container (779 before, 15 new); one review (no MUST FIX; the id heuristic was a blocklist, so the fix round made it an allowlist); positive control: with `RemoveAllLoggers` taken out both DI tests fail, restored afterwards.
- **Not done (owner decides):** the deploy. After it, check one tick: the traces hold `Enable Banking GET /accounts/{id}/transactions -> 200` and no URL with an id. **Old traces keep the ids until the Application Insights retention removes them** (the live N26 session uid is among them; not a credential).

## Delivery plan (decided 2026-10-12)

**Rule:** code-only stories are built and tested **locally** (tests in the Linux container, live check against the dev
database and the Sandbox bank), one commit per story, **no deploy in between**. The cloud keeps running the old,
working N26 build meanwhile. Nothing is pushed unless asked.

**Local batch, in dependency order:** 3.1 → 3.2 → 3.3 → 3.4 → 4.1 → 4.3 → 4.2 (4.2 needs 3.2). Beads `gn-yhb.1`,
`.2`, `.3`, `.4`, `gn-9lf.1`, `.3`, `.2`.

**Deploy 1 — after the local batch is done and reviewed.** Collect the schema migrations while building (3.1 has none).
Order is always *migration on Neon first* (`DB=neon bash tools/ef-container.sh database update`), *then code*
(`bash tools/azure-deploy.sh`). Afterwards: `/debits` answers 200, read the next tick in Application Insights, and
check that N26 still syncs without a push storm (first-sync and reconnect modes must stay silent).

**Deploy 2 — done 2026-10-12 (about 14:10 UTC; code only, no migration; checked: `/status` 401 without key and 200 with key,
`/health` ok, `/debits` 200). The tick log of the new build still has to be read once for payee, amount and IBAN (18:00 UTC
tick). Story 3.7's log changes:** The build deployed in Deploy 1 still logs the
payee name, the booking date and the amount of a skipped (non-EUR or unreadable) charge and a 6-character IBAN fragment in
some log lines (Application Insights). The fix is committed; deploy it with `bash tools/azure-deploy.sh` before real DKB/ING data
flows (before Story 3.5), and check once that the tick logs no payee, amount or IBAN.

**Live stories, one by one, with the owner and the cloud:** 2.11 (a real charge gives a push; needs `gn-xeq` and
`gn-6k5` answered for N26), 3.5 (DKB), 3.6 (ING-DiBa), then 3.7 and 3.8 (partly testable locally, proof only live).
A redeploy happens only when a live story needs a code fix.

**Also before go-live:** ~~`gn-7n2` (switch Entra security defaults back on)~~ done 2026-10-16, and the lesson of Story
2.8 — the phone's device token must be in the cloud before live data flows (5 pushes were lost on 2026-10-10 because 2.7
ran before 2.8).

**Deploy 1 — done 2026-10-12 (about 13:45 UTC).** Both migrations below were applied to Neon first (counts unchanged: 108
debits, 57 payees, 1 sync state, 1 consent), the old build kept answering (200 / 401) in between, then
`bash tools/azure-deploy.sh` put the new build in. Checked afterwards on the cloud: `/status` without key 401, with key 200
(N26 `connected`, valid until 2027-01-07, no attempt recorded yet), `/health` `ok` / `connected`, `/debits` 200. **Still to
read:** the first tick of the new build (next schedule 18:00 UTC) in Application Insights — the first attempt must record
`LastAttemptAt` for N26, `lastError` must stay null, and no push may go out (Steady mode, no new debit means no push).

**Migrations (applied to Neon at Deploy 1, in this order):**
1. `20261012114551_AddConsentPollState` (Story 3.2) — four nullable columns on `BankConsents` (`LastAttemptAt`,
   `LastSuccessAt`, `LastError`, `PollIntervalMinutes`); applied to the dev DB only.
2. `20261012121857_AddConsentExpiryNotified` (Story 4.1) — nullable `BankConsents.ExpiryNotifiedSessionHash`, carries over
   the value from `AccountSyncStates` (checked on the dev DB with a fake value); the old column stays (bead to drop it after
   Deploy 1); applied to the dev DB only.

**Bank names for `/connect` (Story 3.3).** The backend checks the name and country against Enable Banking's own list
(`GET /aspsps?country=XX`, read fresh at `/connect-links` and again at `/connect`) and stores the list's spelling.
In use: `N26` / `DE` (confirmed), `Mock ASPSP` / `DE` (Sandbox). DKB and ING-DiBa: the exact strings and each
bank's `maximum_consent_validity` are read with the Production application in Stories 3.5 and 3.6 (the Sandbox list only has
DKB, flagged beta) and recorded here then. N26 and Mock ASPSP state 15552000 s (180 days), so `valid_until` stays 90 days.

## Progress

| Step | State |
|---|---|
| Slice into epics and stories (`bmad-create-epics-and-stories`) | **done 2026-10-01** — 4 epics, 30 stories in `_bmad-output/planning-artifacts/epics.md`; push retry and failure email (2.9, 2.10) come before the live proof (2.11) |
| Epic 1 — a backend I can trust to deploy (1.1–1.8) | in progress — **1.4 done 2026-10-01** (consent start protected: one-time link + state in `PendingConsents`, 398 tests, race verified live); **1.3 done 2026-10-01** (API key middleware, 346 tests, live check in a container; the backend no longer runs on the host — `tools/run-backend-container.sh`); **1.2 done 2026-10-01** (app sends `Authorization: Bearer <key>`, 211 jest tests, bundle check passed); **1.1 done 2026-10-01**: Smart App Control (not a managed policy) blocked every fresh DLL; tests now run in a Linux container (command in `AGENTS.md`), **baseline 310 passed, 0 failed**, ~11 s, repeatable; the 1.2–1.8 stories are next |
| Epic 2 — N26 live on Azure, end to end (2.1–2.11) | in progress — 2.1–2.4, 2.9, 2.10 done (2026-10-07/08); **2.5 done 2026-10-08** (Neon project in eu-central-1, all 12 migrations applied, seed user present; see "Cloud database (Neon)" above); **2.6 done 2026-10-08** (Function App on Flex Consumption in Frankfurt, Key Vault references resolved, https only, 401/200 checked, tick without consent harmless; see "Azure deployment" above) |
| Story 2.6a — the first sync tried against real N26, locally | **done 2026-10-09** — see "Findings: first sync at N26" below |
| Story 2.7 — N26 through the cloud | **done 2026-10-09** — see "Findings: N26 through the cloud" below; the first Steady tick (12:00 UTC) still to be read |
| Story 2.11 — a real charge produces a push on my phone | **done 2026-10-13** — the owner paid 1 EUR at N26; a manual tick (poll time of N26 reset in Neon, `POST /admin/functions/IngestionTick`, 15:03 UTC, the 4th N26 fetch of the UTC day, no 429) stored the debit (`/debits` 279 → 280) and sent **one** push, a heads-up with the "New payee" reason on the phone, laptop disconnected; no second push. Still to watch: the next real N26 tick (00:00 UTC) must not repeat it; a good payee's silence and the force-stopped-app case (AR14) are not tested. `gn-xeq` and `gn-6k5` stay open as checks before go-live (only 1-page syncs so far at both banks) |
| Story 3.5 — DKB through the cloud | **done 2026-10-13** — see "Findings: DKB through the cloud" below; one account chosen by type and product, 168 debits stored silently, no push |
| Story 3.6 — ING-DiBa through the cloud | **dropped 2026-10-13** (owner: two banks are enough) |
| Story 3.7 — each bank reconnects and alerts on its own | **local part done 2026-10-12** (bead stays open) — 752 tests; two-bank tests for reconnect with and without bad debits, dead by time / closed by the bank next to a reconnecting or first-syncing bank, a non-EUR charge skipped and never compared with a EUR limit; the tick now logs one line per bank with the count of skipped charges (bank name and count only). **Privacy fix** found in review: log lines no longer carry payee, date, amount or an IBAN fragment (deploy: "Deploy 2" above). **Closed 2026-10-13:** the skipped-charge line is logged only when the count is above zero; the DKB first sync (185 transactions) and every tick since Deploy 2 produced none, so **no foreign-currency or unreadable charge was observed** at DKB; N26's first sync ran before the count existed. The real reconnect at DKB (13 Oct) replaced the old session and N26 kept being skipped/polled on its own schedule |
| Story 3.8 — two banks, one list, one alert path | **done 2026-10-13, for two banks** (ING-DiBa dropped by the owner) — the phone shows one list with N26 and DKB (day order only: both banks give the booking date without a time, so same-day debits tie, bead `gn-e8s`); a real 0.50 EUR DKB transfer gave exactly one heads-up push (15:12 UTC, manual tick like N26's, poll time of DKB reset in Neon, the 3rd DKB fetch of the day), N26 in 2.11; failing-bank isolation was shown in 3.2. Left as a bead: the week check for `ASPSP_RATE_LIMIT_EXCEEDED` (`gn-06m`, to 2026-10-20) |
| Story 4.2 — I can see my connection status | **done locally 2026-10-12** (not deployed, no migration) — 745 tests; `GET /status` (API key; `bash tools/status.sh`) lists per bank the state, valid-until, last success/attempt and a sanitised last error, plus the number of debits waiting for a push; no session id, IBAN, uid, amount or payee; `GET /health` only says `ok` and the worst real state. Live on the dev DB: 401 without and with a wrong key, both sandbox consents listed. Two review rounds |
| Story 4.3 — a reconnect closes the old session | **done locally 2026-10-12** (not deployed, no migration) — 716 tests; after the callback has stored the new consent the old session of that bank is closed with `DELETE /sessions/{id}` (5 s, best effort, "already closed" counts as fine, logs carry no session id); a dead, announced session costs no fetch and no status call; "dead" now needs a terminal status (CLOSED, EXPIRED, REVOKED); a tick that races a reconnect does not push. **Open finding (first real reconnect):** whether `DELETE` also revokes the consent at a real bank is untested (Sandbox only) — after the first real reconnect check that the new session still fetches. Two review rounds, no tick-level live check (needs the owner's browser) |
| Story 4.1 — a consent that dies during the first sync still alerts me | **done locally 2026-10-12** (not deployed; migration `AddConsentExpiryNotified` pending for Neon) — 695 tests; the "already told" memory lives on the consent row, both death signals work in first-sync mode, told only after a successful send, reconnect resets by the session hash |
| Story 3.4 — the app never names a bank in its own words | **done locally 2026-10-12** (copy only) — `ConnectBankScreen` and the mocks 01, 01b, 01bb, 01d, 01e, 02b, 06, 06b say "your bank" / "Personal account", no fixed "90 days"; placeholder tile uses the theme tokens; new jest test pins the copy (257 tests) |
| Story 3.3 — connecting a bank uses that bank's own limits | **done locally 2026-10-12** (not deployed, no migration) — 688 tests; `AspspResolver` (fresh list, exact match, never guessed), `valid_until` = min(90 d, the bank's maximum); live on the Sandbox: an unknown bank is refused with a clear message and nothing created, `mock aspsp` is stored as `Mock ASPSP` |
| Story 3.2 — the worker walks every bank | **done locally 2026-10-12** (not deployed; migration `AddConsentPollState` pending for Neon) — 648 tests; live on the dev DB two consents at once: the stale N26 sandbox consent failed (404) and the Mock ASPSP still synced in the same tick, per-bank attempt/success/error recorded. New: per-bank budget `Ingestion:BankTimeout` (40 s, bead `gn-ftd` for multi-page banks) |
| Story 3.1 — the worker takes the user from the consent | **done locally 2026-10-12** (not deployed, no migration) — 617 tests, live check on the dev DB with a fresh Sandbox consent |
| Story 2.8 — release app on the phone | **done 2026-10-12** — signed release APK (arm64; key `C:\Users\Nutzer\.greedy-nose\release.jks` plus untracked `app/android/keystore.properties`), cloud URL from untracked `app/src/data/backend.local.ts`, https only; release status line (unreachable / not authorized / not configured / unreadable / empty / alerts not set up). Verified: `DeviceTokens` = 1 and one rule in Neon. **Lesson:** 2.7 ran before 2.8, so 5 pushes (2026-10-09 18:00 to 10-11 00:00 UTC) ended `NoToken`/`GaveUp` and the failure mail came — the 2.10 mail worked as designed. Those pushes are lost (the 24 h retry is over). |
| Epic 3 — DKB and ING-DiBa watched too (3.1–3.8) | **closed 2026-10-13, two banks** (ING-DiBa dropped by the owner, 3.6 not done) |
| Epic 4 — never silently without alerts (4.1–4.3) | **done 2026-10-12** (local), deployed at Deploy 1 |
| Closing steps before go-live | **2026-10-16:** `gn-6yh` (logs without ids) deployed and checked; `gn-djm` (old column) dropped on dev and Neon (commit `694fd20`); `gn-7n2` done (security defaults on, service principal login, see "Azure login"); `gn-xeq` and `gn-6k5` closed on log evidence (see Open questions), the two leftovers are `gn-61q` and `gn-ubu` (P3); SDK logs to Warning (`a98be13`). **Closed 2026-10-19:** `gn-06m` (week check for 429) done: no 429 at N26 or DKB since 2026-10-13, 4 fetches per bank per day, all 200, both banks `connected`, `lastError` null. **Tracer 3 is closed.** |
