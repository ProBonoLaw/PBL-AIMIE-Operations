#!/usr/bin/env bash
# Create isolated Event Hubs consumer groups for VS Code debug sessions.
set -euo pipefail

subscription_id="${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}"
for group in aimie-ops-local-declaration aimie-ops-local-qa \
  aimie-ops-local-argument-builder aimie-ops-local-timeline-builder; do
  az eventhubs eventhub consumer-group create \
    --subscription "$subscription_id" \
    --resource-group "${EVENTHUB_RESOURCE_GROUP:?Set EVENTHUB_RESOURCE_GROUP}" \
    --namespace-name "${EVENTHUB_NAMESPACE_NAME:?Set EVENTHUB_NAMESPACE_NAME}" \
    --eventhub-name aimie-request-queue \
    --name "$group" --output none
done
for container in aimie-operations-checkpoints aimie-operations-failed-events aimie-operations-results; do
  az storage container create --subscription "$subscription_id" \
    --account-name "${STORAGE_ACCOUNT:?Set STORAGE_ACCOUNT}" --auth-mode login \
    --name "$container" --output none
done
printf 'Local debug consumer groups and Blob containers are ready.\n'
