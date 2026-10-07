#!/bin/bash
# Runs the Functions host (backend/GreedyNose.Functions: the API, the first-sync queue function and
# the ingestion timer — Stories 2.3 and 2.4) in a Linux container. The API answers on
# http://127.0.0.1:5199 (container port 80), so `adb reverse tcp:5199 tcp:5199` still works.
#
# Why a container: Smart App Control blocks every freshly built, unsigned DLL/exe on this machine
# (see AGENTS.md), so the host cannot run the worker. Two steps: publish the project in the SDK
# image into a docker volume, then start the official Functions image on that volume.
# Needs Docker, the dev Postgres and Azurite up: docker compose -f backend/docker-compose.yml up -d
#
# Usage:  bash tools/run-functions-container.sh <container-name> [extra docker args]
# Local test with a short schedule (needs the opt-out, else the 6 h floor refuses to start):
#   bash tools/run-functions-container.sh gn-func -e "IngestionSchedule=*/30 * * * * *" -e Ingestion__AllowShortInterval=true
# Stop:   docker rm -f <container-name>
# Default schedule (no extra args) is every 6 hours, as in Azure. Nothing secret is printed; the
# Azurite account key below is Azurite's public well-known dev key.
set -e
NAME="$1"; shift
cd /c/Workspace/greedy-nose/backend
SID=$(grep -o "<UserSecretsId>[^<]*" GreedyNose.Functions/GreedyNose.Functions.csproj | sed 's/<UserSecretsId>//')
SECRETS_FILE="$(cygpath -m "$APPDATA")/Microsoft/UserSecrets/$SID/secrets.json"
get() { dotnet user-secrets list --project GreedyNose.Api | grep "^$1 " | sed 's/^[^=]*= *//'; }
FIREBASE_PATH=$(get "Firebase:ServiceAccountPath")
PEM_PATH=$(get "EnableBanking:PrivateKeyPath")
PG=$(get "ConnectionStrings:Postgres" | sed -E 's/Host=[^;]+/Host=greedy-nose-postgres/; s/Port=[0-9]+/Port=5432/')
AZURITE="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://greedy-nose-azurite:10000/devstoreaccount1;QueueEndpoint=http://greedy-nose-azurite:10001/devstoreaccount1;TableEndpoint=http://greedy-nose-azurite:10002/devstoreaccount1;"
export MSYS_NO_PATHCONV=1

# Step 1: publish into the named volume (no bin/obj lands in the repo).
docker run --rm --network backend_default \
  -v "C:/Workspace/greedy-nose/backend:/src" \
  -v greedy-nose-nuget:/root/.nuget/packages \
  -v greedy-nose-func-out:/out \
  -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish GreedyNose.Functions -c Release -o /out --artifacts-path /tmp/artifacts

# Step 2: run the Functions host on the published output.
docker run --rm --name "$NAME" --network backend_default -p 127.0.0.1:5199:80 \
  -v greedy-nose-func-out:/home/site/wwwroot \
  -v "$SECRETS_FILE:/home/.microsoft/usersecrets/$SID/secrets.json:ro" \
  -v "$FIREBASE_PATH:/secrets/firebase.json:ro" \
  -v "$PEM_PATH:/secrets/eb.pem:ro" \
  -e AzureWebJobsScriptRoot=/home/site/wwwroot -e FUNCTIONS_WORKER_RUNTIME=dotnet-isolated \
  -e AZURE_FUNCTIONS_ENVIRONMENT=Development -e DOTNET_ENVIRONMENT=Development \
  -e "AzureWebJobsStorage=$AZURITE" \
  -e "IngestionSchedule=0 0 */6 * * *" \
  -e Firebase__ServiceAccountPath=/secrets/firebase.json \
  -e EnableBanking__PrivateKeyPath=/secrets/eb.pem \
  -e "ConnectionStrings__Postgres=$PG" \
  "$@" \
  mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0
