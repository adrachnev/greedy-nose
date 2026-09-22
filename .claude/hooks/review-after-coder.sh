#!/usr/bin/env bash
# SubagentStop hook: ask the main session to run a coder-reviewer pass right
# after a coding agent finishes — unless what it changed is trivial.
#
# Why a command hook and not an `agent` hook: the "agent" hook type only
# exists for tool events (PreToolUse/PostToolUse/PermissionRequest), so a
# SubagentStop hook cannot launch an agent itself. It can only hand the main
# session an instruction, which is what additionalContext below does.
#
# LOOP GUARD 1: coder-reviewer is itself a subagent, so its own SubagentStop
# fires this hook too. Without the coder-reviewer exclusion below, every
# review would trigger another review, forever.
#
# LOOP GUARD 2 (added 2026-09-22, after a real incident): additionalContext on
# SubagentStop lands in the agent that just stopped, not the main session (see
# TODO.md's "Process" item) — so a coder-mobile/coder-backend agent that gets
# told to "launch coder-reviewer" cannot do it (no agent-spawning tool). If it
# tries, fails, and then stops again, THAT stop still matches
# coder-mobile|coder-backend below and re-fires this same hook — which is
# exactly what got a coder-backend agent stuck retrying for over an hour, 100+
# tool calls, on 2026-09-22. The harness's own fix for this class of problem is
# the `stop_hook_active` field on the hook payload: true means this stop is
# already a continuation caused by a previous stop-hook's additionalContext,
# so exit quietly instead of injecting (and re-triggering) again.

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

# --- Already looping? -------------------------------------------------------
# See LOOP GUARD 2 above. Checked before anything else: if this stop is a
# continuation of a stop-hook's own additionalContext, do not inject again.
if printf '%s' "$payload" | jq -e '.stop_hook_active == true' >/dev/null 2>&1; then
  exit 0
fi

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
  systemMessage: "Coding agent finished — a code review is needed before this is done.",
  hookSpecificOutput: {
    hookEventName: "SubagentStop",
    additionalContext: (
      "This message reaches YOU, the coder-mobile/coder-backend agent that just finished — not "
      + "the main session or the user (a current limitation of SubagentStop hooks; see the "
      + "Process item in TODO.md). You have no agent-spawning tool, so do NOT attempt to launch "
      + "coder-reviewer or any other agent, and do not retry or keep working to satisfy this "
      + "message. Simply mention in your normal final report that a coder-reviewer pass is still "
      + "needed (the main session starts it by hand after every coder), then finish exactly as "
      + "you otherwise would."
    )
  }
}'
