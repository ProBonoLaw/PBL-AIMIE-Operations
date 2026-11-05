#!/usr/bin/env bash
set -euo pipefail

export DEPLOYMENT_ENV=aks-production

exec bash scripts/deploy-aks-environment.sh
