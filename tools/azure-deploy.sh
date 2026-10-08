#!/bin/bash
# Story 2.6: builds the Functions host in a Linux container (Smart App Control blocks fresh binaries on the
# host), zips the publish output and deploys it to the Azure Function App (tools/azure-setup.sh created it).
# Nothing secret is involved: settings and secrets live in the app settings / Key Vault.
#
# Usage:  bash tools/azure-deploy.sh
set -e -o pipefail
cd "$(dirname "$0")/.."
export MSYS_NO_PATHCONV=1

RG=rg-greedy-nose
SUFFIX=$(bash tools/az.sh account show --query id -o tsv | tr -d '\r' | cut -c1-5)
FUNC=func-greedy-nose-$SUFFIX

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
WORK_WIN=$(cygpath -m "$WORK")
mkdir "$WORK/pub"

echo "== Publish (Release, in a container)"
docker run --rm \
  -v "C:/Workspace/greedy-nose/backend:/src:ro" \
  -v greedy-nose-nuget:/root/.nuget/packages \
  -v "$WORK_WIN/pub:/out" \
  -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish GreedyNose.Functions -c Release -o /out --artifacts-path /tmp/artifacts >/dev/null

echo "== Zip"
docker run --rm -v "$WORK_WIN:/w" --entrypoint python3 mcr.microsoft.com/azure-cli \
  -c "import shutil; shutil.make_archive('/w/app', 'zip', '/w/pub')"

echo "== Deploy to $FUNC"
AZ_MOUNT="$WORK_WIN" bash tools/az.sh functionapp deployment source config-zip -g "$RG" -n "$FUNC" \
  --src /mnt/app.zip -o none | tr -d '\r'

HOST=$(bash tools/az.sh resource show --ids "$(bash tools/az.sh functionapp show -g "$RG" -n "$FUNC" --query id -o tsv | tr -d '\r')" \
  --query properties.defaultHostName -o tsv | tr -d '\r')
echo "Deployed. https://$HOST"
