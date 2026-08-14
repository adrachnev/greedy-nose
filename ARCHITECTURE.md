# Architecture

Status: decided at a high level; not yet implemented. Written after the mocks settled, per
the project's mocks-first ordering — but **before `REQUIREMENTS.md` existed, and not yet
reconciled with it**.

## ⚠ Known divergence from `REQUIREMENTS.md`

Reviewed against the spec on **2026-08-14**; the reconciliation itself is the next session's
work, and it happens **before any code is written** (owner's call — a design that contradicts
the spec would otherwise get built into both halves of the system before anyone notices).
Everything not listed here still describes the intended system, but do not implement `A1`–`A4`
as written — `REQUIREMENTS.md` wins wherever the two disagree.

**Contradictions — the text below is wrong, not merely incomplete:**

- **A1 — The rule engine's logic is inverted.** "Backend → Rule engine" and the diagram edge
  `Rules -- "Bad, thresholds (if set) met" --> Notify` both say: if the payee is **Bad**,
  evaluate the amount threshold. R5 says the reverse — the amount only applies to a **good**
  payee, and a bad payee alerts on every charge with the amount ignored. Built as written,
  every bad payee carrying an old limit would fall silent, which is the one failure R1 exists
  to prevent.
- **A2 — The consent-expiry push is explicitly ruled out.** "Reliability: poll health
  monitoring" routes expired consents to in-app state only, and the diagram sends
  `Health --> App` without touching FCM. R19 requires a **push to the user** when the consent
  expires by itself, because a user who does not open the app for days cannot see in-app state.
- **A3 — Frequency/count thresholds are gone** (amount is the only threshold). Still present in
  the component table, the "Rule engine" bullet, and its rolling-window rationale.
- **A4 — Terminology (R0)**: `debitor` → payee, `Trusted`/`Bad` → good/bad, `Transactions`
  table and "tx history" → debits. Bank-facing uses stay as they are: Enable Banking's
  *transaction feed* and the bank-issued *transaction ID* keep their names.

**Gaps — the spec requires something this document does not describe:**

- **A5 — Nothing filters incoming money (R2a).** Enable Banking returns credits and debits;
  R2a says credits are never listed, never notify, and never create a payee. That filter has
  to live in the ingestion worker, and without it the first salary payment creates an alerting
  payee.
- **A6 — Payee identity is one phrase**, "matches counterparty → debitor". R3a's three-tier key
  (SEPA creditor ID → IBAN → normalized name), storing the resolved key with the raw strings
  seen, and R3b's "split rather than merge when uncertain" have no design yet. This is the
  hardest correctness problem in the system.
- **A7 — Classification is derived, never stored (R6).** Not stated anywhere. R7 needs a rule
  edit to re-label existing debits instantly — free if derived at read time, a migration job if
  persisted on the debit.
- **A8 — Reconnecting after a gap is an unhandled third mode (R20).** The worker has two:
  first-run bulk pull and steady-state incremental. A reconnect is neither — the gap's charges
  are unseen IDs, so steady state would fire one push each instead of R20's single summary.
- **A9 — No data lifecycle for R18**: disconnect keeps rules and history, account deletion
  wipes both.

**Smaller:** the rule engine must hand the *reason* to the dispatcher so R12a can word the body;
R16's "compare against the booked amount in the account currency" is unstated; and "Initial sync
vs steady-state polling" still cites `01c-classify-debitors.html`, renamed to
`01c-classify-payees.html`.

**Verified aligned:** "Transaction identity" matches R10b exactly. The polling design, health
monitoring and cost sections are untouched by the spec.

## Component diagram

```mermaid
%%{init: {'themeVariables': {'fontSize': '28px'}, 'flowchart': {'nodeSpacing': 60, 'rankSpacing': 90}}}%%
flowchart TB
    subgraph Mobile["Mobile Device"]
        App["Mobile App
React Native"]
    end

    subgraph Backend["Cloud Backend — Azure"]
        API["API
ASP.NET Core / Functions"]
        Ingest["Ingestion
Worker
Timer Function"]
        Health["Health Monitor
Timer Function"]
        Rules["Rule Engine"]
        Notify["Notification
Dispatcher"]
        KV["Key Vault"]
    end

    subgraph ThirdParty["Third-Party Services"]
        DB[("Data Store
Postgres, e.g. Supabase/Neon")]
        EB["Enable Banking API"]
        Bank["N26 — ASPSP"]
        FCM["Firebase Cloud
Messaging"]
        Email["Email Alerts
e.g. SendGrid"]
    end

    App -- "auth, CRUD,
start consent" --> API
    API -- "read/write" --> DB
    API -- "encrypt/decrypt
consent tokens" --> KV
    API -- "start consent" --> EB
    Ingest -- "poll 6h /
on app-open" --> EB
    EB --> Bank
    Ingest -- "new tx" --> Rules
    Ingest -- "records
success/failure" --> DB
    Rules -- "read/write" --> DB
    Rules -- "Bad, thresholds
(if set) met" --> Notify
    Notify --> FCM
    FCM -- push --> App
    Health -- "checks staleness" --> DB
    Health -- "bug: alert operator" --> Email
    Health -- "expired consent:
in-app state" --> App
```

| Component | Tech | Execution place | Responsibility |
|---|---|---|---|
| Mobile App | React Native | Mobile device | UI (list/detail/rules/settings), receives push, in-app classification |
| API | ASP.NET Core / Azure Functions (HTTP), C# | Cloud backend | Auth, debitor/rule CRUD, tx history, brokers Enable Banking consent flow |
| Ingestion Worker | Azure Functions (Timer), C# | Cloud backend | Polls every 6h per consent (retries transient failures), plus on-demand fetch when the app is open; records success/failure per consent. First run per consent is a bulk historical pull feeding onboarding classify, not the Rule Engine |
| Health Monitor | Azure Functions (Timer), C# | Cloud backend | Daily check for consents stuck failing 20h+; routes expired-consent cases to in-app state, everything else to an email alert |
| Rule Engine | C# | Cloud backend | Matches counterparty → debitor, evaluates Trusted/Bad + amount/count thresholds (AND logic) |
| Notification Dispatcher | C# | Cloud backend | Sends the push via FCM; alert decision belongs to the Rule Engine only |
| Key Vault | Azure Key Vault | Cloud backend | Holds the encryption key for BankConsent tokens |
| Data Store | Postgres (free tier, e.g. Supabase/Neon) | Third party | Users, BankConsents (encrypted), Debitors, Transactions, NotificationLog, poll status per consent |
| Enable Banking API | PSD2/XS2A aggregator | Third party | Consent flow, transaction feed |
| N26 | ASPSP (bank) | Third party | Source of truth for transactions |
| Firebase Cloud Messaging | Push service | Third party | Push delivery to APNs + Android |
| Email Alerts | e.g. SendGrid | Third party | Notifies the operator when ingestion is failing for a non-consent reason |

## Client

**React Native.** Considered .NET MAUI and Flutter.

- Mocks in `mocks/` are Flexbox HTML/CSS by deliberate choice, so they map closely to RN
  components.
- Given the intent to commercialize later, RN's ecosystem (subscription/monetization SDKs
  like RevenueCat, analytics, A/B testing) and larger hiring pool tipped the decision over
  Flutter. Flutter's main edge (pixel-consistent rendering, smoother animation) matters less
  for this app's plain list/card/settings UI.
- MAUI was attractive given existing C# experience, but was dropped due to a thinner
  ecosystem for the two riskiest integrations (bank consent webview flow, push
  notifications).

## Backend

Separate service from the client — its language doesn't need to match RN. **ASP.NET Core /
Azure Functions (C#)**, leveraging existing team experience for the reliability-critical half
of the system.

Components:

- **API** — auth, debitor/rule CRUD, transaction history for the app's list/detail screens,
  and brokering the Enable Banking consent flow (session start + redirect callback, encrypted
  token storage).
- **Ingestion worker** — see below.
- **Rule engine** — on each new transaction: match counterparty → debitor → Trusted/Bad →
  if Bad, evaluate amount/count thresholds (AND logic, per settled product decision). The
  count threshold's "period" is a rolling window (e.g. last N days), not a calendar
  month/week — more intuitive for the user, no confusing reset at a boundary.
- **Notification dispatch** — Firebase Cloud Messaging (unified path to APNs + Android).
- **Data store** — Users, BankConsents (encrypted), Debitors, Transactions (cache), and a
  light NotificationLog.

## Transaction ingestion: polling, not webhooks

Verified against Enable Banking's docs: webhooks only exist for **payment status** (PIS,
outgoing payments initiated through them), not for account information / new transactions.
There is no push mechanism for "a new transaction appeared" — it must be polled via
`GET /accounts/{account_id}/transactions`.

The binding constraint is on the bank side, not Enable Banking's own limits: most ASPSPs
(N26 included, presumptively) cap **background** data fetches — i.e. when the end user isn't
actively in the app — at **4 times per day**, backing off 6 hours on
`ASPSP_RATE_LIMIT_EXCEEDED`.

Given the product owner's call that same-day notification is an acceptable user experience
(not instant), the design is:

- A timer-triggered poll every **6 hours** (4x/day) per active consent — sits exactly at the
  ASPSP background limit with no rate-limit risk, worst-case latency ~6h.
- When the user has the app open, trigger an **on-demand fetch with PSU headers** (their
  IP/user-agent). Enable Banking's background-fetch limit doesn't apply when there's a
  signal of an active user session, so this is effectively a free "refresh on open" outside
  the 4x/day quota.

## Initial sync vs steady-state polling

First poll after consent is different from every poll after: there's no "last-seen"
transaction to diff against, and the result feeds the onboarding bulk-classify screen
(`01c-classify-debitors.html`), not the Rule Engine — no notifications should fire for
pre-existing history.

Handled as a first-run mode inside the Ingestion Worker (not a separate component): same
fetch logic, but on first run for a consent it pulls full available history and routes it to
onboarding classify instead of the Rule Engine. Once the user finishes classifying, the
worker switches to normal incremental 6h polling for that consent.

Chosen over a separate "Initial Sync" component to avoid duplicating the Enable Banking
fetch logic in two places, at the cost of the worker having two modes to keep straight.

## Transaction identity

Bank transactions can change status (e.g. pending → booked) between polls. To avoid alerting
twice on the same charge: each transaction is keyed by its bank-issued transaction ID, kept
as one record that gets updated in place. The Rule Engine evaluates a transaction the first
time it's seen, regardless of status, and does not re-alert on a later status change alone.

Assumes Enable Banking gives a stable transaction ID across status changes — not yet
confirmed, see Open Questions.

## Reliability: poll health monitoring

The product's core promise — never miss a genuinely new Bad charge — depends entirely on the
6h poll actually running for every user every cycle. A silently failing poll would break that
promise without anyone noticing, so this gets explicit handling rather than being left to
chance:

1. The Ingestion Worker retries transient failures automatically (a few attempts with
   backoff) before giving up on a given run.
2. Every poll attempt — success or failure — is recorded per consent (`LastAttemptAt`,
   `LastSuccessAt`, `LastError`).
3. A separate daily Health Monitor job looks for any consent whose last successful poll is
   more than 20h old (a one-cycle buffer over the 6h cadence) and branches on why:
   - **Expired/revoked consent** — user-fixable, not a bug. Surfaced in-app via the existing
     `01d-connection-expired.html` state; no operator alert.
   - **Anything else** (API error, unexpected exception) — this means monitoring is broken
     for reasons the user can't fix themselves, so it emails the operator directly.

This keeps alerts limited to the failures that actually need a human to intervene.

## Consent lifecycle

PSD2 access consents expire periodically (commonly ~90 days) and need re-authorization. This
needs its own check/notification path, independent of the transaction poller — the mock
`01d-connection-expired.html` already anticipates this state.

## Cost

Enable Banking gives free "Restricted Production" access for accounts you personally link,
matching the project's zero-cost-to-start decision and current single-user/N26 scope. Paid,
volume-based pricing (unpublished, contact-sales) only becomes relevant once onboarding other
people's accounts at commercial scale.

On Azure: Functions (consumption plan), the storage account behind them, and Key Vault are
all effectively free at this scale. Azure SQL was the one component with a real baseline
cost even serverless, which is why the data store moved to a free-tier Postgres host
(Supabase/Neon) instead — keeps the whole backend at zero cost to start.

## Accepted tradeoffs

- **On-demand fetch cold start**: the app-open on-demand fetch runs on Azure Functions'
  consumption plan, which can take a few seconds to wake up if idle. Accepted as-is (no
  Premium plan, to keep costs at zero) — the syncing screen's UI should be designed to feel
  intentional during this wait rather than stuck.

## Open questions

- Confirm N26 specifically enforces the "4x/day background" limit (documented as an ASPSP-
  general behavior, not verified per-bank).
- Enable Banking's own request rate limits/quotas on the free tier are not published —
  worth confirming before scaling beyond a handful of users.
- Confirm Enable Banking gives a stable transaction ID that survives a status change
  (e.g. pending → booked), since the duplicate-alert prevention in Transaction identity
  depends on it.
