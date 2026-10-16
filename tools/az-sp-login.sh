#!/bin/bash
# One-time (gn-7n2): log the Azure CLI in as the service principal `sp-greedy-nose-tools`, so no device-code
# login with MFA is needed any more (Entra security defaults stay on; they do not apply to service principals).
# The principal itself is created by hand in the Azure portal (app registration + secret + role Contributor on
# the resource group rg-greedy-nose only).
#
# Usage:  bash tools/az-sp-login.sh <application-client-id> <tenant-id> <file-with-the-secret>
# The secret is read from the file, the file is deleted after a successful login, nothing is printed. The login
# lives in the Docker volume `greedy-nose-azure` (like before). Keep the secret file OUTSIDE the repo.
set -e
APPID="$1"; TENANT="$2"; FILE="$3"
if [ -z "$APPID" ] || [ -z "$TENANT" ] || [ ! -s "$FILE" ]; then
  echo "Usage: bash tools/az-sp-login.sh <client-id> <tenant-id> <secret-file>" >&2
  exit 1
fi
SECRET=$(tr -d '\r\n ' < "$FILE")
bash tools/az.sh login --service-principal -u "$APPID" -p "$SECRET" --tenant "$TENANT" --output none
unset SECRET
rm -f "$FILE"
echo "Logged in as the service principal; secret file deleted."
bash tools/az.sh group show -n rg-greedy-nose --query name -o tsv
