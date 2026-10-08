#!/bin/bash
# Story 2.6: copies one value from the local user secrets into the Azure Key Vault, without printing it.
#
# Usage:  bash tools/azure-secret.sh <vault-name> <secret-name> secret:<UserSecretKey>
#         bash tools/azure-secret.sh <vault-name> <secret-name> file:<UserSecretKey holding a file path>
#   secret: the value of that user secret (e.g. secret:Api:Key).
#   file:   the user secret is a path; the CONTENT of that file is stored (the PEM, the Firebase JSON).
# The value goes through a temporary file (mounted read-only into the CLI container, removed afterwards),
# never through the command line, an echo or a log. Needs the Key Vault Secrets Officer role (the setup
# script gave it to the signed-in user) and `az login` (tools/az.sh).
set -e -o pipefail
cd "$(dirname "$0")/.."

VAULT="$1"; NAME="$2"; SOURCE="$3"
KIND="${SOURCE%%:*}"; KEY="${SOURCE#*:}"

SECRET_VALUE=$(dotnet user-secrets list --project backend/GreedyNose.Api | grep "^$KEY " | sed 's/^[^=]*= *//' | tr -d '\r')
if [ -z "$SECRET_VALUE" ]; then
  echo "The user secret $KEY is not set." >&2
  exit 1
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
if [ "$KIND" = "file" ]; then
  FILE=$(cygpath -u "$SECRET_VALUE")
  if [ ! -f "$FILE" ]; then
    echo "The file named by $KEY does not exist." >&2
    exit 1
  fi
  cp "$FILE" "$TMP/value"
else
  printf '%s' "$SECRET_VALUE" > "$TMP/value"
fi
unset SECRET_VALUE

AZ_MOUNT="$(cygpath -m "$TMP")" bash tools/az.sh keyvault secret set --vault-name "$VAULT" --name "$NAME" \
  --file /mnt/value --encoding utf-8 --query "{name:name, enabled:attributes.enabled}" -o tsv | tr -d '\r'
