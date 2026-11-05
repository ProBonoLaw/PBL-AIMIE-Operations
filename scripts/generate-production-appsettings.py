#!/usr/bin/env python3
"""Generate local production worker and converter settings from Azure resources."""

import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
from urllib.parse import urlsplit
from configuration import required_env, sharepoint_settings


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SUBSCRIPTION = os.environ.get("AZURE_PRODUCTION_SUBSCRIPTION_ID")
DEFAULT_OUTPUT = ROOT / ".local" / "appsettings.Production.json"
DEFAULT_CONVERTER_OUTPUT = ROOT / ".local" / "markdown-converter-appsettings.Production.json"

APPSETTINGS_VAULT = os.environ.get("KEY_VAULT_NAME")
APPSETTINGS_SECRET = "account-operations-appsettings"
AIMIE_SERVICE_BASE_URL = "http://aimie-service.aimie-operations.svc.cluster.local"
CONVERTER_SERVICE = "markdown-converter.aimie-operations.svc.cluster.local"

EVENTHUB_NAME = "aimie-request-queue"
RESPONSE_EVENTHUB_NAME = "aimie-response-queue"
CHECKPOINT_CONTAINER = "aimie-operations-checkpoints"
FAILED_EVENTS_CONTAINER = "aimie-operations-failed-events"
OUTPUT_CONTAINER = "aimie-operations-results"
WORKER_CONTAINERS = {CHECKPOINT_CONTAINER, FAILED_EVENTS_CONTAINER, OUTPUT_CONTAINER}

REQUIRED = {
    "COSMOS_ENDPOINT", "COSMOS_DATABASE", "EVENTHUB_NAMESPACE", "EVENTHUB_NAME",
    "RESPONSE_EVENTHUB_NAME", "NOTIFICATION_SERVICE_URL", "CHECKPOINT_BLOB_CONTAINER_URI",
    "FAILED_EVENTS_BLOB_CONTAINER_URI", "OUTPUT_BLOB_CONTAINER_URI",
    "DECLARATION_WRITER_FUNCTION_URL", "FAST_QA_FUNCTION_URL",
    "ARGUMENT_BUILDER_FUNCTION_URL", "TIMELINE_BUILDER_FUNCTION_URL",
    "EXHIBIT_INDEX_URL", "MARKDOWN_TO_DOCX_URL", "SHAREPOINT_TENANT_ID",
    "SHAREPOINT_CLIENT_ID", "SHAREPOINT_CLIENT_SECRET", "SHAREPOINT_HOST",
    "SHAREPOINT_SITE_PREFIX",
}

SERVICE_ROUTES = {
    "DECLARATION_WRITER_FUNCTION_URL": "/api/AIInteractionService/declaration",
    "FAST_QA_FUNCTION_URL": "/api/AIInteractionService/qa/fast",
    "ARGUMENT_BUILDER_FUNCTION_URL": "/api/AIInteractionService/argument_builder",
    "TIMELINE_BUILDER_FUNCTION_URL": "/api/AIInteractionService/timeline",
}


def azure_json(subscription, *args):
    completed = subprocess.run(
        ["az", *args, "--subscription", subscription, "--output", "json", "--only-show-errors"],
        capture_output=True, text=True, check=True,
    )
    return json.loads(completed.stdout)


def required_string(settings, name):
    value = settings.get(name)
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"Production source settings do not contain {name}")
    return value.strip()


def graph_secret(subscription, source_settings):
    vault_uri = required_string(source_settings, "GRAPH_KEYVAULT_URI")
    vault_host = urlsplit(vault_uri).hostname
    if not vault_host or not vault_host.endswith(".vault.azure.net"):
        raise ValueError("GRAPH_KEYVAULT_URI must identify an Azure Key Vault")
    vault_name = vault_host.removesuffix(".vault.azure.net")
    secret_name = required_string(source_settings, "GRAPH_SECRET_NAME")
    result = azure_json(
        subscription, "keyvault", "secret", "show",
        "--vault-name", vault_name, "--name", secret_name,
    )
    value = result.get("value")
    if not isinstance(value, str) or not value:
        raise ValueError("Production SharePoint Graph secret is empty")
    return value


def production_origins(subscription, include_localhost):
    hostnames = azure_json(
        subscription, "webapp", "config", "hostname", "list",
        "--resource-group", required_env("PORTAL_RESOURCE_GROUP"), "--webapp-name", required_env("PORTAL_WEB_APP"),
    )
    hosts = sorted({item.get("name", "").lower() for item in hostnames
                    if item.get("name") and not item["name"].lower().endswith(".azurewebsites.net")})
    if not hosts:
        raise ValueError("No custom production portal hostnames were found")
    origins = [f"https://{host}" for host in hosts]
    if include_localhost:
        origins.append("http://localhost:3000")
    return ",".join(origins)


def missing_blob_containers(subscription):
    try:
        containers = azure_json(
            subscription, "storage", "container-rm", "list",
            "--resource-group", required_env("STORAGE_RESOURCE_GROUP"), "--storage-account", required_env("STORAGE_ACCOUNT"),
        )
    except subprocess.CalledProcessError:
        return None
    existing = {item.get("name") for item in containers}
    return sorted(WORKER_CONTAINERS - existing)


def validate_worker_settings(settings):
    for name, value in settings.items():
        if name.endswith("_URL") or name.endswith("_URI") or name == "COSMOS_ENDPOINT":
            is_internal_converter_url = name in {"EXHIBIT_INDEX_URL", "MARKDOWN_TO_DOCX_URL"} \
                and value.startswith(f"http://{CONVERTER_SERVICE}/")
            is_internal_aimie_url = name in SERVICE_ROUTES \
                and value.startswith(f"{AIMIE_SERVICE_BASE_URL}/")
            is_internal_notification_url = name == "NOTIFICATION_SERVICE_URL" \
                and value == "http://notification-service.email-operations.svc.cluster.local/api/sendNotice"
            if not value.startswith("https://") and not is_internal_converter_url and not is_internal_aimie_url \
                    and not is_internal_notification_url:
                raise ValueError(f"{name} must use HTTPS or its explicitly allowed in-cluster HTTP endpoint")


def write_private_json(path, payload):
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{os.getpid()}.tmp")
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    try:
        with os.fdopen(descriptor, "w") as handle:
            json.dump(payload, handle, indent=2)
            handle.write("\n")
        os.chmod(temporary, 0o600)
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--subscription", default=DEFAULT_SUBSCRIPTION, required=not DEFAULT_SUBSCRIPTION,
                        help="Azure production subscription ID")
    parser.add_argument("--vault", default=APPSETTINGS_VAULT, required=not APPSETTINGS_VAULT,
                        help="Vault containing account-operations-appsettings")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT,
                        help="Local worker settings output path")
    parser.add_argument("--converter-output", type=Path, default=DEFAULT_CONVERTER_OUTPUT,
                        help="Local converter settings output path")
    parser.add_argument("--include-localhost", action="store_true",
                        help="Also allow http://localhost:3000 in converter CORS")
    args = parser.parse_args()
    sharepoint_config = sharepoint_settings()

    source = azure_json(
        args.subscription, "keyvault", "secret", "show",
        "--vault-name", args.vault, "--name", APPSETTINGS_SECRET,
    )
    source_settings = json.loads(source["value"])

    cosmos = azure_json(
        args.subscription, "cosmosdb", "show",
        "--resource-group", required_env("COSMOS_RESOURCE_GROUP"), "--name", required_env("COSMOS_ACCOUNT"),
    )
    storage = azure_json(
        args.subscription, "storage", "account", "show",
        "--resource-group", required_env("STORAGE_RESOURCE_GROUP"), "--name", required_env("STORAGE_ACCOUNT"),
    )
    blob_root = storage["primaryEndpoints"]["blob"].rstrip("/")

    settings = {
        "COSMOS_ENDPOINT": cosmos["documentEndpoint"],
        "COSMOS_DATABASE": required_string(source_settings, "COSMOS_DATABASE"),
        "EVENTHUB_NAMESPACE": required_string(source_settings, "EVENTHUB_NAMESPACE"),
        "EVENTHUB_NAME": EVENTHUB_NAME,
        "RESPONSE_EVENTHUB_NAME": RESPONSE_EVENTHUB_NAME,
        "NOTIFICATION_SERVICE_URL": required_string(source_settings, "NOTIFICATION_SERVICE_URL"),
        "CHECKPOINT_BLOB_CONTAINER_URI": f"{blob_root}/{CHECKPOINT_CONTAINER}",
        "FAILED_EVENTS_BLOB_CONTAINER_URI": f"{blob_root}/{FAILED_EVENTS_CONTAINER}",
        "OUTPUT_BLOB_CONTAINER_URI": f"{blob_root}/{OUTPUT_CONTAINER}",
        "EXHIBIT_INDEX_URL": f"http://{CONVERTER_SERVICE}/api/markdowndocumentexhibitindexer",
        "MARKDOWN_TO_DOCX_URL": f"http://{CONVERTER_SERVICE}/api/markdowntodocxconverter",
        "SHAREPOINT_CLIENT_SECRET": graph_secret(args.subscription, source_settings),
        **sharepoint_config,
    }
    settings.update({key: f"{AIMIE_SERVICE_BASE_URL}{route}"
                     for key, route in SERVICE_ROUTES.items()})
    if set(settings) != REQUIRED or not all(
            isinstance(value, str) and value.strip() for value in settings.values()):
        raise ValueError("Generated production worker settings are incomplete")
    validate_worker_settings(settings)

    converter_settings = {
        "CORS_ALLOWED_ORIGINS": production_origins(args.subscription, args.include_localhost)
    }
    write_private_json(args.output, settings)
    write_private_json(args.converter_output, converter_settings)
    print(f"Wrote {args.output} with {len(settings)} settings; secret values were not printed")
    print(f"Wrote {args.converter_output} with 1 CORS setting; origins: {converter_settings['CORS_ALLOWED_ORIGINS']}")
    missing = missing_blob_containers(args.subscription)
    if missing:
        print(f"Required production Blob containers need provisioning before worker startup: {', '.join(missing)}",
              file=sys.stderr)
    elif missing == []:
        print("All production worker Blob containers exist")
    else:
        print("Could not check production Blob container existence; verify them before worker startup",
              file=sys.stderr)


if __name__ == "__main__":
    main()
