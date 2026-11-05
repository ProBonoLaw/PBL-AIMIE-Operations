#!/usr/bin/env bash
set -euo pipefail

export DEPLOYMENT_ENV=aks-test

exec bash scripts/deploy-aks-environment.sh
