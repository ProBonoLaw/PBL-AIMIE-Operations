"""Read deployment inputs without embedding environment identifiers in source."""

import os
from urllib.parse import urlsplit


SHAREPOINT_VARIABLES = (
    "SHAREPOINT_TENANT_ID", "SHAREPOINT_CLIENT_ID",
    "SHAREPOINT_HOST", "SHAREPOINT_SITE_PREFIX",
)


def required_env(name):
    value = os.environ.get(name, "").strip()
    if not value:
        raise ValueError(f"Set {name} in the deployment environment")
    return value


def sharepoint_settings():
    settings = {name: required_env(name) for name in SHAREPOINT_VARIABLES}
    host = settings["SHAREPOINT_HOST"]
    parsed = urlsplit(f"https://{host}")
    if parsed.hostname != host or parsed.netloc != host or parsed.path or parsed.query or parsed.fragment:
        raise ValueError("SHAREPOINT_HOST must be a hostname without a scheme or path")
    prefix = settings["SHAREPOINT_SITE_PREFIX"]
    if not prefix.startswith("/") or any(char in prefix for char in "?#\\"):
        raise ValueError("SHAREPOINT_SITE_PREFIX must be an absolute site path")
    return settings


def seed_settings(seed):
    """Resolve entire ${NAME} values; JSON encoding happens after substitution."""
    return {
        name: required_env(value[2:-1])
        if isinstance(value, str) and value.startswith("${") and value.endswith("}")
        else value
        for name, value in seed.items()
    }
