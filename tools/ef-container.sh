#!/bin/bash
# Runs `dotnet ef` for the backend in a Linux container (Smart App Control blocks the host, see
# AGENTS.md). The source is copied (without bin/obj) into a scratch directory first, because the
# host's obj/project.assets.json carries Windows package paths and dotnet-ef reads it.
#
# Usage:  bash tools/ef-container.sh <dotnet ef arguments, without --project>
#   bash tools/ef-container.sh migrations list            # read-only
#   bash tools/ef-container.sh dbcontext info             # read-only
#   bash tools/ef-container.sh database update            # CHANGES the dev database
# Needs Docker and the dev Postgres up (backend/docker-compose.yml). The connection string is the
# dev one from the compose file (throwaway data), passed as ConnectionStrings__Postgres; the
# design-time factory (GreedyNose.Api/Data/DesignTimeDbContextFactory.cs) reads it.
# `migrations add` would write into the scratch copy, not the repo — for that, mount backend
# writable instead of :ro and run it by hand.
set -e
export MSYS_NO_PATHCONV=1
ARGS="$*"
docker run --rm --network backend_default \
  -e "ConnectionStrings__Postgres=Host=greedy-nose-postgres;Port=5432;Database=greedynose;Username=greedynose;Password=greedynose-dev" \
  -v "C:/Workspace/greedy-nose/backend:/src:ro" -v greedy-nose-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:10.0 bash -c "
    mkdir /work && cd /src && tar cf - --exclude=bin --exclude=obj . | tar xf - -C /work && cd /work &&
    dotnet tool install -g dotnet-ef --version 10.0.12 >/dev/null && export PATH=\$PATH:/root/.dotnet/tools &&
    dotnet restore GreedyNose.Api >/dev/null &&
    dotnet ef $ARGS --project GreedyNose.Api"
