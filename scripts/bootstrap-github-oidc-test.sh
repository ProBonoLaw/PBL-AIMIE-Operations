#!/usr/bin/env bash
# Create a dedicated CI identity for ACR publishing and AKS test deployment.
set -euo pipefail

repository="${DEPLOYMENT_REPOSITORY:?Set DEPLOYMENT_REPOSITORY}"
identity_group="${IDENTITY_RESOURCE_GROUP:?Set IDENTITY_RESOURCE_GROUP}"
aks_group="${AKS_RESOURCE_GROUP:?Set AKS_RESOURCE_GROUP}"
aks_name="${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME}"
acr_group="${ACR_RESOURCE_GROUP:?Set ACR_RESOURCE_GROUP}"
identity_name=aimie-operations-ci-test
az account set --subscription "${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}"
if ! az identity show --resource-group "$identity_group" \
  --name "$identity_name" --output none 2>/dev/null; then
  az identity create --resource-group "$identity_group" \
    --name "$identity_name" --output none
fi
identity_client_id=$(az identity show --resource-group "$identity_group" \
  --name "$identity_name" --query clientId --output tsv)
identity_principal_id=$(az identity show --resource-group "$identity_group" \
  --name "$identity_name" --query principalId --output tsv)
subject_prefix=$(gh api "repos/$repository/actions/oidc/customization/sub" --jq '.sub_claim_prefix')
if [[ -z "$subject_prefix" || "$subject_prefix" == null ]]; then
  echo 'Could not determine the repository OIDC subject prefix' >&2
  exit 1
fi

for environment in test aks-test; do
  credential_name="aimie-operations-${environment}"
  if ! az identity federated-credential show --resource-group "$identity_group" \
    --identity-name "$identity_name" --name "$credential_name" --output none 2>/dev/null; then
    az identity federated-credential create --resource-group "$identity_group" \
      --identity-name "$identity_name" --name "$credential_name" \
      --issuer https://token.actions.githubusercontent.com \
      --subject "${subject_prefix}:environment:${environment}" \
      --audiences api://AzureADTokenExchange --output none
  fi
done

assign_role() {
  local role="$1" scope="$2" count
  count=$(az role assignment list --assignee-object-id "$identity_principal_id" \
    --scope "$scope" --role "$role" --query 'length(@)' --output tsv)
  if [[ "$count" == 0 ]]; then
    az role assignment create --assignee-object-id "$identity_principal_id" \
      --assignee-principal-type ServicePrincipal --role "$role" --scope "$scope" --output none
  fi
}

acr_id=$(az acr show --resource-group "$acr_group" \
  --name "${ACR_NAME:?Set ACR_NAME}" --query id --output tsv)
aks_id=$(az aks show --resource-group "$aks_group" \
  --name "$aks_name" --query id --output tsv)
assign_role AcrPush "$acr_id"
assign_role Reader "$acr_id"
assign_role 'Azure Kubernetes Service Contributor Role' "$aks_id"
assign_role 'Azure Kubernetes Service RBAC Cluster Admin' "$aks_id"

printf 'Use CI identity client ID for AZURE_CLIENT_ID and AKS_DEPLOY_CLIENT_ID: %s\n' "$identity_client_id"
printf 'Set AZURE_SUBSCRIPTION_ID and AZURE_TENANT_ID as repository variables.\n'
printf 'A successful main-branch image build now triggers the aks-test deployment.\n'
