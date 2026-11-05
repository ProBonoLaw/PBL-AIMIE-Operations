#!/usr/bin/env bash
# Provision workload identities and data access for AKS production workers and converter.
set -euo pipefail

subscription_id="${AZURE_PRODUCTION_SUBSCRIPTION_ID:?Set AZURE_PRODUCTION_SUBSCRIPTION_ID}"
repository="${DEPLOYMENT_REPOSITORY:?Set DEPLOYMENT_REPOSITORY}"
identity_group="${IDENTITY_RESOURCE_GROUP:?Set IDENTITY_RESOURCE_GROUP}"
aks_name="${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME}"
worker_name=aimie-operations-prod-workload
converter_name=markdown-converter-prod-workload
vault_name="${KEY_VAULT_NAME:?Set KEY_VAULT_NAME}"
eventhub_group="${EVENTHUB_RESOURCE_GROUP:?Set EVENTHUB_RESOURCE_GROUP}"
eventhub_namespace="${EVENTHUB_NAMESPACE_NAME:?Set EVENTHUB_NAMESPACE_NAME}"
storage_group="${STORAGE_RESOURCE_GROUP:?Set STORAGE_RESOURCE_GROUP}"
storage_account="${STORAGE_ACCOUNT:?Set STORAGE_ACCOUNT}"
cosmos_group="${COSMOS_RESOURCE_GROUP:?Set COSMOS_RESOURCE_GROUP}"
cosmos_account="${COSMOS_ACCOUNT:?Set COSMOS_ACCOUNT}"

identity_details() {
  local name=$1 client_id principal_id
  if ! az identity show --subscription "$subscription_id" --resource-group "$identity_group" \
    --name "$name" --output none 2>/dev/null; then
    az identity create --subscription "$subscription_id" --resource-group "$identity_group" \
      --name "$name" --output none
  fi
  client_id=$(az identity show --subscription "$subscription_id" \
    --resource-group "$identity_group" --name "$name" --query clientId --output tsv)
  principal_id=$(az identity show --subscription "$subscription_id" \
    --resource-group "$identity_group" --name "$name" --query principalId --output tsv)
  printf '%s %s\n' "$client_id" "$principal_id"
}
read -r worker_client_id worker_principal_id < <(identity_details "$worker_name")
read -r converter_client_id converter_principal_id < <(identity_details "$converter_name")
[[ -n "$worker_client_id" && -n "$worker_principal_id" && \
   -n "$converter_client_id" && -n "$converter_principal_id" ]] || {
  echo 'Could not determine production workload identity IDs' >&2
  exit 1
}
issuer=$(az aks show --subscription "$subscription_id" --resource-group "$identity_group" \
  --name "$aks_name" --query oidcIssuerProfile.issuerUrl --output tsv)

federate() {
  local name=$1 credential=$2 service_account=$3
  if ! az identity federated-credential show --subscription "$subscription_id" \
    --resource-group "$identity_group" --identity-name "$name" \
    --name "$credential" --output none 2>/dev/null; then
    az identity federated-credential create --subscription "$subscription_id" \
      --resource-group "$identity_group" --identity-name "$name" \
      --name "$credential" --issuer "$issuer" \
      --subject "system:serviceaccount:aimie-operations:$service_account" \
      --audiences api://AzureADTokenExchange --output none
  fi
}
federate "$worker_name" aks-production aimie-operations
federate "$converter_name" aks-production markdown-converter

assign_role() {
  local principal=$1 role=$2 scope=$3 count
  count=$(az role assignment list --subscription "$subscription_id" \
    --assignee-object-id "$principal" --scope "$scope" --role "$role" \
    --query 'length(@)' --output tsv)
  if [[ "$count" == 0 ]]; then
    az role assignment create --subscription "$subscription_id" \
      --assignee-object-id "$principal" --assignee-principal-type ServicePrincipal \
      --role "$role" --scope "$scope" --output none
  fi
}

bash scripts/provision-consumer-groups.sh "$subscription_id" "$eventhub_namespace"
request_hub_id=$(az eventhubs eventhub show --subscription "$subscription_id" \
  --resource-group "$eventhub_group" --namespace-name "$eventhub_namespace" \
  --name aimie-request-queue --query id --output tsv)
response_hub_id=$(az eventhubs eventhub show --subscription "$subscription_id" \
  --resource-group "$eventhub_group" --namespace-name "$eventhub_namespace" \
  --name aimie-response-queue --query id --output tsv)
assign_role "$worker_principal_id" 'Azure Event Hubs Data Receiver' "$request_hub_id"
assign_role "$worker_principal_id" 'Azure Event Hubs Data Sender' "$response_hub_id"

storage_id=$(az storage account show --subscription "$subscription_id" \
  --resource-group "$storage_group" --name "$storage_account" --query id --output tsv)
for container in aimie-operations-checkpoints aimie-operations-failed-events aimie-operations-results; do
  if ! az storage container-rm show --subscription "$subscription_id" \
    --resource-group "$storage_group" --storage-account "$storage_account" \
    --name "$container" --output none 2>/dev/null; then
    az storage container-rm create --subscription "$subscription_id" \
      --resource-group "$storage_group" --storage-account "$storage_account" \
      --name "$container" --public-access off --output none
  fi
  assign_role "$worker_principal_id" 'Storage Blob Data Contributor' \
    "$storage_id/blobServices/default/containers/$container"
done

vault_id=$(az keyvault show --subscription "$subscription_id" --name "$vault_name" \
  --query id --output tsv)
assign_role "$worker_principal_id" 'Key Vault Secrets User' \
  "$vault_id/secrets/aimie-operations-appsettings"
assign_role "$converter_principal_id" 'Key Vault Secrets User' \
  "$vault_id/secrets/markdown-converter-appsettings"

if ! az cosmosdb sql role assignment list --subscription "$subscription_id" \
  --resource-group "$cosmos_group" --account-name "$cosmos_account" --output json | jq -e \
  --arg principal "$worker_principal_id" \
  'any(.[]; .principalId == $principal and .scope == "/" and (.roleDefinitionId | endswith("/00000000-0000-0000-0000-000000000002")))' \
  >/dev/null; then
  az cosmosdb sql role assignment create --subscription "$subscription_id" \
    --resource-group "$cosmos_group" --account-name "$cosmos_account" \
    --role-definition-id 00000000-0000-0000-0000-000000000002 \
    --principal-id "$worker_principal_id" --scope / --output none
fi

gh variable set AZURE_WORKLOAD_IDENTITY_CLIENT_ID --env aks-production \
  --repo "$repository" --body "$worker_client_id"
gh variable set AZURE_CONVERTER_WORKLOAD_IDENTITY_CLIENT_ID --env aks-production \
  --repo "$repository" --body "$converter_client_id"
printf 'Production worker identity client ID: %s\n' "$worker_client_id"
printf 'Production converter identity client ID: %s\n' "$converter_client_id"
