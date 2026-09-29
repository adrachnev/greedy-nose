# PRD Quality Review — REQUIREMENTS.md

## Overall verdict

This is an unusually rigorous requirements doc for its scope — a solo/hobby, single-user app —
and it earns that rigor rather than performing it: decisions are dated and stated plainly, the
conservative bias (prefer a false alert over a missed one) is named once and then genuinely
carried through the notification and identity requirements, terminology is disciplined and
mechanically checkable (R0), and the doc revises itself against real bank data with cited
evidence rather than asserting. Its main risk is a real hole, not a theater problem: the
onboarding classify/bulk-review flow is referenced as load-bearing by two requirements (R4b,
R10c) but was never itself written as a requirement, and that gap has already caused a
documented divergence between what `ARCHITECTURE.md` promises and what the ingestion worker
actually built (see the Done-ness finding below). Everything else — done-ness, scope honesty,
downstream usability, shape fit — holds up well with only minor, fixable gaps.

## Decision-readiness — strong

Decisions read as decisions, not considerations: every settled requirement carries a date and,
where it superseded an open point, the `On` it replaced (e.g. R8: "the existing automatic flip
is therefore removed in full (settled 2026-08-14, was `O1`)"). Trade-offs are named with what
was given up, not just what was chosen — R3b spells out that splitting a payee produces "a false
alert: annoying but safe" while merging "lets an unknown payee inherit 'good' → a missed alert,
which breaks R1"; R10c states the cost plainly ("The cost is latency — typically under a day")
and rejects the alternative by naming its own failure mode ("a false 'you were charged twice' is
worse than an alert arriving a few hours later"). R22 is the strongest example of a PRD not
smoothing over its own tension: it states its bank-agnostic requirement and then, in the same
entry, flags that R15's EUR assumption contradicts it — "not yet a contradiction in practice...
but R22 as written does not itself carve that out."

### Findings

- **medium** "Open points" section is not actually exhaustive (§ Open points) — The section
  states "**None.** All nine points raised on 2026-08-14 are settled," which is true of the
  original `O1`–`O9` list, but it is not the doc's only live tension. R22's self-flagged EUR
  contradiction and the closing paragraph's "Still unspecified, but not blocking: the Settings
  screen's contents... and the onboarding classify flow's details" are both real open items that
  a reader scanning "Open points" for the current list of unresolved questions would miss
  entirely, since they live in unrelated sections. *Fix:* either rename the section (it is really
  "history of the original nine open points") or add a short "Currently open" subsection that
  pools the EUR/R22 gap and the two "still unspecified" items in one place.

## Substance over theater — strong

No personas, no Vision statement, no Success Metrics section, no NFR boilerplate — and none of
that reads as a gap, because none of it was needed for this document's job. What stands in for
an NFR section (R17's locale formatting, R19's "a dead connection is never silent") is stated
with product-specific bounds, not adjectives. R1's purpose line ("The app notifies the user when
a new bad debit arrives. Nothing else. It is not a budgeting or general finance app.") is sharp
and specific rather than swappable boilerplate. No findings — there is no furniture to flag here.

## Strategic coherence — strong

The thesis in R1 is carried through consistently rather than stated once and forgotten. The
conservative bias it implies is made explicit twice, in mirrored language that cross-references
itself: R3b ("split rather than merge") and R10c's consequences ("the mirror of R3b, in the
opposite direction: for identity, merging is the dangerous move; for de-duplication, it is
treating two charges as one"). Feature depth follows the thesis, not ease of implementation —
payee identity and debit de-duplication (R3a/R3b, R10a–R10c) get the most detailed, most revised
treatment in the document, which is correct given that alerting integrity is the entire product.
The section order (Terminology → Purpose → Core objects → Classification → List → Navigation →
Notifications → Editing → Banks → Currency → Connection → Open points → Follow-ups) reads as an
argument, not a backlog with headings. No Success Metrics section exists, but per this project's
calibration (hobby/solo, one user) that absence is appropriate, not a defect.

## Done-ness clarity — strong, with one high-severity hole

Most requirements carry a directly testable consequence: R5's table, R5a's "less than or equal"
tie-break, R4a's exact validation rule ("a positive number, at most two decimal places; zero or
negative is rejected"), R12a's verbatim notification strings for all three bad-debit reasons,
R18's three-row disconnect/delete consequence table, and R23a's concrete fold examples
(`müller`/`muller`/`mueller`, `12,99`/`12.99`) all give an implementer something to check against
directly. A grep for the rubric's classic vague-adjective tells ("gracefully," "reasonable
performance," "user-friendly," "intuitive") returns nothing in this file — that is a genuinely
clean result, not a coincidence.

### Findings

- **high** Onboarding classify/bulk-review flow is referenced as load-bearing but never specified
  (§R4b, §R10c, § closing paragraph) — R4b's whole justification for the reviewed/unreviewed
  distinction is "Only onboarding uses the distinction, for its progress"; R10c's consequences
  depend on it too ("Everything pulled during the first sync (onboarding) is stored as
  already-seen and never notifies"). Yet no R-number anywhere in this file defines what the
  onboarding classify flow actually does — what screen(s), what "classify" means operationally,
  what happens if the user skips it. The only description of it at all is one sentence in
  `CLAUDE.md`'s Product decisions section (not this file, which this file's own opening line says
  loses ties: "Where either disagrees with this file, this file wins"), plus this doc's own
  closing admission that it is "still unspecified, but not blocking." This is not a hypothetical
  risk — `CLAUDE.md`'s 2026-09-23 status entry records that it already caused a real divergence:
  "the 'First run' ingestion-mode row promises onboarding-classify routing that step 6
  deliberately didn't build (silent bootstrap only)." A requirement two other requirements lean
  on, with no requirement of its own, is exactly the kind of gap that produces exactly this kind
  of divergence. *Fix:* give the onboarding classify flow its own R-number(s) — at minimum what
  "classify" means (is it R4/R5's same good/bad/limit form, applied per-payee in bulk before live
  monitoring starts?), what triggers it, and what "skip" or "not completed" means for R10c's
  bootstrap behavior.
- **medium** R19's self-triggered push has no wording spec, and the doc already flags a related
  mock gap (§R19, § Follow-ups items 1–2) — R12a gives verbatim title/body strings for every
  bad-debit notification reason; R19's "plus one push when the consent expires by itself" gives
  none. The Follow-ups section itself lists this as gap #2 ("R19's 'one push when the consent
  expires by itself' has no mock... That push is what reaches a user who has not opened the app
  in a week") and gap #1 (the disconnected-state banner is undrawn, only the expired one is), both
  marked "decide before the matching screen is coded." Since that screen is presumably close to
  being coded (the tracer bullet is complete through step 7), this is a live, not hypothetical,
  gap. *Fix:* extend R12a's wording table to this push, and close or explicitly re-defer the two
  Follow-ups items with a date.
- **low** R3a's tier-2 trigger condition is soft (§R3a) — "the creditor agent... where it helps
  separate two payees that would otherwise collide" does not say when creditor agent is actually
  included (always, when present? only on detected collision?). Kept low because R3a is already
  explicitly labeled best-effort and "accepted, not solved" elsewhere in the same section, so the
  softness is consistent with the section's own stated confidence level.

## Scope honesty — strong

This is close to a model example of the dimension. Omissions are stated, not left to inference:
R2a names its own accepted side effect ("a refund from a bad payee is invisible in the app"); R3a
says outright "Direct debits — the charge type this product cares most about — get the weakest
matching. This is accepted, not solved"; the "Refining these from real data" section names two
paths with "zero evidence from any account pulled so far" (a non-EUR charge, a pending
transaction) and states the fallback stance explicitly rather than assuming the untested path is
fine. The Follow-ups section's numbered "Known mock gaps, deliberately left open" list is exactly
the kind of honest, specific de-scoping the rubric asks for — each gap is dated, attributed, and
either resolved (struck through with a resolution note) or still open.

### Findings

- **low** The doc's own open-items bucket is thinner than its own house style (§ closing
  paragraph before Follow-ups) — "Still unspecified, but not blocking: the Settings screen's
  contents..., and the onboarding classify flow's details" is one sentence with no elaboration,
  in contrast to the numbered, specific treatment the Follow-ups section gives its mock gaps.
  Given the onboarding item's demonstrated downstream cost (see the Done-ness finding above), it
  deserves the same numbered, specific treatment as the "Known mock gaps" list. *Fix:* expand this
  line into a short numbered list naming what's actually undecided about each item.

## Downstream usability — strong

The Glossary (R0) is a real, enforced table, not decoration — it names two explicit exceptions
("charge" in prose; bank field names keep their own spelling) rather than leaving the reader to
guess at inconsistencies, and a spot-check of the rest of the document confirms the convention is
followed. ID continuity is clean: R0 through R24 (with sub-letters) has no gaps and no duplicates,
and all nine original open points (`O1`–`O9`) roundtrip into the final mapping table with no
orphans on either side. Cross-references resolve: `ARCHITECTURE.md`'s "Payee identity" and "Debit
identity" sections exist as named; every mock ID cited (`01`, `01b`, `01bb`, `01d`,
`01d-connection-expired`, `01e`, `02b`, `02c`, `02d`, `03b`, `04b`, `04d`, `05b`, `05c`, `06`,
`06b`, `06c`) exists in `mocks/`. Each requirement is written to stand alone when cited by number
— cross-references point at R-numbers and named `ARCHITECTURE.md`/mock sections, not "see above."

### Findings

- **low** One glossary-discipline slip (line ~397, "Refining these from real data") — "a
  non-EUR charge, and a pending (not-yet-booked) **transaction**" uses the term R0 explicitly
  retires, outside either of R0's two named exceptions (prose "charge," or a bank field name).
  Minor and isolated, but worth a fix given how deliberately R0 is enforced everywhere else.
  *Fix:* "a pending (not-yet-booked) debit."

## Shape fit — strong

The capability-spec shape (atomic, numbered, testable requirements; no personas, no Vision
statement, no user journeys) correctly matches a solo/hobby, single-operator-style product built
by and for its own author — this is not under-formalized (behavior is still precisely specified
per requirement) or over-formalized (no persona or journey padding for an audience of one).
`mocks/` does the job UJs would otherwise do for a consumer product: REQUIREMENTS.md cites
specific mock IDs by number (`01d-connection-expired`, `06b`, `05c`, …) rather than re-narrating
flows in prose, which keeps the behavioral spec and the visual spec from drifting apart without
duplicating either. No findings against fit itself; one neutral observation carried to Mechanical
notes below.

## Mechanical notes

- **Glossary drift**: one instance (see Downstream usability finding above) — "transaction" at
  the "Refining these from real data" section, otherwise the R0 vocabulary is followed
  consistently throughout, including in the Follow-ups section's historical references to the
  pre-rename names (`setDebtorTrusted`, `useDebtors`, etc.), which are correctly presented as
  superseded rather than current usage.
- **ID continuity**: clean. R0–R24 (with sub-letters) contiguous, no gaps or duplicates. `O1`–`O9`
  all resolved and mapped 1:1 in the closing table with no orphans.
- **Assumptions Index roundtrip**: not applicable in the rubric's literal sense — this document
  predates the BMAD tooling in this repo (added 2026-09-24, same day as this review) and does not
  use `[ASSUMPTION: …]`/`[NOTE FOR PM]`/`[NON-GOAL]` bracket tags. The substance those tags exist
  to surface (assumptions, deferred decisions, explicit non-goals) is present throughout in prose
  — "accepted, not solved," "deliberate exception," "not yet a contradiction in practice" — so
  this is a convention difference, not a gap, and shouldn't be read as one.
- **UJ protagonist naming**: not applicable — no UJs, by design, consistent with the Shape fit
  judgment above.
- **Style note (not a finding)**: nearly every requirement embeds its own revision history inline
  (settlement date, superseded `On`, later "revised" notes) rather than separating "current rule"
  from "how we got here." For a solo author this doubles as an audit trail and is arguably a
  strength, but it does mean a downstream reader extracting "just the current rule" for, say,
  architecture or story generation has to parse past a parenthetical history clause in most
  entries. Worth being aware of if this doc is ever fed to a downstream skill that expects flat,
  history-free FR statements.
