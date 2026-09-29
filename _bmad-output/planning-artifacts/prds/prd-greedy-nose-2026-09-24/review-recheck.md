# Re-check of the 2026-09-24 REQUIREMENTS.md edit pass

Audited file: `REQUIREMENTS.md` (474 lines), read in full. Cross-checked against
`mocks/01c-classify-payees.html` (the cited source for R26/R26a).

## Part 1 — closure verdicts on the 9 prior findings

| # | Verdict | Justification |
|---|---|---|
| 1 | CLOSED | R26/R26a accurately reflect `01c-classify-payees.html` (bulk toggle, "Start Monitoring", the footer hint text is echoed almost verbatim by R26a). R12a's reworded "no rule" body ("You haven't reviewed this payee yet") is generic enough to stay true for a genuinely-new payee, a skipped-at-onboarding payee with visible history, and any other future "no rule" case — no scenario found where the wording becomes false. |
| 2 | CLOSED | R20a's "fires only when the gap contains ≥1 bad debit" directly supplies the missing floor; a zero-bad-debit gap now explicitly produces silence, removing the conflict with R1 that the original finding described. |
| 3 | CLOSED | All three named boundaries are now explicit: connection-start (R25's first sentence), interrupted-first-sync (R25's own "resuming it is still the same first sync, never a gap" clause), and delete-then-reconnect (R20b, routing back into R25). No boundary is left implicit. |
| 4 | CLOSED | R23b now names both round trips explicitly ("list → debit detail → Back on the Debits tab, and equally list → Edit Rule → Back or Save on the Rules tab"), closing the asymmetry the finding described. |
| 5 | PARTIALLY CLOSED | The two new bullets do answer the literal questions asked (a provisional row classifies via R6; booking replaces the provisional row "in place"). But the replacement mechanism newly asserted here reuses R10b's composite key, which includes **date** — and pending vs. booked reports commonly carry different dates for the same real-world charge (authorization date vs. settlement date, especially for card payments). The bullet states "It is never shown as a second row" as settled fact, with no caveat, even though the document's own closing section ("Refining these from real data") admits pending debits have **zero real evidence** behind them. The double-count risk the finding was about is narrowed, not eliminated, and the remaining risk isn't flagged. |
| 6 | CLOSED | The reworded trigger ("folded into the key whenever the ASPSP reports one for that charge, not only once a collision... is detected — there is no separate collision-detection step") removes the soft, undefined "where it helps" language entirely and states an unconditional rule. |
| 7 | CLOSED | The new R10b paragraph states the 28/91 figure plainly and calls the fallback "a permanent, load-bearing path... not a stopgap," matching and cross-consistent with the same figure already used in the "Refining these from real data" section at the bottom of the file. |
| 8 | CLOSED | R24a's citation now points at R13a, and R13a actually defines the single-commit Save/Back mechanic R24a needs — the dangling citation to an R4 that never defined it is gone. |
| 9 | CLOSED | The new R10c bullet ("A charge still pending at the moment of a reconnect-gap resync... isn't counted in that gap's summary either... rolls into an ordinary per-charge notification... Neither double-counted nor dropped") gives the pending-at-resync charge an explicit, single home. |

**Summary: 8 CLOSED, 1 PARTIALLY CLOSED, 0 NOT CLOSED.**

## Part 2 — new issues found in this edit pass

### 1. R20a's floor doesn't say what happens at exactly N=1
**R-numbers:** R20, R20a, R10/R10a
**Scenario:** A reconnect gap turns up exactly one new debit, and it's bad. R20a's floor ("fires only when the gap contains at least one bad debit") is satisfied, so by construction the R20 summary path applies rather than R10/R10a's ordinary per-charge path — but this is only inferable from the absence of a stated exception, never spelled out. R20's own title template ("«N» new debits while you were disconnected") also reads grammatically odd at N=1 ("1 new debits"), a pre-existing copy issue that the new threshold now makes reachable as a documented, expected state rather than a theoretical one.
**Why it matters:** Not a contradiction, but a natural question the new threshold invites and doesn't answer — worth one clarifying clause ("even a single bad debit in the gap still uses the summary, never a per-charge alert") so an implementer doesn't invent a third branch.

### 2. R4b's "for its progress" claim is still uncashed after R26/R26a
**R-numbers:** R4b, R26, R26a
**Scenario:** R4b (untouched by this pass) says "Only onboarding uses the distinction [reviewed vs. not], for its progress." R26/R26a now specify onboarding's actual mechanics — bulk mark good/bad, leave anytime via "Start Monitoring," unreached payees stay bad — but neither describes any progress indicator (a counter, a "X of Y reviewed" label, or anything else that visibly uses the reviewed/not-reviewed distinction). The `01c-classify-payees.html` mock itself shows no progress counter either, only a static "We found 5 payees" hint.
**Why it matters:** R4b's forward-reference now resolves to a real section instead of nothing, but the specific claim it makes ("for its progress") is still not backed by any described mechanic. Either R26 should say what the progress UI is, or R4b's "for its progress" phrase should be softened — as written, the two sections don't actively contradict each other, but R4b makes a promise R26 never delivers on.

### 3. The Open Points changelog paragraph misattributes this pass's own edits to the earlier session
**R-numbers:** R12a, R20a, R20b (all touched/added 2026-09-24) vs. the "Open points" section's dating
**Scenario:** The Open Points section says: *"Currency (R15–R17), notification wording (R12a) and bank-connection handling (R18–R21) were settled in the same session [2026-08-14]. Onboarding (R25/R26/R26a) was settled 2026-09-24..."* But R12a's own body text now contains a paragraph that explicitly cites `R26a` ("the second case has visible history... so claiming it's never been seen would be false" — referencing "R26a" by name), which cannot have existed on 2026-08-14 since R26a is dated 2026-09-24 by the very next sentence. Likewise R20a and R20b — both new in this pass per the findings above — fall inside the "(R18–R21)" range the same sentence attributes to the old session.
**Why it matters:** This is the document's own bookkeeping contradicting itself: content demonstrably written today is dated to a session five weeks earlier by the very paragraph whose job is to track that. Given this project's stated practice of using such changelog notes for session continuity (per `CLAUDE.md`/`AGENTS.md` conventions), a stale provenance trail here is a real (if low-severity) defect, not just cosmetic.

No other new contradictions were found. Specifically checked and found clean: the new "## Onboarding" section has no forward references to anything undefined at that point in the document (R25, R4, R13, R13a, R10, R12a are all defined earlier); R26a's "next new charge... notifies normally" is consistent with R10a's first-time-seen test (the scenario of a skipped payee's 21st debit is unambiguously a new debit identifier, distinct from its 20 already-seen historical ones); and R25 sitting textually before the lower-numbered R18 does not read as a contradiction — it follows the document's pre-existing convention of ordering by topic/section rather than by requirement number (R23/R24 already sit before R10–R14 for the same reason), and R25's forward reference to R20 (two paragraphs later in the same section) is a short, easily-resolved hop consistent with other forward references throughout the file.
