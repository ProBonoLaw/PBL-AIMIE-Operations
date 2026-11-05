#!/usr/bin/env python3
"""Review or publish Markdown Converter appsettings to an environment's Key Vault."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
from urllib.parse import urlsplit


SECRET = "markdown-converter-appsettings"
DEFAULT_SUBSCRIPTION = os.environ.get("AZURE_SUBSCRIPTION_ID")
DEFAULT_VAULT = os.environ.get("KEY_VAULT_NAME")
DEFAULT_FILE = Path(__file__).resolve().parents[1] / ".local" / "markdown-converter-appsettings.Test.json"


def azure(subscription, *args):
    completed = subprocess.run(
        ["az", *args, "--subscription", subscription, "--output", "json", "--only-show-errors"],
        capture_output=True, text=True, check=True,
    )
    return json.loads(completed.stdout)


def validate_origins(value):
    origins = [origin.strip() for origin in value.split(",") if origin.strip()]
    if not origins:
        raise ValueError("CORS_ALLOWED_ORIGINS must include at least one origin")
    for origin in origins:
        parsed = urlsplit(origin)
        if parsed.scheme not in {"http", "https"} or not parsed.netloc \
                or parsed.path or parsed.query or parsed.fragment or parsed.username or parsed.password:
            raise ValueError(f"Invalid CORS origin: {origin}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--file", type=Path, default=DEFAULT_FILE, help="Converter appsettings JSON file")
    parser.add_argument("--vault", default=DEFAULT_VAULT, required=not DEFAULT_VAULT, help="Target Key Vault name")
    parser.add_argument("--subscription", default=DEFAULT_SUBSCRIPTION, required=not DEFAULT_SUBSCRIPTION, help="Azure subscription ID")
    parser.add_argument("--apply", action="store_true", help="Upload after explicit review")
    args = parser.parse_args()
    raw = args.file.read_bytes()
    payload = json.loads(raw)
    if set(payload) != {"CORS_ALLOWED_ORIGINS"} or not isinstance(payload["CORS_ALLOWED_ORIGINS"], str):
        raise ValueError("Converter appsettings must contain only CORS_ALLOWED_ORIGINS")
    validate_origins(payload["CORS_ALLOWED_ORIGINS"])
    digest = hashlib.sha256(raw).hexdigest()
    print(f"Vault: {args.vault}; secret: {SECRET}; settings: 1; SHA-256: {digest}")
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
