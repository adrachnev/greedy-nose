# Greedy Nose

@AGENTS.md

## Status

Last cleaned up 2026-10-19. This section stays short on purpose: the state of the work lives in Beads, not here
(see `AGENTS.md`, "Work tracking with Beads"). Do not write session diaries into this file.

- **Where we are:** Tracer 3 (go-live) is closed (2026-10-19). The backend runs in Azure (Functions + Neon), N26 and DKB
  are connected, the release app is on the owner's phone, a real charge gives one push, the week check found no 429.
  ING-DiBa is deliberately not connected.
- **What is next:** `bd ready`. The order is `gn-hkx` (choose the account in the app), then `gn-tn2`
  (finish the documentation cleanup), then the resilience beads.
- **Live document:** `TRACER-03-GO-LIVE.md`, read its Progress first. Older history: `git log` and the two earlier
  tracer documents.
- **Where things are written down:** what the app does `REQUIREMENTS.md`; design `ARCHITECTURE.md`; stories and
  acceptance criteria `_bmad-output/planning-artifacts/epics.md`; working rules and operations `AGENTS.md`; state and
  order of work Beads.

## Product decisions (settled)

- **Bank connectivity**: [Enable Banking](https://enablebanking.com) — a PSD2/XS2A-licensed
  aggregator with a free tier. Chosen specifically to start with zero cost/investment. The app
  is **bank-agnostic** (`R22`): any ASPSP Enable Banking reaches is fair game, and **N26 is only
  the first one integrated**; DKB is live next to it (2026-10-13), ING-DiBa was dropped by the owner but
  nothing prevents adding it. Nothing may hardcode a bank. Several banks at once work: one consent per bank,
  the worker walks them in turn, one merged debit list.
- **Onboarding**: after granting bank consent, a one-time bulk-review screen lets the user
  classify their existing payees before live monitoring starts — otherwise every historical
  charge would fire a notification on first connect.
- Classification (`R4`/`R5`/`R6`/`R8`) is specified in full in `REQUIREMENTS.md` and implemented
  in `app/src/domain/classification.ts`. This section no longer carries its own copy — see
  `AGENTS.md`'s Known pitfalls for the one thing worth restating (the deleted auto-flip).

## Open / deferred (explicitly not v1)

- App-level lock (Face ID/passcode) before opening the app.
- Telling the banks apart in the debit list (a per-account filter or a bank label), and several
  accounts *within* one bank. v1 (`R22a`) connects **one account per bank and several banks at once**
  (the owner's N26 and DKB, merged list). Choosing the account in the app is bead `gn-hkx`.
- Offline state handling for the main list (only the initial-connect error state exists).
- Notification grouping/bundling (a "Group multiple alerts" toggle exists in the Settings mock
  but defaults Off — one notification per charge is the current decision).
- Non-EUR bank accounts: `R15` hard-assumes EUR ("no currency picker anywhere"), which
  contradicts `R22`'s bank-agnostic promise the moment a non-eurozone ASPSP is connected. Needs
  a currency model (store and compare per-currency) that doesn't exist yet — found by
  `bmad-review` 2026-09-23, not yet decided.
