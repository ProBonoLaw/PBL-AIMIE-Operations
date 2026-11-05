#!/usr/bin/env python3
"""Review or publish worker appsettings to an environment's Key Vault."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess


SECRET = "aimie-operations-appsettings"
DEFAULT_SUBSCRIPTION = os.environ.get("AZURE_SUBSCRIPTION_ID")
DEFAULT_VAULT = os.environ.get("KEY_VAULT_NAME")
DEFAULT_FILE = Path(__file__).resolve().parents[1] / ".local" / "appsettings.Test.json"
REQUIRED = {
    "COSMOS_ENDPOINT", "COSMOS_DATABASE", "EVENTHUB_NAMESPACE", "EVENTHUB_NAME",
    "RESPONSE_EVENTHUB_NAME", "NOTIFICATION_SERVICE_URL", "CHECKPOINT_BLOB_CONTAINER_URI",
    "FAILED_EVENTS_BLOB_CONTAINER_URI", "OUTPUT_BLOB_CONTAINER_URI",
    "DECLARATION_WRITER_FUNCTION_URL", "FAST_QA_FUNCTION_URL",
    "ARGUMENT_BUILDER_FUNCTION_URL", "TIMELINE_BUILDER_FUNCTION_URL",
    "EXHIBIT_INDEX_URL", "MARKDOWN_TO_DOCX_URL",
    "SHAREPOINT_TENANT_ID", "SHAREPOINT_CLIENT_ID", "SHAREPOINT_CLIENT_SECRET",
    "SHAREPOINT_HOST", "SHAREPOINT_SITE_PREFIX",
}


def azure(subscription, *args):
    completed = subprocess.run(
        ["az", *args, "--subscription", subscription, "--output", "json", "--only-show-errors"],
        capture_output=True, text=True, check=True,
    )
    return json.loads(completed.stdout)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--file", type=Path, default=DEFAULT_FILE, help="Appsettings JSON file")
    parser.add_argument("--vault", default=DEFAULT_VAULT, required=not DEFAULT_VAULT, help="Target Key Vault name")
    parser.add_argument("--subscription", default=DEFAULT_SUBSCRIPTION, required=not DEFAULT_SUBSCRIPTION, help="Azure subscription ID")
    parser.add_argument("--apply", action="store_true", help="Upload after explicit review")
    args = parser.parse_args()
    raw = args.file.read_bytes()
    payload = json.loads(raw)
    if set(payload) != REQUIRED or not all(isinstance(value, str) and value for value in payload.values()):
        raise ValueError("Local appsettings keys or values are incomplete")
    for name, value in payload.items():
        if name.endswith("_URL") or name.endswith("_URI") or name == "COSMOS_ENDPOINT":
            is_internal_converter_url = name in {"EXHIBIT_INDEX_URL", "MARKDOWN_TO_DOCX_URL"} \
                and value.startswith("http://markdown-converter.aimie-operations.svc.cluster.local/")
            is_internal_aimie_url = name in {
                "DECLARATION_WRITER_FUNCTION_URL", "FAST_QA_FUNCTION_URL",
                "ARGUMENT_BUILDER_FUNCTION_URL", "TIMELINE_BUILDER_FUNCTION_URL",
            } and value.startswith("http://aimie-service.aimie-operations.svc.cluster.local/")
            is_internal_notification_url = name == "NOTIFICATION_SERVICE_URL" \
                and value == "http://notification-service.email-operations.svc.cluster.local/api/sendNotice"
            if not value.startswith("https://") and not is_internal_converter_url and not is_internal_aimie_url \
                    and not is_internal_notification_url:
                raise ValueError(f"{name} must use HTTPS or its explicitly allowed in-cluster HTTP endpoint")
    digest = hashlib.sha256(raw).hexdigest()
    print(f"Vault: {args.vault}; secret: {SECRET}; settings: {len(payload)}; SHA-256: {digest}")
    print("Protected keys: SHAREPOINT_CLIENT_SECRET")
    if not args.apply:
        print("Review only. Pass --apply after approval to publish.")
        return
    azure(args.subscription, "keyvault", "secret", "set", "--vault-name", args.vault, "--name", SECRET,
          "--file", str(args.file), "--encoding", "utf-8")
    stored = azure(args.subscription, "keyvault", "secret", "show", "--vault-name", args.vault, "--name", SECRET)
    if stored["value"].encode() != raw:
        raise RuntimeError("Key Vault value differs from the local file")
    print(f"Published and verified version: {stored['id'].rsplit('/', 1)[-1]}")


if __name__ == "__main__":
    main()
