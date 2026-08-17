#!/usr/bin/env bash
# SubagentStop hook: ask the main session to run a coder-reviewer pass right
# after a coding agent finishes — unless what it changed is trivial.
#
# Why a command hook and not an `agent` hook: the "agent" hook type only
# exists for tool events (PreToolUse/PostToolUse/PermissionRequest), so a
# SubagentStop hook cannot launch an agent itself. It can only hand the main
# session an instruction, which is what additionalContext below does.
#
# LOOP GUARD: coder-reviewer is itself a subagent, so its own SubagentStop
# fires this hook too. Without the coder-reviewer exclusion below, every
# review would trigger another review, forever.

set -uo pipefail

root="${CLAUDE_PROJECT_DIR:-.}"
hooks_dir="$root/.claude/hooks"

# Changed lines at or below this count count as trivial and skip the review.
# Deliberately low: a handful of lines is where a review rarely pays for
# itself, but a wrong *word* in four strings is a real bug this project has
# already shipped once, so the bar stays near the floor rather than at
# "small change".
threshold="${GREEDY_NOSE_REVIEW_MIN_LINES:-10}"

payload="$(cat)"
# Kept so the undocumented payload shape can be inspected after a real fire and
# the agent match above tightened from text-matching to a precise field.
mkdir -p "$hooks_dir" 2>/dev/null || true
printf '%s\n' "$payload" >"$hooks_dir/last-subagent-stop.json" 2>/dev/null || true

# --- Which agent finished? -------------------------------------------------
# The payload field naming for SubagentStop is not documented, so match on the
# whole payload rather than guessing a path: any coding-agent mention triggers,
# any reviewer mention suppresses. Failing closed (no review) is the safe
# direction here — a missed auto-review is recoverable, an infinite loop is not.
if printf '%s' "$payload" | grep -qi 'coder-reviewer'; then
  exit 0
fi

if ! printf '%s' "$payload" | grep -qiE 'coder-mobile|coder-backend'; then
  exit 0
fi

# --- Is the change trivial? ------------------------------------------------
# Unknowns fail OPEN here (review anyway): the cost of an unnecessary review is
# tokens, the cost of a skipped one is a bug reaching the user.
skip_reason=""

changed_paths="$(
  {
    git -C "$root" diff HEAD --name-only 2>/dev/null
    git -C "$root" ls-files --others --exclude-standard 2>/dev/null
  } | sort -u
)"

if [ -n "$changed_paths" ] && ! printf '%s\n' "$changed_paths" | grep -qvE '\.(md|txt)$'; then
  # Documentation only. There is no code here for a code review to look at.
  skip_reason="only docs changed"
elif [ -r "$hooks_dir/.diff-baseline" ]; then
  before="$(cat "$hooks_dir/.diff-baseline" 2>/dev/null)"
  after="$(bash "$hooks_dir/diff-size.sh" 2>/dev/null)"
  if [ -n "${before//[^0-9]/}" ] && [ -n "${after//[^0-9]/}" ]; then
    delta=$((after - before))
    [ "$delta" -lt 0 ] && delta=$((-delta))
    if [ "$delta" -le "$threshold" ]; then
      skip_reason="$delta changed line(s), at or below the $threshold-line threshold"
    fi
  fi
fi

rm -f "$hooks_dir/.diff-baseline" 2>/dev/null || true

if [ -n "$skip_reason" ]; then
  jq -n --arg reason "$skip_reason" '{
    systemMessage: ("Skipping the automatic code review — " + $reason + "."),
    suppressOutput: true
  }'
  exit 0
fi

jq -n '{
  systemMessage: "Coding agent finished — requesting an automatic code review.",
  hookSpecificOutput: {
    hookEventName: "SubagentStop",
    additionalContext: (
      "A coder-mobile or coder-backend agent just finished. Before reporting its result to the user, "
      + "launch the coder-reviewer agent on the changes it made (git diff HEAD plus untracked files), "
      + "then present the findings to the user as a MARKDOWN TABLE with these columns: "
      + "Prio | Severity | Where (file:line) | What is wrong (short, plain words) | Why it matters. "
      + "Order rows by importance, most important first. "
      + "Any MUST FIX / critical finding ranks above everything else and is highlighted in bold with a leading warning sign. "
      + "Keep each cell to one short sentence in simple language — no code blocks inside the table. "
      + "If there are no findings, say so in one line instead of printing an empty table."
    )
  }
}'
