#!/bin/bash
# Story 2.6: creates the Azure resources for the backend (Flex Consumption Function App, storage, Key Vault,
# Application Insights). Safe to run again: every step checks first. Uses the Azure CLI in a container
# (tools/az.sh, login first: `bash tools/az.sh login --use-device-code`). Prints names and ids only, never a
# secret. Secrets are set by tools/azure-secret.sh, the code by tools/azure-deploy.sh.
set -e -o pipefail
cd "$(dirname "$0")/.."

LOC=germanywestcentral
SUFFIX=$(bash tools/az.sh account show --query id -o tsv | tr -d '\r' | cut -c1-5)
RG=rg-greedy-nose
ST=stgreedynose$SUFFIX
KV=kv-greedynose-$SUFFIX
FUNC=func-greedy-nose-$SUFFIX
LAW=log-greedy-nose
AI=appi-greedy-nose

az() { bash tools/az.sh "$@" | tr -d '\r'; }

echo "== Resource providers"
for p in Microsoft.Web Microsoft.Storage Microsoft.KeyVault Microsoft.Insights Microsoft.OperationalInsights; do
  az provider register --namespace "$p" >/dev/null
done

echo "== Resource group $RG"
az group create -n "$RG" -l "$LOC" -o none

echo "== Storage account $ST"
if ! az storage account show -g "$RG" -n "$ST" -o none 2>/dev/null; then
  az storage account create -g "$RG" -n "$ST" -l "$LOC" --sku Standard_LRS --kind StorageV2 \
    --min-tls-version TLS1_2 --allow-blob-public-access false -o none
fi

echo "== Log Analytics $LAW and Application Insights $AI"
az config set extension.use_dynamic_install=yes_without_prompt extension.dynamic_install_allow_preview=true >/dev/null
if ! az monitor log-analytics workspace show -g "$RG" -n "$LAW" -o none 2>/dev/null; then
  az monitor log-analytics workspace create -g "$RG" -n "$LAW" -l "$LOC" --retention-time 30 -o none
fi
LAW_ID=$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query id -o tsv)
if ! az monitor app-insights component show -g "$RG" --app "$AI" -o none 2>/dev/null; then
  az monitor app-insights component create -g "$RG" --app "$AI" -l "$LOC" --kind web \
    --application-type web --workspace "$LAW_ID" -o none
fi

echo "== Key Vault $KV"
if ! az keyvault show -g "$RG" -n "$KV" -o none 2>/dev/null; then
  az keyvault create -g "$RG" -n "$KV" -l "$LOC" --enable-rbac-authorization true --retention-days 7 -o none
fi
KV_ID=$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)
ME=$(az ad signed-in-user show --query id -o tsv)
az role assignment create --assignee-object-id "$ME" --assignee-principal-type User \
  --role "Key Vault Secrets Officer" --scope "$KV_ID" -o none || true

echo "== Function App $FUNC (Flex Consumption, 512 MB)"
if ! az functionapp show -g "$RG" -n "$FUNC" -o none 2>/dev/null; then
  az functionapp create -g "$RG" -n "$FUNC" --storage-account "$ST" \
    --flexconsumption-location "$LOC" --runtime dotnet-isolated --runtime-version 10.0 \
    --instance-memory 512 --assign-identity '[system]' \
    --deployment-storage-auth-type SystemAssignedIdentity \
    --app-insights "$AI" -o none
fi
az functionapp update -g "$RG" -n "$FUNC" --set httpsOnly=true -o none
az functionapp config set -g "$RG" -n "$FUNC" --min-tls-version 1.2 --ftps-state Disabled -o none

echo "== Roles of the app identity"
APP_PRINCIPAL=$(az functionapp identity show -g "$RG" -n "$FUNC" --query principalId -o tsv)
ST_ID=$(az storage account show -g "$RG" -n "$ST" --query id -o tsv)
for role in "Storage Blob Data Owner" "Storage Queue Data Contributor" "Storage Table Data Contributor"; do
  az role assignment create --assignee-object-id "$APP_PRINCIPAL" --assignee-principal-type ServicePrincipal \
    --role "$role" --scope "$ST_ID" -o none || true
done
az role assignment create --assignee-object-id "$APP_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "$KV_ID" -o none || true

HOST=$(az functionapp show -g "$RG" -n "$FUNC" --query defaultHostName -o tsv)
echo
echo "Done. Function App: https://$HOST"
echo "Key Vault: https://$KV.vault.azure.net/   Storage: $ST   Resource group: $RG"
