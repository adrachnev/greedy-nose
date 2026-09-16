# Tracer bullet — real data from Enable Banking

Plan for the first code that touches a real bank. Written 2026-08-17.

A tracer bullet in the Pragmatic Programmer sense: the **thinnest end-to-end path through every
layer**, built to be seen working and corrected in flight — not a prototype to throw away, and
not a layer built in isolation. `REQUIREMENTS.md` still says what the app does; this file says
only how the first shot is fired.

## The path

```
Enable Banking (sandbox)  →  local C# API  →  LAN  →  DebitListScreen on the device
```

One account, hardcoded. Everything the bullet does not need is deliberately absent — see
"Deliberately out of scope".

## Decisions taken before writing this (2026-08-17)

| | Decision | Why |
|---|---|---|
| Depth | All the way to the **device** | A bullet that stops at the backend proves the wiring of one layer, not the aim |
| Bank | **Sandbox ASPSP first**, then real N26 | The consent loop can be re-run endlessly while wiring up; real data is still the point (steps 6–7) |
| Stack | **ASP.NET Core minimal API**, run locally with `dotnet run` | The C# from `ARCHITECTURE.md`, without the Azure Functions toolchain. Splitting into Functions later is an entry-point change, not a rewrite |
| Credentials | **Registration still to do** — step 0 | No Enable Banking application exists yet |

## Progress

| Step | State |
|---|---|
| 0 Register the application | **done** — sandbox app `007a8a74-7a48-4213-82e1-d017d44b81b0`, key at the repo root, gitignored. Restricted Production: **status unknown, check the console** |
| 1 JWT + first call | **done, verified** — `GET /aspsps?country=DE` returned 686 banks through our own JWT |
| 2 Consent round trip | **done, verified 2026-08-18** — the Mock ASPSP consent completed in a browser and `/callback` showed the account UID |
| 3 Raw transactions | **done, verified 2026-08-18** — 100 transactions, 2026-05-13 … 2026-08-18, in `raw/transactions-20260818-122928.json`. See Findings |
| 4 Map to the domain | **done, verified 2026-08-18** — `GET /debits` returns 92 debits and 45 payees from the 100-row dump: 92 unique ids, no orphan payees, no zero or negative amounts, the 8 credits gone. Reviewed against Enable Banking's own C# sample the same day |
| 5 Point the app at the backend | **done, verified on the device 2026-08-19** — the Debits tab lists the real sandbox charges. See "Step 5 as built" |
| 6–7 | not started; 6 waits on Restricted Production |

### Step 5 as built — 2026-08-19

The seam held: no screen learned where its data comes from. What changed, all under `app/`:

- **`src/data/config.ts`** — `USE_BACKEND` (currently `true`), the base URL and a request
  timeout. Flipping back to fixtures is one line in one file, which is what "keep the fixtures
  reachable behind a flag" meant.
- **`src/data/backendFeed.ts`** — `GET /debits` into a module store shaped exactly like the
  fixture store, so `hooks.ts` can bind either. Same new-array-reference discipline, and the
  empty case is a shared constant because a fresh `[]` from `getSnapshot` is an infinite render
  loop rather than a cosmetic problem. It **validates what it reads**: a row with an unusable
  amount, timestamp or id is dropped and counted, and an **unknown `paymentType` is kept**, not
  dropped — hiding real money because the bank used a word we lack is the one trade this product
  cannot make. An orphan debit (one whose payee the payload never sent) was dropped here at
  first; the review round below replaced that with a placeholder payee, because a drop is the
  charge-you-never-see failure `R1` exists to prevent.
- **`src/data/hooks.ts`** — the source is bound **once at module level**, not branched inside
  each hook: `USE_BACKEND` cannot change while the process lives, and binding per call would
  subscribe to both stores and start a backend fetch even in fixture mode. `useAddDebit` was
  removed — it wrote to the fixture store, so under the live feed it would have been a silent
  no-op wearing the name of the thing that adds a charge.
- **`src/components/DataSourceBadge.tsx`** replaces `MockDataBadge.tsx`. Once the feed is real
  an empty list is ambiguous — backend down, `adb reverse` missing, consent expired, or an
  account with genuinely no debits all paint the same blank screen — and the device is the most
  expensive place to guess. The bar says which, and tapping it retries.
- **`src/screens/DebitDetailScreen.tsx`** — the IBAN and Reference rows render only when the
  bank actually sent the field. This was `TODO.md`'s open question for step 5, and real data
  answered it: a labelled row with nothing after it reads as data the app lost.

**Rules stay local under both flags** — the backend sends no classification (`R6`), so there is
nothing on the wire to switch. Two consequences worth knowing before the next session:

- The two sources use **different payee id schemes** (`payee-netflix` vs. `name:LIDL CONNECT`),
  so fixture rules simply match nothing against live data. Every real payee therefore reads as
  unreviewed, therefore bad — which is `R4b` behaving correctly, not a bug.
- Rules are still **in memory only**. Mark a payee good, reload the bundle, and it is gone.
  Acceptable for the bullet, wrong the moment anyone uses the app; see `TODO.md`.

`npx tsc --noEmit` clean, `npx eslint .` back to its 3 pre-existing warnings, **117 tests pass**
(99 before, 18 new ones on the feed).

**Process note:** this code was written by the main session rather than by `coder-mobile`, so
the `SubagentStop` review hook never fired and step 5 initially shipped unreviewed. That is what
prompted the "How we work" section now at the top of `CLAUDE.md`. The review was run afterwards.

### The review round, and the repo's first tests — 2026-08-19/20

Step 5's belated review returned no MUST FIX and twelve findings. Fixing the ones that mattered
turned into the first work run properly through plan → `coder-backend`/`coder-mobile` → review,
and it produced more than the fixes.

**`backend/GreedyNose.Api.Tests` exists — the repo's first test project** (xunit, 41 tests, run
from the root via `GreedyNose.slnx`; note SDK 10's `dotnet new sln` emits `.slnx`, not `.sln`).
It replays `TransactionMapper` against
`TestData/mock-aspsp-transactions.json`, a **curated fixture with invented merchants and IBANs**
that reproduces every shape the first real dump showed: null `entry_reference` throughout, one
creditor IBAN in twenty, aggregator prefixes, branch numbers, the `ue` transliteration, nameless
`ICDT` rows, a `CRDT` row, a pending row, a non-EUR row. The real dumps stay gitignored — the
decision was to keep the evidence and drop the account history, so the tests run on a machine
that has never held real data. Deliberately-wrong behaviour (the aggregator prefixes, the
stranded hyphen) is pinned *with the requirement that will change it cited*, so step 7 changing
it is a decision rather than an accident.

**The test found a real bug before it shipped, in the plan rather than the code.** The plan said
`DateOnly.TryParse` succeeding is the signal for "this string carried no time". It is not —
measured on .NET 10, `DateOnly.TryParse("2026-08-18T14:32:05")` returns **true and discards the
time**, while the `Z` and `+02:00` forms return false. A bank sending a local ISO timestamp, the
one common shape with no offset, would have had real charge times thrown away under a flag
claiming there were none. `ToTimestamp` now decides from the string (`T` or `:`) and only then
picks a parser, and the evidence is recorded at the method.

**Two findings were the same failure in different places.** `ResolveDebitId` embedded the bank's
*raw* date string, so once the mapper started accepting timestamped dates, a bank reformatting
the same instant would re-key every debit and re-alert the entire history — the `R20` storm,
caused by the fix that widened the parse. It now keys on the normalised day. On the app side,
three layers each had their own quiet way of dropping a charge whose payee was missing (the feed
dropped the row, the list's `renderItem` returned null, the detail screen said "Debit not
found"); all three now resolve to one placeholder in the new `app/src/domain/payees.ts`.

**Also worth keeping:** the `CRDT` filter protects more than tidiness — real credit rows carry
`creditor: null` and `creditor_account` = *the account holder's own IBAN*, so without `R2a`'s
filter `R3a` tier 1 would key a payee on the user's own account and file their salary under it.
A test now asserts no payee carries the account's own IBAN.

### Picking this up next session

**Steps 0–5 are done. The next action is step 6, which waits on Restricted Production.**

1. Start the backend:
   `dotnet run --project backend/GreedyNose.Api/GreedyNose.Api.csproj --launch-profile http`
   (listens on `http://localhost:5199`. Note the SDK is not on the PATH of shells opened before
   it was installed — see Environment notes.)
2. `curl http://localhost:5199/health` — it should report `"connected": true` **without a browser
   click**, because the consent is restored from disk. If it says `false`, the consent expired or
   the file is gone: open `http://localhost:5199/connect` and click through **Mock ASPSP**, which
   needs no credentials.
3. `GET /debits` is step 4's output — `Payee`/`Debit` exactly as `app/src/domain/model.ts`
   declares them. 92 debits and 45 payees from the current sandbox data.
4. Get the app onto the device — the part with the most moving pieces, so in order:
   `adb reverse tcp:5199 tcp:5199` (backend) **and** `adb reverse tcp:8081 tcp:8081` (Metro),
   `npx react-native start`, then `.\gradlew.bat app:installDebug` from `app/android` in
   PowerShell with `JAVA_HOME` on JDK 17. The badge at the top of every screen says whether the
   feed is live, loading or unreachable — check it before debugging anything else.

**Wireless adb needs a network that allows device-to-device traffic.** Public WiFi (the airport
network this was hit on) runs AP client isolation: the phone and laptop cannot see each other,
`adb pair` fails with a bare `protocol fault`, and `adb mdns services` finds nothing — none of
which names the real cause. The fix is the **phone's own hotspot**, with the laptop joining it.
Two details that cost time: the IP in the phone's pairing dialog is its address *on the current
network*, so it changes with the network, and running `adb connect <ip>` on top of the mDNS
auto-connect produces **two transports for one device**, after which every `adb` command fails
with "more than one device". Note `adb reverse` tunnels through adb itself, so a USB cable works
just as well and needs no network at all.

`/debits` still returns a single page, so the device sees roughly three months of history, not a
year — see `TODO.md`.

The Mock ASPSP's accounts and transactions are whatever you put in the **mock ASPSP tab** of the
control panel — hand-entered, or a real account export imported as JSON. The 2026-08-18 dump is an
import of real German account data, which is why its findings are worth more than invented rows.

The consent **survives restarts** since 2026-08-18: it is written to `consent.local.json` beside
the backend (gitignored — it holds a session id that reads a real account). Step 2 had chosen
memory-only on the argument that a restart costs one cheap sandbox consent; that stopped being
true once every mapping tweak in steps 4–7 meant clicking through the consent page again, and the
session had never actually died — only our memory of its id. Delete the file to force a fresh
consent. Still not the database: one consent, no users, no history.

### What exists in `backend/GreedyNose.Api`

`Program.cs` holds the endpoints in step order — `/health`, `/aspsps` (step 1), `/connect` +
`/callback` (step 2), `/raw` (step 3), `/debits` (step 4). Everything Enable Banking-specific sits
in `EnableBanking/`: `EnableBankingOptions` (config), `EnableBankingSigner` (the RS256 JWT, key
loaded once at startup), `EnableBankingClient` (raw passthrough, snake_case policy), `ConsentStore`
(the one consent, plus its file), `AuthContracts` (request bodies), `TransactionContracts` (the
bank's transaction shape and the app's `Payee`/`Debit` DTOs) and `TransactionMapper`.

`TransactionMapper` is the only piece with real rules in it, and it is pure and static so it can
be replayed against the dumps in `raw/` without a bank — which is what step 7 will want. Every
requirement it implements is cited at the method that implements it, including the two places
where real data forced a departure from the spec.

Configuration is in **user secrets**, not `appsettings.json`: `EnableBanking:ApplicationId` and
`EnableBanking:PrivateKeyPath`. Re-create them with
`dotnet user-secrets set "EnableBanking:ApplicationId" "<id>"` from the project directory.

### Environment notes

- **.NET SDK 10.0.400** was installed this session via
  `winget install --id Microsoft.DotNet.SDK.10`. It is on the machine PATH, but **shells opened
  before the install do not see it** — prefix with `$env:PATH += ";$env:ProgramFiles\dotnet"` or
  open a new terminal.
- The build server keeps port 5199 held after a crash. Free it with
  `Get-NetTCPConnection -LocalPort 5199 -State Listen | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }`.

### Findings so far

- **N26 does not appear in the sandbox ASPSP list** (no match for "n26" among 686 German entries),
  so step 6 cannot be rehearsed against it — it needs Restricted Production. DKB is present but
  flagged `beta`. Nothing here contradicts `R22`; it only means the sandbox cannot prove step 6.
- **`GET /aspsps` carries the sandbox test credentials** for each bank in a `sandbox.users` object
  (username/password/OTP). Mock ASPSP has none — it needs no login at all.
- ASPSPs advertise `maximum_consent_validity` (15552000s = 180 days for Mock ASPSP), which the
  requested `valid_until` must respect. The backend currently asks for 90 days.

### What the first real dump said — 2026-08-18

100 transactions (92 `DBIT`, 8 `CRDT`), 44 distinct payee names, 2026-05-13 … 2026-08-18. Real
German account data imported into Mock ASPSP, so the shapes are a bank's, not a fixture's. **Read
every null below with one caveat**: this data went through Enable Banking's export/import round
trip, which may itself drop fields. Step 6 against the real bank is what turns these into facts.

**Settled:**

- **`credit_debit_indicator` is `DBIT`/`CRDT`.** `ARCHITECTURE.md` was right and the API
  reference's `DBTR` was wrong — this question is closed, and step 4 codes `DBIT`.
- Amounts arrive **positive**, as strings (`"20.00"`), with the direction carried only by the
  indicator. `R17a` fits the wire format rather than fighting it.
- Every row was `status: "BOOK"`. `R10c`'s booked-vs-pending split had nothing to act on here, so
  it remains untested.

**Three guesses that real data broke:**

- **The account `uid` is not stable.** Consenting three times to the same account returned three
  different uids — `e6b83c96…`, `280cfc98…`, `4a96fa90…` — for the same IBAN. It identifies the
  account *within a session*, not the account, and the API reference says so outright: the uid
  "is valid only until the session to which the account belongs is in the AUTHORIZED status".
  **This was findable in the docs before it was found by accident** — the danger is that it reads
  exactly like a stable key. Had R10b's "connected account" been the uid, every reconnect would
  have re-keyed the whole history and alerted on all of it — the storm `R20` exists to prevent,
  fired by the identifier meant to prevent it. `ConnectedAccount.Key` is the IBAN for this reason.

  Enable Banking's own answer to the same problem is `identification_hash`, documented for
  "matching accounts between multiple sessions". Base64-decoding its prefix shows it hashes
  exactly `(account.account_id.iban, account.currency)` — so **the IBAN key is their identity**,
  minus 130 characters of opacity in every debit id. The hash is kept as the fallback because it
  is the only one of the two that exists for an account with no IBAN.

- **`entry_reference` was null on all 100 rows.** So was `transaction_id`. `R10b`'s identifier
  `(connected account, entry_reference)` **did not exist in this data at all**. Step 4 therefore
  falls back to a composite of `(account, booking date, amount, payee key, ordinal)` when
  `entry_reference` is absent — decided 2026-08-18, and documented as a stopgap in
  `TransactionMapper.ResolveDebitId`, including the assumption it rests on (stable intra-day
  ordering between fetches). **This is the single most important thing for step 6 to re-check.**
- **A creditor IBAN was present on 1 of 92 debits**, and `creditor_agent` on none. `R3a`'s tier 1
  is essentially unavailable for card payments (84 rows), so the **normalized name is the real
  key** and the two-tier design leans almost entirely on its weaker tier. Tier 1 stays — transfers
  and direct debits do carry it — but it is not the common path the requirement implies.

**Observations worth keeping for step 7:**

- **Payee names are SEPA-truncated to ~22 characters** (`Landeshauptstadt Stutt`,
  `BENZ WEIN- UND GETRÄNK`). Stable per bank, so it does not break the key — but it is what the
  user will read, and no full name is recoverable.
- **One name arrived corrupted**: `Papas D?ner`, a literal `?` where `ö` belongs, in an otherwise
  correct UTF-8 payload (`ORTERER GETRÄNKE-MÄRKT` came through intact). The bank mangled it, not
  us. Another row reads `MUeLLER STUTTGART` — exactly the `ue` spelling `R23a`'s umlaut folding
  was built for, now confirmed as a real thing banks send.
- **Aggregators prefix the real merchant**: `PAYPAL *C24MIETWAG BR8`, `SumUp  *By Doner`,
  `Zettle_*Waldklettergar`, `ANTHROPIC* CLAUDE SUB`. Under `R3a` these key as PayPal-the-payee
  rather than the shop behind it, which merges unrelated merchants — the one direction `R3b`
  says to avoid. Not solved here; `R3a`'s normalization was implemented exactly as written.
- R3a's "strip digits" does its job — `Tegut Filiale 3134` and `KAUFLAND OSTFILDERN 51` lose their
  branch numbers and group correctly — but leaves punctuation stranded (`MUeLLER STUTTGART 2-2` →
  `MUELLER STUTTGART -`). Deterministic and harmless; noted rather than fixed, because widening
  the normalization is a spec change.
- **Two debits carried no creditor name, no IBAN and no remittance text** (outgoing transfers,
  `ICDT`). They map to a single shared **"Unknown payee"** — decided 2026-08-18 over dropping them
  (hiding a charge is the failure `R1` exists to prevent) and over one payee each (which floods
  the Rules list with un-reviewable one-offs).
- **`bank_transaction_code` is the only payment-type signal**, and it does not cover the app's
  vocabulary: `CCRD`/`MCRD` (89 rows) → card, `ICDT`/`RCDT` → transfer, `DDBT` → direct debit, and
  **`Subscription` is unreachable** — no bank code means "recurring", so a monthly Netflix charge
  is indistinguishable from any other card payment. Unknown codes currently fall back to
  `Card payment`, a guess dressed as a fact; the app's `PaymentType` union has no member for "the
  bank did not say".
- **Booking is a date, not a moment.** `transaction_date` was null on every row, so timestamps are
  midnight UTC off `booking_date`. Times of day are not recoverable from this feed.
- `merchant_category_code`, `reference_number` and `balance_after_transaction` were null
  throughout; `remittance_information` was non-empty on 4 of 100.
- **The page was 100 transactions with a `continuation_key`**, not the "fixed batches of 10" the
  sandbox notes claim. Pagination exists and works; step 4 currently reads the first page only.

### Reviewed against Enable Banking's own C# sample — 2026-08-18

Compared `backend/GreedyNose.Api` against
[`cs_example`](https://github.com/enablebanking/enablebanking-api-samples/tree/master/cs_example).

**The JWT is a clean bill.** Header (`typ`, `alg`, `kid`), claims (`iss`, `aud`, `iat`, `exp`),
RS256 with PKCS#1 v1.5 over SHA-256, unpadded base64url — all identical to the sample, nothing
missing and nothing extra. Ours additionally loads the key once instead of per token and injects
the clock, so the token is testable. `POST /auth` and `POST /sessions` match too, and we verify the
`state` on the callback, which the sample generates but never checks. **When step 6 misbehaves,
authentication is not the place to look.**

Thirteen findings came back; **nine were fixed the same day**. The one that mattered:

- **Currency was read and thrown away.** The DTO field is `amountEUR` and the app's limit is in
  euros, so a CHF 109 charge would have shipped as `109`, rendered `109,00 €`, and been
  limit-checked against a euro limit — under `R5` either a false alert or a missed one, with
  nothing on screen to explain it. Invisible in a 100/100 EUR dump and invisible until step 6.
  Non-EUR charges are now skipped **and logged**, because a silently dropped charge is the one
  failure this product cannot have. Step 7 counts how often it fires before choosing the real fix:
  a currency on `Debit`, or a conversion.

The other eight: unparseable amounts defaulting to `0m` (a charge no limit could ever catch),
unreadable dates rendering as "Invalid Date", consent expiry judged against the validity we
requested rather than the one the bank granted, the missing `identification_hash` fallback,
`reference` echoing the payee name on 88 of 92 debits, `date_from` unescaped, `GetProperty`
throwing on an unexpected 200 body, and a doc comment naming the wrong weakness in the fallback
debit id. The four deferred ones — pagination, `maximum_consent_validity`, `date_from`, and the
payee key inside the debit id — are in `TODO.md` with reasons.

## Steps

Each step ends in something visible. If a step cannot be *seen* working, it is not done.

### 0. Register the application — owner's task, in the browser

In Enable Banking's control panel: add a new application, keep the **Sandbox** environment and
the default (browser-generated) private key. The download is `<app-id>.pem`, and **the filename
is the application ID**.

- Redirect URL: `http://localhost:5199/callback`. Verify plain `http` on localhost is accepted;
  if it is not, fall back to pasting the `code` from the redirect by hand — the bullet does not
  need a pretty callback.
- **Start the Restricted Production application at the same time.** It needs separate approval
  ("testing with your own bank accounts"), and step 6 waits on it. Applying in parallel means
  the wait overlaps steps 1–5 instead of following them.

### 1. JWT and the first authenticated call

`backend/GreedyNose.Api`, minimal API, one endpoint proxying `GET /aspsps?country=DE`.

Auth is a JWT signed **RS256** with the `.pem`: header `kid` = application ID, body
`iss: enablebanking.com`, `aud: api.enablebanking.com`, `iat`/`exp` (max TTL 24h), sent as
`Authorization: Bearer …`. Base URL `https://api.enablebanking.com`.

**Done when:** a real list of German banks comes back on localhost.

This step is alone on purpose — key handling, signing and connectivity are the most likely
things to be broken, and debugging them is far cheaper before a consent flow sits on top.

### 2. Consent round trip

- `GET /connect` → `POST /auth` (`aspsp`, `access`, `redirect_url`, `state`, `psu_type`) →
  redirect the browser to the returned authorization URL.
- `GET /callback?code=…` → `POST /sessions` → hold `session_id` and the account. ~~Deliberately in
  memory: persistence is a later step's job, and a restart costs one sandbox consent.~~ Revised
  2026-08-18 — the restart cost turned out to be paid once per code change, not once per session,
  so the consent is now written to a gitignored local file. See "Picking this up next session".
- **The account `uid` from `/sessions` is per-session, not per-account** — see Findings. Anything
  that must be stable keys on `ConnectedAccount.Key` (the IBAN) instead.

**Done when:** the Mock ASPSP consent completes in a browser and the callback page shows the
account UID.

### 3. Raw transactions, untouched

`GET /raw` → `GET /accounts/{uid}/transactions`, returned **verbatim** and written to a file.

**Done when:** real transaction JSON is on screen.

Keep that file. `REQUIREMENTS.md` ("Refining these from real data") and `ARCHITECTURE.md` both
promise a tuning pass against what banks actually send; this is the first evidence for it, and
raw text costs nothing to keep.

### 4. Map to the domain

Enable Banking transaction → `Payee` / `Debit`, following the spec exactly:

| Rule | Requirement |
|---|---|
| Keep debits only, drop credits at the earliest point | `R2a` |
| Booked only for identity; pending never notifies | `R10c` |
| De-duplication key `(connected account, entry_reference)` — never `transaction_id` | `R10b` |
| Payee key: normalized creditor IBAN, else normalized name (+ creditor agent) | `R3a` |
| Amounts stored **positive** | `R17a` |
| Classification is **not** transmitted — the client derives it | `R6` |

**Done when:** `GET /debits` returns JSON that already matches `app/src/domain/model.ts`.

~~**Check against real data here:** the API reference lists `credit_debit_indicator` values as
`CRDT`/`DBTR`, while `ARCHITECTURE.md` says `DBIT`.~~ Settled 2026-08-18 by the raw dump: the
values are **`DBIT`/`CRDT`**, `ARCHITECTURE.md` was right, and the API reference's `DBTR` is
wrong. No code works around the disagreement.

The dump broke two other assumptions on contact — `entry_reference` was absent entirely, and the
creditor IBAN nearly so. Both are recorded under Findings with what step 4 does instead, and both
are the first things step 6 must re-check.

### 5. Point the app at the backend

`app/src/data/hooks.ts` is the seam the client was built around, so this changes that file and
nothing else. Fixtures stay reachable behind a flag until the real feed is trusted.

The device reaches the laptop with `adb reverse tcp:5199 tcp:5199`, which works over the
wireless adb setup already in use.

**Done when:** the Debits tab on the device lists sandbox charges, with `R23` search and the
date grouping still working on real rows.

### 6. Re-fire at the real bank

Same code, Restricted Production credentials, N26, the owner's own account. Steps 2–5 run again
unchanged — if any of them needs a code change, that is a finding worth recording, since `R22`
says nothing may assume a bank.

**Done when:** real charges from the real account are on the device.

### 7. Record what real data taught us

Update the "Refining … from real data" sections of `REQUIREMENTS.md` and `ARCHITECTURE.md` with
observations, not guesses: how often the creditor account is missing, whether normalized names
collide, whether `entry_reference` is present and stable, what pending items actually look like.

This is the step the whole exercise exists for. `R3a` and `R10b`/`R10c` are currently built from
documentation plus one support answer from an AI agent; only this step replaces that with
evidence.

## Deliberately out of scope

None of these are forgotten — each has a home in `ARCHITECTURE.md` and grows onto the skeleton
the bullet leaves behind:

Postgres, user auth, FCM push and `R12a`'s wording, the 6h polling timer, the first-run and
reconnect ingestion modes (`R20`), the health monitor, multi-account (`R22a`), Azure deployment,
and the bank-selection screen.

## Secrets

The `.pem` is never committed. `dotnet user-secrets` for local development plus a `.gitignore`
entry, both set up in step 1 — before there is a key on disk to leak.

## Sandbox caveats

- Mock ASPSP returns transactions in **fixed batches of 10** and its date filtering does not
  work. Fine for the bullet; do not read pagination or filtering behaviour into it.
- Sandboxes have hard rate limits and are unstable by reputation. A failure there is not
  necessarily a bug in our code — step 6 is the real test.
