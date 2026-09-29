# Use-Case Walkthrough Review — REQUIREMENTS.md

Two step-by-step walkthroughs of `REQUIREMENTS.md` (read in full, 2026-08-14 baseline plus all
revisions through 2026-09-16), citing the R-number governing each step and flagging contradictions
or gaps not already acknowledged by the doc's own "Open points" / "Follow-ups" sections.

---

## Walkthrough 1 — First use

| # | Step | Governing requirement(s) |
|---|---|---|
| 1 | Install and launch the app | none needed — UI chrome |
| 2 | Connect bank / consent flow starts | R22 (bank-agnostic target), R22a (one account) |
| 3 | Historical transactions pulled ("first sync") | R10c — everything pulled is stored as **already-seen**, never notifies |
| 4 | Incoming money filtered out before payee resolution | R2a |
| 5 | Payees resolved from raw charges | R3a (IBAN → normalized name + creditor agent), R3b (split over merge) |
| 6 | Every pulled debit is classified for display | R5 — no rule yet exists for any payee, so every historical debit reads **bad** (R5 row 1) |
| 7 | Onboarding bulk-review screen lets the user classify existing payees | R4b only — "reviewed" = has a rule; **no requirement specifies the screen itself** (order, exit condition, skippability) |
| 8 | User classifies some/all payees good or bad, optionally with a limit | R4, R4a, R13, R14 (same rule-edit mechanics used post-onboarding) |
| 9 | User reaches steady state / live monitoring | R10c's "already-seen" boundary implicitly marks the transition; no explicit "monitoring is now live" requirement |

### Findings

**[Contradiction]** — R4b commits to a specific onboarding-flow detail that "Open points" calls unsettled — **R4b vs. the closing "Still unspecified" paragraph** — Scenario: a reader hits the doc's own line "Still unspecified, but not blocking: ... the onboarding classify flow's details" and reasonably concludes nothing about that screen is decided — order, exit condition, whether it's skippable, what it looks like. But R4b, in "Core objects" (settled 2026-08-14), already states *why* the reviewed/unreviewed distinction exists in the data model at all: **"Only onboarding uses the distinction, for its progress."** That sentence commits to a specific flow detail — the classify screen tracks and displays progress (something like "12 of 45 reviewed") — which is exactly the kind of thing the "unspecified" disclaimer claims is still open. Why it matters: an implementer following the "not blocking, not yet specified" framing at face value could build a classify flow with no progress indicator (e.g. a plain swipe stack), silently orphaning the entire reason R4b gives for the reviewed-flag existing in the data model, with nothing in the doc to catch the mismatch.

**[Gap]** — R12a's "New payee" wording assumes "no rule" always means "never seen before," but R4b's own definition of "no rule" includes payees with prior, unreviewed history — **R12a vs. R4b, R10c** — Scenario: onboarding's first sync pulls in a payee with 20 historical debits (R10c: stored as already-seen). The user does not classify that payee during onboarding (skippable or not is itself unspecified — see the finding above) and reaches steady state with it still at "no rule." Weeks later, that payee charges again. Per R5 row 1 it's bad, and per R10 it's the *debit's* first-seen occurrence, so it notifies — but R12a's wording table fires the body text *"New payee — you haven't seen this one before."* That's false: the user has 20 prior charges from this exact payee sitting in their list, just never classified. Why it matters: R12a's three-reason table is written as if "no rule" and "genuinely unfamiliar payee" are the same thing, but R4b explicitly defines "no rule" as simply *unreviewed*, a state that (by onboarding's whole purpose) routinely applies to payees the user has extensive history with. The notification actively misleads the user about their own bank history in exactly the case onboarding was built to handle.

**[Gap]** — No requirement covers an interrupted first sync — **R18 vs. R10c** — Scenario: the historical pull that seeds onboarding (R10c) is cut short partway (app killed, network drop, consent revoked mid-pull) before the user has seen or classified anything. R18's table only defines consequences for an *established* connection ending (expiry / disconnect / delete) — it says nothing about a first sync that never completed. If the app later resumes the pull, are the previously-unpulled historical debits still "first sync" (silent, R10c) or does the app no longer know it's still mid-first-sync and treat them as a live, notification-eligible gap (R20's machinery, meant for post-connection reconnects)? Why it matters: this boundary condition changes whether the user is silently bootstrapped (as intended) or gets an unexpected burst of "New payee" pushes for old charges the very first time they use the app — the opposite of the calm first-run experience R10c exists to guarantee.

---

## Walkthrough 2 — Normal ongoing use

| # | Step | Governing requirement(s) |
|---|---|---|
| 1 | Charge from a known-good payee, no limit | R5 row 3 → good → R10 never notifies |
| 2 | Charge from a known-good payee, at the limit | R5 row 4 + R5a (`≤` is good) → good → no notification |
| 3 | Charge from a known-good payee, over the limit | R5 row 4 → bad → R10 fires, R12a "Over your limit of «limit»." |
| 4 | Charge from a known-bad payee | R5 row 2 → bad regardless of amount → R12a "You marked this payee as bad." |
| 5 | Charge from a brand-new payee | R5 row 1 → bad → R12a "New payee..." (accurate here, unlike Walkthrough 1's finding above) |
| 6 | Identifier/dedup check before any of the above fire | R10b `(connected account, entry_reference)`, R10c (booked-only) |
| 7 | User searches the debit list / rules list | R23, R23a |
| 8 | User opens debit detail from a search hit | R23b (query survives list → detail → Back) |
| 9 | User edits a rule (lowers a limit, flips good/bad) | R7 (re-labels existing debits immediately), R10a (re-labeling never re-notifies — explicitly reconciled, see below), R13, R14 |
| 10 | User switches tabs mid-edit | R24, R24a (draft discarded silently), R23b (query cleared on tab blur) |
| 11 | Connection ends one of three ways | R18 |
| 12 | Dead connection is surfaced | R19 |
| 13 | Reconnect after a gap | R20 |
| 14 | Rules editable throughout the outage | R21 |

Checked explicitly and found **consistent, not a finding**: R7's "editing a rule re-labels existing
debits" against R10a's "re-labeling never re-notifies." R10a's own text anticipates precisely the
scenario (lowering a limit turns an already-good debit bad) and states the no-renotify outcome
deliberately, with its own rationale ("otherwise one rule edit could fire a burst of
notifications"). No conflict between the two.

### Findings

**[Contradiction]** — R24a's justification cites a requirement that doesn't say what it's cited for — **R24a vs. R4, R13, R14** — Scenario: R24a justifies tab-switch behavior by saying "Back already discards on that screen (R4's single-commit form)." But R4 ("Core objects — Rule") only defines the rule's *data* (a classification plus an optional amount) — it says nothing about Save/Back commit semantics. R13 lists which fields are editable; R14 covers the amount field's visibility. **No R-number in this file actually states that Save commits both fields together while Back discards the draft** — the very mechanic R24a leans on to argue tab-press should behave the same way as Back. Why it matters: REQUIREMENTS.md declares itself the single source of truth ("Where either disagrees with this file, this file wins"), yet the one place that names the single-commit save/discard model points at a section that doesn't define it, and no other section fills the gap. An implementer building the edit screen from this file alone has no requirement to point to for "Save writes classification+amount together; Back discards the draft" — only a citation that assumes it already exists.

**[Gap]** — R20's summary notification has no defined wording, unlike every other notification type — **R20 vs. R12, R12a** — Scenario: after a gap, 12 new charges arrive across an arbitrary number of payees, 3 of them bad. R20 says this "sends one summary notification" but the *only* wording specification in the doc, R12a, is built entirely around a single payee/amount pair (`Title: «Payee» · «amount»`) and enumerates exactly three body reasons for a single bad debit (no rule / marked bad / over limit) — none of which fit an aggregate across multiple payees. R12 further says tapping a notification "opens the debit detail" (singular) — which debit, out of 12, does R20's summary open? Why it matters: R20 is the one notification type in the whole doc left with no title/body spec and no defined tap target, even though R12a reads as an exhaustive wording table for "every way a debit notification can look." An implementer has to invent the copy and the tap behavior from nothing.

**[Contradiction]** — R20 mandates a summary notification unconditionally, which can conflict with R1's "nothing else" — **R20 vs. R1** — Scenario: the connection is down for a day; the gap's historical pull turns up 5 new charges, all from known-good payees under their limits — zero bad debits. R20 as written ("Reconnecting after a gap sends one summary notification, e.g. ... '3 bad'") has no stated threshold — its own example always includes a nonzero bad count, but the requirement's language doesn't condition the notification on that count being nonzero. R1 states the app's entire purpose in absolute terms: "The app notifies the user when a new bad debit arrives. Nothing else." A summary push reporting "5 new charges, 0 bad" is not a bad-debit alert — sending it anyway would violate R1's "nothing else"; suppressing it when the count is zero is never stated as the rule either. Why it matters: this is a real behavioral fork (send a no-op informational push after every reconnect, or go silent when nothing bad happened) that the doc doesn't resolve, and either wrong guess produces a visible defect — either notification spam or a reconnect that goes unexpectedly quiet, which is the exact ambiguity R19 ("a dead connection is never silent") was written to prevent.

**[Gap]** — R5/R9's unconditional good/bad binary doesn't reconcile with R10c's "provisional" display state — **R5, R9 vs. R10c** — Scenario: R10c states pending (not-yet-booked) charges "may appear in the list as provisional." But R5 states "Every debit is either good or bad" with no exception, and R9 states the list shows debits "each marked good or bad" — also with no exception. It's left undefined whether a provisional debit carries a good/bad marking *in addition to* being tagged provisional, or whether classification is withheld until booking; and whether, once the charge books, the provisional row is replaced in place or a second row is added alongside it (relevant because, per R10b/R10c's own admission, pending charges often lack the `entry_reference` needed to link the two). Why it matters: this exact case has "zero evidence from any account pulled so far" per the doc's own Follow-ups, so it is untested as well as unspecified — an implementer has no worked example to fall back on and could easily ship a list that double-counts a charge across its pending → booked transition, which is precisely the "false 'you were charged twice'" outcome R10c elsewhere says is worse than a late alert.

**[Gap]** — R23b's persistence guarantee is written only for the debit list's detail round-trip, not the rules list's edit round-trip — **R23b vs. R23, R13/R14** — Scenario: R23 explicitly makes *both* list screens searchable (debit list and rules list), but R23b's persistence rule is phrased around one concrete round trip only: "It survives list → debit detail → Back." The rules list's equivalent drill-down is the Edit Rule screen (R13/R14), reached and left via Save-or-Back rather than a "detail" screen — and R23b never states whether a rules-list search query survives that round trip the same way. Why it matters: the two searchable screens (R23) are given one shared persistence rule in name, but the rule's literal wording covers only one of the two round trips a user actually takes — an implementer building the rules-list search from R23b's text alone could reasonably clear the query on Save (it's a different transition than "Back") and produce an inconsistency between the two tabs that nothing in the doc flags as wrong.

**[Gap]** — The boundary between "first sync" (silent) and "reconnect after a gap" (summary push) is undefined for the delete-then-reconnect path — **R20 vs. R10c, R18** — Scenario: per R18, deleting the account wipes both rules and history — "gone, not undoable." If the user then reconnects (even the same bank), the app's data state is empty, indistinguishable from a brand-new install. Taken literally, R10c's rule ("everything pulled during the first sync is stored as already-seen and never notifies") would apply again, meaning the reconnect produces *no* notification at all — not even R20's summary. Nothing in the doc confirms this is the intended reading versus treating a post-deletion reconnect as a "gap" reconnect (R20). Why it matters: unlike the ordinary first-time user (where R10c's silence is clearly correct), this user has just been through R18's "not undoable" warning and may be watching closely for confirmation the app is monitoring again — going fully silent on reconnect here is a plausible but unstated behavior that could read as broken rather than correct.

---

## Summary

- Walkthrough 1: 3 findings (1 contradiction, 2 gaps)
- Walkthrough 2: 6 findings (3 contradictions, 3 gaps)
- 9 findings total. All are either direct textual contradictions between two REQUIREMENTS.md
  sections or concrete scenarios the doc's existing requirements don't resolve; none duplicate an
  item already named in the doc's own "Open points" or "Follow-ups" sections (the R22/EUR gap, the
  two "still unspecified" items, and the R19 mock-coverage gaps were all excluded on that basis).
