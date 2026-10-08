#!/bin/bash
# Runs `dotnet ef` for the backend in a Linux container (Smart App Control blocks the host, see
# AGENTS.md). The source is copied (without bin/obj) into a scratch directory first, because the
# host's obj/project.assets.json carries Windows package paths and dotnet-ef reads it.
#
# Usage:  bash tools/ef-container.sh <dotnet ef arguments, without --project>
#   bash tools/ef-container.sh migrations list            # read-only
#   bash tools/ef-container.sh dbcontext info             # read-only
#   bash tools/ef-container.sh database update            # CHANGES the dev database
#   EF_RW=1 bash tools/ef-container.sh migrations add <Name>
#       # writes the new migration files back into backend/GreedyNose.Api/Data/Migrations
#       # (the model snapshot included); review the files, then commit them like code.
#   DB=neon bash tools/ef-container.sh database update   # the Neon database (secret ConnectionStrings:Neon)
# Needs Docker and the dev Postgres up (backend/docker-compose.yml). The connection string is the
# dev one from the compose file (throwaway data), passed as ConnectionStrings__Postgres; the
# design-time factory (GreedyNose.Api/Data/DesignTimeDbContextFactory.cs) reads it.
set -e
export MSYS_NO_PATHCONV=1
ARGS="$*"
MODE=ro; COPYBACK=""
if [ "${EF_RW:-}" = "1" ]; then
  MODE=rw
  COPYBACK="&& cp -r /work/GreedyNose.Api/Data/Migrations/. /src/GreedyNose.Api/Data/Migrations/"
fi
if [ "${DB:-}" = "neon" ]; then
  # Story 2.5: the Neon database. The connection string is the user secret ConnectionStrings:Neon
  # (the direct, not the pooled, host); nothing is printed. No dev network needed.
  CONN=$(dotnet user-secrets list --project C:/Workspace/greedy-nose/backend/GreedyNose.Api \
    | grep '^ConnectionStrings:Neon ' | sed 's/^[^=]*= *//')
  if [ -z "$CONN" ]; then
    echo "The user secret ConnectionStrings:Neon is not set." >&2
    exit 1
  fi
  NET=()
else
  CONN="Host=greedy-nose-postgres;Port=5432;Database=greedynose;Username=greedynose;Password=greedynose-dev"
  NET=(--network backend_default)
fi
docker run --rm "${NET[@]}" \
  -e "ConnectionStrings__Postgres=$CONN" \
  -v "C:/Workspace/greedy-nose/backend:/src:$MODE" -v greedy-nose-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c "
    mkdir /work && cd /src && tar cf - --exclude=bin --exclude=obj . | tar xf - -C /work && cd /work &&
    dotnet tool install -g dotnet-ef --version 10.0.12 >/dev/null && export PATH=\$PATH:/root/.dotnet/tools &&
    dotnet restore GreedyNose.Api >/dev/null &&
    dotnet ef $ARGS --project GreedyNose.Api $COPYBACK"
