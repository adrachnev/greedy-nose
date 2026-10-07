#!/bin/bash
# Runs the backend in a Linux container on http://127.0.0.1:5199.
#
# Why: Smart App Control blocks every freshly built, unsigned DLL/exe on this machine, so
# `dotnet run` fails on the host (see AGENTS.md, "Running and verifying"). A container is not
# subject to it. Needs Docker running and the dev Postgres up (backend/docker-compose.yml).
#
# What it does: mounts the dotnet user-secrets file and the Firebase / Enable Banking key files
# READ-ONLY, points the connection string at the compose network, and starts the backend with
# --artifacts-path in the container, so no bin/obj lands in the repo. Nothing secret is printed.
# Usage:  bash tools/run-backend-container.sh <container-name> [extra docker args, e.g. -e Api__Key=x]
# Stop:   docker rm -f <container-name>
# Other command in the same environment (e.g. migrations): GN_CMD="<command>" bash tools/run-backend-container.sh <name>
#   migrations (changes the dev DB!): GN_CMD='export ArtifactsPath=/tmp/artifacts PATH=$PATH:/root/.dotnet/tools; dotnet tool install -g dotnet-ef --version 10.0.12 >/dev/null 2>&1; dotnet restore GreedyNose.Api >/dev/null && dotnet ef database update --project GreedyNose.Api'
set -e
NAME="$1"; shift
cd /c/Workspace/greedy-nose/backend
SID=$(grep -o "<UserSecretsId>[^<]*" GreedyNose.Api/GreedyNose.Api.csproj | sed 's/<UserSecretsId>//')
SECRETS_FILE="$(cygpath -m "$APPDATA")/Microsoft/UserSecrets/$SID/secrets.json"
get() { dotnet user-secrets list --project GreedyNose.Api | grep "^$1 " | sed 's/^[^=]*= *//'; }
FIREBASE_PATH=$(get "Firebase:ServiceAccountPath")
PEM_PATH=$(get "EnableBanking:PrivateKeyPath")
PG=$(get "ConnectionStrings:Postgres" | sed -E 's/Host=[^;]+/Host=greedy-nose-postgres/; s/Port=[0-9]+/Port=5432/')
export MSYS_NO_PATHCONV=1
docker run --rm --name "$NAME" --network backend_default -p 127.0.0.1:5199:5199 \
  -v "C:/Workspace/greedy-nose/backend:/src" \
  -v "$SECRETS_FILE:/root/.microsoft/usersecrets/$SID/secrets.json:ro" \
  -v "$FIREBASE_PATH:/secrets/firebase.json:ro" \
  -v "$PEM_PATH:/secrets/eb.pem:ro" \
  -v greedy-nose-nuget:/root/.nuget/packages \
  -e ASPNETCORE_ENVIRONMENT=Development -e ASPNETCORE_URLS=http://0.0.0.0:5199 \
  -e Firebase__ServiceAccountPath=/secrets/firebase.json \
  -e EnableBanking__PrivateKeyPath=/secrets/eb.pem \
  -e "ConnectionStrings__Postgres=$PG" \
  "$@" \
  -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c "${GN_CMD:-dotnet run --project GreedyNose.Api --artifacts-path /tmp/artifacts --no-launch-profile}"
