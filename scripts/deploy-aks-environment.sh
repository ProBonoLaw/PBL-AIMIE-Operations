#!/usr/bin/env bash
set -euo pipefail

: "${DEPLOYMENT_ENV:?Set DEPLOYMENT_ENV}"
: "${AKS_SUBSCRIPTION_ID:?Set AKS_SUBSCRIPTION_ID}"
: "${AZURE_TENANT_ID:?Set AZURE_TENANT_ID}"
: "${AZURE_WORKLOAD_IDENTITY_CLIENT_ID:?Set AZURE_WORKLOAD_IDENTITY_CLIENT_ID}"
: "${AZURE_CONVERTER_WORKLOAD_IDENTITY_CLIENT_ID:?Set AZURE_CONVERTER_WORKLOAD_IDENTITY_CLIENT_ID}"
: "${AKS_RESOURCE_GROUP:?Set AKS_RESOURCE_GROUP}"
: "${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME}"
: "${KEY_VAULT_NAME:?Set KEY_VAULT_NAME}"
: "${IMAGE_TAG:?Set IMAGE_TAG}"
: "${ACR_LOGIN_SERVER:?Set ACR_LOGIN_SERVER}"

manifest_dir=k8s/aimie-operations
case "$DEPLOYMENT_ENV" in
  aks-test|aks-production)
    : "${MARKDOWN_CONVERTER_INGRESS_HOST:?Set MARKDOWN_CONVERTER_INGRESS_HOST}"
    : "${MARKDOWN_CONVERTER_TLS_CERT_KEYVAULT_URI:?Set MARKDOWN_CONVERTER_TLS_CERT_KEYVAULT_URI}"
    if [[ ! "$MARKDOWN_CONVERTER_INGRESS_HOST" =~ ^[[:alnum:].-]+$ ||
          ! "$MARKDOWN_CONVERTER_TLS_CERT_KEYVAULT_URI" =~ ^https://[[:alnum:]-]+\.vault\.azure\.net/certificates/[[:alnum:]-]+$ ]]; then
      echo 'Ingress host or Key Vault certificate URI is invalid' >&2
      exit 1
    fi
    ingress_manifest="$manifest_dir/markdown-converter-ingress.template.yaml"
    ;;
  *)
    echo 'DEPLOYMENT_ENV must be aks-test or aks-production' >&2
    exit 1
    ;;
esac

guid_pattern='^[[:xdigit:]]{8}(-[[:xdigit:]]{4}){3}-[[:xdigit:]]{12}$'
if [[ ! "$AKS_SUBSCRIPTION_ID" =~ $guid_pattern || ! "$AZURE_TENANT_ID" =~ $guid_pattern || ! "$AZURE_WORKLOAD_IDENTITY_CLIENT_ID" =~ $guid_pattern || \
      ! "$AZURE_CONVERTER_WORKLOAD_IDENTITY_CLIENT_ID" =~ $guid_pattern ]]; then
  echo 'Azure subscription, tenant, and workload identity client IDs must be GUIDs' >&2
  exit 1
fi
if [[ ! "$IMAGE_TAG" =~ ^sha-[[:xdigit:]]{40}$ ]]; then
  echo 'IMAGE_TAG must be sha- followed by a 40-character Git commit SHA' >&2
  exit 1
fi
if [[ ! "$KEY_VAULT_NAME" =~ ^[[:alnum:]][[:alnum:]-]{1,22}[[:alnum:]]$ ]]; then
  echo 'KEY_VAULT_NAME must be a valid Azure Key Vault name' >&2
  exit 1
fi

acr_login_server="$ACR_LOGIN_SERVER"
if [[ ! "$acr_login_server" =~ ^[[:alnum:].-]+(:[0-9]+)?$ ]]; then
  echo 'ACR_LOGIN_SERVER must be a registry hostname, optionally with a port' >&2
  exit 1
fi

rendered_dir=$(mktemp -d)
trap 'rm -rf "$rendered_dir"' EXIT
bundle="$rendered_dir/aimie-operations.yaml"
append_manifest() {
  local path=$1
  if [[ -s "$bundle" ]]; then printf '\n---\n' >> "$bundle"; fi
  sed \
    -e "s#<workload-identity-client-id>#$AZURE_WORKLOAD_IDENTITY_CLIENT_ID#g" \
    -e "s#<converter-workload-identity-client-id>#$AZURE_CONVERTER_WORKLOAD_IDENTITY_CLIENT_ID#g" \
    -e "s#<key-vault-name>#$KEY_VAULT_NAME#g" \
    -e "s#<azure-tenant-id>#$AZURE_TENANT_ID#g" \
    -e "s#<acr-login-server>/aimie-operations:latest#$acr_login_server/aimie-operations:$IMAGE_TAG#g" \
    -e "s#<acr-login-server>/markdown-converter:latest#$acr_login_server/markdown-converter:$IMAGE_TAG#g" \
    "$path" >> "$bundle"
}

for name in namespace service-account markdown-converter-service-account \
  markdown-converter-appsettings appsettings-secretproviderclass \
  declaration-deployment qa-deployment argument-builder-deployment timeline-builder-deployment \
  markdown-converter; do
  append_manifest "$manifest_dir/$name.yaml"
done

sed \
    -e "s#<markdown-converter-ingress-host>#$MARKDOWN_CONVERTER_INGRESS_HOST#g" \
    -e "s#<markdown-converter-tls-cert-keyvault-uri>#$MARKDOWN_CONVERTER_TLS_CERT_KEYVAULT_URI#g" \
    "$ingress_manifest" > "$rendered_dir/markdown-converter-ingress.yaml"
append_manifest "$rendered_dir/markdown-converter-ingress.yaml"

if grep -q '<[^>]*>' "$bundle"; then
  echo 'An unresolved deployment placeholder remains' >&2
  exit 1
fi
if [[ "${DRY_RUN:-0}" == 1 ]]; then
  output="${RENDER_OUTPUT:-.local/aimie-operations-${DEPLOYMENT_ENV}-rendered.yaml}"
  mkdir -p "$(dirname "$output")"
  cp "$bundle" "$output"
  printf 'Rendered %s manifests to %s without applying them\n' "$DEPLOYMENT_ENV" "$output"
  exit 0
fi

bash scripts/aks-run-command.sh "$bundle" \
  'kubectl apply -f aimie-operations.yaml && kubectl delete configmap aimie-operations-config -n aimie-operations --ignore-not-found && for name in declaration-generation qa-generation argument-builder-generation timeline-builder-generation markdown-converter; do timeout=5m; if [ "$name" = markdown-converter ]; then timeout=10m; fi; kubectl rollout status deployment/$name -n aimie-operations --timeout="$timeout" || { kubectl get pods -n aimie-operations -l app="$name" -o wide; kubectl describe pods -n aimie-operations -l app="$name"; exit 1; }; done'
