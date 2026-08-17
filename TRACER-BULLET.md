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
| 2 Consent round trip | **built, half-verified** — `/connect` redirects to the sandbox consent page; the browser click-through has **not** happened yet |
| 3 Raw transactions | **built, never run** — needs a live session from step 2 |
| 4–7 | not started |

### Picking this up next session

1. Start the backend:
   `dotnet run --project backend/GreedyNose.Api/GreedyNose.Api.csproj --launch-profile http`
   (listens on `http://localhost:5199`; `/health` shows whether a consent is live)
2. Open `http://localhost:5199/connect` in a browser and click through **Mock ASPSP** — no
   credentials needed for that one.
3. The callback page lists the account UID; follow its link to `/raw`.
4. Then step 4: map what comes back onto `Payee`/`Debit`.

The consent lives **in memory** (step 2's deliberate choice), so restarting the backend means
clicking `/connect` again. Cheap in sandbox; the database that fixes it is out of scope here.

### What exists in `backend/GreedyNose.Api`

`Program.cs` holds the endpoints in step order — `/health`, `/aspsps` (step 1), `/connect` +
`/callback` (step 2), `/raw` (step 3). Everything Enable Banking-specific sits in
`EnableBanking/`: `EnableBankingOptions` (config), `EnableBankingSigner` (the RS256 JWT, key
loaded once at startup), `EnableBankingClient` (raw passthrough, snake_case policy), `ConsentStore`
(the one in-memory consent), `AuthContracts` (request bodies).

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
- Still unresolved: the `CRDT`/`DBIT`/`DBTR` naming question in step 4 — only the raw dump settles
  it.

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
- `GET /callback?code=…` → `POST /sessions` → hold `session_id` and the account UID in a static
  field. Deliberately in memory: persistence is a later step's job, and a restart costs one
  sandbox consent.

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

**Check against real data here:** the API reference lists `credit_debit_indicator` values as
`CRDT`/`DBTR`, while `ARCHITECTURE.md` says `DBIT`. Step 3's raw dump settles it; whichever is
right, correct the losing document rather than coding around the disagreement.

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
