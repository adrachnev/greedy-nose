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
| **debit** | transaction |

"Debtor" is plain wrong here — a debtor owes *you* money, while these parties take it. Existing
identifiers (`setDebtorTrusted`, `useDebtors`, `DebitorEditScreen`, the `debitor` mock files)
and the "Trusted" UI labels are renamed accordingly.

"Transaction" is dropped because it promises both directions while the app only ever shows
outgoing money (R2a) — a user hunting for their salary under that heading finds nothing and
concludes the app is broken. The tab and list are **Debits**, the detail screen is **Debit**
(settled 2026-08-14). Two exceptions stay:
- **"charge"** is fine in prose where it reads better — "alerts on every charge".
- **Bank field names** keep their own spelling wherever R10b/R10c name one — `entry_reference`,
  `transaction_id` — because those are the aggregator's identifiers, not our vocabulary.

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

**R3a — Payee identity** (settled 2026-08-14 as `O5`; **revised 2026-08-17** after Enable Banking
answered). The bank text differs per charge, so the name alone cannot key a rule. Use the
strongest identifier the charge carries, in order:

1. **Normalized creditor account identification** — the IBAN, where the charge carries one
2. **Normalized name** (uppercase, strip digits and extra whitespace), plus the **creditor
   agent** where it helps separate two payees that would otherwise collide

The resolved key is stored on the payee, together with the raw strings seen for it.

**The SEPA creditor ID is not available and the key is therefore best-effort.** Asked Enable
Banking support directly on 2026-08-14; answered 2026-08-17. Their normalized `Transaction` model
has **no SEPA creditor identifier and no mandate ID**, and they could not confirm any passthrough,
enablement option or roadmap item that would expose one. Their own recommendation is the two-tier
key above, explicitly as best-effort grouping. N26's PSD2 interface does publish `creditorID` and
`mandateID`, so the data exists at the bank and dies in the aggregator's model — noted in case
that ever changes.

Two consequences of losing the exact key:

- **R3b stops being a nicety and becomes the safety net.** With a fuzzy key, ambiguous cases are
  the normal case, not the exception.
- Direct debits — the charge type this product cares most about — get the weakest matching. This
  is accepted, not solved.

Also from the same answer: `reference_number` is meant for credit-transfer references and must
**not** be used as a payee key. Payee grouping and debit de-duplication (R10b) are separate
problems with separate keys; do not let one leak into the other.

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

**R9** — The debit list shows debits, newest first, each marked good or bad.

**R23 — Both list screens are searchable** (settled 2026-08-17). The debit list matches on the
**payee's name or the debit's amount**; the rules list matches on the payee's name. Matching is
**substring**, not prefix and not exact: typing `9` finds both `9.99` and `19.90`. The payment
type is deliberately **not** matched — a word like "card" would return half the list and teach
the user nothing.

Two consequences worth stating, because both are easy to lose in an implementation:

- The **date grouping survives filtering**. Searching a subscription must show one row per month
  under its month header, since the rhythm of the charge is the thing worth seeing.
- A search matching nothing shows **why**, naming the query — an empty screen is
  indistinguishable from a lost bank connection (`R19` exists for the same reason).

**R23a — Matching is tolerant of how the user types.** A German keyboard produces a comma and
an umlaut; neither may cost the user a result. `12,99` and `12.99` both find the same charge,
and `müller`, `muller` and `mueller` all find *Bäckerei Müller*. Case is ignored. Amounts are
matched against the **unformatted** value, not the rendered string, so `R17`'s locale formatting
cannot break search.

**R23b — The query is scoped to the visit, not the screen.** It survives list → debit detail →
Back, so several hits can be worked through without retyping, and it is **cleared when the tab
is left**, so a tab always hands back the full list. Anything else leaves the user staring at a
short list with no visible cause.

## Navigation

**R24 — Tapping a tab shows that tab's list** (settled 2026-08-17). Arriving at a tab always
lands on its list, never on a detail or edit screen left open from a previous visit.
**Re-tapping the tab you are already on does nothing** — it is not a "go back" gesture.

**R24a** — Leaving a tab mid-edit **discards an unsaved rule draft**, silently. Back already
discards on that screen (`R4`'s single-commit form), so a tab tap behaving differently would be
the inconsistency, and a confirm dialog fired by a tab press is not a gesture Android users
expect.

## Notifications

**R10** — A **new** incoming debit that is classified bad triggers a notification. Good debits
never notify.

**R10a** — Only a debit the app sees for the **first time** can notify. A debit that turns bad
later because the user edited a rule is re-labelled in the list but never notifies (settled
2026-08-14, was `O3`). Otherwise one rule edit could fire a burst of notifications for charges
the user is looking at right then.

**R10b** — A debit is **new** when the app has never seen its identifier before — not when its
date is recent (settled 2026-08-14, was `O4`). Banks deliver late, and a charge from three days
ago still deserves an alert.

**The identifier is `(connected account, entry_reference)`** (**revised 2026-08-17**, was "the
bank transaction ID"). Per Enable Banking support: `entry_reference` is documented as unique and
immutable for the same account, and matches across authentication sessions. It is **not** globally
unique, hence the account scope. `transaction_id` must **not** be used — it exists to fetch
transaction details, is not guaranteed to identify a transaction uniquely, and **may change
between fetches**.

**R10c — Only booked debits notify** (settled 2026-08-17). There is **no identifier that survives
pending → booked** across banks: for most ASPSPs `entry_reference` only exists once the charge is
booked. So pending charges may appear in the list as provisional, but the alert fires when the
charge books. The cost is latency — typically under a day, which the "same-day notification is
fine" decision already accepted. The alternative, alerting on pending, would double-alert on every
bank that re-keys a charge at booking, and a false "you were charged twice" is worse than an alert
arriving a few hours later.

Consequences:
- Everything pulled during the first sync (onboarding) is stored as already-seen and never
  notifies.
- Some banks omit entry references or hand out duplicates. **When the identifier is unreliable,
  risk the duplicate alert, never the missed one** — the mirror of R3b, in the opposite direction:
  for identity, merging is the dangerous move; for de-duplication, it is treating two charges as
  one.

**R11** — One notification per bad debit. Grouping stays deferred (see `CLAUDE.md`).

**R12** — Notifications carry no quick actions. Tapping one opens the debit detail, which links
to the payee's rule screen.

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

## Banks

**R22 — The app is bank-agnostic** (settled 2026-08-14). Any ASPSP reachable through Enable
Banking is a valid target; **N26 is simply the first one integrated**, chosen for its free tier,
with ING-DiBa and DKB as likely next. Nothing in the product — copy, data model, or rule logic —
may assume a particular bank.

**R22a** — v1 connects **one account at a time**. Supporting several banks simultaneously, and
labelling which bank a debit came from, stays deferred (see `CLAUDE.md`). "Bank-agnostic" means
the app works with whichever bank you connect, not that it aggregates several at once.

## Currency

**R15** — Amounts use the **account currency**. Every target bank is in the eurozone, so EUR in
practice. The rule's limit is in that same currency, and there is no currency picker anywhere
(settled 2026-08-14).

**R16** — A charge made in a foreign currency is compared against the **booked amount in the
account currency** — what actually left the account. If the bank also reports the original
foreign amount, it may be shown on the debit detail screen as extra information, but it
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
happened", which is the one failure that breaks R1. So: a persistent banner on the debit list
whenever the connection is expired or disconnected (`01d-connection-expired`), **plus one
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

*(`mocks/` is done: renamed to payee/Good and transaction/debit, per-debit classification in the
list, the amount field split across 04b/04d, R12a notification wording, the reconnect summary
05b, the disconnected banner folded into 01d, and the good-payee-over-limit case drawn in 03b +
05c.)*

**`R23`/`R24` are new on 2026-08-17 and land in that order**: the search field and the no-match
state go into `mocks/` first, then the client. Neither is drawn or implemented yet at the time
of writing.

**Known mock gaps, deliberately left open** (from the 2026-08-14 review — decide before the
matching screen is coded):

1. R19 covers expired **and** disconnected, but only expired is drawn (`01d`). After the user
   taps Disconnect there is no mock of the debit list in that state, and Settings still shows an
   "Active" pill.
2. R19's "one push when the consent expires by itself" has no mock. That push is what reaches a
   user who has not opened the app in a week.
3. ~~`03` shows the payee's **IBAN** for a direct debit, but R3a keys on the SEPA creditor ID~~ —
   resolved 2026-08-17: there is no creditor ID, and the IBAN *is* R3a's tier 1. The mock was
   right by accident; nothing to change.
4. **No bank-selection screen exists.** `01-connect-bank` and `01e-connect-error` both assume
   N26 by name, and `01e`'s "Choose a different bank" link goes to the mock gallery because
   there is nowhere to send it. R22 makes that link correct in principle — the screen it needs
   just hasn't been drawn. (Revised 2026-08-14: previously recorded as the opposite problem,
   back when v1 was N26-only.)

   **Wider than recorded** (2026-08-17): N26 is hardcoded in **eight** files, not two —
   `01`, `01b`, `01bb`, `01d`, `01e`, `02b`, `06` and `06b`. So this is not only a missing
   screen: connect, consent, syncing, the expired banner, the empty list and both settings
   screens all name one bank in body copy. Whatever the bank-selection screen ends up being,
   the fix is a *placeholder* everywhere the connected institution is mentioned, and none of
   those files may keep a literal bank name.

### ~~Do this first — reconcile `ARCHITECTURE.md`~~ — done 2026-08-17

Reviewed against this file on 2026-08-14 and found to diverge in ten places (`A1`–`A10`).
Reconciled on 2026-08-17: both contradictions are fixed (`A1`, the rule engine evaluating the
amount for **bad** payees, now follows R5's table; `A2`, the ruled-out consent-expiry push, is
back per R19), the stale terminology is renamed to R0, and the six missing designs are written —
the `DBIT` filter, derived classification, the reconnect ingestion mode, R18's data lifecycle and
the bank-agnostic consequences. See "Reconciliation with `REQUIREMENTS.md`" at the top of that
document for the map.

`A6` (payee identity) was left open that morning and closed the same day, once Enable Banking
answered — see R3a. Their reply also corrected R10b and added R10c, which no one had asked about.
All ten divergences are now closed.

### ~~Then — the `app/` code rework~~ — done 2026-08-17

- ~~**Rename for R0**~~ — done: `debtor`/`debitor` → payee, "Trusted" → Good, `transaction` →
  debit, throughout screens, hooks, fixtures and the tab label.
- ~~**Code to delete for R8**~~ — done: the save-time auto-flip and its toast, the passive
  Bad-only flip, the "Auto-marked Bad · Xm ago" marker and its cleared-on-open logic, and the
  rule-clearing side effect of the manual toggle are all gone, along with the tests that pinned
  them.

Two things the rework changed beyond the checklist above, both required to satisfy R4/R5:

- **Good/bad moved from the payee onto the rule** (`classification`), so "has a rule" is what
  reviewed means (R4b) and no separate flag exists.
- **A domain layer** (`app/src/domain/`) now holds the vocabulary and R5's table, separate from
  the mock fixtures it outlives. Classification is computed at read time and never stored (R6).

Still to port: Settings, the onboarding screens (consent/syncing/classify), and the
error/empty/disconnected states.

### Independent of both

- ~~**Verify R3a against Enable Banking**~~ — closed 2026-08-17. Asked their support directly;
  the SEPA creditor ID is absent and unreachable, so R3a is now a two-tier best-effort key and
  `A6` is unblocked. The same answer changed R10b's identifier and added R10c (booked-only
  alerting) — a bigger correction than the question that prompted it.
- `CLAUDE.md`'s "Product decisions" section must be trimmed to point here for anything about
  classification.

### Refining these from real data

**Settled 2026-09-16 against a real N26 account** — full evidence in `TRACER-BULLET.md`, "What
real N26 data said." Headline results:

- `entry_reference` exists on real data, but per-row within one account, not as a bank-wide flag:
  28 of 91 real debits carried one, the rest fell back to R10b's composite key. That fallback is a
  permanent path for a meaningful share of any one bank's charges, not only a stopgap for banks
  that omit the field entirely.
- No normalized-name collision has been seen across either the sandbox or the N26 payee set, but
  aggregator prefixes (`PAYPAL *…`, `Zettle_*…`) still key as the aggregator rather than the real
  merchant — an accepted gap (R3b), not a collision.
- The creditor account is present on essentially no card payments, and on real credit rows it
  reliably equals the account's own IBAN — confirming why R2a's filter has to run before payee
  resolution, not after.

Two things still have **zero evidence** from any account pulled so far, sandbox or production: a
non-EUR charge, and a pending (not-yet-booked) transaction. R10c's booked-vs-pending split and the
currency-skip path added after the Enable Banking C# sample review remain untested by real data.

Until those are observed, keep the same conservative defaults R1 demands: split rather than merge
for identity, duplicate rather than miss for de-duplication. Both cost the user a false alert at
worst.
