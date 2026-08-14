# Requirements

This file is the single source of truth for **what the app does**. `CLAUDE.md` records status
and past decisions; `ARCHITECTURE.md` records how it is built. Where either disagrees with
this file, **this file wins**. Mocks and code must follow it.

Requirements are numbered (`R1`, `R2`, …) so we can refer to them precisely. Open points are
numbered `O1`, `O2`, … and must be answered before the affected code or mock is touched.

Settled 2026-08-14. The mocks in `mocks/` were reworked to match on the same day. The code in
`app/` still predates this file and does **not** match it — see "Follow-ups" at the bottom.

## Terminology

**R0** — One vocabulary across requirements, mocks, code and UI (settled 2026-08-14, was `O9`):

| Use | Not |
|---|---|
| **payee** | debtor, debitor, creditor |
| **good** / **bad** | trusted, untrusted, flagged |
| **debit** | transaction, charge (fine in prose, not in identifiers) |

"Debtor" is plain wrong here — a debtor owes *you* money, while these parties take it. Existing
identifiers (`setDebtorTrusted`, `useDebtors`, `DebitorEditScreen`, the `debitor` mock files)
and the "Trusted" UI labels are renamed accordingly.

## Purpose

**R1** — The app notifies the user when a **new bad debit** arrives. Nothing else. It is not a
budgeting or general finance app.

## Core objects

**R2 — Debit.** A charge taken from the user's bank account. It has an **amount**, a **payee**,
and a **date**.

**R2a** — **Incoming money is ignored entirely** (settled 2026-08-14, was `O7`): salary,
refunds, transfers from other people. It never appears in the list, never notifies, and never
creates a payee. Accepted side effect: a refund from a bad payee is invisible in the app.

**R3 — Payee.** Whoever took the money. Payees come from the bank feed, not from the user.

**R3a — Payee identity** (settled 2026-08-14, was `O5`). The bank text differs per charge, so
the name alone cannot key a rule. Use the strongest identifier the charge carries, in order:

1. **SEPA creditor ID** (direct debits)
2. **IBAN** (transfers)
3. **Normalized name** (card payments — uppercase, strip digits and extra whitespace)

The resolved key is stored on the payee, together with the raw strings seen for it.

**R3b — When matching is uncertain, split rather than merge.** Splitting one payee into two
shows a known payee as unknown → a false alert: annoying but safe. Merging two payees into one
lets an unknown payee inherit "good" → a missed alert, which breaks R1.

**R4 — Rule.** A payee has **at most one rule**. Only the user creates or edits it. A rule has:
- a classification: **good** or **bad**
- an optional **amount**, which only has meaning when the classification is *good*

**R4a** — The amount is **empty by default**, and empty means **no limit** — every charge from
that payee is good, whatever its size.

**R4b** — "Payee has a rule" is what **reviewed** means (settled 2026-08-14, was `O8`). A payee
marked bad has a rule record; an untouched payee has none. Both classify as bad, so no extra
flag is needed to tell them apart. Only onboarding uses the distinction, for its progress; it
is invisible everywhere else in the app.

## Classification

**R5** — Every debit is either **good** or **bad**. The payee's rule decides which:

| Rule for the payee | Result |
|---|---|
| No rule | Debit is **bad** |
| Bad | Debit is **bad**. The amount is not considered. |
| Good, no amount | Debit is **good** |
| Good, amount set | Debit is **good** if it does not exceed the amount, otherwise **bad** |

The last row is the "small charges are fine" case: the payee is acceptable up to a limit.

**R5a** — "Does not exceed" means **less than or equal**. A charge exactly equal to the limit
is **good** (settled 2026-08-14, was `O2`). The user-facing wording stays "alert if it exceeds
€X", which matches that reading.

**R6** — Classification is **derived from the current rule**, never stored on the debit.

**R7** — A rule applies to **existing and future debits** of that payee. Editing a rule
immediately re-labels that payee's debits already in the list.

**R8** — The payee's good/bad flag changes **only when the user changes it**. Nothing the app
does on its own moves it. The existing automatic flip is therefore removed in full (settled
2026-08-14, was `O1`): the save-time flip, the passive Bad-only flip on an arriving charge, the
"Auto-marked Bad · Xm ago" marker, and the "manually toggling clears the rule" behavior all go.

**R8a** — Toggling good/bad does **not** clear the payee's amount. Both fields belong to the
same rule and are edited independently.

## List

**R9** — The transaction list shows debits, newest first, each marked good or bad.

## Notifications

**R10** — A **new** incoming debit that is classified bad triggers a notification. Good debits
never notify.

**R10a** — Only a debit the app sees for the **first time** can notify. A debit that turns bad
later because the user edited a rule is re-labelled in the list but never notifies (settled
2026-08-14, was `O3`). Otherwise one rule edit could fire a burst of notifications for charges
the user is looking at right then.

**R10b** — A debit is **new** when the app has never seen its **bank transaction ID** before —
not when its date is recent (settled 2026-08-14, was `O4`). Banks deliver late, and a charge
from three days ago still deserves an alert. Two consequences:
- Everything pulled during the first sync (onboarding) is stored as already-seen and never
  notifies.
- A charge moving from pending to booked keeps its ID, so it notifies once, not twice.

**R11** — One notification per bad debit. Grouping stays deferred (see `CLAUDE.md`).

**R12** — Notifications carry no quick actions. Tapping one opens the transaction detail, which
links to the payee's rule screen.

**R12a — Wording** (settled 2026-08-14). A debit can be bad for three different reasons, and the
notification says which:

- **Title**: `«Payee» · «amount»`, e.g. `ScamyLoans GmbH · 49,00 €`. The OS already shows the app
  name above the title, so repeating "Greedy Nose" there would waste the most valuable line.
- **Body**, depending on why the debit is bad:
  - no rule → *"New payee — you haven't seen this one before."*
  - rule is bad → *"You marked this payee as bad."*
  - over the limit → *"Over your limit of «limit»."*

No minus sign on the amount (R17a).

*Deferred:* hiding amounts on the lock screen — a real privacy question, but it belongs to
Settings, which is not specified yet.

## Editing a rule

**R13** — A rule cannot be deleted (settled 2026-08-14, was `O6`). Deleting one would leave the
payee at "no rule" = bad, which the user already reaches by marking them bad. The only edits
are: switch good ↔ bad, and set or clear the amount.

**R14** — The amount field is **hidden while the payee is marked bad**, since it has no effect
there (R5). It appears when the payee is good. A hidden amount is kept, not wiped (R8a) — mark
the payee good again and the previous limit is back.

## Currency

**R15** — Amounts use the **account currency**, EUR for the single N26 account in v1. The rule's
limit is in that same currency. No currency picker anywhere (settled 2026-08-14).

**R16** — A charge made in a foreign currency is compared against the **booked amount in the
account currency** — what actually left the account. If the bank also reports the original
foreign amount, it may be shown on the transaction detail screen as extra information, but it
never affects classification.

**R17** — Amounts are formatted by **device locale** (`Intl.NumberFormat`): `49,00 €` on a
German phone, `€49.00` on an English one. The mocks are written in English and therefore show
the English rendering.

**R17a** — Amounts never carry a **minus sign**, anywhere in the UI. Every debit is money going
out (R2a), so the sign distinguishes nothing. (Extends R12a's rule from notifications to the
whole app — decided while reworking the mocks on 2026-08-14.)

## Bank connection

**R18** — The connection ends in three ways, with different consequences (settled 2026-08-14):

| How it ends | Rules | History | Connection |
|---|---|---|---|
| PSD2 consent expires on its own (~90 days) | kept | kept | re-authorize to resume |
| User taps Disconnect (`06b`) | kept | kept | reconnect anytime |
| User deletes the account (`06c`) | wiped | wiped | gone, not undoable |

**R19 — A dead connection is never silent.** No alerts arriving looks exactly like "nothing bad
happened", which is the one failure that breaks R1. So: a persistent banner on the transaction
list whenever the connection is expired or disconnected (`01d-connection-expired`), **plus one
push** when the consent expires by itself — the user may not open the app for days.

**R20 — Reconnecting after a gap sends one summary notification**, e.g. "12 new charges while
you were disconnected, 3 bad" — not one push per bad debit. The charges from the gap carry IDs
the app has never seen, so R10b would otherwise fire a burst all at once. Deliberate exception
to R11; normal per-charge alerts resume afterwards.

**R21** — Rules stay **editable while disconnected**. Harmless, and it lets the user prepare
before reconnecting.

## Open points

**None.** All nine points raised on 2026-08-14 are settled and folded into the requirements
above: `O1` → R8/R8a, `O2` → R5a, `O3` → R10a, `O4` → R10b, `O5` → R3a/R3b, `O6` → R13/R14,
`O7` → R2a, `O8` → R4b, `O9` → R0.

Currency (R15–R17), notification wording (R12a) and bank-connection handling (R18–R21) were
settled in the same session.

Still unspecified, but not blocking: the Settings screen's contents (including whether amounts
may show on the lock screen), and the onboarding classify flow's details.

## Follow-ups — bringing the code in line

*(`mocks/` is done: renamed to payee/Good, per-debit classification in the list, the amount
field split across 04b/04d, R12a notification wording, the reconnect summary 05b, and the
disconnected banner folded into 01d.)*

- **Rename for R0**: `debtor`/`debitor` → `payee`, "Trusted" → "Good", throughout `app/src/`.
- **Code to delete for R8** (`app/src/mocks/data.ts`, `app/src/data/hooks.ts`,
  `DebitorEditScreen`, `RulesListScreen`): the save-time auto-flip and its toast, the passive
  Bad-only flip, the "Auto-marked Bad · Xm ago" marker and its cleared-on-open logic, and the
  rule-clearing side effect of the manual toggle — plus the tests that pin that behavior.
- **Verify R3a against Enable Banking** before implementing it: which of SEPA creditor ID,
  IBAN, and merchant name their transaction payload actually returns per charge type is
  unconfirmed.
- `ARCHITECTURE.md` still describes count/frequency thresholds and "AND logic" in the Rule
  Engine (component table + Backend section). Frequency was cut — that text is stale.
- `CLAUDE.md`'s "Product decisions" section must be trimmed to point here for anything about
  classification.
