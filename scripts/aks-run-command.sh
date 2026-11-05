#!/usr/bin/env bash
# Execute a command in AKS through ARM so deployment works with the API server IP allowlist.
set -euo pipefail

if [[ $# != 2 || ! -f "$1" ]]; then
  echo 'Usage: aks-run-command.sh <file-to-attach> <command>' >&2
  exit 2
fi

attachment=$1
command=$2
: "${AKS_RESOURCE_GROUP:?Set AKS_RESOURCE_GROUP}"
: "${AKS_CLUSTER_NAME:?Set AKS_CLUSTER_NAME}"
cluster_group=$AKS_RESOURCE_GROUP
cluster_name=$AKS_CLUSTER_NAME
subscription_args=()
if [[ -n "${AKS_SUBSCRIPTION_ID:-}" ]]; then
  subscription_args=(--subscription "$AKS_SUBSCRIPTION_ID")
fi
api_version=2025-06-01
scratch=$(mktemp -d)
umask 077
trap 'rm -rf "$scratch"' EXIT

cluster_id=$(az aks show "${subscription_args[@]}" --resource-group "$cluster_group" --name "$cluster_name" --query id --output tsv)
[[ "$cluster_id" == /subscriptions/*/providers/Microsoft.ContainerService/managedClusters/* ]] || {
  echo 'Could not determine the AKS cluster resource ID' >&2
  exit 1
}
run_url="https://management.azure.com${cluster_id}/runCommand?api-version=${api_version}"

# The AKS API requires an Entra cluster token and a base64 ZIP for attached files.
az account get-access-token "${subscription_args[@]}" --resource https://management.azure.com/ --query accessToken --output tsv > "$scratch/arm-token"
az account get-access-token "${subscription_args[@]}" --resource 6dae42f8-4368-4678-94ff-3960e28e3630 \
  --query accessToken --output tsv > "$scratch/cluster-token"
awk '{printf "header = \"Authorization: Bearer %s\"\n", $0}' "$scratch/arm-token" > "$scratch/auth"
zip -j -q "$scratch/context.zip" "$attachment"
base64 -w0 "$scratch/context.zip" > "$scratch/context"
jq -n --arg command "$command" --rawfile context "$scratch/context" \
  --rawfile clusterToken "$scratch/cluster-token" \
  '{command:$command,context:$context,clusterToken:($clusterToken | rtrimstr("\n"))}' > "$scratch/request"

post_status=$(curl --silent --show-error --max-time 60 --request POST --url "$run_url" \
  --header 'Content-Type: application/json' --config "$scratch/auth" \
  --data-binary "@$scratch/request" --dump-header "$scratch/headers" \
  --output "$scratch/result" --write-out '%{http_code}')
if [[ "$post_status" != 202 ]]; then
  jq -r '.error.message // .message // "AKS run command request failed"' "$scratch/result" >&2 || true
  echo "AKS run command request returned HTTP $post_status" >&2
  exit 1
fi

# Location has a signed query string. Keep it in the private scratch directory, never in logs.
location=$(awk 'tolower($1)=="location:" {sub(/\r$/, "", $2); print $2; exit}' "$scratch/headers")
[[ "$location" == https://management.azure.com/* ]] || {
  echo 'AKS run command did not return a valid result URL' >&2
  exit 1
}
printf 'url = "%s"\n' "$location" > "$scratch/location"
unset location

for ((attempt=1; attempt<=360; attempt++)); do
  status=$(curl --silent --show-error --max-time 60 --config "$scratch/auth" \
    --config "$scratch/location" --output "$scratch/result" --write-out '%{http_code}')
  if [[ "$status" == 202 ]]; then
    sleep 5
    continue
  fi
  if [[ "$status" != 200 ]]; then
    jq -r '.error.message // .message // "AKS run command result failed"' "$scratch/result" >&2 || true
    echo "AKS run command result returned HTTP $status" >&2
    exit 1
  fi
  state=$(jq -r '.properties.provisioningState // ""' "$scratch/result")
  if [[ "$state" == Succeeded || "$state" == Failed || "$state" == Canceled ]]; then
    jq -r '.properties.logs // empty' "$scratch/result"
    exit_code=$(jq -r '.properties.exitCode // "unknown"' "$scratch/result")
    if [[ "$state" != Succeeded || "$exit_code" != 0 ]]; then
      jq -r '.properties.error.message // .error.message // .properties.reason // empty' "$scratch/result" >&2
      echo "AKS run command failed: state=$state exitCode=$exit_code" >&2
      exit 1
    fi
    exit 0
  fi
  sleep 5
done

echo 'Timed out waiting for AKS run command result' >&2
exit 1
