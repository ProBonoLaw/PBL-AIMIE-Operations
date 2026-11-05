#!/usr/bin/env bash
# Provision the dedicated GitHub Actions identity for AKS production deployment.
set -euo pipefail

subscription_id="${AZURE_PRODUCTION_SUBSCRIPTION_ID:?Set AZURE_PRODUCTION_SUBSCRIPTION_ID}"
repository="${DEPLOYMENT_REPOSITORY:?Set DEPLOYMENT_REPOSITORY}"
identity_group="${IDENTITY_RESOURCE_GROUP:?Set IDENTITY_RESOURCE_GROUP}"
identity_name=aimie-operations-ci-prod
aks_name="${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME}"
acr_name="${ACR_NAME:?Set ACR_NAME}"

if ! az identity show --subscription "$subscription_id" --resource-group "$identity_group" \
  --name "$identity_name" --output none 2>/dev/null; then
  az identity create --subscription "$subscription_id" --resource-group "$identity_group" \
    --name "$identity_name" --output none
fi
identity_client_id=$(az identity show --subscription "$subscription_id" \
  --resource-group "$identity_group" --name "$identity_name" --query clientId --output tsv)
identity_principal_id=$(az identity show --subscription "$subscription_id" \
  --resource-group "$identity_group" --name "$identity_name" --query principalId --output tsv)

subject_prefix=$(gh api "repos/$repository/actions/oidc/customization/sub" --jq '.sub_claim_prefix')
if [[ -z "$subject_prefix" || "$subject_prefix" == null ]]; then
  echo 'Could not determine the repository OIDC subject prefix' >&2
  exit 1
fi
credential_name=aimie-operations-aks-production
if ! az identity federated-credential show --subscription "$subscription_id" \
  --resource-group "$identity_group" --identity-name "$identity_name" \
  --name "$credential_name" --output none 2>/dev/null; then
  az identity federated-credential create --subscription "$subscription_id" \
    --resource-group "$identity_group" --identity-name "$identity_name" \
    --name "$credential_name" --issuer https://token.actions.githubusercontent.com \
    --subject "${subject_prefix}:environment:aks-production" \
    --audiences api://AzureADTokenExchange --output none
fi

assign_role() {
  local role=$1 scope=$2 count
  count=$(az role assignment list --subscription "$subscription_id" \
    --assignee-object-id "$identity_principal_id" --scope "$scope" \
    --role "$role" --query 'length(@)' --output tsv)
  if [[ "$count" == 0 ]]; then
    az role assignment create --subscription "$subscription_id" \
      --assignee-object-id "$identity_principal_id" \
      --assignee-principal-type ServicePrincipal --role "$role" \
      --scope "$scope" --output none
  fi
}

acr_id=$(az acr show --subscription "$subscription_id" --name "$acr_name" --query id --output tsv)
aks_id=$(az aks show --subscription "$subscription_id" --resource-group "$identity_group" \
  --name "$aks_name" --query id --output tsv)
assign_role AcrPush "$acr_id"
assign_role Reader "$acr_id"
assign_role 'Azure Kubernetes Service Contributor Role' "$aks_id"
assign_role 'Azure Kubernetes Service RBAC Cluster Admin' "$aks_id"

gh variable set AKS_DEPLOY_CLIENT_ID --env aks-production --repo "$repository" --body "$identity_client_id"
gh variable set AZURE_SUBSCRIPTION_ID --env aks-production --repo "$repository" --body "$subscription_id"
gh variable set AZURE_PRODUCTION_SUBSCRIPTION_ID --env aks-production --repo "$repository" --body "$subscription_id"
printf 'Production deploy identity client ID: %s\n' "$identity_client_id"
