#!/bin/bash
# Starts a bank consent (Story 1.4): asks the backend for a one-time link with the API key, then
# opens that link in the default browser. The link works once and expires after 30 minutes; the
# token is not printed unless you pass --print.
#
# Usage:  bash tools/connect-bank.sh [bank name] [country] [base-url] [--print]
#   bank name  default: the backend's configured default (Mock ASPSP in the sandbox)
#   country    default: the backend's default (DE)
#   base-url   default: http://127.0.0.1:5199 (the local container, tools/run-backend-container.sh)
set -e
PRINT=0; ARGS=()
for a in "$@"; do if [ "$a" = "--print" ]; then PRINT=1; else ARGS+=("$a"); fi; done
BANK="${ARGS[0]:-}"; COUNTRY="${ARGS[1]:-}"; BASE="${ARGS[2]:-http://127.0.0.1:5199}"
KEYFILE="$(dirname "$0")/../app/src/data/apiKey.local.ts"
KEY=$(sed -n "s/.*'\(.*\)'.*/\1/p" "$KEYFILE")
[ -n "$KEY" ] || { echo "No API key in app/src/data/apiKey.local.ts" >&2; exit 1; }
BODY="{"; SEP=""
[ -n "$BANK" ] && { BODY="$BODY\"aspsp\":\"$BANK\""; SEP=","; }
[ -n "$COUNTRY" ] && BODY="$BODY$SEP\"country\":\"$COUNTRY\""
BODY="$BODY}"
RESP=$(curl -s -m 30 -X POST -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" -d "$BODY" "$BASE/connect-links")
URL=$(printf '%s' "$RESP" | sed -n 's/.*"url":"\([^"]*\)".*/\1/p' | sed 's/\u0026/\&/g')
[ -n "$URL" ] || { echo "No link returned: $RESP" >&2; exit 1; }
[ "$PRINT" = 1 ] && echo "$URL"
echo "Opening the one-time link (valid once, 30 minutes) in your browser..."
[ "${GN_NO_OPEN:-0}" = 1 ] && exit 0   # GN_NO_OPEN=1: do not open a browser (tests)
start "" "$URL" 2>/dev/null || cmd.exe /c start "" "$URL"
