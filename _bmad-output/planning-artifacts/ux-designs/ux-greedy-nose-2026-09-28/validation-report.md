# Validation Report — greedy-nose

- **Validated artifact:** `mocks/` (22 screens) — no `DESIGN.md`/`EXPERIENCE.md` spine exists yet for this project; validated directly against `REQUIREMENTS.md` per `validate.md`'s "or any format of UX the user provides" allowance.
- **Source of truth:** `REQUIREMENTS.md`
- **Run at:** 2026-09-28

## Overall verdict

The mocks are in good shape for the bulk of `REQUIREMENTS.md` — R9, R13/R13a, R14/R8a, R17/R17a, R19, R20/R20a wording, R23/R23a on the debit list, R25/R26/R26a's onboarding behavior, the R8 anti-pitfall check, and all four already-tracked "known mock gaps" are correctly and consistently represented, with no undisclosed drift in either direction. The one sharp gap is R12a's "no rule" notification wording: the string rewritten in the 2026-09-24 revision specifically to stop implying an unfamiliar payee was never propagated into the two mock files that quote it — and those two quotes don't even agree with each other. A handful of smaller citation/terminology staleness items round out that pass.

The accessibility pass (not sourced from `REQUIREMENTS.md`, run as an ad-hoc lens) found a design system that is more accessibility-literal than most mockup sets: every good/bad state is backed by a redundant text label (never color alone), and touch targets are 44px by default via shared CSS classes — so the colorblind-with-no-secondary-cue failure mode does not occur. What is missing is contrast discipline: the `--good`/`--bad`/`--accent` tokens fail WCAG AA (3.3–4.0:1 against a 4.5:1 requirement) against their own tinted backgrounds and against white in light mode, hitting the Good/Bad pills, the classification toggle, and the primary Save button specifically — plus a handful of controls authored as plain non-semantic `<div>`s with no button role. Dark mode is unaffected throughout. None of the accessibility findings risk the app's one unacceptable failure (a charge that silently never notifies) since every state is still backed by redundant text.

## Category verdicts

- Requirements coverage — **thin** (one critical finding sits directly on shipped-risk copy — a live notification body string — despite ~15 other requirement areas checked clean)
- Accessibility (ad-hoc lens) — **adequate** (solid structural foundation, no critical findings, but real contrast gaps on the screens that matter most)

## Findings by severity

### Critical (1)

**Requirements coverage** — R12a's "no rule" notification body is stale and self-contradictory across the two mocks that quote it (mocks/03-debit-detail.html:29, mocks/05-notification.html:24 — R12a)
`REQUIREMENTS.md` (revised 2026-09-24) fixes this string to "You haven't reviewed this payee yet." — specifically because the old wording falsely implied the payee itself was unfamiliar. `03-debit-detail.html`'s comment still reads "This payee is new — you haven't reviewed it yet."; `05-notification.html`'s comment reads a third, different string: "New payee — you haven't seen this one before." Neither file was updated for the revision, and the two disagree with each other and with the spec. No mock screen for the "no rule" case is ever fully drawn, so these two comments are the only place this reason's wording lives in `mocks/`.
Fix: Replace both comments with the canonical "You haven't reviewed this payee yet." Consider drawing a `05a-notification-no-rule.html` mirroring 05/05c so all three R12a reasons have an actual rendered mock.

### High (3)

**Accessibility** — Primary CTA button text (white-on-accent-blue) fails WCAG AA, ~3.65:1 vs 4.5:1 required (mocks/style.css `.btn-primary`)
This is the button for "Continue with N26," "Allow Access," "Start Monitoring," "Try Again," and — most importantly — "Save" on both Edit Rule screens, the button that commits the Good/Bad classification.
Fix: Darken `--accent` (e.g. toward #0068d6/#0060c0) or switch to dark text on a lighter accent tint.

**Accessibility** — `--good`/`--bad` pill and toggle text fails WCAG AA against their own tinted backgrounds, ~3.5–4.0:1 (mocks/style.css `.pill-good`/`.pill-bad`/`.toggle-btn` — 01c, 04b, 04d, 02, 03, 06)
This is the literal "Good"/"Bad" pill text and the toggle-button label — the classification signal itself and the alert-reason pill render below the legibility bar for low-vision users.
Fix: Darken `--good` and/or lighten `--good-bg` (and similarly nudge `--bad`/`--bad-bg`) until the pair clears 4.5:1.

**Accessibility** — Core Good/Bad toggle and "Mark all Good" are markup-level `<div>`s with no button role (mocks/01c-classify-payees.html, 04b-payee-edit.html, 04d-payee-edit-bad.html)
The real risk is a literal RN port (a bare `View` instead of `Pressable`/`TouchableOpacity`) making the single most important control in the app — deciding whether a payee is Good or Bad — unreachable by a screen-reader user.
Fix: When porting, make each toggle a `Pressable` with `accessibilityRole="button"` and `accessibilityState={{selected: isActive}}`.

### Medium (5)

**Requirements coverage** — 05b's explanatory comment uses the exact terminology R10b forbids ("transaction ID") (mocks/05b-notification-summary.html:19-20 — R10b/R20)
Fix: Reword to "every charge from the gap carries an identifier (`entry_reference`) the app has never seen."

**Requirements coverage** — 01bb-syncing.html cites R10b instead of R25 for why first-sync debits don't notify (mocks/01bb-syncing.html:20 — R25)
Fix: Change the citation to (R25).

**Requirements coverage** — 05b reuses the defined term "first sync" for an ordinary reconnect resync (mocks/05b-notification-summary.html:34 — R20/R20b/R25)
Fix: Reword to "Sent once, right after the app resyncs following a reconnect," avoiding the term "first sync" for this event.

**Requirements coverage** — No mock demonstrates the Rules-list search states R23b requires parity for (mocks/04-payee-rules.html — R23/R23b)
Fix: Add a 04e-style pair (or at least a filtered-results variant) mirroring 02c/02d for the Rules list.

**Accessibility** — Payee-status avatar color is the only cue for a payee's own Good/Bad status on Debit List/Detail (mocks/02d-debit-search-results.html, 03b-debit-detail-over-limit.html, 02-debit-list.html)
Fix: Add a small persistent text/shape cue tied to the avatar itself.

**Accessibility** — Nav-bar text links have no minimum touch-target sizing, unlike every other interactive class (mocks/style.css `.nav-link`)
Fix: Give `.nav-link` the same treatment `.search-clear` got — a visible box plus an invisible hit-slop overlay to 44px.

### Low (9)

**Requirements coverage** — Onboarding copy undersells R25's full-history scope by calling it "recent history" (mocks/01bb-syncing.html:15, mocks/01c-classify-payees.html:14 — R25)
Fix: Reword to "your account history" / "your available transaction history."

**Accessibility** — Settings toggle rows are far under the 44px floor (mocks/06-settings.html:32-43) — Fix: apply `min-height:44px`.

**Accessibility** — `text-muted` on `--neutral-bg` fails AA, ~4.30:1 vs 4.5:1 (search placeholders, Settings "Off" pill) — Fix: darken `--text-muted` slightly.

**Accessibility** — `--bad` section-label text on plain `--bg` is borderline-under AA, ~4.26:1 (mocks/01b-connect-consent.html:27) — Fix: move onto a card (white surface).

**Accessibility** — Nav-link and modal "Cancel"-style accent-blue text fails AA, ~3.27-3.65:1 (same `--accent` token as the primary-button finding) — Fix: same token fix.

**Accessibility** — Amount-limit field has no visible input/label association in the mock (mocks/04b-payee-edit.html:38-43) — not confirmable, numeric-entry state isn't in the mock set.

**Accessibility** — Settings "On"/"Off" pills are not marked up as a semantic switch (mocks/06-settings.html:32-43) — Fix: use a native Switch component when built.

**Accessibility** — Modal overlay has no `role="dialog"`/`aria-modal` equivalent in the markup (06b, 06c) — likely a non-issue given RN's native Modal; verify on-device.

**Accessibility** — Small font sizes (11-13px) used for information that matters on a money app (`.tx-date`, `.pill`, `.notif-time`) — worth verifying OS-level text scaling on-device.

**Accessibility** — Syncing screen's status message isn't marked as a heading, no apparent live-region equivalent (mocks/01bb-syncing.html) — worth verifying real state-announcement behavior on-device.

## Requirements checked with no issue found

R9, R13/R13a, R14/R8a, R17/R17a, R19, R20/R20a, R23/R23a, R23b, R24/R24a (correctly absent from `mocks/` per `REQUIREMENTS.md`'s own Follow-ups — R24 was scoped straight to code), R26/R26a, the R8 anti-pitfall check (no "Auto-marked Bad" marker, no auto-flip, no neutral third state), and all three still-open R22 Follow-up items re-verified unchanged (Settings/06b's unconditional "Active" pill; no consent-expiry push mock; N26 hardcoded in the same eight files, confirmed by direct grep — `01`, `01b`, `01bb`, `01d`, `01e`, `02b`, `06`, `06b`).

## Reviewer files

- `review-requirements-coverage.md`
- `review-accessibility.md`
- `validation-report.html` (rendered twin of this file)
