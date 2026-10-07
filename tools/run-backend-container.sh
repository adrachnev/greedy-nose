#!/bin/bash
# Runs the backend in a Linux container on http://127.0.0.1:5199.
#
# Story 2.4: there is no standalone API host any more — the Functions host serves the API (and the
# first-sync queue function and the ingestion timer). This script only forwards to
# tools/run-functions-container.sh with the same arguments; read that file for what it does.
# Usage:  bash tools/run-backend-container.sh <container-name> [extra docker args]
# Migrations moved to tools/ef-container.sh. Stop: docker rm -f <container-name>
exec bash "$(dirname "$0")/run-functions-container.sh" "$@"
