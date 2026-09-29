# BMad Review — REQUIREMENTS.md

**Content:** `C:\Workspace\greedy-nose\REQUIREMENTS.md` (document, full-file scope — not a diff)
**Lenses run:** Edge-Case Hunter, Verification Gap (both explicitly requested)
**Also-considered focus:** the two real-world flows — First Use (consent → onboarding bulk-review →
steady state) and Normal Ongoing Use (classify → notify → edit rules → search → navigate →
expire/disconnect/reconnect) — hunting for contradictory requirements, uncovered flow steps, and
preconditions never established by an earlier step, following the R-number cross-references.

Verification Gap is normally scoped to code/diffs (`applies_to: "code"`); since it was named
explicitly, it ran adapted to this document: "behavior" = what each `R`-numbered requirement
specifies, "verification" = whether the actual `app/` and `backend/GreedyNose.Api` code and test
suites enforce/observe it, cross-checked against `TODO.md`/`CLAUDE.md` so already-recorded,
deliberately-deferred gaps are not re-reported as new findings.

Total findings: **11** (8 edge-case-hunter, 3 verification-gap). Overlap between lenses is signal,
not duplication, and is kept rather than deduped: notably, both lenses converge on the onboarding/
first-sync boundary and the R3a payee-identity data model being underspecified in ways the code
already reflects.

---

## Edge-Case Hunter (8 findings)

### 1. R3a's exception claims an R10b fallback that R10b never independently establishes as its primary mechanism
- **Location:** `REQUIREMENTS.md:181-188` (R10b) & `:78-82` (R3a)
- **Trigger condition:** R3a cites "R10b's fallback key" for missing `entry_reference`, but reading R10b on its own gives no indication this fallback is the common case, not a rare exception.
- **Guard/fix:** State directly in R10b (not only via TRACER-BULLET evidence folded in at the bottom of the file) that the composite fallback `(account, booking date, amount, payee key, ordinal)` is a permanent path for a large share of real debits, not an edge case.
- **Potential consequence:** An implementer trusts R10b's framing as "the rare case" and under-invests in the composite key path, mishandling the majority of real-world debits that lack `entry_reference` (confirmed elsewhere in the same file: 63 of 91 real N26 debits fell back to it).

### 2. Undefined interaction between R18 (account deletion), R10c (silent first sync), and R20 (reconnect summary push)
- **Location:** `REQUIREMENTS.md:271-277` (R18) & `:284-287` (R20) & `:190-204` (R10c)
- **Trigger condition:** After a user deletes the account (R18: rules and history wiped, "gone, not undoable") and later reconnects, it's undefined whether that reconnection is treated as R10c's silent first-sync (fresh onboarding) or R20's "summary push" reconnect path (which assumes prior history to diff against).
- **Guard/fix:** Add an explicit rule: reconnecting after an account deletion always re-enters onboarding/R10c's silent bootstrap, never R20's gap-summary path, because there is no prior "seen" state left to diff against.
- **Potential consequence:** Depending on which code path an implementation takes, the app either stays silent when it shouldn't (masking a truly new payee's first debit) or fires a spurious "N new charges, M bad" push referencing history that was supposedly wiped.

### 3. R20's summary notification has no floor for a zero-new-debits reconnect gap
- **Location:** `REQUIREMENTS.md:284-287` (R20)
- **Trigger condition:** R20 describes the summary push's happy-path wording ("12 new charges... 3 bad") but never states what happens when a reconnect gap produces zero new debits.
- **Guard/fix:** Add: "the summary notification fires only when the new-charge count is greater than zero."
- **Potential consequence:** A pointless "0 new charges, 0 bad" push could fire on every reconnect regardless of content, directly contradicting R1's "notifies only on a new bad debit" purpose statement.

### 4. R10c's booked-only rule vs. R20's reconnect-gap count creates a double-count/drop risk for still-pending charges
- **Location:** `REQUIREMENTS.md:190-204` (R10c) & `:284-287` (R20)
- **Trigger condition:** A charge that is pending at the moment of the reconnect-gap resync (and thus has no `entry_reference` yet per R10c) is never defined as counted in R20's gap summary, in the *next* normal poll's summary, in both, or in neither.
- **Guard/fix:** State explicitly that R20's count covers only debits that are already booked at resync time; a charge still pending during the gap rolls into ordinary per-charge notification once it books, on a later poll — not into the gap summary at all.
- **Potential consequence:** The same physical charge could be double-counted across two different notifications, or silently dropped from both, depending on implementation timing.

### 5. R10c gives no merge rule for a provisional (pending) list row that later receives its booked identity
- **Location:** `REQUIREMENTS.md:190-196` (R10c)
- **Trigger condition:** R10c says pending charges "may appear in the list as provisional" and that the alert fires only once booked — but never states what happens to the *already-displayed provisional row* once the same charge arrives booked with a new `entry_reference`.
- **Guard/fix:** Add: the booked version replaces the provisional row for the same underlying charge (matched by non-identity heuristics — date/amount/payee), it is never appended as an additional row.
- **Potential consequence:** The same physical charge could appear twice in the debit list — once as its original provisional row, once as its newly-booked row with a different key — with no de-duplication rule to prevent it.

### 6. R4b's onboarding "reviewed" progress depends on an onboarding classify flow the doc explicitly leaves unspecified
- **Location:** `REQUIREMENTS.md:97-100` (R4b) & `:301-302` (Follow-ups, "onboarding classify flow's details" listed as unspecified)
- **Trigger condition:** R4b defines "reviewed" (has a rule) as the basis for onboarding progress tracking, but the onboarding flow that is supposed to *drive payees toward* having a rule is explicitly flagged elsewhere in the same document as not yet specified — including whether onboarding can be exited with payees still unreviewed.
- **Guard/fix:** Define onboarding's completion criteria: must every payee be reviewed before onboarding ends, or can the user exit early leaving some unreviewed (= bad, per R4b/R5)?
- **Potential consequence:** A payee left permanently unreviewed after onboarding "ends" re-fires a "New payee — you haven't seen this one before" notification (R12a) on every future charge indefinitely, since no-rule-yet is a stable state, not a transient one.

### 7. No requirement covers granting initial consent / first connect at all — R18 only specifies how a connection *ends*
- **Location:** `REQUIREMENTS.md:269-289` (Bank connection section, R18-R21)
- **Trigger condition:** The "Bank connection" section's only lifecycle table (R18) enumerates three ways a connection *ends*; nothing in the document specifies the *start* of a connection — what "first connect" actually pulls (how much history), what its scope is, or how it hands off into onboarding.
- **Guard/fix:** Add an R-number for first consent/first-sync scope (e.g., how far back history is pulled, what "onboarding" formally receives from that first sync), mirroring R18's ended-connection table with a started-connection counterpart.
- **Potential consequence:** R10c's "everything pulled during the first sync is stored as already-seen and never notifies" depends on a well-defined "first sync" whose actual scope (all history? last N days? last N transactions?) is never established anywhere in the document.

### 8. R23b's scoped-query persistence rule names only one navigation path, leaving the Rules-list equivalent unaddressed
- **Location:** `REQUIREMENTS.md:136-158` (R23/R23b)
- **Trigger condition:** R23b states the search query "survives list → debit detail → Back" and is "cleared when the tab is left" — but the Rules list's analogous drill-down (rules list → Payee Edit screen → Back) is never named, even though R23 establishes the Rules list is also searchable.
- **Guard/fix:** Extend R23b to explicitly also cover: "…and rules list → Payee Edit → Back."
- **Potential consequence:** Read literally, R23b's persistence guarantee applies only to the debit-detail path; a rules-list search query could be silently lost when the user edits a payee and returns, breaking the "several hits can be worked through without retyping" promise R23b states as its rationale.

---

## Verification Gap (3 findings)

Ruled out as already recorded/deferred (not re-reported): R19/R20 (Health Monitor / reconnect
summary — `TODO.md` "Health Monitor (not built)" / "Reconnect mode (not built for this bullet)"),
R21/disconnect UI (onboarding/disconnect screens explicitly "still to port" per `CLAUDE.md`
Status), R18 account deletion ("not built" per `TODO.md`), R10c/R2a/R3a's core mapping rules
(covered by `TransactionMapperTests.cs`), R23/R23a's pure search logic (covered by
`search.test.ts`).

### 1. R24/R24a's entire behavior rides on one untested navigator option
- **Location:** `app/src/navigation/AppNavigator.tsx:160` (`const LIST_TAB_OPTIONS = { popToTopOnBlur: true } as const;`), applied at lines 189 and 195 to the Debits and Rules `Tab.Screen` entries.
- **Gap shape:** regression-gap
- **Trigger condition:** R24 ("arriving at a tab always lands on its list") and R24a ("leaving a tab mid-edit discards an unsaved rule draft, silently") are both delivered entirely by `popToTopOnBlur: true`, per the code's own comment at lines 153-158 — but nothing in the test suite exercises that option.
- **Consumer:** The Debits and Rules `Tab.Screen` entries in `MainTabs` at `app/src/navigation/AppNavigator.tsx:186-197` — i.e., every real tab switch a user performs.
- **Evidence:** Read `app/src/navigation/__tests__/AppNavigator.test.ts` in full (37 lines) — it only unit-tests `listTabListeners` against a hand-built `{ isFocused }` stub; it never renders `AppNavigator`/`MainTabs` and never references `LIST_TAB_OPTIONS` or `popToTopOnBlur`. Globbed `app/src/screens/**/*.test.*` (0 results) and `app/src/hooks/__tests__/*.ts` (0 results) — no screen-level or navigation-integration test exists anywhere that could exercise this option.
- **Guard/missing verification:** A render test that mounts the tab navigator, pushes DebitList → PayeeEdit with draft state, fires the Debits tab's blur (e.g., by switching to Rules), and asserts the Debits stack reset to DebitList with the draft gone.
- **Potential consequence:** Dropping `popToTopOnBlur: true` from `LIST_TAB_OPTIONS`, or a react-navigation upgrade changing its semantics, silently breaks both R24 and R24a while `npx jest` stays green — the *other* half of R24 (the `tabPress`/`listTabListeners` re-tap guard) is tested and would still pass, masking the regression.

### 2. R4a's "at most two decimal places" is not enforced by the backend rule-amount validator
- **Location:** `backend/GreedyNose.Api/Rules/RulesValidation.cs:62-65`
- **Gap shape:** regression-gap
- **Trigger condition:** R4a requires a rule's amount to be "a positive number, at most two decimal places." `RulesValidation.TryValidate` only checks `request?.AmountEUR is <= 0` and never checks decimal scale — `POST /rules` accepts e.g. `amountEUR: 12.999`.
- **Consumer:** `RulesEndpoint.HandleAsync` (`backend/GreedyNose.Api/Rules/RulesEndpoint.cs:66`, sole caller of `TryValidate`), and downstream `RuleEngine.Classify` (`backend/GreedyNose.Api/Domain/RuleEngine.cs:79-102`), which reads the stored `Rule.AmountEUR` to decide R5's over-limit branch.
- **Evidence:** Read `RulesValidation.cs` in full — the only `AmountEUR` check is `<= 0`. Grepped `RulesTests.cs` for `decimal|AmountEUR` — matches are only the non-positive theory data, `30m`, and `null` cases; none post a value with more than two decimal digits. Grepped `TODO.md`/`CLAUDE.md` for "decimal place"/"two decimals"/"R4a" — no matches, so this is not an already-recorded/deferred gap.
- **Guard/missing verification:** A test such as `Assert.False(RulesValidation.TryValidate(Req(amountEUR: 12.999m), out _, out var problems))` asserting on `problems["amountEUR"]`.
- **Potential consequence:** A fractional limit with more than two decimals is silently accepted by validation, then silently rounded by Postgres's `numeric(12,2)` column (`GreedyNoseDbContext.cs`, `HasPrecision(12, 2)`) to a different number than the caller submitted, rather than being rejected as R4a's wording implies — and no test pins either the correct rejection or the current silent-rounding behavior.

### 3. R3a's "raw strings stored alongside the resolved key" is asserted by the doc/architecture but not implemented
- **Location:** `backend/GreedyNose.Api/Data/Payee.cs:15` (single `Name` property, no history/collection type), with unconditional overwrite at `backend/GreedyNose.Api/Ingestion/IngestionRunner.cs:144-146` and `backend/GreedyNose.Api/Rules/RulesEndpoint.cs:92-94`.
- **Gap shape:** regression-gap
- **Trigger condition:** R3a states "the resolved key is stored on the payee, together with the raw strings seen for it," and `ARCHITECTURE.md`'s "Payee identity" section repeats this as settled design ("store every raw string ever seen for a payee... the only way to re-tune matching later"). The actual `Payee` entity has one `Name` field, unconditionally overwritten — not appended to — on every ingestion tick and every `POST /rules` save.
- **Consumer:** `IngestionRunner.UpsertPayeesAsync` (`backend/GreedyNose.Api/Ingestion/IngestionRunner.cs:113-178`, runs every steady-state poll tick) and `RulesEndpoint.HandleAsync` (`backend/GreedyNose.Api/Rules/RulesEndpoint.cs:74-95`).
- **Evidence:** Read `Payee.cs` (single `Name` property), `IngestionRunner.cs`'s `UpsertPayeesAsync` (plain overwrite on the `else` branch, lines 142-151), and `RulesEndpoint.cs`'s `HandleAsync` (same overwrite pattern, lines 89-95). Grepped the whole `backend/` tree for `RawName|RawStrings|SeenAs|Aliases|Spellings` — no matches. Grepped `TODO.md` for "raw string"/"raw spelling" — no matches; `ARCHITECTURE.md` and `CLAUDE.md` both currently assert the opposite ("the design stores every raw string it sees") as settled fact, so this is not a recorded/deferred gap.
- **Guard/missing verification:** No test in `IngestionRunnerTests.cs`, `RulesTests.cs`, or `DataModelTests.cs` asserts a payee's prior raw-name spelling survives a second write supplying a different spelling for the same payee id. The closest existing test (`An_existing_payee_row_from_step_4s_synthetic_now_gets_overwritten_with_the_real_earlier_date`) only asserts on `FirstSeenAt`, never on `Name`/spelling history.
- **Potential consequence:** Once a real bank sends two spellings of the same creditor over time (already observed in `TransactionMapperTests.cs`'s branch-number and aggregator-prefix cases), every spelling but the most recent is permanently discarded on the next ingestion tick or rule save — exactly the data R3a's own text and `ARCHITECTURE.md` say is needed for "a future payee-key re-tuning pass," which would then have nothing to re-tune against, with nothing in the test suite flagging the loss.

---

## Summary

No lens returned zero findings. The clearest cross-lens convergence: the R3a payee-identity model
and the onboarding/first-sync boundary are both underspecified in the requirements *and*
under-enforced in the shipped code — the doc's own "raw strings stored" claim (R3a) is not true of
`Payee.cs` today, and R4b's onboarding "reviewed" concept has no defined completion condition on
either side. R20's reconnect-summary interacts with R10c and R18 in three different places where
the document is silent on precedence or edge counts.
