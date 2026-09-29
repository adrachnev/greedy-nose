# PRD Quality Review — REQUIREMENTS.md

## Overall verdict

This remains one of the strongest documents of its kind in scope: exceptionally well-evidenced (real Enable Banking answers, real N26 account counts), honest about its own gaps, and correctly shaped for a solo hobby project. But today's revision (R25, R26/R26a, R20a/R20b, R13a, plus wording fixes) introduces two concrete, checkable contradictions between brand-new normative text and the mocks it cites as ground truth — R13a's flat "there is no per-field save" is contradicted by `04b-payee-edit.html`'s "Clear alert limit" button, and R26's "the same as the Edit Rule screen" is contradicted by `01c-classify-payees.html`'s actual mechanics (no Save, no Back, a "Mark all Good" bulk action). Neither is a sign of a broken document, but both sit exactly on the seam this PRD cares most about — the mechanics that keep a bad charge from silently not alerting — and both were introduced today without being checked against the mocks AGENTS.md designates as ground truth for `coder-mobile`.

## Decision-readiness — adequate

Trade-offs are named honestly and specifically throughout the document, not smoothed to neutral: R3b states the cost of each choice ("Splitting... a false alert: annoying but safe. Merging... a missed alert, which breaks R1"), R10c openly prices its own latency cost ("The cost is latency — typically under a day"), and R22 self-flags its own contradiction with R15 rather than hiding it. This is the opposite of "every choice balances everything."

Today's edits mostly continue this honesty (R20a explains exactly why silence is correct, not a bug; R20b explains why a post-delete reconnect isn't R20's path). The exception is that two of today's additions are written as settled fact without the verification the rest of the document holds itself to — see Findings.

### Findings
- **high** R13a asserts a screen mechanic that its own cited screen contradicts (§ R13a / `mocks/04b-payee-edit.html`) — R13a states flatly: *"There is no per-field save — this is the actual screen-level mechanic that R4's 'at most one rule' and R8's 'changes only when the user changes it' require in practice."* But `04b-payee-edit.html` (already "done," per the Follow-ups section) has, alongside **Save**, a separate **Clear alert limit** button whose `href` points to the same target as Save (`04-payee-rules.html`) with the hint text *"Clears the amount above — every charge from this payee counts as good again. Status (Good) is unchanged."* — worded as a completed, standalone action, not a draft-field edit awaiting a later Save. This reads exactly like the per-field save R13a says doesn't exist. *Fix:* either reconcile the wording ("no per-field save, except the dedicated Clear-limit shortcut, which commits only the amount") or confirm the mock is wrong and correct it — but the flat claim as written is not true of the shipped screen.
- **medium** R26's equivalence claim is not demonstrated by the mock it cites (§ R26) — *"the user sees every payee that sync turned up and can mark any of them good or bad, the same as the Edit Rule screen (R4, R13, R13a)."* See the Done-ness clarity finding below for the full evidence; noted here because the citation reads as a settled decision rather than the open interaction question it actually is.

## Substance over theater — strong

No persona theater, no Vision-statement filler, no NFR boilerplate — none of these apparatus exist, correctly, for a solo/hobby scope. What's here is earned: `R3a`'s two-tier key traces to an actual Enable Banking support answer; `R10b`'s "28 of 91 real debits carried an `entry_reference`" and the "Refining these from real data" section's specific counts are the opposite of copied boilerplate. Today's additions keep this bar — `R4a`'s new bound ("positive number, at most two decimal places; zero or negative is rejected") and `R3a`'s clarified creditor-agent folding rule are both concrete, falsifiable statements, not padding.

No findings.

## Strategic coherence — strong

The thesis (`R1`: notify on new bad debits, nothing else) drives every feature; nothing reads as a backlog item bolted on because it seemed useful. Today's onboarding requirements are a clean example: `R26`/`R26a` derive directly from `R4b`'s "has a rule = reviewed" and `R10c`'s "already-seen" mechanic rather than inventing new concepts, and `R20a`/`R20b` are tightly scoped corrections to `R20`'s existing burst-prevention logic, not new territory. Success-metric apparatus is absent, appropriately, since this is a single-user tool, not a product needing engagement metrics.

No findings.

## Done-ness clarity — thin

Most of the document is exemplary here — `R5`'s table, `R10b`/`R10c`'s concrete identifier and booked/pending rules, `R19`/`R20`'s literal Title/Body templates, and today's `R4a` addition (explicit positive-number/two-decimal-place/zero-rejected bound) all give an engineer a testable target. But two of today's additions fail exactly the test this dimension is "unforgiving" about: would an engineer know what to build. Both trace to the same root cause — new requirements text asserting a mechanic without checking it against the mock that is supposed to be ground truth for the same screen.

### Findings
- **high** R26's cited mock doesn't have the mechanic R26 says it has (§ R26, `mocks/01c-classify-payees.html`) — R26: *"...can mark any of them good or bad, the same as the Edit Rule screen (R4, R13, R13a) — this is the only point where classifying happens in bulk instead of one payee at a time (`01c-classify-payees`)."* The cited mock's nav bar has no Back link at all (`<div class="nav-bar"><h1>Review Payees</h1><span></span></div>`, vs. 04b/04d's `<a class="nav-link">‹ Rules</a>`), has no per-card Save button, has no amount-limit field on any card, and its own embedded HTML comment states the actual mechanic plainly: *"tapping either button writes a rule, and a payee with a rule counts as reviewed."* That is an immediate per-tap commit with no discard path — the opposite of R13a's "Save writes... Back discards... there is no per-field save" model. An implementer following R26's citation literally would build Save/Back gating that the approved mock doesn't have. *Fix:* describe the onboarding screen's actual mechanic (immediate write per tap, no amount field, no back/discard) instead of citing R13a's model, or update the mock to match if Save/Back parity is actually intended.
- **medium** Onboarding drops the amount-limit half of "the same as the Edit Rule screen" without saying so (§ R26, contrast with R4/R4a and `04b-payee-edit.html`) — the Edit Rule screen the citation points to includes an "Alert limit (optional)" field (`R4a`); the onboarding screen shows only a Good/Bad toggle per payee, with no way to set a limit during bulk review. A user wanting "Good, up to €X" during onboarding must go back later via the Rules tab — reasonable, but R26 doesn't say so, so a reader takes "the same as the Edit Rule screen" at face value. *Fix:* one sentence stating onboarding is classification-only, limits are a follow-up edit.

## Scope honesty — adequate

This document has an unusually good scope-honesty track record: the "Open points" section (freshly restructured today) explicitly tracks the two remaining open items rather than letting them hide, `R22` self-flags its own EUR-only gap, and the "Follow-ups" section maintains a live list of known mock gaps including ones only just resolved today (item 2, the R19 push-notification mock). That discipline is genuinely rare and worth crediting.

### Findings
- **high** "Mark all Good" is a safety-relevant bulk action with zero requirements coverage (§ R26 / `mocks/01c-classify-payees.html` line 27) — the onboarding screen R26 cites has a green **"Mark all Good"** button above the per-payee list. Per the mock's own comment ("tapping ... writes a rule"), this would set every payee from the historical pull — including the mock's own example "ScamyLoans GmbH," shown right there as a bad-looking direct debit — to Good with no limit, i.e. permanently silent for every future charge under `R5`'s table. `R1`'s entire purpose is catching exactly this kind of payee, and `R3b`'s stated philosophy is "when matching is uncertain, split rather than merge... missing an alert breaks R1" — yet a one-tap bulk-approve of everything, sight unseen, is left completely ungoverned: no confirmation step, no exclusion logic, no `[NON-GOAL]`/open-question flag anywhere in `R26`, `R26a`, or the Open points section. *Fix:* either add a requirement governing this control (confirmation dialog, or scope it out explicitly as a Follow-ups/mock-gap item), or state plainly that it's not meant to ship as-is.

## Downstream usability — adequate

This is explicitly a chain-top, standalone document ("where either disagrees with this file, this file wins") feeding `ARCHITECTURE.md`, `app/`, and `backend/`, so this dimension matters more than usual here. The mechanics check out well: R-numbers are contiguous with no gaps or duplicates (verified R0 through R26a); today's cross-references resolve — `ARCHITECTURE.md` does have "Payee identity," "Debit identity," and "Refining these from real data" sections matching the pointers added in `R3a`/`R10b`; the `05b` mock's literal notification text ("12 new debits while you were disconnected" / "3 of them are bad. Tap to review them.") matches `R20`'s new Title/Body template exactly; `01d`'s "Bank connection expired" matches `R19`'s new Title. Terminology (`R0`) is followed consistently in the new text — no stray "debtor"/"transaction"/"trusted."

The two Done-ness findings above are also downstream-usability failures in a stricter sense: a workflow that "source-extracts" `R26` or `R13a` in isolation would build the wrong screen. See those findings rather than repeating them here.

### Findings
- **low** Stray capitalization on the classification value (§ R26a) — *"a payee they didn't get to simply has no rule, which R4b/R5 already make Bad."* Every other unquoted, mid-sentence use of the value in running prose is lowercase ("classified bad," R10; "switch good ↔ bad," R13). *Fix:* lowercase to "bad."
- **low** `ASPSP` is used from `R3a` onward and never expanded (§ R3a, R10c, R22, R25). Minor friction for a document that positions itself as the sole source of truth read section-by-section. *Fix:* expand on first use.

## Shape fit — strong

Correctly light on the apparatus a solo/hobby, single-operator-role tool doesn't need — no personas, no Vision statement, no Success Metrics — while staying heavy where this domain actually needs rigor: precise rule tables (`R5`), exact identifier semantics (`R10b`/`R10c`), and worked examples embedded directly in the requirements (`ScamyLoans GmbH · 49,00 €`; the 28/91 `entry_reference` split) that do the job UJs would do in a multi-stakeholder product, more economically. This is the right shape for a notification-correctness spec, not a product pitch.

No findings.

## Mechanical notes

- **ID continuity**: contiguous and unique, R0 through R26a, no gaps or duplicates. `O1`–`O9` are all mapped to their resolving R-numbers in "Open points."
- **Cross-ref resolution**: all mock filenames checked today (`01c-classify-payees`, `05b`, `01d-connection-expired`, `06b`, `06c`) exist in `mocks/`; both new `ARCHITECTURE.md` pointers ("Payee identity," "Debit identity," "Refining these from real data") resolve to real section headers there.
- **Section order vs. number order** (pre-existing, not introduced today): physical section placement doesn't track R-numbers — "Banks" (`R22`/`R22a`) sits before "Currency" (`R15`–`R17`) despite the higher number, and "Bank connection" opens with the newly-added `R25` ahead of the lower-numbered `R18`–`R21`. This is deliberate (numbers = chronological settlement order, sections = topic) but is worth a one-line note at the top of the file to pre-empt confusion when pulling a section out alone.
- **Citation style drift** (low): mock references are sometimes the short code (`` `05b` the mock``) and sometimes the full filename (`` `01c-classify-payees` ``) — cosmetic only.
- No `[ASSUMPTION]`/`[NOTE FOR PM]` tag convention is used in this document at all (it uses its own "Open points"/"Follow-ups" mechanism instead) — consistent throughout, not a drift, just noting the convention for anyone expecting the tag format literally.
