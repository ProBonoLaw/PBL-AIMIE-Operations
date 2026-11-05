#!/usr/bin/env bash
# One-time, idempotent infrastructure setup for the AKS test workers and converter.
set -euo pipefail

subscription_id="${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}"
identity_group="${IDENTITY_RESOURCE_GROUP:?Set IDENTITY_RESOURCE_GROUP}"
aks_group="${AKS_RESOURCE_GROUP:?Set AKS_RESOURCE_GROUP}"
aks_name="${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME}"
identity_name=aimie-operations-test-workload
converter_identity_name=markdown-converter-test-workload
vault_name="${KEY_VAULT_NAME:?Set KEY_VAULT_NAME}"
storage_account="${STORAGE_ACCOUNT:?Set STORAGE_ACCOUNT}"
cosmos_group="${COSMOS_RESOURCE_GROUP:?Set COSMOS_RESOURCE_GROUP}"
cosmos_account="${COSMOS_ACCOUNT:?Set COSMOS_ACCOUNT}"

az account set --subscription "$subscription_id"
if ! az identity show --resource-group "$identity_group" --name "$identity_name" --output none 2>/dev/null; then
  az identity create --resource-group "$identity_group" --name "$identity_name" --output none
fi
identity_client_id=$(az identity show --resource-group "$identity_group" --name "$identity_name" --query clientId --output tsv)
identity_principal_id=$(az identity show --resource-group "$identity_group" --name "$identity_name" --query principalId --output tsv)
if ! az identity show --resource-group "$identity_group" --name "$converter_identity_name" --output none 2>/dev/null; then
  az identity create --resource-group "$identity_group" --name "$converter_identity_name" --output none
fi
converter_identity_client_id=$(az identity show --resource-group "$identity_group" --name "$converter_identity_name" --query clientId --output tsv)
converter_identity_principal_id=$(az identity show --resource-group "$identity_group" --name "$converter_identity_name" --query principalId --output tsv)
issuer=$(az aks show --resource-group "$aks_group" --name "$aks_name" --query oidcIssuerProfile.issuerUrl --output tsv)

if ! az identity federated-credential show --resource-group "$identity_group" \
  --identity-name "$identity_name" --name aks-test-northcentral --output none 2>/dev/null; then
  az identity federated-credential create --resource-group "$identity_group" \
    --identity-name "$identity_name" --name aks-test-northcentral \
    --issuer "$issuer" --subject system:serviceaccount:aimie-operations:aimie-operations \
    --audiences api://AzureADTokenExchange --output none
fi
if ! az identity federated-credential show --resource-group "$identity_group" \
  --identity-name "$converter_identity_name" --name aks-test-northcentral --output none 2>/dev/null; then
  az identity federated-credential create --resource-group "$identity_group" \
    --identity-name "$converter_identity_name" --name aks-test-northcentral \
    --issuer "$issuer" --subject system:serviceaccount:aimie-operations:markdown-converter \
    --audiences api://AzureADTokenExchange --output none
fi

assign_role() {
  local role="$1" scope="$2" count
  count=$(az role assignment list --assignee-object-id "$identity_principal_id" \
    --scope "$scope" --role "$role" --query 'length(@)' --output tsv)
  if [[ "$count" == 0 ]]; then
    az role assignment create --assignee-object-id "$identity_principal_id" \
      --assignee-principal-type ServicePrincipal --role "$role" --scope "$scope" --output none
  fi
}

bash scripts/provision-consumer-groups.sh "$subscription_id"
request_hub_id=$(az eventhubs eventhub show --resource-group "${EVENTHUB_RESOURCE_GROUP:?Set EVENTHUB_RESOURCE_GROUP}" \
  --namespace-name "${EVENTHUB_NAMESPACE_NAME:?Set EVENTHUB_NAMESPACE_NAME}" --name aimie-request-queue --query id --output tsv)
response_hub_id=$(az eventhubs eventhub show --resource-group "${EVENTHUB_RESOURCE_GROUP:?Set EVENTHUB_RESOURCE_GROUP}" \
  --namespace-name "${EVENTHUB_NAMESPACE_NAME:?Set EVENTHUB_NAMESPACE_NAME}" --name aimie-response-queue --query id --output tsv)
assign_role 'Azure Event Hubs Data Receiver' "$request_hub_id"
assign_role 'Azure Event Hubs Data Sender' "$response_hub_id"

storage_id=$(az storage account show --resource-group "${STORAGE_RESOURCE_GROUP:?Set STORAGE_RESOURCE_GROUP}" \
  --name "$storage_account" --query id --output tsv)
for container in aimie-operations-checkpoints aimie-operations-failed-events aimie-operations-results; do
  az storage container create --account-name "$storage_account" --auth-mode login \
    --name "$container" --output none
  assign_role 'Storage Blob Data Contributor' \
    "$storage_id/blobServices/default/containers/$container"
done

secret_scope="$(az keyvault show --name "$vault_name" --query id --output tsv)/secrets/aimie-operations-appsettings"
assign_role 'Key Vault Secrets User' "$secret_scope"
converter_secret_scope="$(az keyvault show --name "$vault_name" --query id --output tsv)/secrets/markdown-converter-appsettings"
converter_role_count=$(az role assignment list --assignee-object-id "$converter_identity_principal_id" \
  --scope "$converter_secret_scope" --role 'Key Vault Secrets User' --query 'length(@)' --output tsv)
if [[ "$converter_role_count" == 0 ]]; then
  az role assignment create --assignee-object-id "$converter_identity_principal_id" \
    --assignee-principal-type ServicePrincipal --role 'Key Vault Secrets User' \
    --scope "$converter_secret_scope" --output none
fi

if ! az cosmosdb sql role assignment list --resource-group "$cosmos_group" \
  --account-name "$cosmos_account" --output json | jq -e \
  --arg principal "$identity_principal_id" \
  'any(.[]; .principalId == $principal and .scope == "/" and (.roleDefinitionId | endswith("/00000000-0000-0000-0000-000000000002")))' \
  >/dev/null; then
  az cosmosdb sql role assignment create --resource-group "$cosmos_group" \
    --account-name "$cosmos_account" --role-definition-id 00000000-0000-0000-0000-000000000002 \
    --principal-id "$identity_principal_id" --scope / --output none
fi

printf 'AKS workload identity client ID: %s\n' "$identity_client_id"
printf 'Set the GitHub aks-test environment variable AZURE_WORKLOAD_IDENTITY_CLIENT_ID to this value.\n'
printf 'Markdown Converter workload identity client ID: %s\n' "$converter_identity_client_id"
printf 'Set the GitHub aks-test environment variable AZURE_CONVERTER_WORKLOAD_IDENTITY_CLIENT_ID to this value.\n'
