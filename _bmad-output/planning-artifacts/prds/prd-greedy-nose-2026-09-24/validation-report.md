# Validation Report — REQUIREMENTS.md

- **PRD:** `C:\Workspace\greedy-nose\REQUIREMENTS.md`
- **Rubric:** `C:\Workspace\greedy-nose\.claude\skills\bmad-prd\assets\prd-validation-checklist.md`
- **Run at:** 2026-09-24
- **Grade:** Fair
- **Reviewers:** PRD quality rubric walker · `bmad-review` (edge-case-hunter, verification-gap lenses) · ad-hoc use-case walkthrough (first use, normal ongoing use)

## Overall verdict

REQUIREMENTS.md is a well-run requirements doc for its scope: dated decisions, named trade-offs,
disciplined terminology, and honest self-correction against real bank data. All three reviewers
converge on one root problem, though: **the onboarding classify/bulk-review flow is load-bearing
for two requirements (R4b, R10c) but was never itself specified**, and this has already caused a
real divergence between `ARCHITECTURE.md`'s promise and what the ingestion worker actually built
(per `CLAUDE.md`'s 2026-09-23 entry). Beyond that single root cause, the two scenario-focused
reviewers (use-case walkthrough, edge-case-hunter) independently converged on the same set of
further problems — most importantly that **R20's reconnect-summary push has no floor for a
zero-bad-debit gap**, which both reviewers flagged, unprompted, as a direct conflict with R1's
"nothing else" purpose statement. Independent convergence across separately-run reviewers on the
same findings (not just the same theme) is a strong signal these are real, not artifacts of one
reviewer's framing.

## Dimension verdicts (PRD rubric)

- Decision-readiness — strong
- Substance over theater — strong
- Strategic coherence — strong
- Done-ness clarity — strong, with one high-severity hole
- Scope honesty — strong
- Downstream usability — strong
- Shape fit — strong

## Findings by severity

Findings are clustered by root cause; duplicate findings independently surfaced by more than one
reviewer are merged and marked **(convergent)**.

### High (3 clusters)

**[Convergent — rubric, use-case walkthrough, edge-case-hunter] Onboarding classify flow is unspecified but load-bearing**
(§R4b, §R10c, § closing "still unspecified" paragraph)
R4b's whole justification for the reviewed/unreviewed distinction is "Only onboarding uses the
distinction, for its progress" — R10c depends on it too ("Everything pulled during the first sync
is stored as already-seen"). No R-number defines what the classify screen actually does: order,
exit condition, or whether a payee can leave onboarding still unreviewed. Concrete consequence
(use-case walkthrough): a payee with 20 historical debits, left unclassified at onboarding, gets
R12a's *"New payee — you haven't seen this one before"* on its next charge — false, since R4b
defines "no rule" as merely unreviewed, not unseen. Not hypothetical: `CLAUDE.md`'s 2026-09-23
status entry records the ingestion worker already diverged from `ARCHITECTURE.md`'s promised
onboarding-classify routing for exactly this reason.
*Fix:* give onboarding its own R-number(s): what "classify" means operationally, what triggers it,
completion criteria (must every payee be reviewed, or can the user exit early?), and what "skip"
means for R10c's bootstrap.

**[Convergent — use-case walkthrough, edge-case-hunter] R20's reconnect summary conflicts with R1 and has no wording spec**
(§R20 vs §R1, §R12/R12a)
R20 ("sends one summary notification... e.g. '3 bad'") states no floor — nothing says it's
suppressed when the gap produces zero new/zero bad debits. R1 states the app's entire purpose in
absolute terms: "notifies when a new bad debit arrives. Nothing else." A "5 new charges, 0 bad"
push after every reconnect is not a bad-debit alert; sending it anyway contradicts R1, but going
silent is never stated as the rule either. Separately, R20 is the one notification type in the
doc with no title/body wording and no defined tap target — R12a's table is otherwise exhaustive
for single-debit notifications but doesn't cover an aggregate across several payees, and R12 says
tapping opens "the debit detail" (singular) with no rule for which of the N debits that means.
*Fix:* state the zero-count behavior explicitly (most likely: suppress below a threshold), and
extend R12a-style wording + a defined tap target to R20's summary push.

**[Convergent — use-case walkthrough, edge-case-hunter] Connection start / reconnect / delete boundaries are undefined**
(§R18 vs §R10c vs §R20)
Three related gaps, same root cause — R18 only specifies how a connection *ends*, never how one
*starts* (what "first sync" actually pulls, its scope, how it hands off to onboarding) or what
happens when: (a) the historical pull seeding onboarding is interrupted partway through — is the
resumed pull still "first sync" (silent, R10c) or a live gap (R20)? (b) the user deletes the
account (R18: rules and history wiped) and reconnects — same bank, empty local state — is that a
fresh silent first-sync (R10c) or a gap-summary reconnect (R20), given there's no prior "seen"
state to diff against? Both reviewers independently flagged the delete-then-reconnect case with
near-identical reasoning.
*Fix:* add a start-of-connection counterpart to R18's table, and state explicitly that a
post-deletion reconnect re-enters R10c's silent bootstrap, never R20's path.

### Medium (4 clusters)

**[Convergent — use-case walkthrough, edge-case-hunter] R23b's search-persistence rule only names the debit-detail path**
(§R23b vs §R23, §R13/R14)
R23 makes both the debit list and rules list searchable; R23b's persistence wording only says the
query "survives list → debit detail → Back." The rules list's equivalent round trip (rules list →
Edit Rule → Back/Save) is never named, so an implementer could reasonably clear the query on Save
(a different transition than "Back") and break the "several hits without retyping" promise on one
of the two searchable screens with nothing in the doc to flag it as wrong.
*Fix:* extend R23b to name both round trips explicitly.

**Pending/provisional debit lifecycle is unspecified**
(§R10c vs §R5/§R9, and internally within §R10c)
R10c says a pending charge "may appear in the list as provisional," but R5/R9 state the good/bad
binary and the list marking with no stated exception for a provisional row — unclear whether a
provisional debit carries a classification at all. Separately, R10c never states what happens to
an already-displayed provisional row once the same charge arrives booked with a new
`entry_reference` — replace in place, or a second row appended? This case has "zero evidence from
any account pulled so far" per the doc's own admission, so it's untested as well as unspecified,
and the wrong guess produces exactly the double-charge-looking artifact R10c elsewhere calls worse
than a late alert.
*Fix:* state whether provisional rows carry a classification, and add an explicit replace-not-append
merge rule keyed on a non-identity match (date/amount/payee) for the booked version.

**R3a's payee-identity key is fragile in three distinct, separately-surfaced ways**
(§R3a, §R10b, and the implementation)
(1) Doc: the tier-2 "creditor agent... where it helps separate two payees that would otherwise
collide" trigger condition never says when creditor agent is actually included. (2) Doc: R3a cites
"R10b's fallback key" as if it were the exception case, but real data (cited later in the same
file) shows 63 of 91 real debits used the fallback — the majority path, stated as a minority
elsewhere. (3) Code: R3a's own text — and `ARCHITECTURE.md`'s repetition of it as settled fact —
requires "the raw strings seen" to be stored alongside the resolved key, for future re-tuning; the
actual `Payee` entity has a single `Name` field, unconditionally overwritten on every ingestion
tick and rule save, so this is asserted but not built.
*Fix:* tighten the tier-2 trigger wording; restate R10b's fallback as the common case, not the
exception; and either implement raw-string history or strike the claim from R3a/`ARCHITECTURE.md`.

**R24a cites a save/discard mechanic that no requirement actually defines**
(§R24a vs §R4, §R13/R14)
R24a justifies tab-switch behavior with "Back already discards on that screen (R4's single-commit
form)" — but R4 only defines the rule's *data* (classification + optional amount); no R-number in
the file states that Save commits both fields together while Back discards the draft. The doc
declares itself the single source of truth, yet the one place naming this mechanic points at a
section that doesn't define it. Separately (code-level, `bmad-review` verification-gap): the
actual behavior R24/R24a describe rests entirely on one untested `popToTopOnBlur: true` navigator
option (`app/src/navigation/AppNavigator.tsx:160`) — no render/integration test exercises it, so
either the doc gap above or a react-navigation change could silently break both requirements while
`npx jest` stays green.
*Fix:* add the missing Save/Back commit-semantics requirement (likely belongs in R13/R14), and add
a render test asserting the tab-blur reset + draft-discard behavior.

### Low (4)

- **[rubric]** "Open points" section reads as exhaustive ("**None.**") but isn't — it misses R22's
  self-flagged EUR/bank-agnostic contradiction and the closing paragraph's two "still unspecified"
  items, both real live tensions living in unrelated sections. *Fix:* add a short "currently open"
  pool, or rename the section to reflect it only covers the original nine points.
- **[rubric]** R19's self-triggered push (consent expiry) has no wording spec, unlike R12a's
  otherwise-exhaustive table — and the doc's own Follow-ups already flags two related undrawn
  mock states. *Fix:* extend R12a's table to this push.
- **[rubric]** One glossary-discipline slip: "a pending (not-yet-booked) **transaction**" near
  "Refining these from real data," outside R0's two named exceptions. *Fix:* "…debit."
- **[bmad-review verification-gap, code-level, not a doc issue]** R4a requires amounts to have "at
  most two decimal places," but the backend validator (`RulesValidation.cs:62-65`) only checks
  `<= 0` — `12.999` is accepted and then silently rounded by Postgres's `numeric(12,2)` column
  rather than rejected. Flagged for awareness; doesn't change REQUIREMENTS.md itself.

## Mechanical notes

- ID continuity (R0–R24, O1–O9) is clean — no gaps, no duplicates, all nine open points roundtrip
  into the closing mapping table.
- All cited mock IDs and `ARCHITECTURE.md` section references resolve.
- No `[ASSUMPTION]`/`[NOTE FOR PM]` bracket-tag convention is used (doc predates the BMAD tooling
  in this repo) — the substance is present in prose throughout, so this is a convention
  difference, not a gap.
- R7 vs R10a ("editing a rule re-labels existing debits" vs "re-labeling never re-notifies") was
  checked explicitly by the use-case walkthrough and found **consistent** — R10a's own text
  already anticipates and resolves this case. Named here so it isn't re-checked later.

## Reviewer files

- `review-rubric.md`
- `review-bmad-review.md` (edge-case-hunter, verification-gap lenses)
- `review-usecase.md` (first-use and normal-use walkthroughs)
