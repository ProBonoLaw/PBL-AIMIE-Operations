#!/usr/bin/env bash
set -euo pipefail

subscription_id="${1:?usage: provision-consumer-groups.sh <subscription-id> [eventhub-namespace]}"
resource_group="${EVENTHUB_RESOURCE_GROUP:?Set EVENTHUB_RESOURCE_GROUP}"
namespace="${2:-${EVENTHUB_NAMESPACE_NAME:?Set EVENTHUB_NAMESPACE_NAME}}"
hub=aimie-request-queue

for group in aimie-ops-declaration aimie-ops-qa aimie-ops-argument-builder aimie-ops-timeline-builder; do
  az eventhubs eventhub consumer-group create \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --namespace-name "$namespace" \
    --eventhub-name "$hub" \
    --name "$group" \
    --output none
done
