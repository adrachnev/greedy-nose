#!/usr/bin/env bash
# Prints the number of changed lines in the working tree (tracked + untracked)
# as a single integer. Shared by the SubagentStart/SubagentStop hook pair so
# both measure the same thing the same way.

set -uo pipefail

root="${CLAUDE_PROJECT_DIR:-.}"

tracked="$(git -C "$root" diff HEAD --numstat 2>/dev/null |
  awk '{ a += ($1 == "-" ? 0 : $1); d += ($2 == "-" ? 0 : $2) } END { print a + d + 0 }')"

# Untracked files count in full: a brand-new file is entirely new code, and
# `git diff HEAD` does not see it at all.
untracked="$(git -C "$root" ls-files --others --exclude-standard 2>/dev/null |
  while IFS= read -r f; do
    [ -f "$root/$f" ] && wc -l <"$root/$f" 2>/dev/null || echo 0
  done |
  awk '{ s += $1 } END { print s + 0 }')"

echo $((${tracked:-0} + ${untracked:-0}))
