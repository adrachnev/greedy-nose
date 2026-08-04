# Greedy Nose

A mobile app (iOS + Android) that connects to a European bank account and notifies the user
the moment a debitor they've flagged as "Bad" charges them. That's the whole product — it is
deliberately not a general finance/budgeting app.

## Status

Design phase. No app code yet — only HTML/CSS phone mockups in `mocks/`. Framework (React
Native vs Flutter) is not decided yet; that choice comes after the mocks settle.

## Product decisions (settled)

- **Bank connectivity**: [Enable Banking](https://enablebanking.com) — a PSD2/XS2A-licensed
  aggregator with a free tier. Chosen specifically to start with zero cost/investment. First
  (and currently only) bank target: **N26**.
- **Trust model is opt-out, not opt-in**: every debitor defaults to **Bad** the first time
  they're seen. The user marks debitors **Trusted** to silence them — not the other way round.
  This was a deliberate choice over a neutral "unmarked" state, since the goal is to never miss
  a genuinely new/unknown charge.
- **Onboarding**: after granting bank consent, a one-time bulk-review screen lets the user
  classify their existing debitors before live monitoring starts — otherwise every historical
  transaction would fire a notification on first connect.
- **Alert thresholds**: per-debitor, optional "only alert if amount exceeds €X" and/or "only
  alert if charged more than N times per period." If both are set, they combine with **AND**
  logic. Explicitly kept in scope after a lean-scope review suggested cutting them — the owner
  wants this.
- **Notifications do not carry quick actions.** Earlier iteration explored "Trust"/"Keep Bad"
  buttons directly on the push notification; this was deliberately removed. Tapping a
  notification opens the transaction detail, where classification happens in-app.
- **No "untracked" third state.** Only Trusted/Bad exist. An earlier "reset to default /
  untrack" concept was cut as scope creep — it behaved identically to Bad anyway.
- **Design language is lean and icon-driven**: no emoji, minimal decorative icons. State
  (Trusted/Bad, active tab, etc.) is communicated through **color** (green/red) and simple
  custom-drawn line-style SVG icons (`stroke="currentColor"`, `stroke-width="1.75"`,
  round caps/joins — matches the tab-bar icons in `mocks/style.css`/the HTML files), not
  through library icon sets or emoji glyphs.

## Open / deferred (explicitly not v1)

- App-level lock (Face ID/passcode) before opening the app.
- Multi-bank / multi-account differentiation in the transaction list.
- Offline state handling for the main list (only the initial-connect error state exists).
- Notification grouping/bundling (a "Group multiple alerts" toggle exists in the Settings mock
  but defaults Off — one notification per charge is the current decision).

## `mocks/`

Static HTML/CSS phone mockups, one file per screen, ~375×812 viewport, Flexbox layout
throughout (chosen so it maps fairly directly to React Native if that's the eventual stack, and
still serves as a precise blueprint for Flutter or elsewhere).

- `index.html` — overview embedding every screen with a labelled flow order; open this first.
- `style.css` — shared design tokens/components (`.card`, `.pill`, `.btn`, `.tab-item`,
  `.modal-overlay`, `.toast`, `.spinner`, dark-mode variants via `prefers-color-scheme`).
- Screens are numbered by flow position (`01` connect bank → `01b` consent → `01bb` syncing →
  `01c` onboarding classify → `02` transaction list → `03` transaction detail → `04` rules →
  `04b` edit rule → `05` notification → `06` settings), with lettered variants for
  error/empty/confirm states (e.g. `01e` connect error, `02b` empty list, `06b`/`06c` confirm
  modals).

Iterate on mocks before touching architecture/code — that was an explicit ordering decision:
mocks first, then pick the stack, then implement and test.
