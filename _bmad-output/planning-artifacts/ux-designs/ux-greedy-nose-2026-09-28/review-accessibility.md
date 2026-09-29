# Mock UI — Accessibility Review — greedy-nose

Reviewed: `mocks/style.css` (design tokens) plus all 22 screen mocks listed in the task. All
contrast ratios below are computed from the literal hex/rgba values in `mocks/style.css` using
the WCAG relative-luminance formula (sRGB → linearize → `0.2126R+0.7152G+0.0722B` →
`(L1+0.05)/(L2+0.05)`), not measured on a device. Where a background is a translucent `rgba()`
(dark mode's `--good-bg`/`--bad-bg`), the ratio is stated as "very high / not a concern" rather
than computed exactly, since the composited value depends on what sits behind it.

## Overall verdict

The design system is more accessibility-literal than most mockup sets — `.btn`, `.toggle-btn`,
`.tab-item`, `.modal-actions a`, and `.search-field` all bake in an explicit 44px `min-height`,
tab icons carry `aria-label`, the search-clear "✕" has a documented 44px hit-slop overlay around
an 18px glyph, and every good/bad state is backed by a text label ("Good"/"Bad"/"Active"/"On"),
never color alone — so the colorblind-with-no-secondary-cue failure mode the task asked about
does not occur anywhere. What's missing is contrast discipline for the `--good`/`--bad`/`--accent`
tokens against their own tinted backgrounds and against white (several sit at 3.3–4.0:1 against a
4.5:1 requirement), a handful of interactive controls authored as plain `<div>`s with no
button/role semantics (the Good/Bad toggle itself, chief among them), and a small, consistent set
of controls (nav-bar back/cancel links, two Settings rows) that fall outside the codebase's
otherwise-consistent 44px touch-target floor. None of these cause the one failure this product
cannot have — a charge that silently never notifies, since every state is still backed by
redundant text — but several sit on the screens that most matter (the classification toggle, the
Save button that commits it, the alert-reason pill) and are worth fixing before a real build ships.
Dark mode is comfortably fine throughout (recomputed separately below) — the light-mode palette is
where the numbers are tight.

## Findings

- **high** Primary CTA button text (white-on-accent-blue) fails WCAG AA, ~3.65:1 against a 4.5:1
  requirement (file: `mocks/style.css` `.btn-primary`, used by every screen with a primary action).
  `color:#fff` on `background:var(--accent)` (`#0a84ff`) computes to L(white)=1.0, L(accent)=0.238,
  ratio `(1.0+.05)/(0.238+.05) ≈ 3.65:1`. 16px semibold text does not qualify as WCAG "large text"
  (needs ≥18.66px bold), so 4.5:1 applies, not 3:1. This is the button used for "Continue with
  N26" (`01-connect-bank.html`), "Allow Access" (`01b-connect-consent.html`), "Start Monitoring"
  (`01c-classify-payees.html`), "Try Again" (`01e-connect-error.html`), and — most importantly —
  **"Save" on both Edit Rule screens** (`04b-payee-edit.html`, `04d-payee-edit-bad.html`), the
  button that commits the Good/Bad classification. *Fix:* darken `--accent` (e.g. toward
  `#0068d6`/`#0060c0`, ~4.5:1+ against white) or switch these buttons to dark text on a lighter
  accent tint; verify with a contrast checker, not eyeballing, since iOS system blue is a known
  offender here.

- **high** `--good`/`--bad` text fails WCAG AA against their own tinted pill/toggle backgrounds,
  ~3.5–4.0:1 (files: every screen using `.pill-good`/`.pill-bad`/`.toggle-btn.good-active`/
  `.toggle-btn.bad-active`, e.g. `mocks/01c-classify-payees.html`, `04b-payee-edit.html`,
  `04d-payee-edit-bad.html`, `02-debit-list.html`, `03-debit-detail.html`, `06-settings.html`).
  Computed: `--good` (`#1f9254`, L=0.2149) on `--good-bg` (`#e3f6ea`, L=0.8820) = **3.52:1**;
  `--bad` (`#d5372e`, L=0.1708) on `--bad-bg` (`#fbe6e4`, L=0.8271) = **3.97:1**; `--good` directly
  on white `--surface` (no tint) = **3.96:1**. (`--bad` directly on white surface is fine at
  4.76:1 — only the good/green pairing and both tinted-background pairings fail.) This is the
  literal "Good"/"Bad" pill text and the Good/Bad toggle-button label — the classification signal
  itself and the per-debit alert-reason pill (`pill pill-bad` next to "You marked this payee as
  bad." / "Over your limit of €30.00." in `03-debit-detail.html`/`03b-debit-detail-over-limit.html`)
  render below the legibility bar for low-vision users. Dark mode does not have this problem
  (`--good`/`--bad` there are 8–10:1 against black/surface); this is a light-mode-only gap. *Fix:*
  darken `--good` and/or lighten `--good-bg` (and similarly nudge `--bad`/`--bad-bg`) until the
  pair clears 4.5:1; re-check both the tinted-pill case and the plain-white-surface case.

- **high** Core Good/Bad toggle and bulk "Mark all Good" action are markup-level `<div>`s with no
  button role, `tabindex`, or pressed-state attribute (confirmed from markup) (files:
  `mocks/01c-classify-payees.html` lines 27, 40–41 etc.; `04b-payee-edit.html` lines 31–32;
  `04d-payee-edit-bad.html` lines 38–39). `.toggle-btn`/`.toggle-btn.good-active`/
  `.toggle-btn.bad-active` and the `"Mark all Good"` row are plain `<div>`s with no `href`, no
  `role="button"`, no `aria-pressed`. As static HTML these are outside the tab/accessibility-tree
  focus order entirely; the real risk is that a literal RN port (a bare `View` instead of
  `Pressable`/`TouchableOpacity` with `accessibilityRole="button"`/`accessibilityState`) would make
  the single most important control in the app — deciding whether a payee is Good or Bad —
  unreachable by a screen-reader user. This is confirmed from the markup; whether it actually
  ships broken depends entirely on how `coder-mobile` implements the RN component, which the mock
  can't show. *Fix:* when porting, make each toggle a `Pressable` with
  `accessibilityRole="button"` and `accessibilityState={{selected: isActive}}` (or a segmented
  `radiogroup` pattern), not a bare `View`.

- **medium** Payee-status avatar color is the only cue for the payee's own Good/Bad status on the
  Debit List and Debit Detail screens, and it can visually disagree with the adjacent debit pill
  (files: `mocks/02d-debit-search-results.html`, `03b-debit-detail-over-limit.html`,
  `02-debit-list.html`). On the Rules screen (`04-payee-rules.html`) every payee's status has both
  a colored avatar AND an explicit "Good"/"Bad" pill next to it — fully redundant, no issue there.
  But on the Debit List/Detail, the avatar communicates the *payee's* status by color alone (no
  adjacent "Good"/"Bad" text about the payee — the pill next to it is the *debit's* status, which
  is deliberately allowed to disagree, e.g. `03b-debit-detail-over-limit.html`: green avatar
  ("Bäckerei Müller" is a Good payee) paired with a red "Bad" pill for this specific over-limit
  charge). A deuteranope/protanope cannot reliably distinguish the muted green (`#1f9254`) from the
  muted red (`#d5372e`) avatar tint at a glance — especially given both sit on similarly light
  pastel fills — and has no text fallback for that specific fact on this screen. This never causes
  a missed alert (the debit's own Bad-ness is always separately pilled and colored), but it does
  undercut the exact "a trusted payee can still alert you" distinction these mocks call out as the
  point of the screen. *Fix:* add a small persistent text/shape cue tied to the avatar itself (e.g.
  a tiny "trusted" label under the payee name, or a distinct avatar border style for Good vs
  Bad) rather than relying on fill color alone.

- **medium** Nav-bar text links have no minimum touch-target sizing, unlike every other
  interactive class in the system (files: `mocks/style.css` `.nav-link`; instances in
  `03-debit-detail.html`, `03b-debit-detail-over-limit.html`, `04b-payee-edit.html`,
  `04d-payee-edit-bad.html` "‹ Back", `06-settings.html` "Renew access early"/"Disconnect N26").
  `.nav-link` sets only `font-size`/`color`/`text-decoration` — no `padding` or `min-height`. Its
  parent `.nav-bar` uses `align-items:center` (not the flexbox default `stretch`), so the link's
  clickable box is genuinely just its text line-height (~18–20px for 14–15px text), well under the
  44px floor `.btn`/`.toggle-btn`/`.tab-item`/`.modal-actions a`/`.search-field` all explicitly
  carry, and under the documented 44px hit-slop the search-clear "✕" was deliberately given.
  Confirmed from the CSS cascade, not just eyeballing. *Fix:* give `.nav-link` the same treatment
  `.search-clear` got — a small visible box plus an invisible `::after`/hitSlop overlay to 44px —
  particularly for "‹ Back" (present on every detail/edit screen) and "Disconnect N26" (a
  semi-destructive action, mitigated somewhat by the confirm dialog it leads to).

- **low** Settings toggle rows are far under the 44px floor the rest of the system uses (file:
  `mocks/06-settings.html` lines 32–43). The "Push notifications" and "Group multiple alerts" rows
  use inline `padding:2px 0` with no other height source — total row height is roughly 24–28px for
  a single line of 15px text, versus 44px everywhere else interactive. Lower severity per the
  brief's own guidance (a settings-screen issue, and this screen is an explicit stub per
  `CLAUDE.md`'s Status), but worth fixing before these become real tap targets. *Fix:* apply the
  same `min-height:44px` pattern used by `.tx-row`/`.toggle-btn` to these rows.

- **low** `text-muted` on `--neutral-bg` fails AA, ~4.30:1 against 4.5:1 (files:
  `mocks/style.css` `.search-field`/`.pill-neutral`; instances in every screen with the pinned
  search field, e.g. `02-debit-list.html`, `04-payee-rules.html`, and the "Off" pill in
  `06-settings.html`). L(text-muted)=0.1571, L(neutral-bg)=0.8410, ratio ≈ 4.30:1. Affects the
  "Search debits…"/"Search payees…" placeholder text and the neutral "Off" status pill — both
  low-stakes (placeholder text disappears once typing starts; "Off" is the *inactive* state of a
  non-critical toggle). *Fix:* darken `--text-muted` slightly or lighten `--neutral-bg`; a small
  nudge closes this gap given it's only 0.2 short.

- **low** `--bad` section-label text on plain `--bg` is borderline-under AA, ~4.26:1 (file:
  `mocks/01b-connect-consent.html` line 27, `"Not allowed"` label). Computed: L(bad)=0.1708,
  L(bg)=0.8911, ratio ≈ 4.26:1 (below 4.5, though the "Move or withdraw money" text in the card
  below it sits on white `--surface` and passes at 4.76:1 — only this one heading label, seen
  once, is affected). *Fix:* either move this label onto a card (white surface) like the text
  below it, or darken `--bad` slightly for this context.

- **low** Nav-link and modal "Cancel"-style accent-blue text fails AA for normal text, ~3.27–3.65:1
  (files: `mocks/style.css` `.nav-link`/`.modal-actions a`; instances across nearly every screen —
  "‹ Back", "Choose a different bank" in `01e-connect-error.html`, "Renew access early" in
  `06-settings.html`, "Cancel" in `06b-disconnect-confirm.html`/`06c-delete-confirm.html`). Same
  `--accent` (`#0a84ff`) token as the primary-button finding above, computed against `--bg`
  (3.27:1) and `--surface`/white (3.65:1) — both below the 4.5:1 normal-text requirement. Lower
  severity than the primary-button instance because these are secondary navigation actions, not
  the classification/alert surface itself, and the words themselves ("Back"/"Cancel"/"Disconnect")
  still carry full meaning regardless of the color's legibility. *Fix:* same token fix as the
  primary-button finding covers this too, since it's the same `--accent` color.

- **low** Amount-limit field has no visible input/label association in the mock, and no screen in
  the set shows the actual number-entry UI (file: `mocks/04b-payee-edit.html` lines 38–43,
  `"Only alert if amount exceeds"` → `"€30.00"`). This is rendered as a static display row (plain
  `<div>`s, no `<label for>`/`aria-labelledby`, no `<input>`), presumably because tapping it opens
  a numeric-entry state not included in the 22 mocks. Not a confirmed defect — just a gap the mock
  set doesn't cover — but worth flagging so the eventual numeric `TextInput` gets an explicit
  `accessibilityLabel`/associated label when built, rather than being inferred from a neighboring
  static text node the way the mock implies. *Fix:* when this screen is built, give the amount
  input an explicit label association; not fixable in the mock itself since the control isn't
  shown.

- **low** Settings "On"/"Off" state pills are not marked up as a semantic switch (file:
  `mocks/06-settings.html` lines 32–43). `pill-good`/`pill-neutral` badges reading "On"/"Off" imply
  a toggle but carry no `role="switch"`/`aria-checked` equivalent, and (per this Settings screen
  being an explicit stub in `CLAUDE.md`'s Status) it's unclear from the mock whether these rows are
  even meant to be tappable yet. Confirmed absent from markup; real severity depends on the
  not-yet-designed interaction. *Fix:* when Settings toggles become functional, use a native
  Switch component (which carries correct `role="switch"` semantics by default), not a tappable
  pill-styled row.

- **low** Modal overlay has no `role="dialog"`/`aria-modal` equivalent in the markup (files:
  `mocks/06b-disconnect-confirm.html`, `06c-delete-confirm.html`). `.modal-overlay`/`.modal-box`
  are plain `<div>`s. Likely a non-issue in practice — React Native's native `Modal` component
  handles focus containment and the equivalent of `aria-modal` correctly by default when used —
  but confirmed absent from the static markup and worth a quick check on-device once built rather
  than assumed.

- **low** Small font sizes (11–13px) are used throughout for information that matters on a
  money app: `.tx-date` (12px, charge date/time), `.pill` (11px, the "Good"/"Bad" label itself),
  `.notif-time` (11px). None of these are illegible outright, but combined with the borderline
  `text-muted` contrast numbers above (4.30–4.55:1), they leave little margin for low-vision users.
  This is a "worth verifying on a real device" note, not a confirmed defect — static HTML/CSS can't
  show whether OS-level text scaling (Dynamic Type / Android font scale) is respected, since these
  mocks use fixed px throughout. *Fix:* when built, confirm containers don't clip text at larger
  accessibility font-scale settings, and consider a 12–13px floor rather than 11px for anything
  conveying real information (not pure chrome).

- **low** Syncing screen's status message isn't marked as a heading and has no apparent live-region
  equivalent for the loading→done transition (file: `mocks/01bb-syncing.html`). `"Fetching your
  debits"` is a plain `.tx-name` div, not a heading; the state change to the next screen has no
  visible `aria-live` equivalent in the mock. Minor — this is a transient, auto-advancing screen —
  and, like the modal note above, is a "worth verifying" item since the actual state-announcement
  behavior can't be shown in a static mock.

## What's already good

- Every good/bad status is backed by an explicit text label ("Good"/"Bad"/"Active"/"On") wherever
  it appears — color is never the sole carrier of state anywhere in the set, so the
  "colorblind-with-no-secondary-cue" failure mode this review specifically checked for does not
  occur. (The one nuance — the payee-status avatar on the Debit List/Detail — is flagged above as
  medium precisely because it's the one place that redundancy is thinner, not absent.)
- `.btn`, `.toggle-btn`, `.tab-item`, `.modal-actions a`, and `.search-field` all bake in an
  explicit `min-height:44px` at the design-token level, so most controls meet the touch-target
  floor by construction rather than by per-screen diligence.
- The search-clear "✕" is a deliberately small 18px glyph paired with a documented, explicit 44px
  invisible hit-slop overlay (`.search-clear::after`) — a genuinely thoughtful a11y accommodation,
  called out in the CSS's own comments, that keeps the visual field height unchanged while still
  hitting the touch-target floor.
- Every tab-bar icon anchor carries `aria-label` ("Debits"/"Rules"/"Settings") even though the
  icons are purely decorative line-SVGs with no adjacent visible text — a real, correctly-applied
  accessible-name pattern.
- No emoji and no icon-font glyphs are used anywhere in the app's own UI (only line-style SVGs with
  `stroke="currentColor"`/`stroke-width="1.75"`/round joins, per the project's own convention); the
  one emoji present (`🔋` in the status bar) is OS status-bar chrome being mimicked for the mock,
  not app UI, and would be the real device's own status bar in the shipped app.
- `--bad` text passes AA comfortably against plain white/`--surface` (4.76:1) even though it fails
  against its own tinted `--bad-bg` — so the red amount figures shown directly on white debit rows
  (e.g. `€9.99` in `02-debit-list.html`) are correctly legible; only the tinted-pill/toggle
  instances and the green pairing are the actual gaps.
- Dark mode's `--good`/`--bad`/`--text-muted` tokens are all comfortably AA-compliant (roughly
  5–10:1 against both black and the dark surface) — the contrast gaps found in this review are a
  light-mode-only problem, not a systemic one.
