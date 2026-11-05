#!/usr/bin/env python3
"""Create a local, untracked worker configuration from the active test resources."""

import json
import os
from pathlib import Path
import subprocess
from configuration import required_env, seed_settings, sharepoint_settings


APPSETTINGS_SEED = Path(__file__).resolve().parents[1] / "k8s" / "aimie-operations" / "appsettings.aks-test.seed.json"
CONVERTER_APPSETTINGS_SEED = Path(__file__).resolve().parents[1] / "k8s" / "aimie-operations" / "appsettings-markdown-converter.aks-test.seed.json"
OUTPUT = Path(__file__).resolve().parents[1] / ".local" / "appsettings.Test.json"
CONVERTER_OUTPUT = Path(__file__).resolve().parents[1] / ".local" / "markdown-converter-appsettings.Test.json"
FILE_OPERATIONS_SECRET = "file-operations-appsettings"
AIMIE_SERVICE_BASE_URL = "http://aimie-service.aimie-operations.svc.cluster.local"

AIMIE_ROUTES = {
    "DECLARATION_WRITER_FUNCTION_URL": "/api/AIInteractionService/declaration",
    "FAST_QA_FUNCTION_URL": "/api/AIInteractionService/qa/fast",
    "ARGUMENT_BUILDER_FUNCTION_URL": "/api/AIInteractionService/argument_builder",
    "TIMELINE_BUILDER_FUNCTION_URL": "/api/AIInteractionService/timeline",
}


def azure_json(*args):
    result = subprocess.run(
        ["az", *args, "--subscription", required_env("AZURE_SUBSCRIPTION_ID"), "--output", "json", "--only-show-errors"],
        capture_output=True, text=True, check=True,
    )
    return json.loads(result.stdout)


def main():
    config = seed_settings(json.loads(APPSETTINGS_SEED.read_text()))
    sharepoint_config = sharepoint_settings()
    converter_url = os.environ.get("MARKDOWN_CONVERTER_PUBLIC_BASE_URL", "").rstrip("/")
    aimie_service_url = os.environ.get("AIMIE_SERVICE_BASE_URL", AIMIE_SERVICE_BASE_URL).rstrip("/")
    storage = azure_json("storage", "account", "show", "--resource-group", required_env("STORAGE_RESOURCE_GROUP"),
                         "--name", required_env("STORAGE_ACCOUNT"))
    blob_root = storage["primaryEndpoints"]["blob"].rstrip("/")
    sharepoint = json.loads(azure_json("keyvault", "secret", "show", "--vault-name", required_env("KEY_VAULT_NAME"),
                                      "--name", FILE_OPERATIONS_SECRET)["value"])
    result = {
        "COSMOS_ENDPOINT": config["COSMOS_ENDPOINT"],
        "COSMOS_DATABASE": config["COSMOS_DATABASE"],
        "EVENTHUB_NAMESPACE": config["EVENTHUB_NAMESPACE"],
        "EVENTHUB_NAME": config["EVENTHUB_NAME"],
        "RESPONSE_EVENTHUB_NAME": config["RESPONSE_EVENTHUB_NAME"],
        "NOTIFICATION_SERVICE_URL": config["NOTIFICATION_SERVICE_URL"],
        "CHECKPOINT_BLOB_CONTAINER_URI": f"{blob_root}/aimie-operations-checkpoints",
        "FAILED_EVENTS_BLOB_CONTAINER_URI": f"{blob_root}/aimie-operations-failed-events",
        "OUTPUT_BLOB_CONTAINER_URI": f"{blob_root}/aimie-operations-results",
        "EXHIBIT_INDEX_URL": (f"{converter_url}/api/markdowndocumentexhibitindexer" if converter_url
                              else config["EXHIBIT_INDEX_URL"]),
        "MARKDOWN_TO_DOCX_URL": (f"{converter_url}/api/markdowntodocxconverter" if converter_url
                                 else config["MARKDOWN_TO_DOCX_URL"]),
        "SHAREPOINT_CLIENT_SECRET": sharepoint["SHAREPOINT_CLIENT_SECRET"],
        **sharepoint_config,
    }
    result.update({name: f"{aimie_service_url}{route}" for name, route in AIMIE_ROUTES.items()})
    if not all(result.values()):
        raise ValueError("A required test application setting is empty")

    OUTPUT.parent.mkdir(mode=0o700, exist_ok=True)
    converter_settings = seed_settings(json.loads(CONVERTER_APPSETTINGS_SEED.read_text()))
    for path, settings in ((OUTPUT, result), (CONVERTER_OUTPUT, converter_settings)):
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(descriptor, "w") as handle:
            json.dump(settings, handle, indent=2)
            handle.write("\n")
        os.chmod(path, 0o600)
    print(f"Wrote {OUTPUT} with {len(result)} settings; secret values were not printed")
    print(f"Wrote {CONVERTER_OUTPUT} with {len(converter_settings)} settings")


if __name__ == "__main__":
    main()
