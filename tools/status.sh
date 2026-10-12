#!/bin/bash
# Prints the connection status of every bank (Story 4.2): GET /status with the API key. The key is read
# from app/src/data/apiKey.local.ts and never printed.
#
# Usage:  bash tools/status.sh [base-url]      default: http://127.0.0.1:5199
set -e
BASE="${1:-http://127.0.0.1:5199}"
KEYFILE="$(dirname "$0")/../app/src/data/apiKey.local.ts"
KEY=$(sed -n "s/.*'\(.*\)'.*/\1/p" "$KEYFILE")
[ -n "$KEY" ] || { echo "No API key in app/src/data/apiKey.local.ts" >&2; exit 1; }
curl -s -m 30 -w '\nHTTP %{http_code}\n' -H "Authorization: Bearer $KEY" "$BASE/status"
