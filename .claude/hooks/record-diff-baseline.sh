#!/usr/bin/env bash
# SubagentStart hook: remember how large the working-tree diff was before this
# agent ran, so its SubagentStop counterpart can measure what THIS agent
# actually changed rather than the whole uncommitted tree.
#
# Without this, the triviality check is useless in any session that already
# carries a large uncommitted diff — every agent would look like it changed
# hundreds of lines.
#
# Best-effort by design: one baseline file, so two coding agents running
# concurrently share it and the second one's delta is understated. The
# consequence of getting it wrong is a skipped review, never a missed bug that
# nobody can still catch — the review can always be asked for by hand.

set -uo pipefail

hooks_dir="${CLAUDE_PROJECT_DIR:-.}/.claude/hooks"
cat >/dev/null # drain stdin; the payload is not needed here

bash "$hooks_dir/diff-size.sh" >"$hooks_dir/.diff-baseline" 2>/dev/null || true
exit 0
