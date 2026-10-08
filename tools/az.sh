#!/bin/bash
# Runs the Azure CLI (`az`) in a container, so nothing has to be installed on the host (Smart App Control
# blocks unsigned binaries there, see AGENTS.md). The login lives in the named volume
# `greedy-nose-azure` (mounted as ~/.azure), so it survives between calls.
#
# Usage:  bash tools/az.sh <az arguments>
#   bash tools/az.sh login --use-device-code      # prints a code; open https://microsoft.com/devicelogin
#   bash tools/az.sh account show                 # who and which subscription
#   bash tools/az.sh functionapp list-runtimes --os linux
# A file from the repo can be passed with AZ_MOUNT=<host dir>: it appears read-only as /mnt.
# Nothing secret is printed by this script; `az` itself prints what the commands return.
set -e
export MSYS_NO_PATHCONV=1
EXTRA=()
if [ -n "${AZ_MOUNT:-}" ]; then
  EXTRA=(-v "$AZ_MOUNT:/mnt:ro")
fi
docker run --rm -v greedy-nose-azure:/root/.azure "${EXTRA[@]}" mcr.microsoft.com/azure-cli az "$@"
