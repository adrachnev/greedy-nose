# Open items

Code-level work that is known, deliberately deferred, and easy to forget. Product-level
deferrals live in `REQUIREMENTS.md` (its "Refining these from real data" section and the mock
gaps) and `CLAUDE.md`'s "Open / deferred" — this file is for *implementation* items, mostly
review findings.

Rules for this file: an item is either done and deleted, or it says why it is still here. Do not
let it become a graveyard — anything nobody has touched in months is not a TODO, it is a decision
to not do it, and should be recorded as such or dropped.

## From the `app/` rework review — 2026-08-17

Two review passes over the R0/R4/R5/R8 rework. The first found a critical inverted-copy bug
(fixed); these are what survived the second, independent pass. **No MUST FIX findings remain.**

### Worth doing next

- [ ] **R17 — amounts are hardcoded to `en-US`** (`app/src/utils/format.ts:7`). A manual `€`
      prefix plus `en-US` grouping, where R17 asks for device locale (`49,00 €` on a German
      phone). Sharpened by this pass: `parseAmount.ts` now *accepts* `45,50`, and the app echoes
      `€45.50` straight back at the user. Fix both directions together, and note the mocks are
      deliberately English, so they are not the contract here.
- [ ] **Bad debit's amount is not red on the detail screen**
      (`app/src/screens/DebitDetailScreen.tsx:66`). `mocks/03` and `03b` both paint it, and
      `DebitListScreen` already does — so today the list and the detail screen disagree about the
      same charge.

### Needs a decision before coding

- [ ] **A typed limit is silently dropped when you toggle to Bad and save**
      (`app/src/screens/PayeeEditScreen.tsx:95`). Save while bad writes the last *saved* limit, so
      an amount typed just before toggling disappears with no warning. This is the last remnant of
      the old "unsaved edit vs. toggle" question: the single-commit form removed most of it, but
      not this corner. Options: keep the draft amount on save, warn, or leave as-is and accept it.
      Decide before touching the screen.

### Smaller, no decision needed

- [ ] Reseed effect keys on `rule`, not `route.params.payeeId`
      (`app/src/screens/PayeeEditScreen.tsx:64`) — harmless with fixtures, but once real data
      refreshes in the background it can wipe a half-typed limit mid-edit.
- [ ] Good/Bad toggle has no `accessibilityRole` or selected state
      (`app/src/screens/PayeeEditScreen.tsx:162`) — only colour marks the active half, which
      leaves screen-reader and colour-blind users with nothing.
- [ ] Each debit row subscribes to the store separately via `usePayee`
      (`app/src/screens/DebitListScreen.tsx:36`), while rules come from one memoized map. Fine at
      11 fixture rows; revisit with a real paged history.
- [ ] Sections memo freezes the "Today"/"Yesterday" labels until `debits` changes
      (`app/src/screens/DebitListScreen.tsx:83`) — a session open across midnight shows stale
      headers.
- [ ] Row spacing is 16px (container `gap: 8` + row `marginBottom: 8`) where the mock uses 12
      (`app/src/screens/DebitListScreen.tsx:135`).
- [ ] `useAddDebit` has no caller and `addDebit` is only reached from tests
      (`app/src/data/hooks.ts:72`). Intentional for now — it models the arrival of a charge, which
      the real feed will need — but delete it if the backend lands with a different shape.

## Standing

- [ ] **Run the reworked app on the device.** Everything since the R0/R4/R5/R8 rework has been
      verified by tests and typecheck only; no screen has been seen on hardware since.
- [ ] Port the remaining screens: Settings, the onboarding flow (consent/syncing/classify), and
      the error/empty/disconnected states.
